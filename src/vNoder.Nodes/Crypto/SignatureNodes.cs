using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// RSA and ECDSA keys from whatever form they arrive in: PEM text (PKCS#8, PKCS#1, SEC1, public key,
/// or a certificate), a PFX, or DER bytes. Keys are ephemeral, never stored by the OS.
/// </summary>
internal static class Keys
{
    private static readonly PbeParameters Encryption = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    public static AsymmetricAlgorithm Generate(CertificateKeyType type) => type switch
    {
        CertificateKeyType.RSA3072 => RSA.Create(3072),
        CertificateKeyType.RSA4096 => RSA.Create(4096),
        CertificateKeyType.ECDSAP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        CertificateKeyType.ECDSAP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
        _ => RSA.Create(2048),
    };

    /// <summary>PKCS#8 PEM, encrypted (AES-256, PBKDF2 with 600k iterations) when a password is given.</summary>
    public static string ExportPrivate(AsymmetricAlgorithm key, string password) => password.Length == 0
        ? key.ExportPkcs8PrivateKeyPem()
        : key.ExportEncryptedPkcs8PrivateKeyPem(password, Encryption);

    public static AsymmetricAlgorithm Private(object? value, string password, string what)
    {
        var bytes = NodeValues.AsBytes(value, what);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"{what} is empty.");

        if (!Certificates.LooksLikePem(bytes) && Certificates.IsPkcs12(bytes))
        {
            using var certificate = Certificates.Load(bytes, password, what);
            return (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey()
                ?? throw new InvalidOperationException($"{what} holds no RSA or ECDSA private key.");
        }

        return TryBoth(bytes, password, publicAlso: false)
               ?? throw new InvalidOperationException($"{what} is not an RSA or ECDSA private key (PEM, PKCS#8 or PFX).");
    }

    /// <summary>A key to verify with: a public key, a certificate (PEM, DER or PFX), or a private key.</summary>
    public static AsymmetricAlgorithm Public(object? value, string password, string what)
    {
        var bytes = NodeValues.AsBytes(value, what);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"{what} is empty.");

        var isPem = Certificates.LooksLikePem(bytes);
        var text = isPem ? System.Text.Encoding.UTF8.GetString(bytes) : string.Empty;
        if ((isPem && text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) || (!isPem && Certificates.IsPkcs12(bytes)))
            return FromCertificate(bytes, password, what);

        if (TryBoth(bytes, password, publicAlso: true) is { } key)
            return key;

