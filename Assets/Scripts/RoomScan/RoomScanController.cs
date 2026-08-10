using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// Controller bindings for a scan session, plus the periodic cluster-count log
    /// you need to sanity-check clustering on device over adb logcat.
    ///
    /// Deliberately uses OVRInput directly rather than the sample InputManager in
    /// PassthroughCameraApiSamples/, so the scan pipeline has no dependency on the
    /// sample assets.
    /// </summary>
    public class RoomScanController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Left blank, these are found in the scene at Awake.")]
        public ObjectScanRecorder recorder;

        [Tooltip("Optional. Lets you re-run a rebuild without relaunching.")]
        public RoomScanRebuilder rebuilder;

        [Header("Bindings")]
        [Tooltip("A / X -- write room_scan.json.")]
        public OVRInput.Button exportButton = OVRInput.Button.One;

        [Tooltip("B / Y -- discard all pending clusters and start over.")]
        public OVRInput.Button clearButton = OVRInput.Button.Two;

        [Tooltip("Left X -- reload the JSON and respawn boxes. Ignored if no rebuilder.")]
        public OVRInput.Button rebuildButton = OVRInput.Button.Three;

        [Header("Diagnostics")]
        [Tooltip("Seconds between pending-cluster logs. 0 disables.")]
        public float statusLogInterval = 2f;

        private float _nextStatusLog;

        private void Awake()
        {
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();

            if (recorder == null)
                Debug.LogError("[RoomScanController] No ObjectScanRecorder in the scene.");
        }

        private void Update()
        {
            if (recorder == null) return;

            if (OVRInput.GetDown(exportButton))
            {
                recorder.ExportToJson();
                Debug.Log($"[RoomScanController] Exported to {RoomScanIO.DefaultPath}");
            }

            if (OVRInput.GetDown(clearButton))
            {
                recorder.ClearScan();
                Debug.Log("[RoomScanController] Cleared all pending clusters.");
            }

            if (rebuilder != null && OVRInput.GetDown(rebuildButton))
            {
                rebuilder.Rebuild();
            }

            // A stationary object should settle on a stable count. A count that keeps
            // climbing while you stand still means mergeRadius is too small or depth
            // hits are scattering.
            if (statusLogInterval > 0f && Time.time >= _nextStatusLog)
            {
                _nextStatusLog = Time.time + statusLogInterval;
                Debug.Log($"[RoomScanController] Pending clusters: {recorder.PendingClusterCount}");
            }
        }
    }
}
