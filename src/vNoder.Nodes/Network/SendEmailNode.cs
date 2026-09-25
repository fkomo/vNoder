using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>How <see cref="SendEmailNode"/> secures the SMTP connection.</summary>
public enum SmtpSecurity
{
    /// <summary>Implicit TLS on port 465, STARTTLS where the server offers it on other ports.</summary>
    Auto,

    /// <summary>Upgrade a plain connection with STARTTLS (port 587); fail if the server cannot.</summary>
    StartTls,

    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,

    /// <summary>Unencrypted, for a local relay or a test server such as smtp4dev.</summary>
    None,
}

/// <summary>
/// Sends an email through an SMTP server (MailKit, since .NET's own SmtpClient is obsolete). Like every
/// node with side effects it sends on every pass of a continuous run, so gate it, typically with
/// Changed into Run If.
/// </summary>
/// <remarks>
/// The inline Password is node state and so is saved in the .vnoder file, as the editor label says;
/// wire it in to keep it out. Recipients are separated by commas, semicolons or lines, or wired as a
/// list.
/// </remarks>
[NodeDefinition("Network/Send Email", Description = "Sends an email via SMTP (Gmail, Outlook, any server), with an optional attachment.")]
public sealed class SendEmailNode : NodeBase
{
    private string _server = string.Empty;
    private double _port = 587;
    private SmtpSecurity _security;
    private string _user = string.Empty;
    private string _password = string.Empty;
    private string _from = string.Empty;
    private string _to = string.Empty;
    private string _subject = string.Empty;
    private string _body = string.Empty;
    private bool _html;

    private readonly InputPort _toIn;
    private readonly InputPort _subjectIn;
    private readonly InputPort _bodyIn;
    private readonly InputPort _attachmentIn;
    private readonly InputPort _attachmentNameIn;
    private readonly InputPort _passwordIn;
    private readonly OutputPort _responseOut;

    public SendEmailNode()
    {
        _toIn = AddInput("To", PortTypeCompatibility.Any);
        _subjectIn = AddInput<string>("Subject");
        _bodyIn = AddInput<string>("Body");
        _attachmentIn = AddInput("Attachment", PortTypeCompatibility.Any);
        _attachmentNameIn = AddInput<string>("Attachment Name");
        _passwordIn = AddInput<string>("Password");
        _responseOut = AddOutput<string>("Response");

        AddRunGate();
    }

    /// <summary>The SMTP server's host name, e.g. smtp.gmail.com.</summary>
    public string Server { get => _server; set => Set(ref _server, value ?? string.Empty); }

    public double Port { get => _port; set => Set(ref _port, value); }

    public SmtpSecurity Security { get => _security; set => Set(ref _security, value); }

    /// <summary>The login; empty sends without authenticating.</summary>
    public string User { get => _user; set => Set(ref _user, value ?? string.Empty); }

    public string Password { get => _password; set => Set(ref _password, value ?? string.Empty); }

    /// <summary>The sender; empty uses the login.</summary>
    public string From { get => _from; set => Set(ref _from, value ?? string.Empty); }

    public string To { get => _to; set => Set(ref _to, value ?? string.Empty); }

    public string Subject { get => _subject; set => Set(ref _subject, value ?? string.Empty); }

    public string Body { get => _body; set => Set(ref _body, value ?? string.Empty); }

    /// <summary>Send the body as HTML rather than plain text.</summary>
    public bool Html { get => _html; set => Set(ref _html, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        if (Server.Trim().Length == 0)
            throw new InvalidOperationException("No SMTP server set.");

        var from = (From.Trim().Length > 0 ? From : User).Trim();
        if (!MailboxAddress.TryParse(from, out var sender))
            throw new InvalidOperationException($"'{from}' is not an email address to send from.");

        var recipients = NodeValues.SplitList(_toIn.IsConnected ? _toIn.Value : To);
        if (recipients.Length == 0)
            throw new InvalidOperationException("No recipient set.");

        var message = new MimeMessage();
        message.From.Add(sender);
        foreach (var recipient in recipients)
        {
            message.To.Add(MailboxAddress.TryParse(recipient, out var address)
                ? address
                : throw new InvalidOperationException($"'{recipient}' is not an email address."));
        }

        message.Subject = _subjectIn.ValueOr(Subject);

        var body = new BodyBuilder();
        if (Html)
            body.HtmlBody = _bodyIn.ValueOr(Body);
        else
            body.TextBody = _bodyIn.ValueOr(Body);

        if (_attachmentIn.Value is { } attachment)
        {
            var name = _attachmentNameIn.ValueOr(string.Empty).Trim();
            body.Attachments.Add(name.Length > 0 ? name : "attachment", NodeValues.AsBytes(attachment, "An attachment"));
        }

        message.Body = body.ToMessageBody();

        var port = double.IsNaN(Port) ? 587 : (int)Math.Clamp(Port, 1, 65535);
        var options = Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };

        using var client = new SmtpClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(Server.Trim(), port, options, context.CancellationToken).ConfigureAwait(false);
            if (User.Trim().Length > 0)
                await client.AuthenticateAsync(User.Trim(), _passwordIn.ValueOr(Password), context.CancellationToken).ConfigureAwait(false);

            _responseOut.Value = await client.SendAsync(message, context.CancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MailKit.CommandException or MailKit.ProtocolException
                                              or AuthenticationException or System.Net.Sockets.SocketException or IOException)
        {
            throw new InvalidOperationException($"SMTP: {exception.Message}", exception);
        }
    }
}
