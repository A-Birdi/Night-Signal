using System.Collections.Concurrent;

namespace NightSignal.ControlPlane.Security;

/// <summary>Named sliding-window limits for social actions (Addendum 01 §9.1: rate-limit searches, requests, invitations).</summary>
public static class SocialLimits
{
    public static readonly (int Limit, TimeSpan Window) HandleLookup = (30, TimeSpan.FromMinutes(1));
    public static readonly (int Limit, TimeSpan Window) HandleChange = (10, TimeSpan.FromHours(1));
    public static readonly (int Limit, TimeSpan Window) FriendRequest = (20, TimeSpan.FromMinutes(10));
    public static readonly (int Limit, TimeSpan Window) FriendChange = (60, TimeSpan.FromMinutes(10));
    public static readonly (int Limit, TimeSpan Window) ConvoyInvite = (20, TimeSpan.FromMinutes(10));
    public static readonly (int Limit, TimeSpan Window) CoursePurchase = (20, TimeSpan.FromMinutes(1));
}

/// <summary>In-process sliding-window rate limiter keyed by "action/account". Time comes from the injected clock.</summary>
public sealed class RateLimiter(TimeProvider clock)
{
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> windows = new();

    public bool TryAcquire(string key, (int Limit, TimeSpan Window) rule, out long retryAfterMs)
    {
        Queue<DateTimeOffset> q = windows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            DateTimeOffset now = clock.GetUtcNow();
            while (q.Count > 0 && now - q.Peek() >= rule.Window) q.Dequeue();
            if (q.Count >= rule.Limit)
            {
                retryAfterMs = Math.Max(1, (long)Math.Ceiling((q.Peek() + rule.Window - now).TotalMilliseconds));
                return false;
            }
            q.Enqueue(now);
            retryAfterMs = 0;
            return true;
        }
    }
}
