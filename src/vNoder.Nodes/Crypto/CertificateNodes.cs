using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Key types offered by the certificate and key nodes. Saved by number: append, never reorder.</summary>
public enum CertificateKeyType
{
    RSA2048,
    RSA3072,
    RSA4096,
    ECDSAP256,
    ECDSAP384,
}

/// <summary>
/// Creates a self-signed certificate for development and testing: a server/client leaf, with the
/// names as Subject Alternative Names, which is what browsers and TLS clients match on. With
/// Certificate Authority ticked it is a CA instead, for Issue Certificate to sign leaves with.
/// </summary>
/// <remarks>
/// A fresh key pair on every pass, so a continuous run mints a new certificate each tick. Key
/// generation is CPU-bound (RSA 4096 takes a noticeable moment), so it runs on the thread pool.
/// The PFX is encrypted with AES-256 (PBES2); the private key PEM is unencrypted PKCS#8.
/// </remarks>
[NodeDefinition("Crypto/Self-Signed Certificate", Description = "Creates a self-signed certificate or a CA (RSA or ECDSA) as PFX, DER and PEM, with its private key.")]
public sealed class SelfSignedCertificateNode : NodeBase
{
    private string _subject = "localhost";
    private string _names = string.Empty;
    private double _validDays = 365;
    private CertificateKeyType _keyType;
    private string _password = string.Empty;
    private bool _certificateAuthority;

    private readonly InputPort _subjectIn;
    private readonly InputPort _namesIn;
    private readonly InputPort _validDaysIn;
    private readonly InputPort _passwordIn;
    private readonly CreatedCertificatePorts _out;

    public SelfSignedCertificateNode()
    {
        _subjectIn = AddInput<string>("Subject");

        // "any" so a text list feeds it whole rather than minting one certificate per name.
        _namesIn = AddInput("Names", PortTypeCompatibility.Any);
        _validDaysIn = AddInput<double>("Valid Days");
        _passwordIn = AddInput<string>("Password");
        _out = new CreatedCertificatePorts(AddOutput);
    }

    /// <summary>A distinguished name (<c>CN=example, O=Org</c>), or a bare name, which becomes the CN.</summary>
    public string Subject { get => _subject; set => Set(ref _subject, value ?? string.Empty); }

    /// <summary>Host names or IP addresses, comma or line separated. Empty uses the subject's CN.</summary>
    public string Names { get => _names; set => Set(ref _names, value ?? string.Empty); }

    public double ValidDays { get => _validDays; set => Set(ref _validDays, value); }

    public CertificateKeyType KeyType { get => _keyType; set => Set(ref _keyType, value); }

    /// <summary>
    /// Inline PFX password, used when the Password input is not wired; empty is allowed. Saved in the
    /// .vnoder file like any inline state, as the editor label says.
    /// </summary>
    public string Password { get => _password; set => Set(ref _password, value ?? string.Empty); }

    /// <summary>Make a CA (can sign certificates, no names) rather than a server/client certificate.</summary>
    public bool CertificateAuthority { get => _certificateAuthority; set => Set(ref _certificateAuthority, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var request = CertificateSpec.Read(_subjectIn.ValueOr(Subject), _namesIn.IsConnected ? _namesIn.Value : Names,
            _validDaysIn.ValueOr(ValidDays), KeyType, CertificateAuthority);
        var password = _passwordIn.ValueOr(Password);

        var created = await Task.Run(() => Certificates.Create(request, password, issuer: null), context.CancellationToken)
            .ConfigureAwait(false);
        _out.Fill(created);
    }
}

/// <summary>
/// Creates a certificate signed by a CA, such as one made by Self-Signed Certificate with Certificate
/// Authority ticked: a private development CA whose root is trusted once, and whose leaves are then
/// trusted too. The CA arrives as a PFX (bytes) or as PEM text holding its certificate and key.
/// </summary>
/// <remarks>
/// The validity is kept inside the CA's own, since a certificate outliving its issuer is rejected.
/// </remarks>
[NodeDefinition("Crypto/Issue Certificate", Description = "Creates a certificate signed by your own CA (from Self-Signed Certificate as a CA), as PFX, DER and PEM.")]
public sealed class IssueCertificateNode : NodeBase
{
    private string _subject = "localhost";
    private string _names = string.Empty;
    private double _validDays = 365;
    private CertificateKeyType _keyType;
    private string _password = string.Empty;
    private string _caPassword = string.Empty;
    private bool _certificateAuthority;

    private readonly InputPort _caIn;
    private readonly InputPort _caPasswordIn;
    private readonly InputPort _subjectIn;
    private readonly InputPort _namesIn;
    private readonly InputPort _validDaysIn;
    private readonly InputPort _passwordIn;
    private readonly CreatedCertificatePorts _out;

