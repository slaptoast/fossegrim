using System.Threading.Channels;
using Fossegrim.Contracts.Dtos;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Fossegrim.Api.Services;

/// <summary>An email to send: one recipient, plain text.</summary>
public record OutgoingEmail(string To, string Subject, string Body);

/// <summary>Sends one email through the given server. Tests swap in a fake.</summary>
public interface IMailTransport
{
    Task SendAsync(SmtpSettings settings, OutgoingEmail email, CancellationToken cancellationToken);
}

public class SmtpMailTransport : IMailTransport
{
    public async Task SendAsync(SmtpSettings settings, OutgoingEmail email, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? "Fossegrim", MailboxAddress.Parse(settings.FromAddress).Address));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Subject = email.Subject;
        message.Body = new TextPart("plain") { Text = email.Body };

        using var client = new SmtpClient { Timeout = 20_000 };
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls
        }, cancellationToken);
        if (settings.UserName is not null)
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? "", cancellationToken);
        }
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

/// <summary>
/// Emails waiting to go out. Requests only queue them, so a "Forgot
/// password?" request takes as long whether or not the address has an
/// account, and a slow mail server doesn't hold the request up.
/// </summary>
public class EmailQueue
{
    private readonly Channel<OutgoingEmail> _channel = Channel.CreateBounded<OutgoingEmail>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite });

    public bool Enqueue(OutgoingEmail email) => _channel.Writer.TryWrite(email);

    public IAsyncEnumerable<OutgoingEmail> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

public class EmailWorker : BackgroundService
{
    private readonly EmailQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly IMailTransport _transport;
    private readonly ILogger<EmailWorker> _logger;

    public EmailWorker(EmailQueue queue, IServiceScopeFactory scopes, IMailTransport transport, ILogger<EmailWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _transport = transport;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var email in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<EmailSettingsStore>().GetAsync() is not { } settings)
                {
                    _logger.LogWarning("Dropped an email ({Subject}): email isn't set up any more", email.Subject);
                    continue;
                }
                await _transport.SendAsync(settings, email, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not send an email ({Subject})", email.Subject);
            }
        }
    }
}
