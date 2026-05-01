using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using AdMesh.Internal;

namespace AdMesh.Core
{
    public static class AdMeshPlugin
    {
        private const string DefaultAdSelectorUrl = "https://select.admesh.cloud";
        private const string DefaultEventCollectorUrl = "https://events.admesh.cloud";
        private const string RuntimeHostName = "AdMeshRuntime";
        private const string Version = "0.2.5";

        private static string _adSelectorUrl = DefaultAdSelectorUrl;
        private static string _eventCollectorUrl = DefaultEventCollectorUrl;
        private static string _sdkKey = string.Empty;
        private static string _sessionId = string.Empty;
        private static bool _initialized;
        private static MonoBehaviour _runtimeHost;
        private static AdMeshSignalReporter _signalReporter;

        public static bool IsInitialized => _initialized;
        public static string SdkVersion => Version;

        public static void Initialize(string sdkKey, string adSelectorUrl = null, string eventCollectorUrl = null)
        {
            var normalizedSdkKey = sdkKey?.Trim();

            if (_initialized && string.Equals(_sdkKey, normalizedSdkKey, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(normalizedSdkKey))
            {
                Debug.LogError("[AdMesh] SDK key is required.");
                return;
            }

            LoadConfigOverrides();
            _sdkKey = normalizedSdkKey;
            _adSelectorUrl = string.IsNullOrWhiteSpace(adSelectorUrl) ? _adSelectorUrl : adSelectorUrl.Trim();
            _eventCollectorUrl = string.IsNullOrWhiteSpace(eventCollectorUrl) ? _eventCollectorUrl : eventCollectorUrl.Trim();
            _sessionId = Guid.NewGuid().ToString("N");
            EnsureRuntimeHost();
            _initialized = true;
            AdMeshLogger.Info($"Runtime initialized v{Version}");
            AdMeshLogger.Info($"SDK key present={(!string.IsNullOrWhiteSpace(_sdkKey)).ToString().ToLowerInvariant()}");
            AdMeshLogger.Info($"Selector={_adSelectorUrl}");
            AdMeshLogger.Info($"Collector={_eventCollectorUrl}");
            AdMeshLogger.Info($"Session={_sessionId}");
        }

        public static string GetSdkKey() => _sdkKey;
        public static string GetAdSelectorUrl() => _adSelectorUrl;
        public static string GetEventCollectorUrl() => _eventCollectorUrl;
        public static string GetSessionId() => _sessionId;

        public static Coroutine StartRoutine(IEnumerator routine)
        {
            EnsureRuntimeHost();
            return _runtimeHost != null ? _runtimeHost.StartCoroutine(routine) : null;
        }

        internal static AdMeshSignalReporter GetSignalReporter()
        {
            EnsureRuntimeHost();
            return _signalReporter;
        }

        internal static IEnumerator FetchAdCoroutine(string adUnitId, bool testMode, Action<AdCreative> onComplete)
        {
            if (!_initialized)
            {
                AdMeshLogger.Warning("Fetch skipped because runtime is not initialized", adUnitId);
                onComplete?.Invoke(null);
                yield break;
            }

            if (!AdMeshRateLimiter.CanMakeRequest("ad_fetch", adUnitId))
            {
                AdMeshLogger.Warning("Ad fetch rate-limited", adUnitId);
                onComplete?.Invoke(null);
                yield break;
            }

            AdMeshLogger.Info($"Requesting selector fill testMode={testMode.ToString().ToLowerInvariant()}", adUnitId);

            var body = JsonUtility.ToJson(new AdRequestEnvelope
            {
                ad_unit_id = adUnitId,
                ad_format = "video",
                test_mode = testMode,
                session_id = _sessionId,
                engine = "Unity",
                sdk_version = Version,
                platform = Application.platform.ToString(),
                app_version = Application.version,
                device_class = ResolveDeviceClass(),
                sdk_capabilities = new[]
                {
                    "image",
                    "video",
                    "scheduled_dtl_delivery",
                    "lease_bound_cache",
                    "selection_token",
                    "session_heartbeat",
                    "proximity_analytics_v1"
                }
            });

            using (var request = new UnityWebRequest(_adSelectorUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 10;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-sdk-key", _sdkKey);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    AdMeshRateLimiter.RecordFailure("ad_fetch", adUnitId);
                    AdMeshLogger.Warning($"Ad fetch failed: {request.error}", adUnitId);
                    onComplete?.Invoke(null);
                    yield break;
                }

                AdMeshRateLimiter.RecordSuccess("ad_fetch", adUnitId);

                AdSelectorResponse response = null;
                AdSelectorListResponse listResponse = null;
                try
                {
                    response = JsonUtility.FromJson<AdSelectorResponse>(request.downloadHandler.text);
                    if (response?.ad == null)
                    {
                        listResponse = JsonUtility.FromJson<AdSelectorListResponse>(request.downloadHandler.text);
                    }
                }
                catch (Exception ex)
                {
                    AdMeshLogger.Warning("Failed to parse ad-selector response", adUnitId, ex);
                }

                var ad = response?.ad ?? listResponse?.GetFirstAd();
                if (ad != null)
                {
                    if (response?.serving_config != null)
                    {
                        ad.effective_source = string.IsNullOrWhiteSpace(response.serving_config.source) ? response.source : response.serving_config.source;
                        ad.serving_profile_id = response.serving_config.profile_id;
                        ad.serving_profile_name = response.serving_config.profile_name;
                        ad.serving_pool_id = response.serving_config.pool_id;
                        ad.serving_pool_name = response.serving_config.pool_name;
                        ad.profile_scope = response.serving_config.profile_scope;
                        ad.rotation_interval_seconds = response.serving_config.rotation_interval_seconds;
                        ad.next_change_at = response.serving_config.next_change_at;
                        ad.next_asset_id = response.serving_config.next_asset_id;
                        ad.next_asset_name = response.serving_config.next_asset_name;
                        ad.current_asset_id = response.serving_config.current_asset_id;
                        ad.current_asset_name = response.serving_config.current_asset_name;
                        ad.video_loop_enabled = response.serving_config.video_loop_enabled;
                        ad.serving_enabled = response.serving_config.serving_enabled;
                    }

                    if (string.IsNullOrWhiteSpace(ad.type))
                    {
                        ad.type = ad.media_type;
                    }

                    if (string.IsNullOrWhiteSpace(ad.creative_version))
                    {
                        ad.creative_version = !string.IsNullOrWhiteSpace(ad.override_id) ? ad.override_id : ad.schedule_id;
                    }
                }

                if (ad == null)
                {
                    AdMeshLogger.Warning("Selector returned no creative", adUnitId);
                }
                else
                {
                    AdMeshLogger.Info(
                        $"Selector response delivery={ad.delivery_mode ?? "unknown"} schedule={ad.schedule_id ?? ""} mediaType={ad.type ?? ad.media_type ?? ""} mediaUrl={ad.media_url ?? ""}",
                        adUnitId
                    );
                    AdMeshLogger.Info(
                        $"Serving config source={ad.effective_source ?? response?.source ?? "unknown"} pool={ad.serving_pool_name ?? ""} profile={ad.serving_profile_name ?? ""} interval={ad.rotation_interval_seconds}s nextChange={ad.next_change_at ?? ""} nextAsset={ad.next_asset_name ?? ""} loop={(ad.video_loop_enabled ? "true" : "false")}",
                        adUnitId
                    );
                }

                onComplete?.Invoke(ad);
            }
        }

        internal static IEnumerator FetchServingPlanCoroutine(string[] adUnitIds, bool testMode, int planHorizonSeconds, Action<ServingPlanResponse> onComplete)
        {
            if (!_initialized)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            var filteredAdUnitIds = SanitizeAdUnitIds(adUnitIds);
            if (filteredAdUnitIds.Length == 0)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            var body = JsonUtility.ToJson(new ServingPlanRequestEnvelope
            {
                request_mode = "serving_plan",
                ad_unit_ids = filteredAdUnitIds,
                ad_format = "video",
                test_mode = testMode,
                session_id = _sessionId,
                engine = "Unity",
                sdk_version = Version,
                platform = Application.platform.ToString(),
                app_version = Application.version,
                device_class = ResolveDeviceClass(),
                sdk_capabilities = new[]
                {
                    "image",
                    "video",
                    "serving_plan_v1",
                    "revision_check_v1",
                    "scheduled_dtl_delivery",
                    "lease_bound_cache",
                    "selection_token",
                    "session_heartbeat",
                    "proximity_analytics_v1"
                },
                plan_horizon_seconds = Mathf.Clamp(planHorizonSeconds, 180, 300),
            });

            using (var request = new UnityWebRequest(_adSelectorUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 15;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-sdk-key", _sdkKey);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    AdMeshLogger.Warning($"Serving plan fetch failed: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                ServingPlanResponse response = null;
                try
                {
                    response = JsonUtility.FromJson<ServingPlanResponse>(request.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    AdMeshLogger.Warning("Failed to parse serving plan response", null, ex);
                }

                onComplete?.Invoke(response);
            }
        }

        internal static IEnumerator CheckServingRevisionCoroutine(string[] adUnitIds, PlacementRevisionState[] knownRevisions, bool testMode, Action<ServingRevisionResponse> onComplete)
        {
            if (!_initialized)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            var filteredAdUnitIds = SanitizeAdUnitIds(adUnitIds);
            if (filteredAdUnitIds.Length == 0)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            var body = JsonUtility.ToJson(new ServingRevisionRequestEnvelope
            {
                request_mode = "revision_check",
                ad_unit_ids = filteredAdUnitIds,
                ad_format = "video",
                test_mode = testMode,
                session_id = _sessionId,
                engine = "Unity",
                sdk_version = Version,
                platform = Application.platform.ToString(),
                app_version = Application.version,
                device_class = ResolveDeviceClass(),
                sdk_capabilities = new[]
                {
                    "serving_plan_v1",
                    "revision_check_v1"
                },
                known_revisions = knownRevisions ?? Array.Empty<PlacementRevisionState>(),
            });

            using (var request = new UnityWebRequest(_adSelectorUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 10;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-sdk-key", _sdkKey);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    AdMeshLogger.Warning($"Serving revision check failed: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                ServingRevisionResponse response = null;
                try
                {
                    response = JsonUtility.FromJson<ServingRevisionResponse>(request.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    AdMeshLogger.Warning("Failed to parse serving revision response", null, ex);
                }

                onComplete?.Invoke(response);
            }
        }

        internal static IEnumerator ReportImpressionCoroutine(
            string adId,
            string adUnitId,
            string campaignId,
            string selectionToken,
            string scheduleId,
            string creativeType,
            string creativeVersion,
            string cacheStatus,
            string overrideId)
        {
            return ReportEventCoroutine(
                "impression",
                adId,
                adUnitId,
                campaignId,
                selectionToken,
                scheduleId,
                0f,
                _sessionId,
                creativeType,
                creativeVersion,
                cacheStatus,
                "rendering",
                overrideId,
                null,
                0,
                0,
                0,
                0
            );
        }

        internal static IEnumerator ReportPresenceCoroutine(
            string adId,
            string adUnitId,
            string campaignId,
            string selectionToken,
            string scheduleId,
            float elapsedSeconds,
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
            return ReportEventCoroutine(
                "view",
                adId,
                adUnitId,
                campaignId,
                selectionToken,
                scheduleId,
                elapsedSeconds,
                sessionId,
                creativeType,
                creativeVersion,
                cacheStatus,
                "rendering",
                overrideId,
                null,
                assetSwitchCount,
                renderSuccessCount,
                renderFailureCount,
                fallbackUsageCount
            );
        }

        internal static IEnumerator ReportProximityCoroutine(
            string adId,
            string adUnitId,
            string campaignId,
            string selectionToken,
            string scheduleId,
            string sessionId,
            string creativeType,
            string creativeVersion,
            string cacheStatus,
            string overrideId,
            ProximityPayload proximity)
        {
            return ReportEventCoroutine(
                "proximity",
                adId,
                adUnitId,
                campaignId,
                selectionToken,
                scheduleId,
                0f,
                sessionId,
                creativeType,
                creativeVersion,
                cacheStatus,
                "rendering",
                overrideId,
                proximity,
                0,
                0,
                0,
                0
            );
        }

        private static IEnumerator ReportEventCoroutine(
            string eventType,
            string adId,
            string adUnitId,
            string campaignId,
            string selectionToken,
            string scheduleId,
            float elapsedSeconds,
            string sessionId,
            string creativeType,
            string creativeVersion,
            string cacheStatus,
            string renderStatus,
            string overrideId,
            ProximityPayload proximity,
            int assetSwitchCount,
            int renderSuccessCount,
            int renderFailureCount,
            int fallbackUsageCount)
        {
            if (!_initialized || string.IsNullOrWhiteSpace(adId))
            {
                yield break;
            }

            if (!AdMeshRateLimiter.CanMakeRequest("event_report", adUnitId))
            {
                yield break;
            }

            var payload = new ViewEventPayload
            {
                ad_id = adId,
                ad_unit_id = adUnitId,
                campaign_id = campaignId,
                selection_token = selectionToken ?? string.Empty,
                schedule_id = scheduleId ?? string.Empty,
                event_type = eventType,
                package_type = "DTL",
                lit_seconds = eventType == "view" ? Mathf.Max(0f, elapsedSeconds) : 0f,
                session_id = string.IsNullOrWhiteSpace(sessionId) ? _sessionId : sessionId,
                device_class = ResolveDeviceClass(),
                proximity = proximity,
                meta_data = new SignalMetadata
                {
                    engine = "Unity",
                    sdk_version = Version,
                    creative_type = creativeType ?? string.Empty,
                    creative_version = creativeVersion ?? string.Empty,
                    signal_mode = eventType == "view" ? "session_heartbeat" : (eventType == "proximity" ? "proximity_heartbeat" : "delivery_start"),
                    cache_status = cacheStatus ?? "fresh",
                    render_status = renderStatus ?? "rendering",
                    delivery_mode = string.IsNullOrWhiteSpace(overrideId) ? "scheduled" : "override",
                    override_id = overrideId ?? string.Empty,
                    app_version = Application.version,
                    platform = Application.platform.ToString(),
                    asset_switch_count = assetSwitchCount,
                    render_success_count = renderSuccessCount,
                    render_failure_count = renderFailureCount,
                    fallback_usage_count = fallbackUsageCount,
                }
            };

            using (var request = new UnityWebRequest(_eventCollectorUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 5;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-sdk-key", _sdkKey);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    AdMeshRateLimiter.RecordSuccess("event_report", adUnitId);
                    AdMeshLogger.Debug($"Collector accepted {eventType} signal schedule={scheduleId ?? ""} creative={creativeVersion ?? ""}", adUnitId);
                }
                else
                {
                    AdMeshRateLimiter.RecordFailure("event_report", adUnitId);
                    AdMeshLogger.Warning($"Signal post failed: {request.error}", adUnitId);
                }
            }
        }

        internal static bool TryParseUtc(string value, out DateTime utcValue)
        {
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utcValue
            );
        }

        private static string ResolveDeviceClass()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                case RuntimePlatform.IPhonePlayer:
                    return "mobile";
                case RuntimePlatform.WebGLPlayer:
                    return "web";
                default:
                    return "desktop";
            }
        }

        private static void LoadConfigOverrides()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AdMeshLogger.Debug("Skipping admesh_config.json auto-load on Android. Pass SDK settings to AdMeshPlugin.Initialize(...).", "config");
                return;
#elif UNITY_WEBGL && !UNITY_EDITOR
                AdMeshLogger.Debug("Skipping admesh_config.json auto-load on WebGL. Pass SDK settings to AdMeshPlugin.Initialize(...).", "config");
                return;
#endif

                var configPath = Path.Combine(Application.streamingAssetsPath, "admesh_config.json");
                if (!File.Exists(configPath))
                {
                    return;
                }

                var json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<AdMeshConfig>(json);
                if (config == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(config.sdkKey))
                {
                    _sdkKey = config.sdkKey.Trim();
                }

                if (!string.IsNullOrWhiteSpace(config.adSelectorUrl))
                {
                    _adSelectorUrl = config.adSelectorUrl.Trim();
                }

                if (!string.IsNullOrWhiteSpace(config.eventCollectorUrl))
                {
                    _eventCollectorUrl = config.eventCollectorUrl.Trim();
                }
            }
            catch (Exception ex)
            {
                AdMeshLogger.Warning("Failed to load admesh_config.json", "config", ex);
            }
        }

