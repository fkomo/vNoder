using System.Net.Http.Headers;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>HTTP methods offered by <see cref="HttpRequestNode"/>.</summary>
public enum HttpMethodKind
{
    GET,
    POST,
    PUT,
    PATCH,
    DELETE,
}

/// <summary>
/// One HTTP request per pass. A single node with a method selector rather than one node per verb:
/// they differ only in whether a body is sent, and switching verb should not mean rewiring.
/// </summary>
/// <remarks>
/// A response with an error status is a result, not a failure: it is reported on Status and Success
/// with its body, because an API's error body is usually the thing worth reading. Only a request that
/// gets no response at all (bad URL, DNS, refused, timeout) throws. Like <c>PathDeleteNode</c> this has
/// side effects on every pass, so a continuous run sends a request every tick.
/// </remarks>
[NodeDefinition(
    "Network/HTTP Request",
    Description = "Sends an HTTP GET, POST, PUT, PATCH or DELETE and returns the status and body.",
    Resizable = true)]
public sealed class HttpRequestNode : NodeBase
{
    /// <summary>One client for the process, as HttpClient is meant to be used; timeouts are per request.</summary>
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private HttpMethodKind _method = HttpMethodKind.GET;
    private string _url = string.Empty;
    private string _contentType = "application/json";
    private string _headers = string.Empty;
    private string _body = string.Empty;
    private double _timeoutSeconds = 30;

    private readonly InputPort _urlIn;
    private readonly InputPort _bodyIn;
    private readonly InputPort _headersIn;
    private readonly OutputPort _statusOut;
    private readonly OutputPort _successOut;
    private readonly OutputPort _bodyOut;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _headersOut;

    public HttpRequestNode()
    {
        _urlIn = AddInput<string>("URL");
        _bodyIn = AddInput("Body", PortTypeCompatibility.Any);
        _headersIn = AddInput<string[]>("Headers");
        _statusOut = AddOutput<double>("Status");
        _successOut = AddOutput<bool>("Success");
        _bodyOut = AddOutput<string>("Body");
        _bytesOut = AddOutput<byte[]>("Bytes");
        _headersOut = AddOutput<string[]>("Headers");

        AddRunGate();
    }

    public HttpMethodKind Method
    {
        get => _method;
        set => Set(ref _method, value);
    }

    public string Url
    {
        get => _url;
        set => Set(ref _url, value ?? string.Empty);
    }

    /// <summary>Content-Type of the body sent with POST, PUT and PATCH.</summary>
    public string ContentType
    {
        get => _contentType;
        set => Set(ref _contentType, value ?? string.Empty);
    }

    /// <summary>Extra request headers, one <c>Name: value</c> per line.</summary>
    public string Headers
    {
        get => _headers;
        set => Set(ref _headers, value ?? string.Empty);
    }

    /// <summary>Inline request body, used when the Body input is not wired.</summary>
    public string Body
    {
        get => _body;
        set => Set(ref _body, value ?? string.Empty);
    }

    public double TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => Set(ref _timeoutSeconds, value);
    }

    private bool SendsBody => Method is HttpMethodKind.POST or HttpMethodKind.PUT or HttpMethodKind.PATCH;

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var url = (_urlIn.IsConnected ? _urlIn.ValueOr(string.Empty) : Url).Trim();
        if (url.Length == 0)
            throw new InvalidOperationException("No URL set.");

        // A bare host is the commonest typo; assume https rather than fail on it.
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"'{url}' is not an http or https URL.");

        using var request = new HttpRequestMessage(new HttpMethod(Method.ToString()), uri);

        if (SendsBody)
        {
            var body = _bodyIn.IsConnected ? NodeValues.AsBytes(_bodyIn.Value, "An HTTP body") : Encoding.UTF8.GetBytes(Body);
            request.Content = new ByteArrayContent(body);

            if (!string.IsNullOrWhiteSpace(ContentType)
                && MediaTypeHeaderValue.TryParse(ContentType.Trim(), out var mediaType))
            {
                request.Content.Headers.ContentType = mediaType;
            }
        }

        var headerLines = _headersIn.IsConnected
            ? _headersIn.ValueOr(Array.Empty<string>())
            : Headers.Split('\n');

        foreach (var line in headerLines)
            AddHeader(request, line);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600)));

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {Math.Clamp(TimeoutSeconds, 1, 600)} s.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);

            _statusOut.Value = (double)(int)response.StatusCode;
            _successOut.Value = response.IsSuccessStatusCode;
            _bytesOut.Value = bytes;
            _bodyOut.Value = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            _headersOut.Value = response.Headers
                .Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")
                .ToArray();
        }
    }

    /// <summary>
    /// Adds one <c>Name: value</c> line. Content headers (Content-Type and friends) belong on the
    /// content rather than the request, and HttpClient refuses them in the wrong place.
    /// </summary>
    private static void AddHeader(HttpRequestMessage request, string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
            return;

        var name = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        if (request.Headers.TryAddWithoutValidation(name, value))
            return;

        if (request.Content is { } content)
        {
            content.Headers.Remove(name);
            content.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Unknown charset: fall through to UTF-8, which is what nearly everything is anyway.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
