using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Shared.Models;

namespace Shared.Services;

public class MailService(IOptions<SmtpSettings> settings)
{
    private readonly SmtpSettings _settings = settings.Value;

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        var message = new MimeMessage();

        message.From.Add(MailboxAddress.Parse(_settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var builder = new BodyBuilder
        {
            HtmlBody = htmlBody,
            TextBody = StripHtml(htmlBody)
        };

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();

        var secureOption = _settings.UseSsl
            ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.None;

        await client.ConnectAsync(_settings.Host, _settings.Port, secureOption);

        if (!string.IsNullOrEmpty(_settings.Username))
        {
            await client.AuthenticateAsync(_settings.Username, _settings.Password);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public async Task SendRegistrationLink(string email, string link)
    {
        var html = $@"
        <h2>Registration</h2>
        {(string.IsNullOrWhiteSpace(_settings.RegistrationText) ? "" : $"<p>{_settings.RegistrationText}</p>")}
        <p>Please click the link below:</p>
        <a href='{link}'>{link}</a>
    ";

        await SendAsync(email, "Your Registration Link", html);
    }

    private string StripHtml(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
    }
}
