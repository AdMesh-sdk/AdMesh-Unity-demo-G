using System.Collections.Generic;
using AdMesh.Core;
using UnityEngine;

namespace AdMesh.Internal
{
    [AddComponentMenu("")]
    public sealed class AdMeshSignalReporter : MonoBehaviour
    {
        [SerializeField] private float _reportIntervalSeconds = 300f;

        private readonly Dictionary<string, TrackingSession> _sessions = new Dictionary<string, TrackingSession>();

        private sealed class TrackingSession
        {
            public string AdId;
            public string AdUnitId;
            public string CampaignId;
            public string SelectionToken;
            public string ScheduleId;
            public string SessionId;
            public string CreativeType;
            public string CreativeVersion;
            public string CacheStatus;
            public string OverrideId;
            public int AssetSwitchCount;
            public int RenderSuccessCount;
            public int RenderFailureCount;
            public int FallbackUsageCount;
            public float PendingSeconds;
            public float TimeSinceLastReport;
        }

        public void StartTracking(
            string adUnitId,
            string adId,
            string campaignId,
            string selectionToken,
            string scheduleId,
            string sessionId,
            string creativeType,
            string creativeVersion,
            string cacheStatus,
            string overrideId,
            int assetSwitchCount,
            int renderSuccessCount,
            int renderFailureCount,
            int fallbackUsageCount)
        {
            if (string.IsNullOrWhiteSpace(adUnitId) || string.IsNullOrWhiteSpace(adId))
            {
                return;
            }

            var key = BuildKey(adUnitId, adId);
            if (_sessions.ContainsKey(key))
            {
                return;
            }

            _sessions[key] = new TrackingSession
            {
                AdId = adId,
                AdUnitId = adUnitId,
                CampaignId = campaignId,
                SelectionToken = selectionToken,
                ScheduleId = scheduleId,
                SessionId = sessionId,
                CreativeType = creativeType,
                CreativeVersion = creativeVersion,
                CacheStatus = cacheStatus,
                OverrideId = overrideId,
                AssetSwitchCount = assetSwitchCount,
                RenderSuccessCount = renderSuccessCount,
                RenderFailureCount = renderFailureCount,
                FallbackUsageCount = fallbackUsageCount
            };
        }

        public void UpdateSignals(
            string adUnitId,
            string adId,
            float deltaTime,
            string cacheStatus,
            int assetSwitchCount,
            int renderSuccessCount,
            int renderFailureCount,
            int fallbackUsageCount)
        {
            var key = BuildKey(adUnitId, adId);
            if (!_sessions.TryGetValue(key, out var session))
            {
                return;
            }

            session.TimeSinceLastReport += deltaTime;
            session.CacheStatus = cacheStatus;
            session.PendingSeconds += deltaTime;
            session.AssetSwitchCount = assetSwitchCount;
            session.RenderSuccessCount = renderSuccessCount;
            session.RenderFailureCount = renderFailureCount;
            session.FallbackUsageCount = fallbackUsageCount;
        }

        public void StopTracking(string adUnitId, string adId)
        {
            var key = BuildKey(adUnitId, adId);
            if (!_sessions.TryGetValue(key, out var session))
            {
                return;
            }

            if (session.PendingSeconds > 0f)
            {
                SendReport(session);
            }

            _sessions.Remove(key);
        }

        private void Update()
        {
            if (!AdMeshPlugin.IsInitialized)
            {
                return;
            }

            foreach (var session in _sessions.Values)
            {
                if (session.TimeSinceLastReport < _reportIntervalSeconds)
                {
                    continue;
                }

                SendReport(session);
                session.TimeSinceLastReport = 0f;
            }
        }

        private void OnApplicationQuit()
        {
            foreach (var session in _sessions.Values)
            {
                if (session.PendingSeconds > 0f)
                {
                    SendReport(session);
                }
            }
        }

        private void SendReport(TrackingSession session)
        {
            StartCoroutine(AdMeshPlugin.ReportPresenceCoroutine(
                session.AdId,
                session.AdUnitId,
                session.CampaignId,
                session.SelectionToken,
                session.ScheduleId,
                session.PendingSeconds,
                session.SessionId,
                session.CreativeType,
                session.CreativeVersion,
                session.CacheStatus,
                session.OverrideId,
                session.AssetSwitchCount,
                session.RenderSuccessCount,
                session.RenderFailureCount,
                session.FallbackUsageCount
            ));
            session.PendingSeconds = 0f;
        }

        private static string BuildKey(string adUnitId, string adId) => $"{adUnitId}:{adId}";
    }
}
