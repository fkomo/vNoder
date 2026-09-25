using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>JWT signing algorithms (RFC 7518). Saved by number: append, never reorder.</summary>
public enum JwtAlgorithm
{
    HS256,
    HS384,
    HS512,
    RS256,
    RS384,
    RS512,
    PS256,
    ES256,
    ES384,
}

/// <summary>
/// Creates and signs a JSON Web Token: the counterpart of JWT Decode, for testing an API locally or
/// minting a service token. HS algorithms take a shared secret (inline, or wired as text or bytes);
/// RS, PS and ES take a private key as PEM or PFX.
/// </summary>
/// <remarks>
/// With Issued At ticked, "iat" is set to now, and with Expires In above zero, "exp" is set that many
/// seconds ahead; claims the payload already has are left as they are.
/// </remarks>
[NodeDefinition("Encoding/JWT Encode", Description = "Creates a signed JWT (HS256, RS256, ES256 and more) from a JSON payload.")]
public sealed class JwtEncodeNode : NodeBase
{
    private JwtAlgorithm _algorithm;
    private string _secret = string.Empty;
    private string _keyId = string.Empty;
    private bool _issuedAt = true;
    private double _expiresIn = 3600;

    private readonly InputPort _payloadIn;
    private readonly InputPort _keyIn;
    private readonly InputPort _passwordIn;
    private readonly OutputPort _out;

    public JwtEncodeNode()
    {
        _payloadIn = AddInput<string>("Payload");
        _keyIn = AddInput("Key", PortTypeCompatibility.Any);
        _passwordIn = AddInput<string>("Password");
        _out = AddOutput<string>("Token");
    }

    public JwtAlgorithm Algorithm { get => _algorithm; set => Set(ref _algorithm, value); }

    /// <summary>
    /// Inline HS secret, used when the Key input is not wired. Saved in the .vnoder file like any
    /// inline state, as the editor label says.
    /// </summary>
    public string Secret { get => _secret; set => Set(ref _secret, value ?? string.Empty); }

    /// <summary>A "kid" header naming the key, for servers that hold several; empty leaves it out.</summary>
    public string KeyId { get => _keyId; set => Set(ref _keyId, value ?? string.Empty); }

    public bool IssuedAt { get => _issuedAt; set => Set(ref _issuedAt, value); }

    /// <summary>Seconds until the token expires; 0 adds no "exp".</summary>
    public double ExpiresIn { get => _expiresIn; set => Set(ref _expiresIn, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _payloadIn.ValueOr(string.Empty);
        var payload = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonText.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("A JWT payload must be a JSON object.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (IssuedAt && !payload.ContainsKey("iat"))
            payload["iat"] = now;
        if (ExpiresIn > 0 && !payload.ContainsKey("exp"))
            payload["exp"] = now + (long)ExpiresIn;

        var header = new JsonObject { ["alg"] = Algorithm.ToString(), ["typ"] = "JWT" };
        if (KeyId.Trim().Length > 0)
            header["kid"] = KeyId.Trim();

        var signingInput = $"{Part(header)}.{Part(payload)}";
        var signature = Sign(Encoding.UTF8.GetBytes(signingInput));
        _out.Value = $"{signingInput}.{Base64Url.EncodeToString(signature)}";
        return Task.CompletedTask;
    }

    private static string Part(JsonNode node) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(node.ToJsonString(JsonText.Compact)));

    private byte[] Sign(byte[] data)
    {
        var name = Algorithm.ToString();
        var hash = name.EndsWith("384", StringComparison.Ordinal) ? HashAlgorithmName.SHA384
            : name.EndsWith("512", StringComparison.Ordinal) ? HashAlgorithmName.SHA512
            : HashAlgorithmName.SHA256;

        if (name.StartsWith("HS", StringComparison.Ordinal))
        {
            var secret = _keyIn.IsConnected ? NodeValues.AsBytes(_keyIn.Value, "A JWT secret") : Encoding.UTF8.GetBytes(Secret);
            if (secret.Length == 0)
                throw new InvalidOperationException("No secret set.");

            return hash == HashAlgorithmName.SHA384 ? HMACSHA384.HashData(secret, data)
                : hash == HashAlgorithmName.SHA512 ? HMACSHA512.HashData(secret, data)
                : HMACSHA256.HashData(secret, data);
        }

        if (!_keyIn.IsConnected)
            throw new InvalidOperationException($"{name} needs a private key wired into Key (PEM or PFX).");

        using var key = Keys.Private(_keyIn.Value, _passwordIn.ValueOr(string.Empty), "The key");
        return (name[0], key) switch
        {
            ('R', RSA rsa) => rsa.SignData(data, hash, RSASignaturePadding.Pkcs1),
            ('P', RSA rsa) => rsa.SignData(data, hash, RSASignaturePadding.Pss),

            // JWT wants r and s side by side, not the DER sequence, on the curve the name says.
            ('E', ECDsa ecdsa) when ecdsa.KeySize == (hash == HashAlgorithmName.SHA384 ? 384 : 256) =>
                ecdsa.SignData(data, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            ('E', ECDsa ecdsa) => throw new InvalidOperationException(
                $"{name} needs a P-{(hash == HashAlgorithmName.SHA384 ? 384 : 256)} key; this one is P-{ecdsa.KeySize}."),
            _ => throw new InvalidOperationException($"{name} needs {(name[0] == 'E' ? "an ECDSA" : "an RSA")} key."),
        };
    }
}
