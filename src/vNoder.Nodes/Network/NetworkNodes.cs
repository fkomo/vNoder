using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using vNoder.Core;

namespace vNoder.Nodes;

// Network nodes. Everything that goes out on the wire has Run If / Done. Like HTTP Request, "the other
// end said no" is a result (Found, Success, Open = false); only input that makes no sense throws.

internal static class Hosts
{
    /// <summary>A host from what was typed: a bare name, an IP, host:port, or a whole URL.</summary>
    public static (string Host, int? Port) Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("No host set.");

        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (uri.IdnHost, uri.IsDefaultPort ? null : uri.Port);

        // host:port, but not a bare IPv6 address, whose colons are part of it.
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var port))
            return (text[..colon], port);

        return (text.Trim('[', ']'), null);
    }

    public static int Port(double value, int fallback) =>
        double.IsNaN(value) ? fallback : (int)Math.Clamp(value, 1, 65535);
}

/// <summary>
/// Connects to a TLS server and reports its certificate, as Certificate Load reports a file's, plus
/// whether this machine trusts it. Wired into Days Left and a Compare, it is a certificate-expiry
/// monitor for a continuous run.
/// </summary>
[NodeDefinition("Network/TLS Certificate", Description = "Fetches a server's TLS certificate (e.g. example.com:443): subject, expiry, days left, names and whether it is trusted.")]
public sealed class TlsCertificateNode : NodeBase
{
    private string _host = string.Empty;
    private double _port = 443;
    private double _timeoutSeconds = 10;

    private readonly InputPort _hostIn;
    private readonly InputPort _portIn;
    private readonly CertificateInfoPorts _info;
    private readonly OutputPort _trustedOut;
    private readonly OutputPort _errorsOut;
    private readonly OutputPort _protocolOut;

    public TlsCertificateNode()
    {
        _hostIn = AddInput<string>("Host");
        _portIn = AddInput<double>("Port");
        _info = new CertificateInfoPorts(AddOutput);
        _trustedOut = AddOutput<bool>("Trusted");
        _errorsOut = AddOutput<string>("Errors");
        _protocolOut = AddOutput<string>("Protocol");

        AddRunGate();
    }

    /// <summary>A host name, host:port, or a URL.</summary>
    public string Host { get => _host; set => Set(ref _host, value ?? string.Empty); }

    public double Port { get => _port; set => Set(ref _port, value); }

    public double TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var (host, typedPort) = Hosts.Parse(_hostIn.IsConnected ? _hostIn.ValueOr(string.Empty) : Host);
        var port = _portIn.IsConnected ? Hosts.Port(_portIn.ValueOr(Port), 443) : typedPort ?? Hosts.Port(Port, 443);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(double.IsNaN(TimeoutSeconds) ? 10 : TimeoutSeconds, 1, 120)));

        X509Certificate2? certificate = null;
        var errors = SslPolicyErrors.None;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            // Accept whatever the server presents: judging it is this node's output, not its precondition.
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, policyErrors) =>
            {
                if (cert is not null)
                    certificate = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());

                errors = policyErrors;
                return true;
            });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token)
                .ConfigureAwait(false);
            _protocolOut.Value = ssl.SslProtocol switch
            {
#pragma warning disable SYSLIB0039 // Naming the old protocols is not using them.
                System.Security.Authentication.SslProtocols.Tls => "TLS 1.0",
                System.Security.Authentication.SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore SYSLIB0039
                System.Security.Authentication.SslProtocols.Tls12 => "TLS 1.2",
                System.Security.Authentication.SslProtocols.Tls13 => "TLS 1.3",
                var other => other.ToString(),
            };
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No TLS handshake with {host}:{port} in time.");
        }
        catch (Exception exception) when (exception is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            throw new InvalidOperationException($"{host}:{port}: {exception.Message}", exception);
        }

        using (certificate)
        {
            if (certificate is null)
                throw new InvalidOperationException($"{host}:{port} sent no certificate.");

            _info.Fill(certificate);
        }

        _trustedOut.Value = errors == SslPolicyErrors.None;
        _errorsOut.Value = errors == SslPolicyErrors.None ? string.Empty : errors switch
        {
            _ when errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) && errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)
                => "Name mismatch; untrusted or expired chain",
            _ when errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) => "Name mismatch",
            _ when errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) => "Untrusted or expired chain",
            _ => errors.ToString(),
        };
    }
}