    public IssueCertificateNode()
    {
        _caIn = AddInput("CA", PortTypeCompatibility.Any);
        _caPasswordIn = AddInput<string>("CA Password");
        _subjectIn = AddInput<string>("Subject");
        _namesIn = AddInput("Names", PortTypeCompatibility.Any);
        _validDaysIn = AddInput<double>("Valid Days");
        _passwordIn = AddInput<string>("Password");
        _out = new CreatedCertificatePorts(AddOutput);
    }

    public string Subject { get => _subject; set => Set(ref _subject, value ?? string.Empty); }

    public string Names { get => _names; set => Set(ref _names, value ?? string.Empty); }

    public double ValidDays { get => _validDays; set => Set(ref _validDays, value); }

    public CertificateKeyType KeyType { get => _keyType; set => Set(ref _keyType, value); }

    /// <summary>Inline password for the new PFX. Saved in the .vnoder file like any inline state.</summary>
    public string Password { get => _password; set => Set(ref _password, value ?? string.Empty); }

    /// <summary>Inline password for the CA's PFX or key. Saved in the .vnoder file like any inline state.</summary>
    public string CaPassword { get => _caPassword; set => Set(ref _caPassword, value ?? string.Empty); }

    /// <summary>Make an intermediate CA rather than a server/client certificate.</summary>
    public bool CertificateAuthority { get => _certificateAuthority; set => Set(ref _certificateAuthority, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var caBytes = NodeValues.AsBytes(_caIn.Value, "Issue Certificate");
        if (caBytes.Length == 0)
            throw new InvalidOperationException("No CA wired: connect a CA's PFX, or PEM with its certificate and key.");

        using var ca = Certificates.Load(caBytes, _caPasswordIn.ValueOr(CaPassword), "The CA");
        if (!ca.HasPrivateKey)
            throw new InvalidOperationException("The CA has no private key; wire its PFX, or PEM with the key included.");

        if (ca.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true })
            throw new InvalidOperationException($"{ca.Subject} is not a certificate authority, so what it signs would be rejected.");

        var request = CertificateSpec.Read(_subjectIn.ValueOr(Subject), _namesIn.IsConnected ? _namesIn.Value : Names,
            _validDaysIn.ValueOr(ValidDays), KeyType, CertificateAuthority);
        var password = _passwordIn.ValueOr(Password);

        var created = await Task.Run(() => Certificates.Create(request, password, ca), context.CancellationToken)
            .ConfigureAwait(false);
        _out.Fill(created);
    }
}

/// <summary>
/// A certificate signing request (CSR), the PEM a public CA or a company PKI asks for. The key is the
/// wired one (PEM or PFX), or a new one of Key Type, which then comes out on Private Key.
/// </summary>
[NodeDefinition("Crypto/Certificate Request", Description = "Creates a certificate signing request (CSR) for a subject and names, with a new or given key.")]
public sealed class CertificateRequestNode : NodeBase
{
    private string _subject = "localhost";
    private string _names = string.Empty;
    private CertificateKeyType _keyType;

    private readonly InputPort _subjectIn;
    private readonly InputPort _namesIn;
    private readonly InputPort _keyIn;
    private readonly InputPort _passwordIn;
    private readonly OutputPort _csrOut;
    private readonly OutputPort _privateKeyOut;

    public CertificateRequestNode()
    {
        _subjectIn = AddInput<string>("Subject");
        _namesIn = AddInput("Names", PortTypeCompatibility.Any);
        _keyIn = AddInput("Key", PortTypeCompatibility.Any);
        _passwordIn = AddInput<string>("Password");
        _csrOut = AddOutput<string>("CSR");
        _privateKeyOut = AddOutput<string>("Private Key");
    }

    public string Subject { get => _subject; set => Set(ref _subject, value ?? string.Empty); }

    public string Names { get => _names; set => Set(ref _names, value ?? string.Empty); }

