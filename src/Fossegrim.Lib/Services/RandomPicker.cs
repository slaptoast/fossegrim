namespace Fossegrim.Lib.Services;

/// <summary>
/// Picks random tracks or albums. Full random ignores listening history;
/// adjusted random makes something played recently less likely to come up
/// again, without ever ruling it out.
/// </summary>
public static class RandomPicker
{
    // Right after a play, an item is a tenth as likely as one never played...
    public const double MinWeight = 0.1;

    // ...and it gets halfway back every 10 days (about 90% back after 30).
    public static readonly TimeSpan RecoveryHalfLife = TimeSpan.FromDays(10);

    /// <summary>
    /// How likely an item is to be picked in adjusted mode, from 1 (never
    /// played, or played long ago) down to <see cref="MinWeight"/> (just played).
    /// </summary>
    public static double Weight(DateTime? lastPlayed, DateTime now)
    {
        if (lastPlayed is not { } played)
        {
            return 1;
        }

        // A play stamped slightly in the future (client clock skew) counts as just now.
        var elapsed = Math.Max(0, (now - played).TotalDays);
        return 1 - (1 - MinWeight) * Math.Pow(0.5, elapsed / RecoveryHalfLife.TotalDays);
    }

    /// <summary>
    /// Picks up to <paramref name="count"/> distinct ids, each with probability
    /// proportional to its weight (weighted sampling without replacement,
    /// Efraimidis–Spirakis: keep the largest u^(1/w) keys).
    /// </summary>
    public static List<Guid> Pick(IEnumerable<(Guid Id, double Weight)> candidates, int count, Random random)
    {
        if (count <= 0)
        {
            return [];
        }

        return candidates
            .Where(c => c.Weight > 0)
            .Select(c => (c.Id, Key: Math.Pow(random.NextDouble(), 1 / c.Weight)))
            .OrderByDescending(c => c.Key)
            .Take(count)
            .Select(c => c.Id)
            .ToList();
    }
}
