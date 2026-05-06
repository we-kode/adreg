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
            ? (_settings.Port == 587 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect)
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

    public async Task SendPasswordResetLink(string email, string link, DateTime validUntilUtc)
    {
        var html = $@"
        <h2>Password Reset</h2>
        <p>An administrator has initiated a password reset for your account.</p>
        <p>Please click the link below to set a new password. The link is valid until
        <strong>{validUntilUtc:yyyy-MM-dd HH:mm} UTC</strong> and can only be used once.</p>
        <a href='{link}'>{link}</a>
    ";

        await SendAsync(email, "Password Reset Link", html);
    }

    private string StripHtml(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
    }
}