    /// <summary>The kind of key generated when none is wired.</summary>
    public CertificateKeyType KeyType { get => _keyType; set => Set(ref _keyType, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var spec = CertificateSpec.Read(_subjectIn.ValueOr(Subject), _namesIn.IsConnected ? _namesIn.Value : Names,
            365, KeyType, authority: false);
        var given = _keyIn.IsConnected ? Keys.Private(_keyIn.Value, _passwordIn.ValueOr(string.Empty), "The key") : null;

        var (csr, generated) = await Task.Run(() =>
        {
            var key = given ?? Keys.Generate(spec.KeyType);
            try
            {
                var request = Certificates.Request(spec, key, forIssuer: false);
                return (request.CreateSigningRequestPem(), given is null ? key.ExportPkcs8PrivateKeyPem() : null);
            }
            finally
            {
                key.Dispose();
            }
        }, context.CancellationToken).ConfigureAwait(false);

        _csrOut.Value = csr;
        _privateKeyOut.Value = generated;
    }
}

/// <summary>
/// Loads a certificate file and reports its basic properties: a PFX / P12 (with its password), or a
/// CRT / CER / PEM in DER or PEM form. A PEM holding a chain yields its first certificate; one that
/// also holds the private key loads with it.
/// </summary>
/// <remarks>
/// A wired Data input (bytes, or PEM text) is read instead of the file, so Self-Signed Certificate's
/// PFX or PEM can be inspected without writing it out first.
/// <para>
/// Valid means only that now falls within the validity period; the chain and revocation are not
/// checked. Private keys are loaded ephemerally, never written to the Windows key store.
/// </para>
/// </remarks>
[NodeDefinition("Crypto/Certificate Load", Description = "Loads a PFX, P12, CRT, CER or PEM certificate (from a file or wired data) and outputs its subject, issuer, dates, thumbprint and names.")]
public sealed class CertificateLoadNode : NodeBase
{
    private string _path = string.Empty;
    private string _password = string.Empty;

    private readonly InputPort _dataIn;
    private readonly InputPort _pathIn;
    private readonly InputPort _passwordIn;
    private readonly CertificateInfoPorts _info;
    private readonly OutputPort _hasPrivateKeyOut;

    public CertificateLoadNode()
    {
        // "any": bytes (a PFX or DER) or text (PEM) both arrive here.
        _dataIn = AddInput("Data", PortTypeCompatibility.Any);
        _pathIn = AddInput<string>("Path");
        _passwordIn = AddInput<string>("Password");

        _info = new CertificateInfoPorts(AddOutput);
        _hasPrivateKeyOut = AddOutput<bool>("Has Private Key");

        AddRunGate();
    }

    /// <summary>Inline path, used when the Path input is not wired.</summary>
    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    /// <summary>
    /// Inline password for a PFX or an encrypted PEM key, used when the Password input is not wired.
    /// Saved in the .vnoder file like any inline state, as the editor label says.
    /// </summary>
    public string Password { get => _password; set => Set(ref _password, value ?? string.Empty); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        byte[] bytes;
        string source;
        if (_dataIn.IsConnected)
        {
            bytes = NodeValues.AsBytes(_dataIn.Value, "Certificate Load");
            if (bytes.Length == 0)
                throw new InvalidOperationException("No certificate data.");

            source = "The Data input";
        }
        else
        {
            var path = FilePaths.Existing(_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path);
            bytes = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);
            source = System.IO.Path.GetFileName(path);
        }

        using var certificate = Certificates.Load(bytes, _passwordIn.ValueOr(Password), source);
        _info.Fill(certificate);
        _hasPrivateKeyOut.Value = certificate.HasPrivateKey;
    }
}

/// <summary>
/// The outputs describing a certificate, shared by Certificate Load and TLS Certificate so the two read
/// the same. Constructed with the node's own <c>AddOutput</c>, so the ports are the node's.
/// </summary>
internal sealed class CertificateInfoPorts
{
    private readonly OutputPort _certificate;
    private readonly OutputPort _subject;
    private readonly OutputPort _issuer;
    private readonly OutputPort _notBefore;
    private readonly OutputPort _notAfter;
    private readonly OutputPort _daysLeft;
    private readonly OutputPort _valid;
    private readonly OutputPort _thumbprint;
    private readonly OutputPort _serial;
    private readonly OutputPort _names;
    private readonly OutputPort _key;

    public CertificateInfoPorts(Func<string, Type, OutputPort> add)
    {
        _certificate = add("Certificate", typeof(byte[]));
        _subject = add("Subject", typeof(string));
        _issuer = add("Issuer", typeof(string));
        _notBefore = add("Not Before", typeof(DateTime));
        _notAfter = add("Not After", typeof(DateTime));
        _daysLeft = add("Days Left", typeof(double));
        _valid = add("Valid", typeof(bool));
        _thumbprint = add("Thumbprint", typeof(string));
        _serial = add("Serial Number", typeof(string));
        _names = add("Names", typeof(string[]));
        _key = add("Key", typeof(string));
    }