[NodeDefinition("Network/DNS Lookup", Description = "Resolves a host name to its IP addresses, or an IP address back to its host name.")]
public sealed class DnsLookupNode : NodeBase
{
    private string _host = string.Empty;
    private bool _ipv4Only;

    private readonly InputPort _hostIn;
    private readonly OutputPort _addressOut;
    private readonly OutputPort _addressesOut;
    private readonly OutputPort _hostNameOut;
    private readonly OutputPort _foundOut;

    public DnsLookupNode()
    {
        _hostIn = AddInput<string>("Host");
        _addressOut = AddOutput<string>("Address");
        _addressesOut = AddOutput<string[]>("Addresses");
        _hostNameOut = AddOutput<string>("Host Name");
        _foundOut = AddOutput<bool>("Found");

        AddRunGate();
    }

    public string Host { get => _host; set => Set(ref _host, value ?? string.Empty); }

    public bool Ipv4Only { get => _ipv4Only; set => Set(ref _ipv4Only, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var (host, _) = Hosts.Parse(_hostIn.IsConnected ? _hostIn.ValueOr(string.Empty) : Host);

        try
        {
            // An IP address is looked up the other way, for its name.
            var entry = IPAddress.TryParse(host, out var ip)
                ? await Dns.GetHostEntryAsync(ip.ToString(), context.CancellationToken).ConfigureAwait(false)
                : await Dns.GetHostEntryAsync(host, Ipv4Only ? AddressFamily.InterNetwork : AddressFamily.Unspecified, context.CancellationToken).ConfigureAwait(false);

            var addresses = entry.AddressList
                .Where(a => !Ipv4Only || a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();

            _addressesOut.Value = addresses;
            _addressOut.Value = addresses.FirstOrDefault();
            _hostNameOut.Value = entry.HostName;
            _foundOut.Value = true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            _addressesOut.Value = Array.Empty<string>();
            _addressOut.Value = null;
            _hostNameOut.Value = null;
            _foundOut.Value = false;
        }
    }
}

[NodeDefinition("Network/Ping", Description = "Pings a host (ICMP echo): whether it answered, and the round-trip time.")]
public sealed class PingNode : NodeBase
{
    private string _host = string.Empty;
    private double _timeoutMilliseconds = 1000;

    private readonly InputPort _hostIn;
    private readonly OutputPort _successOut;
    private readonly OutputPort _timeOut;
    private readonly OutputPort _addressOut;
    private readonly OutputPort _statusOut;

    public PingNode()
    {
        _hostIn = AddInput<string>("Host");
        _successOut = AddOutput<bool>("Success");
        _timeOut = AddOutput<double>("Milliseconds");
        _addressOut = AddOutput<string>("Address");
        _statusOut = AddOutput<string>("Status");

        AddRunGate();
    }

    public string Host { get => _host; set => Set(ref _host, value ?? string.Empty); }

    public double TimeoutMilliseconds { get => _timeoutMilliseconds; set => Set(ref _timeoutMilliseconds, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var (host, _) = Hosts.Parse(_hostIn.IsConnected ? _hostIn.ValueOr(string.Empty) : Host);
        var timeout = double.IsNaN(TimeoutMilliseconds) ? 1000 : (int)Math.Clamp(TimeoutMilliseconds, 10, 60_000);

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(timeout), cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);

            _successOut.Value = reply.Status == IPStatus.Success;
            _timeOut.Value = reply.Status == IPStatus.Success ? (double)reply.RoundtripTime : null;
            _addressOut.Value = reply.Address?.ToString();
            _statusOut.Value = reply.Status.ToString();
        }
        catch (PingException exception)
        {
            // An unknown host lands here rather than in the reply.
            _successOut.Value = false;
            _timeOut.Value = null;
            _addressOut.Value = null;
            _statusOut.Value = exception.InnerException?.Message ?? exception.Message;
        }
    }
}

[NodeDefinition("Network/TCP Port Check", Description = "Whether a TCP port on a host accepts connections, and how fast.")]
public sealed class TcpPortCheckNode : NodeBase
{
    private string _host = "localhost";
    private double _port = 443;
    private double _timeoutMilliseconds = 2000;

