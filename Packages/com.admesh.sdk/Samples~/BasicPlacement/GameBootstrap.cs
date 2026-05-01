using AdMesh.Core;
using UnityEngine;

namespace AdMesh.Samples.BasicPlacement
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private string _sdkKey = "";
        [SerializeField] private string _adSelectorUrl = "";
        [SerializeField] private string _eventCollectorUrl = "";

        private void Awake()
        {
            if (string.IsNullOrWhiteSpace(_sdkKey))
            {
                Debug.LogWarning("[AdMesh Sample] Set an SDK key before entering play mode.");
                return;
            }

            AdMeshPlugin.Initialize(_sdkKey, _adSelectorUrl, _eventCollectorUrl);
        }
    }
}
