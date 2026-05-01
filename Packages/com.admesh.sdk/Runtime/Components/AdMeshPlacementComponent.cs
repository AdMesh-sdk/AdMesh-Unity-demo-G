using AdMesh.Core;
using UnityEngine;

namespace AdMesh.Components
{
    [ExecuteAlways]
    [RequireComponent(typeof(Renderer))]
    public sealed class AdMeshPlacementComponent : MonoBehaviour
    {
        [Header("AdMesh Setup")]
        [Tooltip("Placement identifier from the AdMesh portal. This must belong to the same app as the SDK key configured for the game.")]
        [SerializeField] private string _adUnitId = "";
        [Tooltip("When enabled, this placement requests live AdMesh serving. Leave it off for local/test behavior while wiring scenes or placeholders.")]
        [SerializeField] private bool _useRealAds = false;

        [Header("Display")]
        [Tooltip("Enables extra AdMesh runtime logs for this placement. Useful during integration and serving/debug validation.")]
        [SerializeField] private bool _verboseLogging = true;

        [Header("Audio")]
        [Tooltip("If enabled, video creatives can output spatial audio from this billboard.")]
        [SerializeField] private bool _enableAudio = true;
        [Tooltip("Base volume multiplier used before distance attenuation is applied.")]
        [SerializeField, Range(0f, 1f)] private float _audioVolume = 1f;
        [Tooltip("Distance from the billboard where audio stays at full configured volume.")]
        [SerializeField] private float _audioMinDistance = 3f;
        [Tooltip("Distance from the billboard where audio reaches its minimum audible level.")]
        [SerializeField] private float _audioMaxDistance = 20f;
        [Tooltip("Unity rolloff mode applied to the audio source. Linear is recommended for predictable billboard attenuation.")]
        [SerializeField] private AudioRolloffMode _audioRolloffMode = AudioRolloffMode.Linear;

        [Header("Delivery Proof")]
        [Tooltip("When enabled, AdMesh reports view-presence heartbeats while the placement remains visible. Turn this off only for deliberate integration testing.")]
        [SerializeField] private bool _trackPresence = true;

        [Header("Proximity Analytics")]
        [Tooltip("When enabled, this placement reports aggregate proximity analytics when an AdMesh Player Tracker is present in the scene.")]
        [SerializeField] private bool _trackProximity = true;
        [Tooltip("Uses the placement audio max distance as the proximity radius. This is the recommended v1 default for video billboards.")]
        [SerializeField] private bool _useAudioRangeForProximity = true;
        [Tooltip("Fallback proximity radius in meters when audio range is not used for analytics.")]
        [SerializeField] private float _proximityRadius = 20f;
        [Tooltip("Minimum dwell time inside the proximity radius that counts as one qualified exposure.")]
        [SerializeField] private float _qualifiedExposureThresholdSeconds = 3f;

        private Texture2D _fallbackTexture;

        private Renderer _renderer;
        private AdMeshPlacement _placement;
        private MaterialPropertyBlock _propBlock;

        public string AdUnitId => _adUnitId;
        public string RuntimeStatus => _placement?.Status ?? "idle";
        public string RuntimeReason => _placement?.LastReason ?? string.Empty;
        public string DeliverySource => _placement?.DeliverySource ?? string.Empty;
        public string CurrentMediaUrl => _placement?.CurrentMediaUrl ?? string.Empty;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _propBlock = new MaterialPropertyBlock();

            if (Application.isPlaying)
            {
                AdMeshLogger.Info($"Component awake live={_useRealAds.ToString().ToLowerInvariant()}", _adUnitId);
            }
        }

        private void Start()
        {
            if (!Application.isPlaying) return;

            if (_renderer == null || string.IsNullOrWhiteSpace(_adUnitId))
            {
                AdMeshLogger.Warning("Placement skipped because Renderer or Ad Unit ID is missing", _adUnitId);
                return;
            }

            _placement = new AdMeshPlacement(
                _renderer,
                _adUnitId,
                _fallbackTexture,
                _useRealAds,
                _verboseLogging,
                _enableAudio,
                _audioVolume,
                _audioMinDistance,
                _audioMaxDistance,
                _audioRolloffMode,
                _trackProximity,
                _useAudioRangeForProximity,
                _proximityRadius,
                _qualifiedExposureThresholdSeconds
            );
            _placement.OnAdFailedToLoad += HandleAdFailedToLoad;
            AdMeshServingPlanRuntime.RegisterPlacement(_placement, !_useRealAds);

            LoadAd();
        }

