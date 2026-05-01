using UnityEngine;

namespace AdMesh.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("AdMesh/Player Tracker")]
    public sealed class AdMeshPlayerTracker : MonoBehaviour
    {
        private static AdMeshPlayerTracker _activeTracker;

        public static bool TryGetTrackedPosition(out Vector3 position)
        {
            if (_activeTracker != null && _activeTracker.isActiveAndEnabled)
            {
                position = _activeTracker.transform.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private void OnEnable()
        {
            _activeTracker = this;
        }

        private void OnDisable()
        {
            if (_activeTracker == this)
            {
                _activeTracker = null;
            }
        }
    }
}
