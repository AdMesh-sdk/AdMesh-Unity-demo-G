using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AdMesh.Editor
{
    /// <summary>
    /// Provides a Godot-like Project Settings panel for AdMesh.
    /// Saves the settings directly into StreamingAssets/admesh_config.json
    /// so the runtime can automatically pick them up.
    /// </summary>
    public static class AdMeshProjectSettings
    {
        [Serializable]
        private class AdMeshConfig
        {
            public string sdkKey = "";
            public string adSelectorUrl = "https://select.admesh.cloud";
            public string eventCollectorUrl = "https://events.admesh.cloud";
            public bool autoInitialize = true;
        }

        private static AdMeshConfig _cachedConfig;

        public static string SdkKey
        {
            get => GetOrCreateConfig().sdkKey;
            set
            {
                var config = GetOrCreateConfig();
                if (config.sdkKey != value)
                {
                    config.sdkKey = value;
                    SaveConfig(config);
                }
            }
        }

        [SettingsProvider]
        public static SettingsProvider CreateAdMeshSettingsProvider()
        {
            var provider = new SettingsProvider("Project/AdMesh", SettingsScope.Project)
            {
                label = "AdMesh",
                guiHandler = (searchContext) =>
                {
                    var config = GetOrCreateConfig();

                    EditorGUI.BeginChangeCheck();

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("AdMesh SDK Configuration", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Enter your SDK Key from the AdMesh Developer Portal. No code or GameObjects are required to initialize the SDK. It will start automatically when the game runs.", MessageType.Info);
                    EditorGUILayout.Space();

                    config.sdkKey = EditorGUILayout.TextField("SDK Key", config.sdkKey);
                    
                    EditorGUILayout.Space();
                    config.autoInitialize = EditorGUILayout.Toggle("Auto Initialize on Boot", config.autoInitialize);
                    if (config.autoInitialize)
                    {
                        EditorGUILayout.HelpBox("When enabled, AdMesh automatically injects itself at game boot (like a Godot Autoload). You do not need to attach any initializer scripts.", MessageType.None);
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Advanced / Overrides", EditorStyles.boldLabel);
                    config.adSelectorUrl = EditorGUILayout.TextField("Ad Selector URL", config.adSelectorUrl);
                    config.eventCollectorUrl = EditorGUILayout.TextField("Event Collector URL", config.eventCollectorUrl);

                    if (GUILayout.Button("Reset Endpoints to Default"))
                    {
                        config.adSelectorUrl = "https://select.admesh.cloud";
                        config.eventCollectorUrl = "https://events.admesh.cloud";
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        SaveConfig(config);
                    }
                },
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "AdMesh", "Ad", "Ads", "Monetization", "SDK" })
            };

            return provider;
        }

        private static string GetConfigPath()
        {
            var streamingAssetsPath = Application.streamingAssetsPath;
            if (!Directory.Exists(streamingAssetsPath))
            {
                Directory.CreateDirectory(streamingAssetsPath);
            }
            return Path.Combine(streamingAssetsPath, "admesh_config.json");
        }

        private static AdMeshConfig GetOrCreateConfig()
        {
            if (_cachedConfig != null) return _cachedConfig;

            var path = GetConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    _cachedConfig = JsonUtility.FromJson<AdMeshConfig>(json);
                    return _cachedConfig;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AdMesh Editor] Failed to parse existing config: {e.Message}");
                }
            }

            _cachedConfig = new AdMeshConfig();
            return _cachedConfig;
        }

        private static void SaveConfig(AdMeshConfig config)
        {
            try
            {
                var path = GetConfigPath();
                var json = JsonUtility.ToJson(config, true);
                File.WriteAllText(path, json);
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AdMesh Editor] Failed to save config: {e.Message}");
            }
        }
    }
}
