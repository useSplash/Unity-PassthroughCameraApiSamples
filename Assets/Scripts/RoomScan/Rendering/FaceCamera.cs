using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// Keeps a label turned toward the headset. Added at runtime by
    /// RoomScanRebuilder to the text above each rebuilt box.
    /// </summary>
    public class FaceCamera : MonoBehaviour
    {
        private Transform _cam;

        private void Start() => _cam = Camera.main != null ? Camera.main.transform : null;

        private void LateUpdate()
        {
            if (_cam == null) return;
            transform.rotation = Quaternion.LookRotation(transform.position - _cam.position, Vector3.up);
        }
    }
}