        private static void EnsureRuntimeHost()
        {
            if (_runtimeHost != null)
            {
                return;
            }

            var hostObject = GameObject.Find(RuntimeHostName);
            if (hostObject == null)
            {
                hostObject = new GameObject(RuntimeHostName)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                UnityEngine.Object.DontDestroyOnLoad(hostObject);
            }

            _runtimeHost = hostObject.GetComponent<AdMeshRuntimeHost>();
            if (_runtimeHost == null)
            {
                _runtimeHost = hostObject.AddComponent<AdMeshRuntimeHost>();
            }

            _signalReporter = hostObject.GetComponent<AdMeshSignalReporter>();
            if (_signalReporter == null)
            {
                _signalReporter = hostObject.AddComponent<AdMeshSignalReporter>();
            }
        }

        internal static void TickRuntime(float deltaTime)
        {
            AdMeshServingPlanRuntime.Tick(deltaTime);
        }

        private static string[] SanitizeAdUnitIds(string[] adUnitIds)
        {
            if (adUnitIds == null || adUnitIds.Length == 0)
            {
                return Array.Empty<string>();
            }

            var unique = new System.Collections.Generic.List<string>(adUnitIds.Length);
            foreach (var adUnitId in adUnitIds)
            {
                var normalized = adUnitId?.Trim();
                if (string.IsNullOrWhiteSpace(normalized) || unique.Contains(normalized))
                {
                    continue;
                }

                unique.Add(normalized);
            }

            return unique.ToArray();
        }