    private readonly InputPort _hostIn;
    private readonly InputPort _portIn;
    private readonly OutputPort _openOut;
    private readonly OutputPort _timeOut;
    private readonly OutputPort _errorOut;

    public TcpPortCheckNode()
    {
        _hostIn = AddInput<string>("Host");
        _portIn = AddInput<double>("Port");
        _openOut = AddOutput<bool>("Open");
        _timeOut = AddOutput<double>("Milliseconds");
        _errorOut = AddOutput<string>("Error");

        AddRunGate();
    }

    public string Host { get => _host; set => Set(ref _host, value ?? string.Empty); }

    public double Port { get => _port; set => Set(ref _port, value); }

    public double TimeoutMilliseconds { get => _timeoutMilliseconds; set => Set(ref _timeoutMilliseconds, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var (host, typedPort) = Hosts.Parse(_hostIn.IsConnected ? _hostIn.ValueOr(string.Empty) : Host);
        var port = _portIn.IsConnected ? Hosts.Port(_portIn.ValueOr(Port), 443) : typedPort ?? Hosts.Port(Port, 443);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(double.IsNaN(TimeoutMilliseconds) ? 2000 : Math.Clamp(TimeoutMilliseconds, 10, 60_000)));

        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            _openOut.Value = true;
            _timeOut.Value = watch.Elapsed.TotalMilliseconds;
            _errorOut.Value = string.Empty;
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            Closed("Timed out");
        }
        catch (SocketException exception)
        {
            Closed(exception.Message);
        }

        void Closed(string error)
        {
            _openOut.Value = false;
            _timeOut.Value = null;
            _errorOut.Value = error;
        }
    }
}

/// <summary>
/// Splits a URL into its parts. The query also comes out as a JSON object (a repeated key becomes an
/// array) and as lists of keys and values, decoded. A URL that does not parse is a result
/// (Valid = false), not an error.
/// </summary>
[NodeDefinition("Network/URL Parse", Description = "Splits a URL into scheme, host, port, path, query (as JSON and lists) and fragment.")]
public sealed class UrlParseNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _schemeOut;
    private readonly OutputPort _hostOut;
    private readonly OutputPort _portOut;
    private readonly OutputPort _pathOut;
    private readonly OutputPort _queryOut;
    private readonly OutputPort _queryJsonOut;
    private readonly OutputPort _keysOut;
    private readonly OutputPort _valuesOut;
    private readonly OutputPort _fragmentOut;
    private readonly OutputPort _validOut;

    public UrlParseNode()
    {
        _in = AddInput<string>("URL");
        _schemeOut = AddOutput<string>("Scheme");
        _hostOut = AddOutput<string>("Host");
        _portOut = AddOutput<double>("Port");
        _pathOut = AddOutput<string>("Path");
        _queryOut = AddOutput<string>("Query");
        _queryJsonOut = AddOutput<string>("Query JSON");
        _keysOut = AddOutput<string[]>("Query Keys");
        _valuesOut = AddOutput<string[]>("Query Values");
        _fragmentOut = AddOutput<string>("Fragment");
        _validOut = AddOutput<bool>("Valid");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            foreach (var output in Outputs)
                output.Value = null;

            _validOut.Value = false;
            return Task.CompletedTask;
        }

        var pairs = Urls.ParseQuery(uri.Query);
        var json = new JsonObject();
        foreach (var group in pairs.GroupBy(p => p.Key))
        {
            json[group.Key] = group.Count() == 1
                ? JsonValue.Create(group.First().Value)
                : new JsonArray([.. group.Select(p => (JsonNode?)JsonValue.Create(p.Value))]);
        }

        _schemeOut.Value = uri.Scheme;
        _hostOut.Value = uri.Host;
        _portOut.Value = (double)uri.Port;
        _pathOut.Value = Uri.UnescapeDataString(uri.AbsolutePath);
        _queryOut.Value = uri.Query.TrimStart('?');
        _queryJsonOut.Value = json.ToJsonString(JsonText.Compact);
        _keysOut.Value = pairs.Select(p => p.Key).ToArray();
        _valuesOut.Value = pairs.Select(p => p.Value).ToArray();
        _fragmentOut.Value = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        _validOut.Value = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds a URL from a base, a path and a query, encoding each part. The query can be a JSON object
