using System.Collections.Generic;
using UnityEngine;

namespace AdMesh.Internal
{
    internal static class AdMeshRateLimiter
    {
        private sealed class RateLimitBucket
        {
            public int Tokens;
            public long LastRefill;
            public int MaxTokens;
            public long RefillIntervalMs;
        }

        private static readonly Dictionary<string, RateLimitBucket> Buckets = new Dictionary<string, RateLimitBucket>();
        private static readonly Dictionary<string, long> LastRequestTimes = new Dictionary<string, long>();
        private static readonly Dictionary<string, int> ConsecutiveFailures = new Dictionary<string, int>();

        private const int DefaultMaxRequestsPerMinute = 60;
        private const int DefaultMaxRequestsPerHour = 1000;
        private const int MaxConsecutiveFailures = 5;
        private const long FailurePenaltyMs = 60000;

        internal static bool CanMakeRequest(string requestType, string identifier = null)
        {
            var key = $"{requestType}:{identifier ?? "default"}";

            if (ConsecutiveFailures.TryGetValue(key, out var failures) && failures >= MaxConsecutiveFailures)
            {
                if (LastRequestTimes.TryGetValue(key, out var lastTime))
                {
                    if (NowMs() - lastTime < FailurePenaltyMs)
                    {
                        return false;
                    }
                }

                ConsecutiveFailures[key] = 0;
            }

            return CheckRateLimit(key, DefaultMaxRequestsPerMinute, 60000)
                && CheckRateLimit($"{key}:hourly", DefaultMaxRequestsPerHour, 3600000);
        }

        internal static void RecordSuccess(string requestType, string identifier = null)
        {
            var key = $"{requestType}:{identifier ?? "default"}";
            LastRequestTimes[key] = NowMs();
            if (ConsecutiveFailures.TryGetValue(key, out var failures))
            {
                ConsecutiveFailures[key] = Mathf.Max(0, failures - 1);
            }
        }

        internal static void RecordFailure(string requestType, string identifier = null)
        {
            var key = $"{requestType}:{identifier ?? "default"}";
            LastRequestTimes[key] = NowMs();
            ConsecutiveFailures[key] = ConsecutiveFailures.TryGetValue(key, out var failures) ? failures + 1 : 1;
        }

        private static bool CheckRateLimit(string key, int maxRequests, long intervalMs)
        {
            var now = NowMs();
            if (!Buckets.TryGetValue(key, out var bucket))
            {
                bucket = new RateLimitBucket
                {
                    Tokens = maxRequests,
                    LastRefill = now,
                    MaxTokens = maxRequests,
                    RefillIntervalMs = intervalMs / maxRequests
                };
                Buckets[key] = bucket;
            }

            var elapsed = now - bucket.LastRefill;
            var refillCount = (int)(elapsed / bucket.RefillIntervalMs);
            if (refillCount > 0)
            {
                bucket.Tokens = Mathf.Min(bucket.MaxTokens, bucket.Tokens + refillCount);
                bucket.LastRefill = now;
            }

            if (bucket.Tokens <= 0)
            {
                return false;
            }

            bucket.Tokens -= 1;
            return true;
        }

        private static long NowMs() => System.DateTime.UtcNow.Ticks / System.TimeSpan.TicksPerMillisecond;
    }
}
