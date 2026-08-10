using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace RedMist.Backend.Shared.Utilities;

public class EmailHelper
{
    private readonly string host;
    private readonly int port;
    private readonly string username;
    private readonly string password;


    public EmailHelper(IConfiguration configuration)
    {
        host = configuration["Email:Host"] ?? throw new ArgumentNullException(nameof(configuration));
        port = int.Parse(configuration["Email:Port"] ?? throw new ArgumentNullException(nameof(configuration)));
        username = configuration["Email:Username"] ?? throw new ArgumentNullException(nameof(configuration));
        password = configuration["Email:Password"] ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async Task SendEmailAsync(string subject, string bodyHtml, string to, string from)
    {
        await SendEmailAsync(subject, bodyHtml, to, from, bcc: null);
    }

    /// <summary>
    /// Creates the SMTP client a send runs through.
    /// </summary>
    /// <remarks>
    /// Virtual only to give tests a seam; without it there is no way to exercise a send without a
    /// real mail server. Production always uses MailKit's <see cref="SmtpClient"/>.
    /// </remarks>
    protected virtual ISmtpClient CreateSmtpClient() => new SmtpClient();

    public async Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        if (!string.IsNullOrWhiteSpace(bcc))
        {
            message.Bcc.Add(MailboxAddress.Parse(bcc));
        }
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = bodyHtml
        };
        message.Body = bodyBuilder.ToMessageBody();

        using var client = CreateSmtpClient();
        await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(username, password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