/// (an array value repeats the key), a list of key=value lines, or text as it would follow the "?".
/// </summary>
[NodeDefinition("Network/URL Build", Description = "Builds a URL from a base, path and query (a JSON object or key=value lines), encoding each part.")]
public sealed class UrlBuildNode : NodeBase
{
    private string _base = "https://example.com";
    private string _path = string.Empty;
    private string _query = string.Empty;

    private readonly InputPort _baseIn;
    private readonly InputPort _pathIn;
    private readonly InputPort _queryIn;
    private readonly OutputPort _out;

    public UrlBuildNode()
    {
        _baseIn = AddInput<string>("Base");
        _pathIn = AddInput<string>("Path");
        _queryIn = AddInput("Query", PortTypeCompatibility.Any);
        _out = AddOutput<string>("URL");
    }

    public string Base { get => _base; set => Set(ref _base, value ?? string.Empty); }

    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    /// <summary>A JSON object, key=value lines, or a=1&amp;b=2.</summary>
    public string Query { get => _query; set => Set(ref _query, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var url = _baseIn.ValueOr(Base).Trim();
        var path = _pathIn.ValueOr(Path).Trim();

        // Anything already on the base (a query or fragment) stays in front of what is added here.
        var fragmentAt = url.IndexOf('#');
        var fragment = fragmentAt >= 0 ? url[fragmentAt..] : string.Empty;
        if (fragmentAt >= 0)
            url = url[..fragmentAt];

        var queryAt = url.IndexOf('?');
        var existing = queryAt >= 0 ? url[(queryAt + 1)..] : string.Empty;
        if (queryAt >= 0)
            url = url[..queryAt];

        if (path.Length > 0)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
            url = url.TrimEnd('/') + "/" + string.Join('/', segments);
        }

        var pairs = Pairs(_queryIn.IsConnected ? _queryIn.Value : Query)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");
        var query = string.Join('&', new[] { existing }.Concat(pairs).Where(p => p.Length > 0));

        _out.Value = url + (query.Length > 0 ? "?" + query : string.Empty) + fragment;
        return Task.CompletedTask;
    }

