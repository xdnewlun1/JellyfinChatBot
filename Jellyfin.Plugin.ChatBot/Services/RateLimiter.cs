using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ChatBot.Services;

// Simple in-memory token bucket keyed on Jellyfin user GUID.
// Caps blast radius of a single user spamming Ollama or Jellyseerr.
internal static class RateLimiter
{
    internal enum Bucket
    {
        Chat,
        SeerrRequest
    }

    private sealed class Limit
    {
        public double Capacity;
        public double RefillPerSecond;
    }

    private static readonly Limit _chatLimit = new() { Capacity = 5, RefillPerSecond = 5.0 / 60.0 }; // burst 5, ~5/min
    private static readonly Limit _seerrLimit = new() { Capacity = 5, RefillPerSecond = 30.0 / 3600.0 }; // burst 5, ~30/hr

    private sealed class State
    {
        public double Tokens;
        public DateTime LastRefill;
    }

    private static readonly ConcurrentDictionary<string, State> _state = new();

    public static bool TryAcquire(Bucket bucket, Guid userId)
    {
        var limit = bucket switch
        {
            Bucket.Chat => _chatLimit,
            Bucket.SeerrRequest => _seerrLimit,
            _ => _chatLimit
        };

        var key = bucket + ":" + (userId == Guid.Empty ? "anon" : userId.ToString("N"));
        var now = DateTime.UtcNow;

        var state = _state.GetOrAdd(key, _ => new State { Tokens = limit.Capacity, LastRefill = now });
        lock (state)
        {
            var elapsed = (now - state.LastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                state.Tokens = Math.Min(limit.Capacity, state.Tokens + elapsed * limit.RefillPerSecond);
                state.LastRefill = now;
            }
            if (state.Tokens >= 1.0)
            {
                state.Tokens -= 1.0;
                return true;
            }
            return false;
        }
    }
}
