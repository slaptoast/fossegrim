using System.Collections.Concurrent;
using Fossegrim.Api.Services;

namespace Fossegrim.Api.Tests;

/// <summary>Records emails instead of sending them; can be told to fail like a broken mail server.</summary>
public class FakeMailTransport : IMailTransport
{
    private readonly ConcurrentQueue<(SmtpSettings Settings, OutgoingEmail Email)> _sent = new();

    /// <summary>When set, sending throws this instead.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<OutgoingEmail> SentTo(string address) =>
        _sent.Where(s => s.Email.To.Equals(address, StringComparison.OrdinalIgnoreCase)).Select(s => s.Email).ToList();

    public Task SendAsync(SmtpSettings settings, OutgoingEmail email, CancellationToken cancellationToken)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }
        _sent.Enqueue((settings, email));
        return Task.CompletedTask;
    }

    /// <summary>Waits for the background worker to send an email to the address.</summary>
    public async Task<OutgoingEmail> WaitForEmailToAsync(string address)
    {
        for (var i = 0; i < 100; i++)
        {
            if (SentTo(address) is [.., var latest])
            {
                return latest;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"No email was sent to {address}.");
    }
}
