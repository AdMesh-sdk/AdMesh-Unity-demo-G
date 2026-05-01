using System.Collections.Generic;
using UnityEngine;

namespace AdMesh.Components
{
    public sealed class AdMeshPlacementManager : MonoBehaviour
    {
        [SerializeField] private bool _autoDiscoverChildren = true;
        [SerializeField] private bool _loadAllOnStart = true;
        [SerializeField] private List<AdMeshPlacementComponent> _placements = new List<AdMeshPlacementComponent>();

        private void Awake()
        {
            if (_autoDiscoverChildren)
            {
                DiscoverPlacements();
            }
        }

        private void Start()
        {
            if (_loadAllOnStart)
            {
                RefreshPlacements();
            }
        }

        [ContextMenu("Discover Placements")]
        public void DiscoverPlacements()
        {
            _placements.Clear();
            _placements.AddRange(GetComponentsInChildren<AdMeshPlacementComponent>(true));
        }

        [ContextMenu("Refresh Placements")]
        public void RefreshPlacements()
        {
            foreach (var placement in _placements)
            {
                if (placement != null)
                {
                    placement.RefreshAd();
                }
            }
        }
    }
}