        [Serializable]
        private class AdRequestEnvelope
        {
            public string ad_unit_id;
            public string ad_format;
            public bool test_mode;
            public string session_id;
            public string engine;
            public string sdk_version;
            public string platform;
            public string app_version;
            public string device_class;
            public string[] sdk_capabilities;
        }

        [Serializable]
        private class ServingPlanRequestEnvelope
        {
            public string request_mode;
            public string[] ad_unit_ids;
            public string ad_format;
            public bool test_mode;
            public string session_id;
            public string engine;
            public string sdk_version;
            public string platform;
            public string app_version;
            public string device_class;
            public string[] sdk_capabilities;
            public int plan_horizon_seconds;
        }

        [Serializable]
        private class ServingRevisionRequestEnvelope
        {
            public string request_mode;
            public string[] ad_unit_ids;
            public string ad_format;
            public bool test_mode;
            public string session_id;
            public string engine;
            public string sdk_version;
            public string platform;
            public string app_version;
            public string device_class;
            public string[] sdk_capabilities;
            public PlacementRevisionState[] known_revisions;
        }

        [Serializable]
        private class AdSelectorResponse
        {
            public string source;
            public ServingConfig serving_config;
            public AdCreative ad;
        }

        [Serializable]
        private class AdSelectorListResponse
        {
            public AdCreative[] ads;