        private void OnEnable()
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
            if (_propBlock == null) _propBlock = new MaterialPropertyBlock();

            if (Application.isPlaying)
            {
                AdMeshServingPlanRuntime.SetPlacementActive(_adUnitId, true);
            }

            // Auto-load Godot-like default placeholder if empty
            if (_fallbackTexture == null)
            {
                _fallbackTexture = Resources.Load<Texture2D>("AdMeshDefaultPlaceholder");
            }

            // In Editor Mode, draw the placeholder so the user sees something!
            if (!Application.isPlaying && _renderer != null && _fallbackTexture != null)
            {
                _renderer.GetPropertyBlock(_propBlock);
                _propBlock.SetTexture("_BaseMap", _fallbackTexture);  // URP
                _propBlock.SetTexture("_MainTex", _fallbackTexture); // Built-in
                _renderer.SetPropertyBlock(_propBlock);
            }
        }

        private void Update()
        {
            if (_placement == null)
            {
                return;
            }

            _placement.Tick(Time.deltaTime);

            if (_trackPresence)
            {
                _placement.UpdatePresence(Time.deltaTime);
            }

            if (_trackProximity)
            {
                var hasTrackedPosition = AdMeshPlayerTracker.TryGetTrackedPosition(out var trackedPosition);
                _placement.UpdateProximity(Time.deltaTime, hasTrackedPosition, trackedPosition, 300f);
            }
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                AdMeshLogger.Info("Placement component disabled", _adUnitId);
                AdMeshServingPlanRuntime.SetPlacementActive(_adUnitId, false);
            }
        }

        private void OnDestroy()
        {
            if (Application.isPlaying)
            {
                AdMeshLogger.Info("Placement component destroyed", _adUnitId);
                AdMeshServingPlanRuntime.UnregisterPlacement(_adUnitId);
            }

            if (_placement != null)
            {
                _placement.OnAdFailedToLoad -= HandleAdFailedToLoad;
                _placement.Destroy();
                _placement = null;
            }
        }

        public void LoadAd()
        {
            if (_placement == null)
            {
                return;
            }

            AdMeshLogger.Info($"LoadAd requested", _adUnitId);
            _placement.LoadAd();
        }

        [ContextMenu("Load Ad")]
        public void RefreshAd()
        {
            LoadAd();
        }

        [ContextMenu("Make Material Unlit")]
        private void FixMaterial()
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
            if (_renderer != null && _renderer.sharedMaterial != null)
            {
                Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                if (unlitShader == null) unlitShader = Shader.Find("Unlit/Texture");
                
                if (unlitShader != null)
                {
                    _renderer.sharedMaterial.shader = unlitShader;
                    Debug.Log("Material shader changed to Unlit.");
                }
            }
        }

        private void OnValidate()
        {
            _audioVolume = Mathf.Clamp01(_audioVolume);
            _audioMinDistance = Mathf.Max(0.1f, _audioMinDistance);
            _audioMaxDistance = Mathf.Max(_audioMinDistance, _audioMaxDistance);
            _proximityRadius = Mathf.Max(0.1f, _proximityRadius);
            _qualifiedExposureThresholdSeconds = Mathf.Max(0.5f, _qualifiedExposureThresholdSeconds);

            // Immediately apply placeholder in Editor if it's assigned or loaded
            if (!Application.isPlaying)
            {
                if (_fallbackTexture == null)
                {
                    _fallbackTexture = Resources.Load<Texture2D>("AdMeshDefaultPlaceholder");
                }
                
                if (_renderer == null) _renderer = GetComponent<Renderer>();
                if (_propBlock == null) _propBlock = new MaterialPropertyBlock();

                if (_renderer != null && _fallbackTexture != null)
                {
                    _renderer.GetPropertyBlock(_propBlock);
                    _propBlock.SetTexture("_BaseMap", _fallbackTexture);  // URP
                    _propBlock.SetTexture("_MainTex", _fallbackTexture); // Built-in
                    _renderer.SetPropertyBlock(_propBlock);
                }
            }
        }

        private void HandleAdFailedToLoad(object sender, AdFailedToLoadEventArgs args)
        {
            if (_verboseLogging)
            {
                AdMeshLogger.Warning(args.Message, _adUnitId);
            }
        }
    }
}
