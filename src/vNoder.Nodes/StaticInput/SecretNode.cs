using System.Security.Cryptography;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// A password, API key or client secret, kept out of the saved file in plain text. What is typed is
/// encrypted with Windows DPAPI for the current user, and only the encrypted form is a saved property,
/// so the .vnoder file, the clipboard and the undo history never hold the secret itself.
/// </summary>
/// <remarks>
/// <para>
/// The price of DPAPI is that the file only decrypts for the same Windows user on the same machine.
/// Anyone else opening it (a colleague, another PC, a sample from the repository) sees the secret as
/// unreadable and has to type it again; the node then fails with a message saying so, rather than
/// passing on nothing. A secret never set is a result, not an error: Value is empty and Is Set false,
/// so a Coalesce can fall back to a default.
/// </para>
/// <para>
/// The editor types into a password box that calls <see cref="SetSecret"/>; there is deliberately no
/// public property holding the plain text, since every such property is saved (NodeStateProperties).
/// The Value output is the secret itself: wiring it into an Output shows it, which is the user's call.
/// </para>
/// </remarks>
[NodeDefinition("Input/Secret", Description = "A password or API key, saved encrypted for your Windows account rather than as plain text.")]
public sealed class SecretNode : NodeBase
{
    /// <summary>Ties the encrypted bytes to this use, so a DPAPI blob made elsewhere does not decrypt here by accident.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("vNoder.Secret.v1");

    private string _protected = string.Empty;

    private readonly OutputPort _valueOut;
    private readonly OutputPort _isSetOut;

    public SecretNode()
    {
        _valueOut = AddOutput<string>("Value");
        _isSetOut = AddOutput<bool>("Is Set");
    }

    /// <summary>The secret encrypted with DPAPI, as Base64; empty when none is set. This is what is saved.</summary>
    public string Protected
    {
        get => _protected;
        set
        {
            if (Set(ref _protected, value ?? string.Empty))
                OnPropertyChanged(nameof(IsSet));
        }
    }

    public bool IsSet => _protected.Length > 0;

    /// <summary>Encrypts and stores <paramref name="secret"/>; empty clears it.</summary>
    public void SetSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            Protected = string.Empty;
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw NotWindows();

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        Protected = Convert.ToBase64String(bytes);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _isSetOut.Value = IsSet;
        _valueOut.Value = IsSet ? Reveal(Protected) : string.Empty;
        return Task.CompletedTask;
    }

    private static string Reveal(string protectedText)
    {
        if (!OperatingSystem.IsWindows())
            throw NotWindows();

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "This secret was saved by another Windows user or on another machine, so it cannot be read here. Type it again.");
        }
    }

    // Checked inline where DPAPI is called, which is the guard the platform analyzer recognises.
    private static PlatformNotSupportedException NotWindows() =>
        new("Secrets are encrypted with Windows DPAPI and can only be used on Windows.");
}