            public AdCreative GetFirstAd()
            {
                return ads != null && ads.Length > 0 ? ads[0] : null;
            }
        }

        [Serializable]
        private class ViewEventPayload
        {
            public string ad_id;
            public string ad_unit_id;
            public string campaign_id;
            public string selection_token;
            public string schedule_id;
            public string event_type;
            public string package_type;
            public float lit_seconds;
            public string session_id;
            public string device_class;
            public ProximityPayload proximity;
            public SignalMetadata meta_data;
        }

        [Serializable]
        private class SignalMetadata
        {
            public string engine;
            public string sdk_version;
            public string creative_type;
            public string creative_version;
            public string signal_mode;
            public string cache_status;
            public string render_status;
            public string delivery_mode;
            public string override_id;
            public string app_version;
            public string platform;
            public int asset_switch_count;
            public int render_success_count;
            public int render_failure_count;
            public int fallback_usage_count;
        }

        [Serializable]
        internal sealed class ProximityPayload
        {
            public float radius_meters;
            public float qualified_dwell_threshold_seconds;
            public int entries;
            public int repeat_entries;
            public float total_dwell_seconds;
            public int qualified_exposure_count;
            public float active_duration_seconds;
        }

        [Serializable]
        private class AdMeshConfig
        {
            public string sdkKey;
            public string adSelectorUrl;
            public string eventCollectorUrl;
        }

