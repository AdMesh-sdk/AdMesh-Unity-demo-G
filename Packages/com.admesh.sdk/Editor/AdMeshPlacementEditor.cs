using AdMesh.Components;
using AdMesh.Core;
using UnityEditor;
using UnityEngine;

namespace AdMesh.Editor
{
    [CustomEditor(typeof(AdMeshPlacementComponent))]
    public sealed class AdMeshPlacementEditor : UnityEditor.Editor
    {
        private static readonly GUIContent PlacementStatusLabel = new GUIContent("Placement Status", "Current runtime state for this placement.");
        private static readonly GUIContent DeliverySourceLabel = new GUIContent("Delivery Source", "Shows which serving source currently won for this placement, such as schedule, rotation, fallback, or admin direct.");
        private static readonly GUIContent ReasonLabel = new GUIContent("Last Reason", "Latest warning or failure reason reported by the placement runtime.");
        private static readonly GUIContent MediaUrlLabel = new GUIContent("Current Media URL", "Currently active creative media URL as applied by the runtime.");
        private static readonly GUIContent GlobalSdkIdLabel = new GUIContent("Global SDK ID", "Project-wide SDK key used by AdMesh requests in this Unity project.");
        private static readonly GUIContent AdUnitIdLabel = new GUIContent("Ad Unit ID", "Placement identifier from the AdMesh portal. This must belong to the same app as the configured SDK key.");
        private static readonly GUIContent UseRealAdsLabel = new GUIContent("Use Real Ads", "When enabled, this placement requests live AdMesh serving instead of staying on local/test behavior.");
        private static readonly GUIContent VerboseLoggingLabel = new GUIContent("Verbose Logging", "Enables additional integration and serving logs for this placement.");
        private static readonly GUIContent EnableAudioLabel = new GUIContent("Enable Audio", "Enables spatial video audio output from this placement.");
        private static readonly GUIContent AudioVolumeLabel = new GUIContent("Audio Volume", "Base volume multiplier before distance attenuation is applied.");
        private static readonly GUIContent AudioMinDistanceLabel = new GUIContent("Audio Min Distance", "Distance from the billboard where audio remains at full configured volume.");
        private static readonly GUIContent AudioMaxDistanceLabel = new GUIContent("Audio Max Distance", "Distance from the billboard where audio reaches its minimum audible level.");
        private static readonly GUIContent AudioRolloffModeLabel = new GUIContent("Audio Rolloff Mode", "Unity rolloff mode used by the placement audio source.");
        private static readonly GUIContent TrackPresenceLabel = new GUIContent("Track Presence", "Sends view-presence heartbeats while the placement remains visible. This is the delivery-proof toggle.");
        private static readonly GUIContent TrackProximityLabel = new GUIContent("Track Proximity", "Reports aggregate proximity analytics when an AdMesh Player Tracker exists in the scene.");
        private static readonly GUIContent UseAudioRangeForProximityLabel = new GUIContent("Use Audio Range For Proximity", "Uses the audio max distance as the proximity analytics radius. Recommended for v1 billboard placements.");
        private static readonly GUIContent ProximityRadiusLabel = new GUIContent("Proximity Radius", "Fallback analytics radius in meters when audio range is not reused.");
        private static readonly GUIContent QualifiedExposureThresholdLabel = new GUIContent("Qualified Exposure Threshold", "Minimum dwell time inside the proximity radius that counts as one qualified exposure.");

        private SerializedProperty _adUnitIdProp;
        private SerializedProperty _useRealAdsProp;
        private SerializedProperty _verboseLoggingProp;
        private SerializedProperty _enableAudioProp;
        private SerializedProperty _audioVolumeProp;
        private SerializedProperty _audioMinDistanceProp;
        private SerializedProperty _audioMaxDistanceProp;
        private SerializedProperty _audioRolloffModeProp;
        private SerializedProperty _trackPresenceProp;
        private SerializedProperty _trackProximityProp;
        private SerializedProperty _useAudioRangeForProximityProp;
        private SerializedProperty _proximityRadiusProp;
        private SerializedProperty _qualifiedExposureThresholdSecondsProp;

