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
        [Tooltip("The object carrying ObjectScanRecorder.")]
        public ObjectScanRecorder recorder;

        [Tooltip("Optional -- leave empty until step 6. Lets you re-run a rebuild without relaunching.")]
        public RoomScanRebuilder rebuilder;

        [Tooltip("Optional. Outlines MRUK's floor and walls.")]
        public RoomShellVisualizer shellVisualizer;

        // With both controllers connected OVRInput promotes the active controller to
        // Controller.Touch, under which One/Two are the RIGHT controller's A/B and
        // Three/Four are the LEFT controller's X/Y. On a single controller Three and
        // Four resolve to nothing, so those two bindings go dead -- as do all of them
        // if hand tracking takes over as the active controller.
        [Header("Bindings")]
        [Tooltip("A -- write room_scan.json.")]
        public OVRInput.Button exportButton = OVRInput.Button.One;

        [Tooltip("B -- discard all pending clusters and start over.")]
        public OVRInput.Button clearButton = OVRInput.Button.Two;

        [Tooltip("X -- reload the JSON and respawn boxes. Ignored if no rebuilder.")]
        public OVRInput.Button rebuildButton = OVRInput.Button.Three;

        [Tooltip("Y -- toggle the room floor/wall outlines. Ignored if no shell visualizer.")]
        public OVRInput.Button shellToggleButton = OVRInput.Button.Four;

        [Header("Diagnostics")]
        [Tooltip("Seconds between pending-cluster logs. 0 disables.")]
        public float statusLogInterval = 2f;

        private float _nextStatusLog;

        private void Awake()
        {
            if (recorder == null)
                Debug.LogError("[RoomScanController] recorder is not assigned in the Inspector.");
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

            if (shellVisualizer != null && OVRInput.GetDown(shellToggleButton))
            {
                shellVisualizer.Toggle();
                Debug.Log($"[RoomScanController] Room shell {(shellVisualizer.IsShowing ? "shown" : "hidden")}.");
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