        [Serializable]
        private class ServingConfig
        {
            public string source;
            public string profile_id;
            public string profile_name;
            public string pool_id;
            public string pool_name;
            public string profile_scope;
            public int rotation_interval_seconds;
            public string next_change_at;
            public string next_asset_id;
            public string next_asset_name;
            public string current_asset_id;
            public string current_asset_name;
            public bool video_loop_enabled = true;
            public bool serving_enabled = true;
        }

        private sealed class AdMeshRuntimeHost : MonoBehaviour
        {
            private void Update()
            {
                AdMeshPlugin.TickRuntime(Time.unscaledDeltaTime);
            }
        }
    }

    [Serializable]
    public class ServingPlanResponse
    {
        public string status;
        public string request_mode;
        public string serving_revision;
        public string plan_valid_until;
        public PlacementServingPlan[] placements;
    }

    [Serializable]
    public class PlacementServingPlan
    {
        public string ad_unit_id;
        public string serving_revision;
        public string plan_valid_until;
        public ServingPlanSlot[] slots;
    }

    [Serializable]
    public class ServingPlanSlot
    {
        public string asset_id;
        public string asset_name;
        public string asset_version;
        public string media_url;
        public string media_type;
        public string source;
        public string slot_start;
        public string slot_end;
        public bool loop_flag = true;
    }

    [Serializable]
    public class ServingRevisionResponse
    {
        public string status;
        public string request_mode;
        public bool changed;
        public PlacementRevisionState[] placements;
    }

    [Serializable]
    public class PlacementRevisionState
    {
        public string ad_unit_id;
        public string serving_revision;
        public string plan_valid_until;
        public bool changed;
    }

    [Serializable]
    public class AdCreative
    {
        public string id;
        public string name;
        public string media_url;
        public string media_type;
        public string landing_url;
        public string campaign_id;
        public string selection_token;
        public string schedule_id;
        public string override_id;
        public string package_type;
        public string delivery_mode;
        public string valid_until;
        public string refresh_after;
        public string cache_policy;
        public int max_cached_creatives;
        public string fallback_behavior;
        public string creative_version;
        public string type;
        public string effective_source;
        public string serving_profile_id;
        public string serving_profile_name;
        public string serving_pool_id;
        public string serving_pool_name;
        public string profile_scope;
        public int rotation_interval_seconds;
        public string next_change_at;
        public string next_asset_id;
        public string next_asset_name;
        public string current_asset_id;
        public string current_asset_name;
        public bool video_loop_enabled = true;
        public bool serving_enabled = true;
    }
}
