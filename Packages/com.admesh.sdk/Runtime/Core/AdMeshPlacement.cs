using System;
using System.Collections;
using System.Globalization;
using System.IO;
using AdMesh.Internal;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace AdMesh.Core
{
    public sealed class AdMeshPlacement
    {
        private const float RetryAfterFailureSeconds = 60f;
        private const string HostedFallbackImageUrl = "https://assets.admesh.cloud/system/test-assets/admesh-test-image.png";
        private static readonly TimeSpan RefreshSafetyWindow = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RotationLeaseGraceWindow = TimeSpan.FromSeconds(15);

        public event EventHandler<EventArgs> OnAdLoaded;
        public event EventHandler<AdFailedToLoadEventArgs> OnAdFailedToLoad;

        public string AdUnitId { get; }
        public bool HasActiveAd => _currentAd != null;
        public string Status => _status;
        public string LastReason => _lastReason;
        public string DeliverySource => _currentAd?.delivery_mode ?? string.Empty;
        public string CurrentMediaUrl => _currentAd?.media_url ?? string.Empty;
        internal bool HasServingPlan => _servingPlan != null && _servingPlan.Length > 0;
        internal DateTime PlanValidUntilUtc => _planValidUntilUtc;
        internal string ServingRevision => _servingRevision;
        internal bool IsLikelyActive => _targetRenderer != null && _targetRenderer.isVisible;

        private readonly Renderer _targetRenderer;
        private readonly Texture2D _fallbackTexture;
        private readonly bool _useRealAds;
        private readonly bool _verboseLogging;
        private readonly bool _enableAudio;
        private readonly float _audioVolume;
        private readonly float _audioMinDistance;
        private readonly float _audioMaxDistance;
        private readonly AudioRolloffMode _audioRolloffMode;
        private readonly bool _enableProximityAnalytics;
        private readonly bool _useAudioRangeForProximity;
        private readonly float _proximityRadius;
        private readonly float _qualifiedExposureThresholdSeconds;

        private Material _runtimeMaterial;
        private Texture2D _activeTexture;
        private RenderTexture _videoTexture;
        private VideoPlayer _videoPlayer;
        private AudioSource _videoAudioSource;
        private AdCreative _currentAd;
        private bool _isLoading;
        private bool _isTrackingVisible;
        private bool _isServingCachedLease;
        private bool _isLoadingHostedFallback;
        private string _status = "idle";
        private string _lastReason = string.Empty;
        private DateTime _validUntilUtc = DateTime.MinValue;
        private DateTime _refreshAfterUtc = DateTime.MinValue;
        private DateTime _nextRetryAtUtc = DateTime.MinValue;
        private bool _videoPreparing;
        private bool _lastObservedVideoPlayingState;
        private DateTime _lastPlaybackStartUtc = DateTime.MinValue;
        private DateTime _lastLoopPointUtc = DateTime.MinValue;
        private DateTime _lastUnexpectedStopUtc = DateTime.MinValue;
        private bool _holdingExpiredCreativeForReplacement;
        private ServingPlanSlot[] _servingPlan = Array.Empty<ServingPlanSlot>();
        private string _servingRevision = string.Empty;
        private DateTime _planValidUntilUtc = DateTime.MinValue;
        private int _currentSlotIndex = -1;
        private string _pendingSlotAssetKey = string.Empty;
        private string _prefetchedAssetKey = string.Empty;
        private bool _prefetchInFlight;
        private int _assetSwitchCount;
        private int _renderSuccessCount;
        private int _renderFailureCount;
        private int _fallbackUsageCount;
        private bool _isInsideProximityZone;
        private float _currentProximityDwellSeconds;
        private float _pendingProximityEntries;
        private float _pendingRepeatEntries;
        private float _pendingQualifiedExposureCount;
        private float _pendingProximityDwellSeconds;
        private float _pendingProximityActiveDurationSeconds;
        private float _proximityReportTimer;
        private int _lifetimeProximityEntries;

        public AdMeshPlacement(
            Renderer targetRenderer,
            string adUnitId,
            Texture2D fallbackTexture,
            bool useRealAds,
            bool verboseLogging,
            bool enableAudio,
            float audioVolume,
            float audioMinDistance,
            float audioMaxDistance,
            AudioRolloffMode audioRolloffMode,
            bool enableProximityAnalytics,
            bool useAudioRangeForProximity,
            float proximityRadius,
            float qualifiedExposureThresholdSeconds
        )
        {
            _targetRenderer = targetRenderer;
            _fallbackTexture = fallbackTexture;
            _useRealAds = useRealAds;
            _verboseLogging = verboseLogging;
            _enableAudio = enableAudio;
            _audioVolume = Mathf.Clamp01(audioVolume);
            _audioMinDistance = Mathf.Max(0.1f, audioMinDistance);
            _audioMaxDistance = Mathf.Max(_audioMinDistance, audioMaxDistance);
            _audioRolloffMode = audioRolloffMode;
            _enableProximityAnalytics = enableProximityAnalytics;
            _useAudioRangeForProximity = useAudioRangeForProximity;
            _proximityRadius = Mathf.Max(0.1f, proximityRadius);
            _qualifiedExposureThresholdSeconds = Mathf.Max(0.5f, qualifiedExposureThresholdSeconds);
            AdUnitId = adUnitId;

            if (_targetRenderer != null)
            {
                _runtimeMaterial = new Material(_targetRenderer.sharedMaterial != null ? _targetRenderer.sharedMaterial : new Material(Shader.Find("Universal Render Pipeline/Lit")));
                _targetRenderer.material = _runtimeMaterial;
                ApplyFallbackTexture();
                AdMeshLogger.Info("Placement ready", AdUnitId);
            }
        }

        public void LoadAd()
        {
            if (_targetRenderer == null || string.IsNullOrWhiteSpace(AdUnitId) || !AdMeshPlugin.IsInitialized)
            {
                return;
            }

            _status = "loading";
            _lastReason = string.Empty;
            AdMeshServingPlanRuntime.RequestImmediateSync();
        }

        internal void ApplyServingPlan(PlacementServingPlan placementPlan)
        {
            _servingPlan = placementPlan?.slots ?? Array.Empty<ServingPlanSlot>();
            _servingRevision = placementPlan?.serving_revision ?? string.Empty;
            _planValidUntilUtc = ParsePlanTime(placementPlan?.plan_valid_until, DateTime.UtcNow.AddMinutes(3));
            _currentSlotIndex = -1;
            _prefetchedAssetKey = string.Empty;
            AdMeshLogger.Info(
                $"Serving plan applied slots={_servingPlan.Length} revision={_servingRevision} validUntil={_planValidUntilUtc:o}",
                AdUnitId
            );
            ApplyCurrentPlanSlot(DateTime.UtcNow, true);
        }

        public void Tick(float deltaTime)
        {
            if (HasServingPlan)
            {
                HandleServingPlanLifecycle();
            }
            else
            {
                HandleLeaseLifecycle();
            }
            MonitorVideoPlayback();
            UpdateSpatialAudio();
        }

        public void UpdatePresence(float deltaTime)
        {
            if (_currentAd == null || _targetRenderer == null)
            {
                StopTrackingIfNeeded();
                return;
            }

            if (IsFallbackCreative(_currentAd))
            {
                StopTrackingIfNeeded();
                return;
            }

            var visible = _targetRenderer.isVisible;

            if (visible)
            {
                if (!_isTrackingVisible)
                {
                    AdMeshPlugin.GetSignalReporter().StartTracking(
                        AdUnitId,
                        _currentAd.id,
                        _currentAd.campaign_id,
                        _currentAd.selection_token,
                        _currentAd.schedule_id,
                        AdMeshPlugin.GetSessionId(),
                        _currentAd.type,
                        GetCreativeVersion(_currentAd),
                        GetCacheStatus(),
                        _currentAd.override_id,
                        _assetSwitchCount,
                        _renderSuccessCount,
                        _renderFailureCount,
                        _fallbackUsageCount
                    );
                    _isTrackingVisible = true;
                }

                AdMeshPlugin.GetSignalReporter().UpdateSignals(
                    AdUnitId,
                    _currentAd.id,
                    deltaTime,
                    GetCacheStatus(),
                    _assetSwitchCount,
                    _renderSuccessCount,
                    _renderFailureCount,
                    _fallbackUsageCount
                );
            }
            else
            {
                StopTrackingIfNeeded();
            }
        }

        public void UpdateProximity(float deltaTime, bool hasTrackedPosition, Vector3 trackedPosition, float reportIntervalSeconds)
        {
            if (!_enableProximityAnalytics || _currentAd == null || IsFallbackCreative(_currentAd) || _targetRenderer == null)
            {
                ExitProximityZoneIfNeeded();
                return;
            }

            if (!hasTrackedPosition)
            {
                ExitProximityZoneIfNeeded();
                return;
            }

            var radius = _useAudioRangeForProximity ? _audioMaxDistance : _proximityRadius;
            var distance = Vector3.Distance(_targetRenderer.transform.position, trackedPosition);
            var insideZone = distance <= radius;

            if (insideZone)
            {
                if (!_isInsideProximityZone)
                {
                    _isInsideProximityZone = true;
                    _pendingProximityEntries += 1f;
                    if (_lifetimeProximityEntries > 0)
                    {
                        _pendingRepeatEntries += 1f;
                    }
                    _lifetimeProximityEntries += 1;
                }

                _currentProximityDwellSeconds += deltaTime;
                _pendingProximityDwellSeconds += deltaTime;
                _pendingProximityActiveDurationSeconds += deltaTime;
            }
            else
            {
                ExitProximityZoneIfNeeded();
            }

            _proximityReportTimer += deltaTime;
            if (_proximityReportTimer >= Mathf.Max(30f, reportIntervalSeconds))
            {
                FlushProximityReport();
            }
        }

        public void Destroy()
        {
            StopTrackingIfNeeded();
            FlushProximityReport();

            if (_videoPlayer != null)
            {
                UnregisterVideoPlayerEvents(_videoPlayer);
                _videoPlayer.Stop();
                _videoPlayer = null;
            }

            if (_videoAudioSource != null)
            {
                _videoAudioSource.Stop();
                _videoAudioSource = null;
            }

            if (_videoTexture != null)
            {
                _videoTexture.Release();
                UnityEngine.Object.Destroy(_videoTexture);
                _videoTexture = null;
            }

            if (_activeTexture != null)
            {
                AdMeshMemoryPool.ReturnTexture(_activeTexture);
                _activeTexture = null;
            }

            if (_runtimeMaterial != null)
            {
                UnityEngine.Object.Destroy(_runtimeMaterial);
                _runtimeMaterial = null;
            }
        }

        private IEnumerator LoadAdRoutine()
        {
            _isLoading = true;
            var previousAd = _currentAd;
            var previousMediaUrl = previousAd?.media_url;
            var previousType = previousAd?.type;
            AdCreative loadedAd = null;

            yield return AdMeshPlugin.FetchAdCoroutine(AdUnitId, !_useRealAds, ad => loadedAd = ad);

            if (loadedAd == null)
            {
                if (HasActiveLease(DateTime.UtcNow))
                {
                    _isServingCachedLease = true;
                    _status = "active";
                    _lastReason = string.Empty;
                    AdMeshLogger.Info("No fresh fill, keeping cached leased creative", AdUnitId);
                    ScheduleRetry();
                    _isLoading = false;
                    yield break;
                }

                HandleFailure("No ad fill available");
                ScheduleRetry();
                _isLoading = false;
                yield break;
            }

            _currentAd = loadedAd;
            _isServingCachedLease = false;
            ApplyLease(loadedAd);

            var sameMediaUrl = string.Equals(previousMediaUrl, loadedAd.media_url, StringComparison.Ordinal);
            var sameCreativeVersion = string.Equals(previousAd?.creative_version, loadedAd.creative_version, StringComparison.Ordinal);
            if (!sameMediaUrl || !sameCreativeVersion)
            {
                _assetSwitchCount += 1;
            }

            if (_verboseLogging)
            {
                AdMeshLogger.Info($"Loaded ad metadata for {AdUnitId}", loadedAd.id);
            }

            AdMeshLogger.Info(
                $"Selector applied asset={loadedAd.name ?? loadedAd.id} source={loadedAd.effective_source ?? loadedAd.delivery_mode ?? string.Empty} sameUrl={sameMediaUrl.ToString().ToLowerInvariant()} sameVersion={sameCreativeVersion.ToString().ToLowerInvariant()} refreshAfter={loadedAd.refresh_after ?? string.Empty} nextChange={loadedAd.next_change_at ?? string.Empty} nextAsset={loadedAd.next_asset_name ?? string.Empty}",
                AdUnitId
            );

            if (string.Equals(loadedAd.type, "video", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(previousType, "video", StringComparison.OrdinalIgnoreCase) &&
                    sameMediaUrl)
                {
                    ApplyVideoLoopSetting();
                    if (_videoPlayer != null && !_videoPlayer.isPlaying && !_videoPreparing)
                    {
                        AdMeshLogger.Warning("Video asset reused but player was not playing. Restarting playback.", AdUnitId);
                        _videoPlayer.Play();
                    }
                    _status = "active";
                    _lastReason = string.Empty;
                    OnAdLoaded?.Invoke(this, EventArgs.Empty);
                    _isLoading = false;
                    yield break;
                }

                SetupVideo(loadedAd.media_url);
                SendImpression(loadedAd);
                _renderSuccessCount += 1;
                _status = "active";
                _lastReason = string.Empty;
                OnAdLoaded?.Invoke(this, EventArgs.Empty);
                _isLoading = false;
                yield break;
            }

            if (string.Equals(previousType, "image", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previousMediaUrl, loadedAd.media_url, StringComparison.Ordinal) &&
                _activeTexture != null)
            {
                _runtimeMaterial.mainTexture = _activeTexture;
                _status = "active";
                _lastReason = string.Empty;
                OnAdLoaded?.Invoke(this, EventArgs.Empty);
                _isLoading = false;
                yield break;
            }

            yield return DownloadTextureRoutine(loadedAd.media_url);
            _isLoading = false;
        }

        private IEnumerator DownloadTextureRoutine(string mediaUrl)
        {
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                HandleFailure("Creative did not include media_url.");
                yield break;
            }

            if (!AdMeshRateLimiter.CanMakeRequest("texture_download", mediaUrl))
            {
                HandleFailure("Texture download rate-limited.");
                yield break;
            }

            AdMeshLogger.Info($"Downloading media {mediaUrl}", AdUnitId);

            using (var request = UnityWebRequest.Get(mediaUrl))
            {
                request.timeout = 15;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    AdMeshRateLimiter.RecordFailure("texture_download", mediaUrl);

                    if (HasActiveLease(DateTime.UtcNow))
                    {
                        _isServingCachedLease = true;
                        _status = "active";
                        _lastReason = string.Empty;
                        AdMeshLogger.Warning("Media download failed, keeping cached leased creative", AdUnitId);
                        ScheduleRetry();
                        yield break;
                    }

                    HandleFailure($"Texture download failed: {request.error}");
                    yield break;
                }

                var bytes = request.downloadHandler.data;
                var texture = AdMeshMemoryPool.GetTexture(2, 2);
                if (!ImageConversion.LoadImage(texture, bytes, false))
                {
                    AdMeshMemoryPool.ReturnTexture(texture);
                    HandleFailure("Failed to decode downloaded image bytes.");
                    yield break;
                }

                AdMeshRateLimiter.RecordSuccess("texture_download", mediaUrl);
                AdMeshLogger.Info("Image download and decode succeeded", AdUnitId);

                if (_activeTexture != null)
                {
                    AdMeshMemoryPool.ReturnTexture(_activeTexture);
                }

                _activeTexture = texture;
                _runtimeMaterial.mainTexture = _activeTexture;
                _status = "active";
                _lastReason = string.Empty;
                _isLoadingHostedFallback = false;
                SendImpression(_currentAd);
                _renderSuccessCount += 1;
                OnAdLoaded?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SetupVideo(string mediaUrl)
        {
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                HandleFailure("Video creative did not include media_url.");
                return;
            }

            if (_videoPlayer == null)
            {
                _videoPlayer = _targetRenderer.gameObject.GetComponent<VideoPlayer>();
                if (_videoPlayer == null)
                {
                    _videoPlayer = _targetRenderer.gameObject.AddComponent<VideoPlayer>();
                }
            }

            UnregisterVideoPlayerEvents(_videoPlayer);

            if (_videoTexture == null)
            {
                _videoTexture = new RenderTexture(1280, 720, 0);
            }

            if (_enableAudio)
            {
                if (_videoAudioSource == null)
                {
                    _videoAudioSource = _targetRenderer.gameObject.GetComponent<AudioSource>();
                    if (_videoAudioSource == null)
                    {
                        _videoAudioSource = _targetRenderer.gameObject.AddComponent<AudioSource>();
                    }
                }

                _videoAudioSource.playOnAwake = false;
                _videoAudioSource.loop = true;
                _videoAudioSource.spatialBlend = 1f;
                _videoAudioSource.volume = _audioVolume;
                _videoAudioSource.rolloffMode = _audioRolloffMode;
                _videoAudioSource.minDistance = _audioMinDistance;
                _videoAudioSource.maxDistance = _audioMaxDistance;
            }
            else if (_videoAudioSource != null)
            {
                _videoAudioSource.Stop();
            }

            _videoPlayer.playOnAwake = false;
            _videoPlayer.isLooping = _currentAd?.video_loop_enabled ?? true;
            _videoPlayer.waitForFirstFrame = true;
            _videoPlayer.skipOnDrop = true;
            _videoPlayer.source = VideoSource.Url;
            _videoPlayer.url = mediaUrl;
            _videoPlayer.targetTexture = _videoTexture;
            _videoPlayer.audioOutputMode = _enableAudio ? VideoAudioOutputMode.AudioSource : VideoAudioOutputMode.None;
            if (_enableAudio && _videoAudioSource != null)
            {
                _videoPlayer.controlledAudioTrackCount = 1;
                _videoPlayer.EnableAudioTrack(0, true);
                _videoPlayer.SetTargetAudioSource(0, _videoAudioSource);
            }

            _videoPlayer.errorReceived += HandleVideoError;
            _videoPlayer.started += HandleVideoStarted;
            _videoPlayer.loopPointReached += HandleVideoLoopPointReached;
            _videoPlayer.prepareCompleted += HandleVideoPrepared;

            _runtimeMaterial.mainTexture = _videoTexture;
            _status = "active";
            _lastReason = string.Empty;
            _videoPreparing = true;
            _lastObservedVideoPlayingState = false;
            AdMeshLogger.Info($"Preparing video asset url={mediaUrl} loop={_videoPlayer.isLooping.ToString().ToLowerInvariant()} nextChange={_currentAd?.next_change_at ?? string.Empty}", AdUnitId);
            _videoPlayer.Prepare();
        }

        private void UnregisterVideoPlayerEvents(VideoPlayer player)
        {
            if (player == null)
            {
                return;
            }

            player.errorReceived -= HandleVideoError;
            player.started -= HandleVideoStarted;
            player.loopPointReached -= HandleVideoLoopPointReached;
            player.prepareCompleted -= HandleVideoPrepared;
        }

        private void HandleVideoPrepared(VideoPlayer player)
        {
            _videoPreparing = false;
            AdMeshLogger.Info($"Video prepared url={player.url}", AdUnitId);
            _runtimeMaterial.mainTexture = _videoTexture;
            player.Play();
        }

        private void HandleVideoStarted(VideoPlayer player)
        {
            _lastPlaybackStartUtc = DateTime.UtcNow;
            _lastObservedVideoPlayingState = true;
            AdMeshLogger.Info($"Video playback started url={player.url}", AdUnitId);
        }

        private void HandleVideoLoopPointReached(VideoPlayer player)
        {
            _lastLoopPointUtc = DateTime.UtcNow;
            AdMeshLogger.Info($"Video loopPointReached url={player.url} loop={player.isLooping.ToString().ToLowerInvariant()}", AdUnitId);
            if (player.isLooping && !player.isPlaying)
            {
                AdMeshLogger.Info("Video loop point reached but player is not running; replaying current video", AdUnitId);
                player.Play();
            }

            if (!_isLoading &&
                _currentAd != null &&
                string.Equals(_currentAd.effective_source, "rotation", StringComparison.OrdinalIgnoreCase) &&
                _refreshAfterUtc != DateTime.MinValue &&
                _refreshAfterUtc != DateTime.MaxValue &&
                DateTime.UtcNow >= _refreshAfterUtc.Subtract(TimeSpan.FromSeconds(1)))
            {
                AdMeshLogger.Info(
                    $"Loop point reached near scheduled change. Refreshing next rotation asset currentAsset={_currentAd.name ?? _currentAd.id} nextAsset={_currentAd.next_asset_name ?? string.Empty}",
                    AdUnitId
                );
                _refreshAfterUtc = DateTime.MaxValue;
                LoadAd();
            }
        }

        private void HandleVideoError(VideoPlayer player, string message)
        {
            _videoPreparing = false;
            AdMeshLogger.Warning($"Video playback error: {message}", AdUnitId);
        }

        private void ApplyVideoLoopSetting()
        {
            if (_videoPlayer == null)
            {
                return;
            }

            _videoPlayer.isLooping = _currentAd?.video_loop_enabled ?? true;
            if (_videoAudioSource != null)
            {
                _videoAudioSource.loop = _videoPlayer.isLooping;
            }
        }

        private void MonitorVideoPlayback()
        {
            if (_currentAd == null || !string.Equals(_currentAd.type, "video", StringComparison.OrdinalIgnoreCase) || _videoPlayer == null)
            {
                return;
            }

            var isPlaying = _videoPlayer.isPlaying;
            if (isPlaying != _lastObservedVideoPlayingState)
            {
                _lastObservedVideoPlayingState = isPlaying;
                AdMeshLogger.Info($"Video playback state changed playing={isPlaying.ToString().ToLowerInvariant()} url={_videoPlayer.url}", AdUnitId);
            }

            if (_videoPreparing || isPlaying || _isLoading || _validUntilUtc == DateTime.MinValue || DateTime.UtcNow >= _validUntilUtc)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastUnexpectedStopUtc).TotalSeconds < 2)
            {
                return;
            }

            _lastUnexpectedStopUtc = DateTime.UtcNow;
            AdMeshLogger.Warning(
                $"Video playback stopped before lease end. url={_videoPlayer.url} loop={_videoPlayer.isLooping.ToString().ToLowerInvariant()} validUntil={_validUntilUtc:o} refreshAfter={_refreshAfterUtc:o} nextChange={_currentAd?.next_change_at ?? string.Empty}. Attempting replay.",
                AdUnitId
            );
            _videoPlayer.Play();
        }

        private void ApplyFallbackTexture()
        {
            if (_runtimeMaterial != null && _fallbackTexture != null)
            {
                _runtimeMaterial.mainTexture = _fallbackTexture;
            }
        }

        private void HandleFailure(string message)
        {
            ExpireCurrentCreative();
            _status = "failed";
            _lastReason = message;
            _renderFailureCount += 1;
            OnAdFailedToLoad?.Invoke(this, new AdFailedToLoadEventArgs(message));
            if (_verboseLogging)
            {
                AdMeshLogger.Warning(message, AdUnitId);
            }

            if (!TryLoadHostedFallback(message))
            {
                ApplyFallbackTexture();
                _fallbackUsageCount += 1;
                AdMeshLogger.Info("Rendering local fallback texture", AdUnitId);
            }
        }

        private void StopTrackingIfNeeded()
        {
            if (_isTrackingVisible && _currentAd != null)
            {
                AdMeshPlugin.GetSignalReporter().StopTracking(AdUnitId, _currentAd.id);
                _isTrackingVisible = false;
            }
        }

        private void HandleServingPlanLifecycle()
        {
            var now = DateTime.UtcNow;
            ApplyCurrentPlanSlot(now, false);

            var nextSlot = GetNextPlanSlot(now);
            if (nextSlot != null)
            {
                var nextStart = ParsePlanTime(nextSlot.slot_start, now.AddSeconds(5));
                var secondsUntilNext = (nextStart - now).TotalSeconds;
                if (secondsUntilNext <= 3 && secondsUntilNext >= 0)
                {
                    PrefetchPlanSlot(nextSlot);
                }
            }
        }

        private void ApplyCurrentPlanSlot(DateTime now, bool force)
        {
            var nextSlotIndex = GetSlotIndexForTime(now);
            if (nextSlotIndex < 0)
            {
                if (_planValidUntilUtc != DateTime.MinValue && now >= _planValidUntilUtc)
                {
                    AdMeshServingPlanRuntime.RequestImmediateSync();
                }
                return;
            }

            var slot = _servingPlan[nextSlotIndex];
            var slotAssetKey = AdMeshAssetCache.BuildAssetKey(slot.asset_id, slot.asset_version);
            var currentAssetKey = _currentAd != null
                ? AdMeshAssetCache.BuildAssetKey(_currentAd.id, _currentAd.creative_version)
                : string.Empty;

            if (!force && nextSlotIndex == _currentSlotIndex && string.Equals(slotAssetKey, currentAssetKey, StringComparison.Ordinal))
            {
                ApplyPlanLease(slot);
                ApplyVideoLoopSetting();
                return;
            }

            _currentSlotIndex = nextSlotIndex;
            ApplyPlanLease(slot);

            if (!force && string.Equals(slotAssetKey, currentAssetKey, StringComparison.Ordinal))
            {
                _currentAd = BuildCreativeFromPlanSlot(slot);
                ApplyVideoLoopSetting();
                _status = "active";
                _lastReason = string.Empty;
                AdMeshLogger.Info($"Serving plan retained current asset={slot.asset_name} until={slot.slot_end}", AdUnitId);
                return;
            }

            LoadPlannedSlot(slot);
        }

        private int GetSlotIndexForTime(DateTime now)
        {
            for (var index = 0; index < _servingPlan.Length; index++)
            {
                var slot = _servingPlan[index];
                var slotStart = ParsePlanTime(slot.slot_start, now);
                var slotEnd = ParsePlanTime(slot.slot_end, now.AddSeconds(5));
                if (now >= slotStart && now < slotEnd)
                {
                    return index;
                }
            }

            return _servingPlan.Length > 0 ? _servingPlan.Length - 1 : -1;
        }

        private ServingPlanSlot GetNextPlanSlot(DateTime now)
        {
            var currentIndex = GetSlotIndexForTime(now);
            if (currentIndex >= 0 && currentIndex + 1 < _servingPlan.Length)
            {
                return _servingPlan[currentIndex + 1];
            }

            return null;
        }

        private void HandleLeaseLifecycle()
        {
            var now = DateTime.UtcNow;

            if (_currentAd != null && LeaseExpired(now))
            {
                if (ShouldHoldCurrentCreativeDuringRefresh())
                {
                    if (!_holdingExpiredCreativeForReplacement)
                    {
                        AdMeshLogger.Info(
                            $"Creative lease reached rotation boundary for asset={_currentAd.name ?? _currentAd.id}; retaining current creative until replacement is ready",
                            AdUnitId
                        );
                    }

                    _holdingExpiredCreativeForReplacement = true;
                    _validUntilUtc = now.Add(RotationLeaseGraceWindow);

                    if (!_isLoading)
                    {
                        AdMeshLogger.Info(
                            $"Rotation replacement request triggered after lease boundary currentAsset={_currentAd.name ?? _currentAd.id} nextAsset={_currentAd.next_asset_name ?? string.Empty}",
                            AdUnitId
                        );
                        LoadAd();
                    }
                }
                else
                {
                    AdMeshLogger.Info($"Creative lease expired for asset={_currentAd.name ?? _currentAd.id}", AdUnitId);
                    ExpireCurrentCreative();
                }
            }

            if (!_isLoading && _currentAd == null && _nextRetryAtUtc != DateTime.MinValue && now >= _nextRetryAtUtc)
            {
                AdMeshLogger.Info("Retry window reached; requesting creative again", AdUnitId);
                _nextRetryAtUtc = DateTime.MinValue;
                LoadAd();
            }

            if (!_isLoading && _currentAd != null && _refreshAfterUtc != DateTime.MinValue && now >= _refreshAfterUtc)
            {
                AdMeshLogger.Info(
                    $"Refresh due. currentAsset={_currentAd.name ?? _currentAd.id} currentUrl={_currentAd.media_url ?? string.Empty} nextChange={_currentAd.next_change_at ?? string.Empty} nextAsset={_currentAd.next_asset_name ?? string.Empty}",
                    AdUnitId
                );
                _refreshAfterUtc = DateTime.MaxValue;
                LoadAd();
            }
        }

        private void ApplyLease(AdCreative ad)
        {
            if (!AdMeshPlugin.TryParseUtc(ad.valid_until, out _validUntilUtc))
            {
                _validUntilUtc = DateTime.UtcNow.AddMinutes(10);
            }

            if (!AdMeshPlugin.TryParseUtc(ad.refresh_after, out _refreshAfterUtc))
            {
                _refreshAfterUtc = DateTime.UtcNow.AddMinutes(5);
            }

            if (ShouldHoldCurrentCreativeDuringRefresh(ad) &&
                _validUntilUtc != DateTime.MinValue &&
                (_refreshAfterUtc == DateTime.MinValue || _refreshAfterUtc >= _validUntilUtc))
            {
                var candidateRefresh = _validUntilUtc - RefreshSafetyWindow;
                _refreshAfterUtc = candidateRefresh > DateTime.UtcNow ? candidateRefresh : DateTime.UtcNow;
            }

            _nextRetryAtUtc = DateTime.MinValue;
            _holdingExpiredCreativeForReplacement = false;
            AdMeshLogger.Info(
                $"Lease applied validUntil={_validUntilUtc:o} refreshAfter={_refreshAfterUtc:o} source={ad.effective_source ?? ad.delivery_mode ?? string.Empty} interval={ad.rotation_interval_seconds}s nextAsset={ad.next_asset_name ?? string.Empty}",
                AdUnitId
            );
        }

        private void ApplyPlanLease(ServingPlanSlot slot)
        {
            var slotEnd = ParsePlanTime(slot.slot_end, DateTime.UtcNow.AddSeconds(5));
            var slotStart = ParsePlanTime(slot.slot_start, DateTime.UtcNow);
            _validUntilUtc = slotEnd;
            _refreshAfterUtc = slotEnd;
            _nextRetryAtUtc = DateTime.MinValue;
            AdMeshLogger.Info(
                $"Plan slot active asset={slot.asset_name} start={slotStart:o} end={slotEnd:o} loop={(slot.loop_flag ? "true" : "false")}",
                AdUnitId
            );
        }

        private void LoadPlannedSlot(ServingPlanSlot slot)
        {
            if (_isLoading)
            {
                return;
            }

            _isLoading = true;
            _status = "loading";
            _lastReason = string.Empty;
            _pendingSlotAssetKey = AdMeshAssetCache.BuildAssetKey(slot.asset_id, slot.asset_version);
            AdMeshPlugin.StartRoutine(LoadPlannedSlotRoutine(slot, _pendingSlotAssetKey));
        }

        private IEnumerator LoadPlannedSlotRoutine(ServingPlanSlot slot, string requestedAssetKey)
        {
            CachedAssetHandle? resolvedAsset = null;
            yield return AdMeshAssetCache.EnsureAssetAvailable(slot.asset_id, slot.asset_version, slot.media_type, slot.media_url, handle => resolvedAsset = handle);

            _isLoading = false;

            if (_pendingSlotAssetKey != requestedAssetKey)
            {
                yield break;
            }

            if (resolvedAsset == null)
            {
                HandleFailure($"Failed to load planned asset {slot.asset_name}");
                yield break;
            }

            var creative = BuildCreativeFromPlanSlot(slot);
            _currentAd = creative;
            _isServingCachedLease = resolvedAsset.Value.CacheHit;
            _assetSwitchCount += 1;

            if (string.Equals(slot.media_type, "video", StringComparison.OrdinalIgnoreCase))
            {
                SetupVideo(resolvedAsset.Value.PlaybackUrl);
                _renderSuccessCount += 1;
                _status = "active";
                _lastReason = string.Empty;
                OnAdLoaded?.Invoke(this, EventArgs.Empty);
                yield break;
            }

            yield return LoadTextureFromCachedAssetRoutine(resolvedAsset.Value.LocalPath);
        }

        private IEnumerator LoadTextureFromCachedAssetRoutine(string localPath)
        {
            if (!File.Exists(localPath))
            {
                HandleFailure("Cached image asset missing on disk.");
                yield break;
            }

            var bytes = File.ReadAllBytes(localPath);
            var texture = AdMeshMemoryPool.GetTexture(2, 2);
            if (!ImageConversion.LoadImage(texture, bytes, false))
            {
                AdMeshMemoryPool.ReturnTexture(texture);
                HandleFailure("Failed to decode cached image asset.");
                yield break;
            }

            if (_activeTexture != null)
            {
                AdMeshMemoryPool.ReturnTexture(_activeTexture);
            }

            _activeTexture = texture;
            _runtimeMaterial.mainTexture = _activeTexture;
            _status = "active";
            _lastReason = string.Empty;
            _renderSuccessCount += 1;
            OnAdLoaded?.Invoke(this, EventArgs.Empty);
        }

        private void PrefetchPlanSlot(ServingPlanSlot slot)
        {
            if (_prefetchInFlight)
            {
                return;
            }

            var assetKey = AdMeshAssetCache.BuildAssetKey(slot.asset_id, slot.asset_version);
            if (string.Equals(assetKey, _prefetchedAssetKey, StringComparison.Ordinal))
            {
                return;
            }

            var currentAssetKey = _currentAd != null ? AdMeshAssetCache.BuildAssetKey(_currentAd.id, _currentAd.creative_version) : string.Empty;
            if (string.Equals(assetKey, currentAssetKey, StringComparison.Ordinal))
            {
                return;
            }

            _prefetchInFlight = true;
            AdMeshPlugin.StartRoutine(PrefetchPlanSlotRoutine(slot, assetKey));
        }

        private IEnumerator PrefetchPlanSlotRoutine(ServingPlanSlot slot, string assetKey)
        {
            CachedAssetHandle? handle = null;
            yield return AdMeshAssetCache.EnsureAssetAvailable(slot.asset_id, slot.asset_version, slot.media_type, slot.media_url, result => handle = result);
            _prefetchInFlight = false;

            if (handle != null)
            {
                _prefetchedAssetKey = assetKey;
                AdMeshLogger.Info($"Prefetched next asset={slot.asset_name} cacheHit={handle.Value.CacheHit.ToString().ToLowerInvariant()}", AdUnitId);
            }
        }

        private AdCreative BuildCreativeFromPlanSlot(ServingPlanSlot slot)
        {
            return new AdCreative
            {
                id = slot.asset_id,
                name = slot.asset_name,
                media_url = slot.media_url,
                media_type = slot.media_type,
                type = slot.media_type,
                delivery_mode = "fallback",
                effective_source = slot.source,
                valid_until = slot.slot_end,
                refresh_after = slot.slot_end,
                creative_version = slot.asset_version,
                current_asset_id = slot.asset_id,
                current_asset_name = slot.asset_name,
                next_change_at = slot.slot_end,
                video_loop_enabled = slot.loop_flag,
                serving_enabled = true,
            };
        }

        private static DateTime ParsePlanTime(string value, DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            return fallback;
        }

        private void ExitProximityZoneIfNeeded()
        {
            if (!_isInsideProximityZone)
            {
                return;
            }

            if (_currentProximityDwellSeconds >= _qualifiedExposureThresholdSeconds)
            {
                _pendingQualifiedExposureCount += 1f;
            }

            _isInsideProximityZone = false;
            _currentProximityDwellSeconds = 0f;
        }

        private void FlushProximityReport()
        {
            ExitProximityZoneIfNeeded();

            if (_currentAd == null || IsFallbackCreative(_currentAd))
            {
                ResetPendingProximityCounters();
                return;
            }

            var hasMeaningfulPayload =
                _pendingProximityEntries > 0f ||
                _pendingRepeatEntries > 0f ||
                _pendingQualifiedExposureCount > 0f ||
                _pendingProximityDwellSeconds > 0f ||
                _pendingProximityActiveDurationSeconds > 0f;

            if (!hasMeaningfulPayload)
            {
                _proximityReportTimer = 0f;
                return;
            }

            AdMeshPlugin.StartRoutine(AdMeshPlugin.ReportProximityCoroutine(
                _currentAd.id,
                AdUnitId,
                _currentAd.campaign_id,
                _currentAd.selection_token,
                _currentAd.schedule_id,
                AdMeshPlugin.GetSessionId(),
                _currentAd.type,
                GetCreativeVersion(_currentAd),
                GetCacheStatus(),
                _currentAd.override_id,
                new AdMeshPlugin.ProximityPayload
                {
                    radius_meters = _useAudioRangeForProximity ? _audioMaxDistance : _proximityRadius,
                    qualified_dwell_threshold_seconds = _qualifiedExposureThresholdSeconds,
                    entries = Mathf.RoundToInt(_pendingProximityEntries),
                    repeat_entries = Mathf.RoundToInt(_pendingRepeatEntries),
                    total_dwell_seconds = _pendingProximityDwellSeconds,
                    qualified_exposure_count = Mathf.RoundToInt(_pendingQualifiedExposureCount),
                    active_duration_seconds = _pendingProximityActiveDurationSeconds,
                }
            ));

            ResetPendingProximityCounters();
        }

        private void ResetPendingProximityCounters()
        {
            _pendingProximityEntries = 0f;
            _pendingRepeatEntries = 0f;
            _pendingQualifiedExposureCount = 0f;
            _pendingProximityDwellSeconds = 0f;
            _pendingProximityActiveDurationSeconds = 0f;
            _proximityReportTimer = 0f;
        }

        private bool HasActiveLease(DateTime now) => _currentAd != null && _validUntilUtc != DateTime.MinValue && now < _validUntilUtc;

        private bool LeaseExpired(DateTime now) => _currentAd != null && _validUntilUtc != DateTime.MinValue && now >= _validUntilUtc;

        private void ExpireCurrentCreative()
        {
            StopTrackingIfNeeded();
            FlushProximityReport();
            UnregisterVideoPlayerEvents(_videoPlayer);
            _currentAd = null;
            _isServingCachedLease = false;
            _isLoadingHostedFallback = false;
            _validUntilUtc = DateTime.MinValue;
            _refreshAfterUtc = DateTime.MinValue;
            _status = "expired";
            _videoPreparing = false;
            _holdingExpiredCreativeForReplacement = false;
            _isInsideProximityZone = false;
            _currentProximityDwellSeconds = 0f;

            if (_videoPlayer != null)
            {
                _videoPlayer.Stop();
            }

            ApplyFallbackTexture();
        }

        private void ScheduleRetry()
        {
            if (!_useRealAds)
            {
                return;
            }

            _nextRetryAtUtc = DateTime.UtcNow.AddSeconds(RetryAfterFailureSeconds);
        }

        private string GetCreativeVersion(AdCreative ad)
        {
            if (ad == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(ad.creative_version))
            {
                return ad.creative_version;
            }

            return !string.IsNullOrWhiteSpace(ad.override_id) ? ad.override_id : ad.schedule_id;
        }

        private string GetCacheStatus() => _isServingCachedLease ? "cached_lease" : "fresh";

        private bool ShouldHoldCurrentCreativeDuringRefresh()
        {
            return ShouldHoldCurrentCreativeDuringRefresh(_currentAd);
        }

        private static bool ShouldHoldCurrentCreativeDuringRefresh(AdCreative ad)
        {
            if (ad == null)
            {
                return false;
            }

            var source = ad.effective_source ?? ad.delivery_mode ?? string.Empty;
            return string.Equals(source, "rotation", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "admin_direct", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "fallback", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFallbackCreative(AdCreative ad)
        {
            return ad != null && string.Equals(ad.delivery_mode, "fallback", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateSpatialAudio()
        {
            if (!_enableAudio || _videoAudioSource == null)
            {
                return;
            }

            var listenerTransform = ResolveListenerTransform();
            if (listenerTransform == null)
            {
                _videoAudioSource.volume = _audioVolume;
                return;
            }

            var distance = Vector3.Distance(_targetRenderer.transform.position, listenerTransform.position);
            float attenuation;
            if (distance <= _audioMinDistance)
            {
                attenuation = 1f;
            }
            else if (distance >= _audioMaxDistance)
            {
                attenuation = 0f;
            }
            else
            {
                attenuation = 1f - ((distance - _audioMinDistance) / (_audioMaxDistance - _audioMinDistance));
            }

            _videoAudioSource.volume = _audioVolume * Mathf.Clamp01(attenuation);
        }

        private static Transform ResolveListenerTransform()
        {
            var listener = UnityEngine.Object.FindFirstObjectByType<AudioListener>();
            if (listener != null)
            {
                return listener.transform;
            }

            return Camera.main != null ? Camera.main.transform : null;
        }

        private void SendImpression(AdCreative ad)
        {
            if (ad == null || IsFallbackCreative(ad))
            {
                return;
            }

            AdMeshLogger.Info($"Sending impression schedule={ad.schedule_id ?? string.Empty} source={ad.delivery_mode ?? string.Empty}", AdUnitId);
            AdMeshPlugin.StartRoutine(AdMeshPlugin.ReportImpressionCoroutine(
                ad.id,
                AdUnitId,
                ad.campaign_id,
                ad.selection_token,
                ad.schedule_id,
                ad.type,
                GetCreativeVersion(ad),
                GetCacheStatus(),
                ad.override_id
            ));
        }

        private bool TryLoadHostedFallback(string reason)
        {
            if (_isLoadingHostedFallback || _targetRenderer == null)
            {
                return false;
            }

            _isLoadingHostedFallback = true;
            _currentAd = new AdCreative
            {
                id = "system-test-image",
                name = "AdMesh Hosted Fallback",
                media_url = HostedFallbackImageUrl,
                media_type = "image",
                type = "image",
                campaign_id = "system-test-fill",
                package_type = "DTL",
                delivery_mode = "fallback",
                schedule_id = string.Empty,
                creative_version = "system-test-image-v1",
            };
            _fallbackUsageCount += 1;
            _status = "loading-fallback";
            AdMeshLogger.Info($"Using hosted fallback because: {reason}", AdUnitId);
            AdMeshPlugin.StartRoutine(DownloadTextureRoutine(HostedFallbackImageUrl));
            return true;
        }

    }
}
