using System.Security.Cryptography;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Hash functions offered by <see cref="HmacNode"/>. Saved by number: append, never reorder.</summary>
public enum HmacAlgorithm
{
    SHA256,
    SHA1,
    SHA384,
    SHA512,
    MD5,
}

[NodeDefinition("Crypto/HMAC", Description = "Keyed hash (HMAC-SHA256 and others) for signing API requests and webhooks.")]
public sealed class HmacNode : NodeBase
{
    private HmacAlgorithm _algorithm;
    private string _key = string.Empty;

    private readonly InputPort _dataIn;
    private readonly InputPort _keyIn;
    private readonly OutputPort _hexOut;
    private readonly OutputPort _bytesOut;

    public HmacNode()
    {
        _dataIn = AddInput("Data", PortTypeCompatibility.Any);
        _keyIn = AddInput("Key", PortTypeCompatibility.Any);
        _hexOut = AddOutput<string>("Hex");

        // The raw MAC as well, since many APIs want it Base64-encoded rather than as hex.
        _bytesOut = AddOutput<byte[]>("Bytes");
    }

    public HmacAlgorithm Algorithm
    {
        get => _algorithm;
        set => Set(ref _algorithm, value);
    }

    /// <summary>Inline secret (as UTF-8), used when the Key input is not wired.</summary>
    public string Key
    {
        get => _key;
        set => Set(ref _key, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var data = NodeValues.AsBytes(_dataIn.Value, "HMAC");
        var key = _keyIn.IsConnected ? NodeValues.AsBytes(_keyIn.Value, "An HMAC key") : Encoding.UTF8.GetBytes(Key);

        var mac = Algorithm switch
        {
            HmacAlgorithm.SHA1 => HMACSHA1.HashData(key, data),
            HmacAlgorithm.SHA384 => HMACSHA384.HashData(key, data),
            HmacAlgorithm.SHA512 => HMACSHA512.HashData(key, data),
            HmacAlgorithm.MD5 => HMACMD5.HashData(key, data),
            _ => HMACSHA256.HashData(key, data),
        };

        _hexOut.Value = Convert.ToHexString(mac).ToLowerInvariant();
        _bytesOut.Value = mac;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Crypto/AES Encrypt", Description = "Encrypts text or bytes with AES-256-GCM under a password (or a raw 16/24/32-byte key).")]
public sealed class AesEncryptNode : NodeBase
{
    private string _password = string.Empty;

    private readonly InputPort _dataIn;
    private readonly InputPort _keyIn;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _base64Out;

    public AesEncryptNode()
    {
        _dataIn = AddInput("Data", PortTypeCompatibility.Any);
        _keyIn = AddInput("Key", PortTypeCompatibility.Any);
        _bytesOut = AddOutput<byte[]>("Encrypted");
        _base64Out = AddOutput<string>("Base64");
    }

    /// <summary>Inline password, used when the Key input is not wired.</summary>
    public string Password
    {
        get => _password;
        set => Set(ref _password, value ?? string.Empty);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var data = NodeValues.AsBytes(_dataIn.Value, "Encryption");
        var key = _keyIn.IsConnected ? _keyIn.Value : Password;
        var token = context.CancellationToken;

        // Key derivation is deliberately slow; off the evaluation path like the password hashes.
        var sealedBytes = await Task.Run(() => AesEnvelope.Seal(data, key), token).ConfigureAwait(false);
        _bytesOut.Value = sealedBytes;
        _base64Out.Value = Convert.ToBase64String(sealedBytes);
    }
}

[NodeDefinition("Crypto/AES Decrypt", Description = "Decrypts what AES Encrypt produced (bytes or Base64), with the same password or key.")]
public sealed class AesDecryptNode : NodeBase
{
    private string _password = string.Empty;

    private readonly InputPort _dataIn;
    private readonly InputPort _keyIn;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _textOut;

    public AesDecryptNode()
    {
        _dataIn = AddInput("Encrypted", PortTypeCompatibility.Any);
        _keyIn = AddInput("Key", PortTypeCompatibility.Any);
        _bytesOut = AddOutput<byte[]>("Bytes");
        _textOut = AddOutput<string>("Text");
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value ?? string.Empty);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var data = _dataIn.Value switch
        {
            byte[] raw => raw,
            string text => FromBase64(text),
            null => throw new InvalidOperationException("Nothing to decrypt."),
            var other => throw new InvalidOperationException($"Decryption needs bytes or Base64 text, got {other.GetType().Name}."),
        };

        var key = _keyIn.IsConnected ? _keyIn.Value : Password;
        var token = context.CancellationToken;

        var plain = await Task.Run(() => AesEnvelope.Open(data, key), token).ConfigureAwait(false);
        _bytesOut.Value = plain;
        _textOut.Value = Encoding.UTF8.GetString(plain);
    }

    private static byte[] FromBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The encrypted text is not valid Base64.");
        }
    }
}

/// <summary>
/// vNoder's AES-GCM envelope: <c>[version 1][salt 16][nonce 12][tag 16][ciphertext]</c>. Every part
/// is standard, so it can be opened elsewhere, but the layout is this app's own. A text key is a
/// password, stretched with PBKDF2-SHA256 over the salt; a 16-, 24- or 32-byte key is used as it is and
/// the salt is ignored. GCM authenticates, so a wrong key or a damaged message fails loudly rather than
/// decrypting to garbage.
/// </summary>
internal static class AesEnvelope
{
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + SaltSize + NonceSize + TagSize;

    /// <summary>OWASP's 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    private const int Iterations = 600_000;

    public static byte[] Seal(byte[] plain, object? key)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[HeaderSize + plain.Length];

        output[0] = Version;
        salt.CopyTo(output, 1);
        nonce.CopyTo(output, 1 + SaltSize);

        using var aes = new AesGcm(DeriveKey(key, salt), TagSize);
        aes.Encrypt(
            nonce,
            plain,
            output.AsSpan(HeaderSize),
            output.AsSpan(1 + SaltSize + NonceSize, TagSize));
        return output;
    }

    public static byte[] Open(byte[] data, object? key)
    {
        if (data.Length < HeaderSize || data[0] != Version)
            throw new InvalidOperationException("This is not data from AES Encrypt.");

        var salt = data.AsSpan(1, SaltSize).ToArray();
        var nonce = data.AsSpan(1 + SaltSize, NonceSize);
        var tag = data.AsSpan(1 + SaltSize + NonceSize, TagSize);
        var cipher = data.AsSpan(HeaderSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(DeriveKey(key, salt), TagSize);
        try
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidOperationException("Wrong password or key, or the data has been changed.");
        }

        return plain;
    }

    private static byte[] DeriveKey(object? key, byte[] salt) => key switch
    {
        byte[] { Length: 16 or 24 or 32 } raw => raw,
        byte[] raw => throw new InvalidOperationException($"A raw AES key is 16, 24 or 32 bytes, not {raw.Length}; use text for a password."),
        string { Length: > 0 } password => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32),
        string or null => throw new InvalidOperationException("No password or key set."),
        var other => throw new InvalidOperationException($"An AES key is text or bytes, got {other.GetType().Name}."),
    };
}
