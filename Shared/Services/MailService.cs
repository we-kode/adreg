using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Shared.Data;
using Shared.Models;

namespace Shared.Services;

// Sends outgoing notifications. Subject + body are loaded from the
// MailTemplates table (with built-in defaults seeded on first use), the
// {{PLACEHOLDER}} tokens are substituted, and the result is wrapped in a
// neutral HTML envelope before going out via SMTP.
public class MailService(
    IOptions<SmtpSettings> settings,
    AppDbContext db,
    MailTemplateService templates,
    ILogger<MailService> logger)
{
    private readonly SmtpSettings _settings = settings.Value;
    private readonly AppDbContext _db = db;
    private readonly MailTemplateService _templates = templates;
    private readonly ILogger<MailService> _logger = logger;

    // ---------- Public entry points ----------

    public Task SendRegistrationLink(string email, string link) =>
        Dispatch(MailKeys.RegistrationLinkUser, email,
            new MailPlaceholders { Email = email, Link = link });

    public Task SendRegistrationReceivedToUser(string email, string firstName, string lastName) =>
        Dispatch(MailKeys.RegistrationReceivedUser, email,
            new MailPlaceholders { Email = email, FirstName = firstName, LastName = lastName });

    // The admin can pick a specific template for this scenario via templateId.
    // If templateId is null we fall back to the template flagged IsDefault.
    public Task SendRegistrationApprovedToUser(
        string email, string firstName, string lastName, string username,
        IEnumerable<string>? groups = null, int? templateId = null) =>
        Dispatch(MailKeys.RegistrationApprovedUser, email,
            new MailPlaceholders
            {
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                Username = username,
                Groups = groups,
            },
            templateId);

    public Task SendRegistrationRejectedToUser(string email, string firstName, string lastName) =>
        Dispatch(MailKeys.RegistrationRejectedUser, email,
            new MailPlaceholders { Email = email, FirstName = firstName, LastName = lastName });

    public Task SendPasswordResetLink(string email, string link, DateTime validUntilUtc) =>
        Dispatch(MailKeys.PasswordResetLinkUser, email,
            new MailPlaceholders { Email = email, Link = link, ValidUntilUtc = validUntilUtc });

    public Task SendPasswordChangedToUser(string email, string username) =>
        Dispatch(MailKeys.PasswordChangedUser, email,
            new MailPlaceholders { Email = email, Username = username });

    public Task SendAdminRegistrationLinkCreated(string recipientEmail) =>
        Dispatch(MailKeys.RegistrationLinkAdmin, recipient: null,
            new MailPlaceholders { Email = recipientEmail });

    public Task SendAdminNewRegistration(
        string firstName, string lastName, string username, string userEmail,
        IEnumerable<string>? groups = null) =>
        Dispatch(MailKeys.RegistrationReceivedAdmin, recipient: null,
            new MailPlaceholders
            {
                FirstName = firstName,
                LastName = lastName,
                Username = username,
                Email = userEmail,
                Groups = groups,
            });

    public Task SendAdminPasswordResetLinkCreated(string username, string recipientEmail) =>
        Dispatch(MailKeys.PasswordResetLinkAdmin, recipient: null,
            new MailPlaceholders { Username = username, Email = recipientEmail });

    public Task SendAdminPasswordChanged(string username, string userEmail) =>
        Dispatch(MailKeys.PasswordChangedAdmin, recipient: null,
            new MailPlaceholders { Username = username, Email = userEmail });

    // ---------- Dispatch & settings ----------

    // Checks the MailSetting toggle, resolves recipient + template, renders
    // placeholders, then hands off to SendAsync. Admin notifications swallow
    // exceptions so they cannot interrupt the user-facing flow that triggered
    // them.
    private async Task Dispatch(string key, string? recipient, MailPlaceholders data, int? templateId = null)
    {
        var meta = MailKeys.Find(key);
        if (meta == null)
        {
            _logger.LogWarning("Unknown mail key '{Key}' — skipping send.", key);
            return;
        }

        var setting = await GetSettingAsync(key);
        if (!setting.Enabled)
        {
            _logger.LogInformation("Skipping mail '{Key}' — disabled in admin settings.", key);
            return;
        }

        var target = ResolveRecipient(meta, setting, recipient);
        if (string.IsNullOrWhiteSpace(target))
        {
            _logger.LogInformation("Skipping mail '{Key}' — no recipient configured.", key);
            return;
        }

        var template = await _templates.GetForSendAsync(key, templateId);
        var subject = data.ResolveSubject(template.Subject);
        var body = data.ResolveBody(template.BodyHtml);
        var html = Wrap(body);

        try
        {
            await SendAsync(target, subject, html);
        }
        catch (Exception ex) when (meta.Audience == MailAudience.Admin)
        {
            _logger.LogError(ex, "Failed to send admin notification '{Key}' to {Recipient}", key, target);
        }
    }

    private string? ResolveRecipient(MailKey meta, MailSetting setting, string? userRecipient)
    {
        if (meta.Audience == MailAudience.User)
            return string.IsNullOrWhiteSpace(userRecipient) ? null : userRecipient.Trim();

        // Admin audience: prefer per-setting override, fall back to From.
        if (!string.IsNullOrWhiteSpace(setting.Recipient)) return setting.Recipient.Trim();
        return string.IsNullOrWhiteSpace(_settings.From) ? null : _settings.From.Trim();
    }

    private async Task<MailSetting> GetSettingAsync(string key)
    {
        var existing = await _db.MailSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (existing != null) return existing;

        // Lazy-seed: a key the admin has not seen yet defaults to enabled with no override.
        return new MailSetting { Key = key, Enabled = true, Recipient = null };
    }

    // ---------- SMTP transport ----------

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        var message = new MimeMessage();

        message.From.Add(MailboxAddress.Parse(_settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        // HTML only — no plain-text fallback.
        var builder = new BodyBuilder { HtmlBody = htmlBody };
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

    // ---------- HTML envelope ----------

    // Wraps the template body in a neutral, mail-client-friendly envelope.
    // The body itself is the admin's HTML (produced by Quill) which already
    // contains paragraphs, images, links, etc.
    private static string Wrap(string content) => $@"<!DOCTYPE html>
<html lang='de'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
</head>
<body style='font-family:Segoe UI, Arial, sans-serif;background:#f5f5f5;margin:0;padding:24px;color:#222;'>
<div style='max-width:600px;margin:0 auto;background:#ffffff;border-radius:6px;
box-shadow:0 1px 3px rgba(0,0,0,0.08);padding:32px;line-height:1.5;'>
{content}
<hr style='border:none;border-top:1px solid #eee;margin:32px 0 16px;'>
<p style='color:#999;font-size:11px;margin:0;'>Diese E-Mail wurde automatisch versendet.</p>
</div>
</body>
</html>";
}