        // DER that is neither a key nor a PFX may still be a certificate.
        return FromCertificate(bytes, password, what);
    }

    private static AsymmetricAlgorithm FromCertificate(byte[] bytes, string password, string what)
    {
        using var certificate = Certificates.Load(bytes, password, what);
        return (AsymmetricAlgorithm?)certificate.GetRSAPublicKey() ?? certificate.GetECDsaPublicKey()
            ?? throw new InvalidOperationException($"{what} holds no RSA or ECDSA key.");
    }

    private static AsymmetricAlgorithm? TryBoth(byte[] bytes, string password, bool publicAlso)
    {
        AsymmetricAlgorithm[] candidates = [RSA.Create(), ECDsa.Create()];
        foreach (var key in candidates)
        {
            if (TryImport(key, bytes, password, publicAlso))
            {
                foreach (var other in candidates.Where(c => c != key))
                    other.Dispose();

                return key;
            }
        }

        foreach (var key in candidates)
            key.Dispose();

        return null;
    }

    private static bool TryImport(AsymmetricAlgorithm key, byte[] bytes, string password, bool publicAlso)
    {
        try
        {
            if (Certificates.LooksLikePem(bytes))
            {
                var pem = System.Text.Encoding.UTF8.GetString(bytes);
                if (!publicAlso && !pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                    return false;

                if (pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
                    key.ImportFromEncryptedPem(pem, password);
                else
                    key.ImportFromPem(pem);

                return true;
            }

            // DER: PKCS#8 (plain or encrypted), then a bare public key.
            try
            {
                if (password.Length > 0)
                    key.ImportEncryptedPkcs8PrivateKey(password, bytes, out _);
                else
                    key.ImportPkcs8PrivateKey(bytes, out _);

                return true;
            }
            catch (CryptographicException) when (publicAlso)
            {
                key.ImportSubjectPublicKeyInfo(bytes, out _);
                return true;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}

[NodeDefinition("Crypto/Key Pair", Description = "Generates an RSA or ECDSA key pair as PEM: a private key (optionally encrypted) and its public key.")]
public sealed class KeyPairNode : NodeBase
{
    private CertificateKeyType _keyType;
    private string _password = string.Empty;

    private readonly InputPort _passwordIn;
    private readonly OutputPort _privateOut;
    private readonly OutputPort _publicOut;

    public KeyPairNode()
    {
        _passwordIn = AddInput<string>("Password");
        _privateOut = AddOutput<string>("Private Key");
        _publicOut = AddOutput<string>("Public Key");
    }

    public CertificateKeyType KeyType { get => _keyType; set => Set(ref _keyType, value); }

    /// <summary>Encrypts the private key when set. Saved in the .vnoder file like any inline state.</summary>
    public string Password { get => _password; set => Set(ref _password, value ?? string.Empty); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var type = KeyType;
        var password = _passwordIn.ValueOr(Password);

        (_privateOut.Value, _publicOut.Value) = await Task.Run(() =>
        {
            using var key = Keys.Generate(type);
            return (Keys.ExportPrivate(key, password), key.ExportSubjectPublicKeyInfoPem());
        }, context.CancellationToken).ConfigureAwait(false);
    }
}

public enum SignatureHash
{
    SHA256,
    SHA384,
    SHA512,
}

public enum RsaPaddingKind
{
    /// <summary>PKCS#1 v1.5, the widely supported one (RS256 in JWT terms).</summary>
    Pkcs1,

    /// <summary>PSS, the modern one (PS256).</summary>
    Pss,
}

public enum EcdsaSignatureFormat
{
    /// <summary>ASN.1 DER, as OpenSSL writes and reads it.</summary>
    Der,

    /// <summary>r and s side by side, as JWT, WebCrypto and most web APIs use it.</summary>
    Raw,
}

/// <summary>The settings Sign Data and Verify Signature share, so a signature made by one checks with the other.</summary>
public abstract class SignatureNode : NodeBase
{
    private SignatureHash _hash;
    private RsaPaddingKind _padding;
    private EcdsaSignatureFormat _format;

    public SignatureHash Hash { get => _hash; set => Set(ref _hash, value); }

    public RsaPaddingKind Padding { get => _padding; set => Set(ref _padding, value); }

    public EcdsaSignatureFormat Format { get => _format; set => Set(ref _format, value); }

    protected HashAlgorithmName HashName => Hash switch
    {
        SignatureHash.SHA384 => HashAlgorithmName.SHA384,
        SignatureHash.SHA512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };

    protected RSASignaturePadding RsaPadding => Padding == RsaPaddingKind.Pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;

    protected DSASignatureFormat EcdsaFormat =>
        Format == EcdsaSignatureFormat.Raw ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence;
}

/// <summary>
/// Signs data with a private key (PEM, or a PFX with its password): RSA with PKCS#1 or PSS padding,
/// or ECDSA. The signature comes out as bytes and as Base64.
/// </summary>
[NodeDefinition("Crypto/Sign Data", Description = "Signs text or bytes with an RSA or ECDSA private key (PEM or PFX); the signature as bytes and Base64.")]
public sealed class SignDataNode : SignatureNode
{
    private readonly InputPort _dataIn;
    private readonly InputPort _keyIn;
    private readonly InputPort _passwordIn;
    private readonly OutputPort _signatureOut;
    private readonly OutputPort _base64Out;

    public SignDataNode()
    {
        _dataIn = AddInput("Data", PortTypeCompatibility.Any);
        _keyIn = AddInput("Private Key", PortTypeCompatibility.Any);
        _passwordIn = AddInput<string>("Password");
        _signatureOut = AddOutput<byte[]>("Signature");
        _base64Out = AddOutput<string>("Base64");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var data = NodeValues.AsBytes(_dataIn.Value, "Signing");
        using var key = Keys.Private(_keyIn.Value, _passwordIn.ValueOr(string.Empty), "The private key");

        var signature = key switch
        {
            RSA rsa => rsa.SignData(data, HashName, RsaPadding),
            ECDsa ecdsa => ecdsa.SignData(data, HashName, EcdsaFormat),
            _ => throw new InvalidOperationException("Only RSA and ECDSA keys can sign."),
        };

        _signatureOut.Value = signature;
        _base64Out.Value = Convert.ToBase64String(signature);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Checks a signature against data with a public key, a certificate or a private key. A signature
/// that does not match, or is not even well formed, reads Valid = false rather than failing the node.
/// </summary>
[NodeDefinition("Crypto/Verify Signature", Description = "Checks an RSA or ECDSA signature with a public key or certificate.")]
public sealed class VerifySignatureNode : SignatureNode
{
    private readonly InputPort _dataIn;
    private readonly InputPort _keyIn;
    private readonly InputPort _signatureIn;
    private readonly OutputPort _validOut;

    public VerifySignatureNode()
    {
        _dataIn = AddInput("Data", PortTypeCompatibility.Any);
        _keyIn = AddInput("Key", PortTypeCompatibility.Any);

        // Bytes, or text read as Base64: raw signature bytes as UTF-8 text would mean nothing.
        _signatureIn = AddInput("Signature", PortTypeCompatibility.Any);
        _validOut = AddOutput<bool>("Valid");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var data = NodeValues.AsBytes(_dataIn.Value, "Verifying");
        using var key = Keys.Public(_keyIn.Value, string.Empty, "The key");

        _validOut.Value = Signature(_signatureIn.Value) is { } signature && key switch
        {
            RSA rsa => rsa.VerifyData(data, signature, HashName, RsaPadding),
            ECDsa ecdsa => ecdsa.VerifyData(data, signature, HashName, EcdsaFormat),
            _ => false,
        };

        return Task.CompletedTask;
    }

    private static byte[]? Signature(object? value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes;
            case string text:
                var base64 = text.Trim().Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
                var buffer = new byte[base64.Length];
                return Convert.TryFromBase64String(base64, buffer, out var written) ? buffer[..written] : null;
            default:
                return null;
        }
    }
}

public enum TotpAlgorithm
{
    SHA1,
    SHA256,
    SHA512,
}

/// <summary>
/// The current time-based one-time password (RFC 6238), as authenticator apps show it, from the
/// Base32 secret behind a 2FA QR code. The default settings (SHA-1, 6 digits, 30 s) are what nearly
/// every service uses.
/// </summary>
[NodeDefinition("Crypto/TOTP", Description = "The current 2FA code (TOTP, as authenticator apps show) from a Base32 secret.")]
public sealed class TotpNode : NodeBase
{
    private string _secret = string.Empty;
    private double _digits = 6;
    private double _period = 30;
    private TotpAlgorithm _algorithm;

    private readonly InputPort _secretIn;
    private readonly InputPort _atIn;
    private readonly OutputPort _codeOut;
    private readonly OutputPort _secondsLeftOut;

    public TotpNode()
    {
        _secretIn = AddInput<string>("Secret");
        _atIn = AddInput<DateTime>("At");
        _codeOut = AddOutput<string>("Code");
        _secondsLeftOut = AddOutput<double>("Seconds Left");
    }

    /// <summary>
    /// Inline Base32 secret, used when the Secret input is not wired. Saved in the .vnoder file like
    /// any inline state, as the editor label says.
    /// </summary>
    public string Secret { get => _secret; set => Set(ref _secret, value ?? string.Empty); }

    public double Digits { get => _digits; set => Set(ref _digits, value); }

    public double Period { get => _period; set => Set(ref _period, value); }

    public TotpAlgorithm Algorithm { get => _algorithm; set => Set(ref _algorithm, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var key = Base32.Decode(_secretIn.ValueOr(Secret));
        if (key.Length == 0)
            throw new InvalidOperationException("No secret set.");

        var digits = double.IsNaN(Digits) ? 6 : (int)Math.Clamp(Digits, 6, 10);
        var period = double.IsNaN(Period) ? 30 : (long)Math.Clamp(Period, 1, 3600);
        var at = _atIn.IsConnected ? _atIn.ValueOr(DateTime.UtcNow) : DateTime.UtcNow;
        var seconds = new DateTimeOffset(at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Local) : at).ToUnixTimeSeconds();

        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, seconds / period);
        var hash = Algorithm switch
        {
            TotpAlgorithm.SHA256 => HMACSHA256.HashData(key, counter),
            TotpAlgorithm.SHA512 => HMACSHA512.HashData(key, counter),
            _ => HMACSHA1.HashData(key, counter),
        };

        // Dynamic truncation (RFC 4226): four bytes at the offset the last nibble names.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = (long)binary % (long)Math.Pow(10, digits);

        _codeOut.Value = code.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
        _secondsLeftOut.Value = (double)(period - (seconds % period));
        return Task.CompletedTask;
    }
}
