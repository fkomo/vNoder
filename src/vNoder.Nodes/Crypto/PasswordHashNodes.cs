using System.Security.Cryptography;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

// Password hashing is deliberately slow, so these run on the thread pool: the executor awaits them
// and evaluates unrelated branches meanwhile, and a continuous run skips ticks rather than queueing.

[NodeDefinition("Crypto/BCrypt Hash", Description = "Hashes a password with bcrypt and a fresh random salt.")]
public sealed class BCryptHashNode : NodeBase
{
    private double _workFactor = 11;

    private readonly InputPort _passwordIn;
    private readonly InputPort _workFactorIn;
    private readonly OutputPort _out;

    public BCryptHashNode()
    {
        _passwordIn = AddInput<string>("Password");
        _workFactorIn = AddInput<double>("Work Factor");
        _out = AddOutput<string>("Hash");
    }

    /// <summary>Log2 of the number of rounds: each step doubles the time. 10–12 is typical.</summary>
    public double WorkFactor
    {
        get => _workFactor;
        set => Set(ref _workFactor, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var password = _passwordIn.ValueOr(string.Empty);

        // bcrypt accepts 4..31, but 20 is already over a minute per hash; clamp rather than hang.
        var workFactor = (int)Math.Clamp(_workFactorIn.ValueOr(WorkFactor), 4, 20);

        _out.Value = await Task.Run(
                () => BCrypt.Net.BCrypt.HashPassword(password, workFactor),
                context.CancellationToken)
            .ConfigureAwait(false);
    }
}

[NodeDefinition("Crypto/BCrypt Verify", Description = "True when a password matches a bcrypt hash.")]
public sealed class BCryptVerifyNode : NodeBase
{
    private readonly InputPort _passwordIn;
    private readonly InputPort _hashIn;
    private readonly OutputPort _out;

    public BCryptVerifyNode()
    {
        _passwordIn = AddInput<string>("Password");
        _hashIn = AddInput<string>("Hash");
        _out = AddOutput<bool>("Match");
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var password = _passwordIn.ValueOr(string.Empty);
        var hash = _hashIn.ValueOr(string.Empty).Trim();

        if (hash.Length == 0)
        {
            _out.Value = false;
            return;
        }

        _out.Value = await Task.Run(
                () =>
                {
                    try
                    {
                        return BCrypt.Net.BCrypt.Verify(password, hash);
                    }
                    catch (BCrypt.Net.SaltParseException)
                    {
                        throw new InvalidOperationException("The hash is not a bcrypt hash.");
                    }
                },
                context.CancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>PRF choices for <see cref="Pbkdf2Node"/>.</summary>
public enum Pbkdf2Algorithm
{
    SHA1,
    SHA256,
    SHA384,
    SHA512,
}

/// <summary>
/// PBKDF2 key derivation. With no salt given a random one is generated each pass and reported on the
/// Salt output, since a hash is useless for verification without the salt that made it.
/// </summary>
[NodeDefinition("Crypto/PBKDF2", Description = "Derives a key from a password with PBKDF2 (salt, iterations, SHA-1/256/384/512).")]
public sealed class Pbkdf2Node : NodeBase
{
    private Pbkdf2Algorithm _algorithm = Pbkdf2Algorithm.SHA256;
    private double _iterations = 600_000;
    private double _length = 32;
    private string _salt = string.Empty;

    private readonly InputPort _passwordIn;
    private readonly InputPort _saltIn;
    private readonly InputPort _iterationsIn;
    private readonly OutputPort _hashOut;
    private readonly OutputPort _saltOut;

    public Pbkdf2Node()
    {
        _passwordIn = AddInput<string>("Password");
        _saltIn = AddInput("Salt", PortTypeCompatibility.Any);
        _iterationsIn = AddInput<double>("Iterations");
        _hashOut = AddOutput<byte[]>("Hash");
        _saltOut = AddOutput<byte[]>("Salt");
    }

    public Pbkdf2Algorithm Algorithm
    {
        get => _algorithm;
        set => Set(ref _algorithm, value);
    }

    /// <summary>Default is OWASP's current recommendation for PBKDF2-HMAC-SHA256.</summary>
    public double Iterations
    {
        get => _iterations;
        set => Set(ref _iterations, value);
    }

    /// <summary>Output length in bytes.</summary>
    public double Length
    {
        get => _length;
        set => Set(ref _length, value);
    }

    /// <summary>Inline salt as text; empty means a random 16-byte salt per pass.</summary>
    public string Salt
    {
        get => _salt;
        set => Set(ref _salt, value ?? string.Empty);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var password = _passwordIn.ValueOr(string.Empty);
        var salt = _saltIn.IsConnected
            ? NodeValues.AsBytes(_saltIn.Value, "PBKDF2 salt")
            : Encoding.UTF8.GetBytes(Salt);

        if (salt.Length == 0)
            salt = RandomNumberGenerator.GetBytes(16);

        var iterations = (int)Math.Clamp(_iterationsIn.ValueOr(Iterations), 1, 10_000_000);
        var length = (int)Math.Clamp(Length, 1, 1024);
        var algorithm = Algorithm switch
        {
            Pbkdf2Algorithm.SHA1 => HashAlgorithmName.SHA1,
            Pbkdf2Algorithm.SHA384 => HashAlgorithmName.SHA384,
            Pbkdf2Algorithm.SHA512 => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

        _hashOut.Value = await Task.Run(
                () => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm, length),
                context.CancellationToken)
            .ConfigureAwait(false);
        _saltOut.Value = salt;
    }
}