    public void Fill(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        _certificate.Value = certificate.RawData;
        _subject.Value = certificate.Subject;
        _issuer.Value = certificate.Issuer;
        _notBefore.Value = certificate.NotBefore;
        _notAfter.Value = certificate.NotAfter;
        _daysLeft.Value = (certificate.NotAfter - now).TotalDays;
        _valid.Value = now >= certificate.NotBefore && now <= certificate.NotAfter;
        _thumbprint.Value = certificate.Thumbprint;
        _serial.Value = certificate.SerialNumber;
        _names.Value = Certificates.AlternativeNames(certificate);
        _key.Value = Certificates.DescribeKey(certificate);
    }
}

/// <summary>The outputs of the nodes that create a certificate.</summary>
internal sealed class CreatedCertificatePorts(Func<string, Type, OutputPort> add)
{
    private readonly OutputPort _pfx = add("PFX", typeof(byte[]));
    private readonly OutputPort _certificate = add("Certificate", typeof(byte[]));
    private readonly OutputPort _pem = add("PEM", typeof(string));
    private readonly OutputPort _privateKey = add("Private Key", typeof(string));
    private readonly OutputPort _thumbprint = add("Thumbprint", typeof(string));

    public void Fill(Certificates.Created created)
    {
        _pfx.Value = created.Pfx;
        _certificate.Value = created.Der;
        _pem.Value = created.Pem;
        _privateKey.Value = created.PrivateKeyPem;
        _thumbprint.Value = created.Thumbprint;
    }
}

/// <summary>What a certificate is to say about itself, read from a node's inputs.</summary>
internal sealed record CertificateSpec(X500DistinguishedName Subject, string[] Names, double Days, CertificateKeyType KeyType, bool Authority)
{
    public static CertificateSpec Read(string subject, object? names, double days, CertificateKeyType keyType, bool authority)
    {
        var name = Certificates.ParseSubject(subject);

        // A CA is named by its subject alone; a leaf needs a SAN, so its CN stands in when none is given.
        var alternatives = authority ? [] : NodeValues.SplitList(names);
        if (!authority && alternatives.Length == 0 && Certificates.CommonName(name) is { Length: > 0 } cn)
            alternatives = [cn];

        // Clamped rather than thrown, so a value mid-edit still yields a certificate.
        return new CertificateSpec(name, alternatives, double.IsNaN(days) ? 365 : Math.Clamp(days, 1, 36500), keyType, authority);
    }
}

internal static class Certificates
{
    public sealed record Created(byte[] Pfx, byte[] Der, string Pem, string PrivateKeyPem, string Thumbprint);

    public static X500DistinguishedName ParseSubject(string subject)
    {
        subject = subject.Trim();
        if (subject.Length == 0)
            throw new InvalidOperationException("No subject set.");

        // A bare name is the common case ("localhost"); anything with '=' is taken as a full DN.
        if (subject.Contains('='))
            return new X500DistinguishedName(subject);

        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(subject);
        return builder.Build();
    }