        private void OnEnable()
        {
            _adUnitIdProp = serializedObject.FindProperty("_adUnitId");
            _useRealAdsProp = serializedObject.FindProperty("_useRealAds");
            _verboseLoggingProp = serializedObject.FindProperty("_verboseLogging");
            _enableAudioProp = serializedObject.FindProperty("_enableAudio");
            _audioVolumeProp = serializedObject.FindProperty("_audioVolume");
            _audioMinDistanceProp = serializedObject.FindProperty("_audioMinDistance");
            _audioMaxDistanceProp = serializedObject.FindProperty("_audioMaxDistance");
            _audioRolloffModeProp = serializedObject.FindProperty("_audioRolloffMode");
            _trackPresenceProp = serializedObject.FindProperty("_trackPresence");
            _trackProximityProp = serializedObject.FindProperty("_trackProximity");
            _useAudioRangeForProximityProp = serializedObject.FindProperty("_useAudioRangeForProximity");
            _proximityRadiusProp = serializedObject.FindProperty("_proximityRadius");
            _qualifiedExposureThresholdSecondsProp = serializedObject.FindProperty("_qualifiedExposureThresholdSeconds");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var placement = (AdMeshPlacementComponent)target;

            EditorGUILayout.LabelField("AdMesh Placement", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Use this on a billboard or other Renderer. Runtime flow is: scheduled paid creative -> cached lease -> AdMesh hosted fallback -> local placeholder.",
                MessageType.Info
            );

            EditorGUILayout.Space();
            DrawSectionHeader("AdMesh Setup");
            
            // Global SDK Key placeholder mapped to Project Settings
            EditorGUI.BeginChangeCheck();
            string newSdkKey = EditorGUILayout.TextField(GlobalSdkIdLabel, AdMeshProjectSettings.SdkKey);
            if (EditorGUI.EndChangeCheck())
            {
                AdMeshProjectSettings.SdkKey = newSdkKey;
            }

            if (string.IsNullOrWhiteSpace(newSdkKey))
            {
                EditorGUILayout.HelpBox("Global SDK ID is missing. Please enter it here or in Edit > Project Settings > AdMesh.", MessageType.Error);
            }

            EditorGUILayout.PropertyField(_adUnitIdProp, AdUnitIdLabel);
            EditorGUILayout.PropertyField(_useRealAdsProp, UseRealAdsLabel);

            EditorGUILayout.Space();
            DrawSectionHeader("Display");
            EditorGUILayout.PropertyField(_verboseLoggingProp, VerboseLoggingLabel);
            CheckMaterialLighting(placement);

            EditorGUILayout.Space();
            DrawSectionHeader("Audio");
            EditorGUILayout.PropertyField(_enableAudioProp, EnableAudioLabel);
            using (new EditorGUI.DisabledScope(!_enableAudioProp.boolValue))
            {
                EditorGUILayout.Slider(_audioVolumeProp, 0f, 1f, AudioVolumeLabel);
                EditorGUILayout.PropertyField(_audioMinDistanceProp, AudioMinDistanceLabel);
                EditorGUILayout.PropertyField(_audioMaxDistanceProp, AudioMaxDistanceLabel);
                EditorGUILayout.PropertyField(_audioRolloffModeProp, AudioRolloffModeLabel);
            }
            EditorGUILayout.HelpBox(
                "Video audio is spatial. Volume attenuates with distance from the billboard using the configured min/max range.",
                MessageType.None
            );

            EditorGUILayout.Space();
            DrawSectionHeader("Delivery Proof");
            EditorGUILayout.PropertyField(_trackPresenceProp, TrackPresenceLabel);

            EditorGUILayout.Space();
            DrawSectionHeader("Proximity Analytics");
            EditorGUILayout.PropertyField(_trackProximityProp, TrackProximityLabel);
            using (new EditorGUI.DisabledScope(!_trackProximityProp.boolValue))
            {
                EditorGUILayout.PropertyField(_useAudioRangeForProximityProp, UseAudioRangeForProximityLabel);
                using (new EditorGUI.DisabledScope(_useAudioRangeForProximityProp.boolValue))
                {
                    EditorGUILayout.PropertyField(_proximityRadiusProp, ProximityRadiusLabel);
                }
                EditorGUILayout.PropertyField(_qualifiedExposureThresholdSecondsProp, QualifiedExposureThresholdLabel);
            }
            EditorGUILayout.HelpBox(
                "Add one AdMesh Player Tracker component to the player root or camera root to enable privacy-safe proximity analytics. Proximity data is analytics-only in v1 and does not affect billing.",
                MessageType.None
            );

            if (string.IsNullOrWhiteSpace(_adUnitIdProp.stringValue))
            {
                EditorGUILayout.HelpBox("Set Ad Unit ID before loading live creatives.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("This component expects the selected Ad Unit to already exist in the AdMesh developer portal.", MessageType.None);
            }

            if (_useRealAdsProp.boolValue && !AdMeshPlugin.IsInitialized && Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Runtime is not initialized. Call AdMeshPlugin.Initialize(...) at startup.", MessageType.Warning);
            }

            if (!_useRealAdsProp.boolValue)
            {
                EditorGUILayout.HelpBox("Use Real Ads is off. This placement will stay on local/test behavior and will not request live scheduled creatives.", MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawRuntimeStatus(placement);

            if (Application.isPlaying)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Load Ad"))
                {
                    placement.LoadAd();
                }
            }
        }

        private static void CheckMaterialLighting(AdMeshPlacementComponent placement)
        {
            var r = placement.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) return;
            
            var shaderName = r.sharedMaterial.shader.name.ToLowerInvariant();
            if (!shaderName.Contains("unlit"))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Your mesh is using a Lit shader, making the screen look dark. Let us fix this automatically for you.",
                    MessageType.Warning
                );
                
                if (GUILayout.Button("Auto-Fix Lighting (Create Unlit Material)"))
                {
                    Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (unlitShader == null) unlitShader = Shader.Find("Unlit/Texture");

                    if (unlitShader != null)
                    {
                        // Create a brand new material asset so it bypasses Unity's "Read Only" default material lock.
                        Material newMat = new Material(unlitShader);
                        newMat.name = "AdMeshScreenMaterial";
                        
                        // We must save it to the Assets folder so it persists
                        string path = "Assets/AdMeshScreenMaterial.mat";
                        int suffix = 1;
                        while (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                        {
                            path = $"Assets/AdMeshScreenMaterial_{suffix}.mat";
                            suffix++;
                        }
                        
                        AssetDatabase.CreateAsset(newMat, path);
                        AssetDatabase.SaveAssets();

                        Undo.RecordObject(r, "Assign Unlit Material");
                        r.sharedMaterial = newMat;
                        EditorUtility.SetDirty(r);
                        
                        Debug.Log($"[AdMesh] Created and assigned an Unlit material at {path}!");
                    }
                }
            }
        }

        private static void DrawSectionHeader(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private static void DrawRuntimeStatus(AdMeshPlacementComponent placement)
        {
            DrawSectionHeader(Application.isPlaying ? "Runtime Status" : "Play Mode Preview");

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to see live delivery state, source, selected media URL, and failure reasons.",
                    MessageType.None
                );
                return;
            }

            EditorGUILayout.LabelField(PlacementStatusLabel, new GUIContent(placement.RuntimeStatus));

            if (!string.IsNullOrWhiteSpace(placement.DeliverySource))
            {
                EditorGUILayout.LabelField(DeliverySourceLabel, new GUIContent(placement.DeliverySource));
            }

            if (!string.IsNullOrWhiteSpace(placement.RuntimeReason))
            {
                EditorGUILayout.LabelField(ReasonLabel, new GUIContent(placement.RuntimeReason));
                EditorGUILayout.HelpBox(placement.RuntimeReason, MessageType.Warning);
            }

            if (!string.IsNullOrWhiteSpace(placement.CurrentMediaUrl))
            {
                EditorGUILayout.LabelField(MediaUrlLabel);
                EditorGUILayout.SelectableLabel(
                    placement.CurrentMediaUrl,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight)
                );
            }
        }
    }
}
