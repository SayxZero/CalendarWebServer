using System.Collections.Concurrent;
using System.Net;

namespace CosmosCalendar.Web;

/// <summary>
/// Wrong passwords counted per visitor of the site, before the sync server sees
/// them.
///
/// The sync server counts them too, but by the address the request came from,
/// and every visitor of the site comes from this server's address: ten wrong
/// passwords for "anna" from one visitor would close the login "anna" to the
/// whole site, fifty from anybody would close the site to everybody. Here the
/// count is by the visitor's own address (behind a trusted proxy, by the address
/// it reports), with limits below the server's, so that one visitor runs into
/// this one long before the shared one.
/// </summary>
public sealed class LoginThrottle
{
    /// <summary>Wrong passwords for one login from one visitor within the window.</summary>
    public const int PerLogin = 5;

    /// <summary>Wrong passwords for any logins from one visitor within the window.</summary>
    public const int PerAddress = 20;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new();
    private readonly Func<DateTime> _now;
    private DateTime _lastSweep;

    public LoginThrottle(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _lastSweep = _now();
    }

    /// <summary>Seconds to wait before the next try, 0 when it may go now.</summary>
    public int RetryAfterSeconds(IPAddress? client, string login)
    {
        var now = _now();
        Sweep(now);
        var byLogin = Wait(LoginKey(client, login), PerLogin, now);
        var byAddress = Wait(AddressKey(client), PerAddress, now);
        var wait = byLogin > byAddress ? byLogin : byAddress;
        return (int)Math.Ceiling(wait.TotalSeconds);
    }

    public void Fail(IPAddress? client, string login)
    {
        var now = _now();
        Add(LoginKey(client, login), now);
        Add(AddressKey(client), now);
    }

    /// <summary>A right password forgets the wrong ones for that login, as the server does.</summary>
    public void Succeed(IPAddress? client, string login)
    {
        _failures.TryRemove(LoginKey(client, login), out _);
    }

    private static string AddressKey(IPAddress? client) => "a|" + (client?.ToString() ?? "?");

    private static string LoginKey(IPAddress? client, string login) =>
        "l|" + (client?.ToString() ?? "?") + "|" + login.Trim().ToLowerInvariant();

    private void Add(string key, DateTime now)
    {
        var queue = _failures.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            queue.Enqueue(now);
            Trim(queue, now);
        }
    }

    private TimeSpan Wait(string key, int limit, DateTime now)
    {
        if (!_failures.TryGetValue(key, out var queue))
            return TimeSpan.Zero;
        lock (queue)
        {
            Trim(queue, now);
            if (queue.Count < limit)
                return TimeSpan.Zero;
            // Free again once enough of the old failures leave the window.
            var oldest = queue.ElementAt(queue.Count - limit);
            return oldest + Window - now;
        }
    }

    private static void Trim(Queue<DateTime> queue, DateTime now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window)
            queue.Dequeue();
    }

    /// <summary>Keys nobody failed with for a whole window are dropped, now and then.</summary>
    private void Sweep(DateTime now)
    {
        if (now - _lastSweep < Window)
            return;
        _lastSweep = now;
        foreach (var pair in _failures)
        {
            lock (pair.Value)
            {
                Trim(pair.Value, now);
                if (pair.Value.Count == 0)
                    _failures.TryRemove(pair.Key, out _);
            }
        }
    }
}