    public static string? CommonName(X500DistinguishedName name) => name
        .EnumerateRelativeDistinguishedNames()
        .Where(rdn => !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.3")
        .Select(rdn => rdn.GetSingleElementValue())
        .LastOrDefault();

    /// <summary>
    /// The request for a certificate over <paramref name="key"/>, with its extensions. One signed by an
    /// issuer is built over the public key alone: the issuer's key does the signing (see
    /// <see cref="Create"/>), and this key never has to sign anything.
    /// </summary>
    public static CertificateRequest Request(CertificateSpec spec, AsymmetricAlgorithm key, bool forIssuer)
    {
        var hash = spec.KeyType == CertificateKeyType.ECDSAP384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;
        var request = forIssuer
            ? new CertificateRequest(spec.Subject, new PublicKey(key), hash, RSASignaturePadding.Pkcs1)
            : key is RSA rsa
                ? new CertificateRequest(spec.Subject, rsa, hash, RSASignaturePadding.Pkcs1)
                : new CertificateRequest(spec.Subject, (ECDsa)key, hash);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(spec.Authority, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            spec.Authority ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature
            : key is RSA ? X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment
            : X509KeyUsageFlags.DigitalSignature,
            critical: true));

        if (!spec.Authority)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication"), new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication")],
                critical: false));
        }

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        if (spec.Names.Length > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in spec.Names)
            {
                if (IPAddress.TryParse(name, out var ip))
                    san.AddIpAddress(ip);
                else
                    san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());
        }

        return request;
    }

    /// <summary>A new key pair and a certificate for it: self-signed, or signed by <paramref name="issuer"/>.</summary>
    public static Created Create(CertificateSpec spec, string password, X509Certificate2? issuer)
    {
        using var key = Keys.Generate(spec.KeyType);
        var request = Request(spec, key, forIssuer: issuer is not null);

        // Backdated a few minutes, so a machine whose clock runs slightly behind still accepts it.
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddMinutes(5).AddDays(spec.Days);

        X509Certificate2 certificate;
        if (issuer is null)
        {
            certificate = request.CreateSelfSigned(notBefore, notAfter);
        }
        else
        {
            request.CertificateExtensions.Add(AuthorityKeyIdentifier(issuer));

            if (notBefore < issuer.NotBefore.ToUniversalTime())
                notBefore = issuer.NotBefore.ToUniversalTime();
            if (notAfter > issuer.NotAfter.ToUniversalTime())
                notAfter = issuer.NotAfter.ToUniversalTime();
            if (notAfter <= notBefore)
                throw new InvalidOperationException($"The CA's validity ended on {issuer.NotAfter:yyyy-MM-dd}.");

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] = (byte)((serial[0] & 0x7F) | 0x01);

            // Signed through a generator for the issuer's own key: the overload taking the issuer's
            // certificate insists its key type matches the new one's, so an ECDSA leaf under an RSA CA
            // would be refused.
            using var issuerKey = (AsymmetricAlgorithm?)issuer.GetRSAPrivateKey() ?? issuer.GetECDsaPrivateKey()
                ?? throw new InvalidOperationException("The CA's key is neither RSA nor ECDSA.");
            var generator = issuerKey is RSA issuerRsa
                ? X509SignatureGenerator.CreateForRSA(issuerRsa, RSASignaturePadding.Pkcs1)
                : X509SignatureGenerator.CreateForECDsa((ECDsa)issuerKey);

            using var signed = request.Create(issuer.SubjectName, generator, notBefore, notAfter, serial);
            certificate = key is RSA rsa ? signed.CopyWithPrivateKey(rsa) : signed.CopyWithPrivateKey((ECDsa)key);
        }

        using (certificate)
        {
            return new Created(
                certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password),
                certificate.RawData,
                certificate.ExportCertificatePem(),
                key.ExportPkcs8PrivateKeyPem(),
                certificate.Thumbprint);
        }
    }

    private static X509AuthorityKeyIdentifierExtension AuthorityKeyIdentifier(X509Certificate2 issuer)
    {
        var identifier = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()
                         ?? new X509SubjectKeyIdentifierExtension(issuer.PublicKey, critical: false);
        return X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(identifier);
    }

    public static X509Certificate2 Load(byte[] bytes, string password, string source)
    {
        try
        {
            if (LooksLikePem(bytes))
                return LoadPem(System.Text.Encoding.UTF8.GetString(bytes), password);

            if (ContentType(bytes) == X509ContentType.Pkcs12)
                return X509CertificateLoader.LoadPkcs12(bytes, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException($"{source} could not be loaded as a certificate: {exception.Message}", exception);
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (CryptographicException exception)
        {
            // The platform's own message here ("Cannot find the requested object") says nothing useful.
            throw new InvalidOperationException($"{source} is not a certificate (expected PFX, P12, or a DER or PEM certificate).", exception);
        }
    }

    public static bool IsPkcs12(byte[] bytes) => ContentType(bytes) == X509ContentType.Pkcs12;

    private static X509ContentType ContentType(byte[] bytes)
    {
        try
        {
            return X509Certificate2.GetCertContentType(bytes);
        }
        catch (CryptographicException)
        {
            return X509ContentType.Unknown;
        }
    }

    public static bool LooksLikePem(byte[] bytes)
    {
        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        return head.Contains("-----BEGIN", StringComparison.Ordinal);
    }

    private static X509Certificate2 LoadPem(string pem, string password)
    {
        if (pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            return X509Certificate2.CreateFromEncryptedPem(pem, pem, password);

        if (pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            return X509Certificate2.CreateFromPem(pem, pem);

        // The first CERTIFICATE block; a chain file lists the leaf first.
        var rest = pem.AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            if (rest[fields.Label].SequenceEqual("CERTIFICATE"))
                return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(rest[fields.Base64Data].ToString()));

            rest = rest[fields.Location.End..];
        }

        throw new CryptographicException("The PEM text holds no CERTIFICATE block.");
    }

    public static string[] AlternativeNames(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        return san is null
            ? []
            : san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(ip => ip.ToString())).ToArray();
    }

    public static string DescribeKey(X509Certificate2 certificate)
    {
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
                return $"RSA {rsa.KeySize}";
        }

        using (var ecdsa = certificate.GetECDsaPublicKey())
        {
            if (ecdsa is not null)
                return $"ECDSA P-{ecdsa.KeySize}";
        }

        return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? "unknown";
    }
}
