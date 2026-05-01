using System;
using System.IO;
using UnityEngine;
using AdMesh.Core;

namespace AdMesh.Internal
{
    /// <summary>
    /// Acts exactly like a Godot Autoload.
    /// This script runs automatically when the game boots, reads the
    /// admesh_config.json created by the Project Settings UI, and 
    /// initializes the AdMeshPlugin globally. No GameObjects required!
    /// </summary>
    internal static class AdMeshAutoInitialize
    {
        [Serializable]
        private class AdMeshConfig
        {
            public string sdkKey = "";
            public string adSelectorUrl = "";
            public string eventCollectorUrl = "";
            public bool autoInitialize = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInit()
        {
            if (AdMeshPlugin.IsInitialized)
            {
                return; // Already initialized manually
            }

            try
            {
                var configPath = Path.Combine(Application.streamingAssetsPath, "admesh_config.json");
                
                if (!File.Exists(configPath))
                {
                    // No config found, meaning they haven't configured the SDK via Project Settings yet.
                    return;
                }

                var json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<AdMeshConfig>(json);

                if (config == null || !config.autoInitialize || string.IsNullOrWhiteSpace(config.sdkKey))
                {
                    return;
                }

                AdMeshPlugin.Initialize(
                    config.sdkKey,
                    string.IsNullOrWhiteSpace(config.adSelectorUrl) ? null : config.adSelectorUrl,
                    string.IsNullOrWhiteSpace(config.eventCollectorUrl) ? null : config.eventCollectorUrl
                );
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AdMesh] Auto-initialize failed: {ex.Message}");
            }
        }
    }
}