    private static IEnumerable<KeyValuePair<string, string>> Pairs(object? value)
    {
        switch (value)
        {
            case null:
                return [];

            case string[] lines:
                return lines.SelectMany(Pairs);

            case string text when text.TrimStart().StartsWith('{'):
                var obj = JsonText.Parse(text) as JsonObject
                          ?? throw new InvalidOperationException("The query JSON must be an object.");
                return obj.SelectMany(p => p.Value is JsonArray array
                    ? array.Select(item => KeyValuePair.Create(p.Key, JsonText.ToText(item)))
                    : [KeyValuePair.Create(p.Key, p.Value is null ? string.Empty : JsonText.ToText(p.Value))]);

            case string text:
                // Lines or a query string, already decoded or not: decode, then encode once on the way out.
                return text.Split(['\n', '\r', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SelectMany(part => Urls.ParseQuery(part));

            default:
                return Pairs(NodeValues.ToText(value));
        }
    }
}

internal static class Urls
{
    public static List<KeyValuePair<string, string>> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part =>
        {
            var equals = part.IndexOf('=');
            var key = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? string.Empty : part[(equals + 1)..];
            return KeyValuePair.Create(Decode(key), Decode(value));
        })
        .ToList();

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}

/// <summary>
/// Downloads a URL straight to a file, without holding it in memory, so large files are fine. The
/// download goes to a ".partial" file that replaces the target only when complete, so a failed or
/// cancelled download never leaves a truncated file under the real name. A folder as the path saves
/// under the server's file name.
/// </summary>
/// <remarks>
/// An error status is a result, as in HTTP Request: nothing is written and Success is false.
/// </remarks>
[NodeDefinition("Network/Download To File", Description = "Downloads a URL straight to a file (streamed, so large files are fine).")]
public sealed class DownloadToFileNode : NodeBase
{
    private string _url = string.Empty;
    private string _path = string.Empty;
    private ExistingFileMode _ifExists;
    private double _timeoutSeconds = 300;

    private readonly InputPort _urlIn;
    private readonly InputPort _pathIn;
    private readonly OutputPort _successOut;
    private readonly OutputPort _savedAsOut;
    private readonly OutputPort _sizeOut;
    private readonly OutputPort _statusOut;

    public DownloadToFileNode()
    {
        _urlIn = AddInput<string>("URL");
        _pathIn = AddInput<string>("Path");
        _successOut = AddOutput<bool>("Success");
        _savedAsOut = AddOutput<string>("Saved As");
        _sizeOut = AddOutput<double>("Size");
        _statusOut = AddOutput<double>("Status");

        AddRunGate();
    }

    public string Url { get => _url; set => Set(ref _url, value ?? string.Empty); }

    /// <summary>A file path, or a folder to save under the server's file name.</summary>
    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    public ExistingFileMode IfExists { get => _ifExists; set => Set(ref _ifExists, value); }

    public double TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _successOut.Value = false;

        var url = _urlIn.ValueOr(Url).Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"'{url}' is not an http or https URL.");

        var path = Environment.ExpandEnvironmentVariables((_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path).Trim().Trim('"'));
        if (path.Length == 0)
            throw new InvalidOperationException("No file path set.");

        var seconds = double.IsNaN(TimeoutSeconds) ? 300 : Math.Clamp(TimeoutSeconds, 1, 86_400);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        HttpResponseMessage response;
        try
        {
            response = await HttpRequestNode.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {seconds} s.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        using (response)
        {
            _statusOut.Value = (double)(int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return;

            if (Directory.Exists(path) || path.EndsWith('\\') || path.EndsWith('/'))
                path = System.IO.Path.Combine(path, FileName(response, uri));

            if (File.Exists(path))
            {
                switch (IfExists)
                {
                    case ExistingFileMode.Skip:
                        _savedAsOut.Value = path;
                        return;
                    case ExistingFileMode.AddNumber:
                        path = FilePaths.NextFreeName(path);
                        break;
                }
            }

            FilePaths.EnsureDirectory(path);
            var partial = path + ".partial";
            try
            {
                await using (var file = File.Create(partial))
                {
                    await response.Content.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
                }

                File.Move(partial, path, overwrite: true);
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The download did not finish within {seconds} s.");
            }
            finally
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
        }

        _successOut.Value = true;
        _savedAsOut.Value = path;
        _sizeOut.Value = (double)new FileInfo(path).Length;
    }

    private static string FileName(HttpResponseMessage response, Uri uri)
    {
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? string.Empty);

        name = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        return name.Length > 0 ? name : "download";
    }
}

