using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Moq;
using RedMist.Backend.Shared.Utilities;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Archive and orchestration failures are reported by email, so a send that quietly builds the wrong
/// message means nobody hears about the failure it was reporting.
/// </summary>
[TestClass]
public class EmailHelperTests
{
    private static readonly Dictionary<string, string?> EmailSettings = new()
    {
        ["Email:Host"] = "smtp.example.com",
        ["Email:Port"] = "587",
        ["Email:Username"] = "mailer",
        ["Email:Password"] = "secret",
    };

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Sends through a mock transport and keeps the message that was handed to it.</summary>
    private sealed class TestableEmailHelper(IConfiguration configuration, Mock<ISmtpClient> client) : EmailHelper(configuration)
    {
        public MimeMessage? Sent { get; private set; }

        protected override ISmtpClient CreateSmtpClient()
        {
            client
                .Setup(c => c.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>(), It.IsAny<ITransferProgress>()))
                .Callback<MimeMessage, CancellationToken, ITransferProgress>((message, _, _) => Sent = message)
                .ReturnsAsync("250 OK");
            return client.Object;
        }
    }

    private static (TestableEmailHelper Helper, Mock<ISmtpClient> Client) CreateHelper()
    {
        var client = new Mock<ISmtpClient>();
        return (new TestableEmailHelper(Configuration(EmailSettings), client), client);
    }

    [TestMethod]
    public async Task SendEmailAsync_AuthenticatesOverStartTlsAndDisconnectsCleanly()
    {
        var (helper, client) = CreateHelper();

        await helper.SendEmailAsync("Archive failed", "<p>details</p>", "ops@example.com", "noreply@example.com");

        client.Verify(c => c.ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.AuthenticateAsync("mailer", "secret", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()), Times.Once,
            "a session left open holds a connection on the mail server");
    }

    [TestMethod]
    public async Task SendEmailAsync_BuildsTheMessageFromTheSuppliedAddressesAndHtmlBody()
    {
        var (helper, _) = CreateHelper();

        await helper.SendEmailAsync("Archive failed", "<p>details</p>", "ops@example.com", "noreply@example.com");

        var sent = helper.Sent!;
        Assert.AreEqual("Archive failed", sent.Subject);
        Assert.AreEqual("noreply@example.com", sent.From.Mailboxes.Single().Address);
        Assert.AreEqual("ops@example.com", sent.To.Mailboxes.Single().Address);
        Assert.AreEqual("<p>details</p>", sent.HtmlBody);
        Assert.IsEmpty(sent.Bcc.Mailboxes);
    }

    [TestMethod]
    public async Task SendEmailAsync_WithABccAddress_AddsIt()
    {
        var (helper, _) = CreateHelper();

        await helper.SendEmailAsync("Archive failed", "<p>details</p>", "ops@example.com", "noreply@example.com", "audit@example.com");

        Assert.AreEqual("audit@example.com", helper.Sent!.Bcc.Mailboxes.Single().Address);
    }

    /// <summary>
    /// A blank bcc is how callers say "no bcc". Passing it through would make MailboxAddress.Parse
    /// throw and lose the notification entirely.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SendEmailAsync_WithABlankBcc_SendsWithoutOne(string? bcc)
    {
        var (helper, _) = CreateHelper();

        await helper.SendEmailAsync("Archive failed", "<p>details</p>", "ops@example.com", "noreply@example.com", bcc);

        Assert.IsEmpty(helper.Sent!.Bcc.Mailboxes);
    }

    [TestMethod]
    [DataRow("Email:Host")]
    [DataRow("Email:Port")]
    [DataRow("Email:Username")]
    [DataRow("Email:Password")]
    public void Constructor_WithAMissingSetting_Throws(string missingKey)
    {
        var settings = new Dictionary<string, string?>(EmailSettings);
        settings.Remove(missingKey);

        Assert.Throws<ArgumentNullException>(() => new EmailHelper(Configuration(settings)));
    }

    [TestMethod]
    public void Constructor_WithANonNumericPort_Throws()
    {
        var settings = new Dictionary<string, string?>(EmailSettings) { ["Email:Port"] = "not-a-port" };

        Assert.Throws<FormatException>(() => new EmailHelper(Configuration(settings)));
    }
}