/// <summary>
/// An IP network in CIDR notation (192.168.1.0/24, 10.1.2.3/8, 2001:db8::/32): its network and
/// broadcast addresses, usable host range, mask and size, and whether an address falls inside. Host
/// bits in the address are allowed and ignored. IPv6 has no broadcast; Broadcast is then its last address.
/// </summary>
[NodeDefinition("Network/IP Range", Description = "CIDR calculator: network, broadcast, first and last host, mask, host count, and whether an address is in range.")]
public sealed class IpRangeNode : NodeBase
{
    private string _cidr = "192.168.1.0/24";
    private string _address = string.Empty;

    private readonly InputPort _cidrIn;
    private readonly InputPort _addressIn;
    private readonly OutputPort _networkOut;
    private readonly OutputPort _broadcastOut;
    private readonly OutputPort _firstOut;
    private readonly OutputPort _lastOut;
    private readonly OutputPort _maskOut;
    private readonly OutputPort _prefixOut;
    private readonly OutputPort _countOut;
    private readonly OutputPort _containsOut;

    public IpRangeNode()
    {
        _cidrIn = AddInput<string>("CIDR");
        _addressIn = AddInput<string>("Address");
        _networkOut = AddOutput<string>("Network");
        _broadcastOut = AddOutput<string>("Broadcast");
        _firstOut = AddOutput<string>("First Host");
        _lastOut = AddOutput<string>("Last Host");
        _maskOut = AddOutput<string>("Mask");
        _prefixOut = AddOutput<double>("Prefix");
        _countOut = AddOutput<double>("Host Count");
        _containsOut = AddOutput<bool>("Contains");
    }

    public string Cidr { get => _cidr; set => Set(ref _cidr, value ?? string.Empty); }

    /// <summary>An address to test against the range; empty leaves Contains empty.</summary>
    public string Address { get => _address; set => Set(ref _address, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _cidrIn.ValueOr(Cidr).Trim();
        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var address))
            throw new InvalidOperationException($"'{text}' is not an IP address or CIDR range.");

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (slash >= 0 && !(int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) && prefix <= bits))
            throw new InvalidOperationException($"The prefix must be a number from 0 to {bits}.");

        var all = (BigInteger.One << bits) - 1;
        var mask = all ^ ((BigInteger.One << (bits - prefix)) - 1);
        var network = ToNumber(address) & mask;
        var last = network | (all ^ mask);
        var size = BigInteger.One << (bits - prefix);

        // IPv4 keeps the network and broadcast addresses back, except in /31 and /32, which have none.
        var ipv4 = bits == 32;
        var hosts = ipv4 && prefix < 31 ? size - 2 : size;
        var first = ipv4 && prefix < 31 ? network + 1 : network;
        var lastHost = ipv4 && prefix < 31 ? last - 1 : last;

        _networkOut.Value = ToAddress(network, bits).ToString();
        _broadcastOut.Value = ToAddress(last, bits).ToString();
        _firstOut.Value = ToAddress(first, bits).ToString();
        _lastOut.Value = ToAddress(lastHost, bits).ToString();
        _maskOut.Value = ipv4 ? ToAddress(mask, bits).ToString() : $"/{prefix}";
        _prefixOut.Value = (double)prefix;
        _countOut.Value = (double)hosts;

        var test = _addressIn.ValueOr(Address).Trim();
        _containsOut.Value = test.Length == 0 ? null
            : IPAddress.TryParse(test, out var candidate) && candidate.AddressFamily == address.AddressFamily
              && (ToNumber(candidate) & mask) == network;
        return Task.CompletedTask;
    }

    private static BigInteger ToNumber(IPAddress address) =>
        new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

    private static IPAddress ToAddress(BigInteger number, int bits)
    {
        var bytes = new byte[bits / 8];
        var raw = number.ToByteArray(isUnsigned: true, isBigEndian: true);
        raw.CopyTo(bytes, bytes.Length - raw.Length);
        return new IPAddress(bytes);
    }
}
