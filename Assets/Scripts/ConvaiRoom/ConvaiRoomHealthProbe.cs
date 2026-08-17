using System.Collections.Generic;
using System.IO;
using System.Text;
using Convai.Runtime.Components;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;
using UnityEngine.AI;

namespace ConvaiRoom
{
    /// <summary>
    /// Periodic health report for the headset and the scan pipeline, written as one
    /// greppable block.
    ///
    /// Every line carries the same prefix, so `adb logcat | grep RoomHealth` shows the
    /// whole picture at once, and every line ends with ok=True/False so a single failing
    /// subsystem can be found with `grep "ok=False"`.
    ///
    /// The ladder matters more than any individual line. Batches arriving with no capture
    /// pose means depth is not being delivered; detections forwarded with no pending
    /// clusters means the raycasts are missing the room; clusters with no scan file means
    /// nothing was ever exported; a scan file with an empty catalogue means everything
    /// fell below the confidence floor. Each of those looks identical from the outside --
    /// the character simply knows nothing about the room.
    /// </summary>
    public class ConvaiRoomHealthProbe : MonoBehaviour
    {
        private const string Prefix = "[RoomHealth]";

        [Header("Reporting")]
        [Tooltip("Seconds between reports. Zero or less reports once on start and stops.")]
        public float intervalSeconds = 5f;

        [Tooltip("Only log when a subsystem flips between ok and not ok, rather than on " +
                 "every interval. Quieter for long sessions, but you lose the heartbeat " +
                 "that tells you the probe is still alive.")]
        public bool logOnlyOnChange;

        [Header("Wiring (left empty, these are found in the scene)")]
        public ObjectDetectionScanBridge bridge;
        public ObjectScanRecorder recorder;
        public RoomScanRebuilder rebuilder;
        public RoomScanNavMeshBuilder navMeshBuilder;
        public RoomScanActionConfigBuilder actionConfigBuilder;
        public ConvaiCharacter character;
        public NavMeshAgent agent;

        private readonly StringBuilder _builder = new StringBuilder();
        private readonly List<string> _problems = new List<string>();
        private float _nextReportTime;
        private string _lastVerdict;

        private void Awake()
        {
            if (bridge == null) bridge = FindAnyObjectByType<ObjectDetectionScanBridge>();
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (navMeshBuilder == null) navMeshBuilder = FindAnyObjectByType<RoomScanNavMeshBuilder>();
            if (actionConfigBuilder == null)
                actionConfigBuilder = FindAnyObjectByType<RoomScanActionConfigBuilder>();
            if (character == null) character = FindAnyObjectByType<ConvaiCharacter>();
            if (agent == null) agent = FindAnyObjectByType<NavMeshAgent>();
        }

        private void Start() => Report();

        private void Update()
        {
            if (intervalSeconds <= 0f) return;
            if (Time.unscaledTime < _nextReportTime) return;

            _nextReportTime = Time.unscaledTime + intervalSeconds;
            Report();
        }

        /// <summary>Writes one report immediately. Public so a button can force one.</summary>
        public void Report()
        {
            _problems.Clear();
            _builder.Clear();

            ReportHeadset();
            ReportMruk();
            ReportDetection();
            ReportRecorder();
            ReportScanFile();
            ReportReplay();
            ReportNavMesh();
            ReportCatalogue();
            ReportConvai();

            var verdict = _problems.Count == 0
                ? "ALL OK"
                : "PROBLEM: " + string.Join(",", _problems);

            _builder.Append(Prefix).Append(" verdict   ").Append(verdict);

            if (logOnlyOnChange && verdict == _lastVerdict) return;
            _lastVerdict = verdict;

            if (_problems.Count == 0) Debug.Log(_builder.ToString());
            else Debug.LogWarning(_builder.ToString());
        }

        // -----------------------------------------------------------------

        private void ReportHeadset()
        {
            var head = Camera.main;
            var hmd = OVRManager.isHmdPresent;
            var controller = OVRInput.GetActiveController();

            Line("headset", hmd && head != null,
                 $"hmd={hmd} controller={controller} inputFocus={OVRManager.hasInputFocus} " +
                 $"camera={(head != null ? head.name : "NONE")}");
        }

        private void ReportMruk()
        {
            var instance = MRUK.Instance;
            var room = instance != null ? instance.GetCurrentRoom() : null;

            // No room means Space Setup was never run on this headset, and every replayed
            // box lands relative to wherever the app started instead of the real room.
            Line("mruk", room != null,
                 $"instance={instance != null} " +
                 $"room={(room != null ? room.Anchor.Uuid.ToString() : "NONE_RUN_SPACE_SETUP")}");
        }

        private void ReportDetection()
        {
            var present = bridge != null;
            var batches = present ? bridge.BatchesSeen : 0;
            var forwarded = present ? bridge.DetectionsForwarded : 0;
            var pose = present && bridge.HasCapturePose;

            Line("detect", present && batches > 0 && pose,
                 $"bridge={present} batches={batches} forwarded={forwarded} capturePose={pose}");
        }

        private void ReportRecorder()
        {
            var present = recorder != null;

            Line("recorder", present,
                 $"present={present} enabled={present && recorder.enabled} " +
                 $"pendingClusters={(present ? recorder.PendingClusterCount : 0)}");
        }

        private void ReportScanFile()
        {
            var path = RoomScanIO.DefaultPath;
            var exists = File.Exists(path);
            long bytes = 0;

            if (exists)
            {
                // Size rather than a parse: this runs every few seconds and the file is
                // the only thing here that costs disk IO to inspect properly.
                bytes = new FileInfo(path).Length;
            }

            Line("scanfile", exists && bytes > 0, $"exists={exists} bytes={bytes} path={path}");
        }

        private void ReportReplay()
        {
            var boxes = rebuilder != null && rebuilder.Rebuilt != null ? rebuilder.Rebuilt.Count : 0;
            var anchored = rebuilder != null && rebuilder.Room != null;
            var loaded = rebuilder != null && rebuilder.Scan != null && rebuilder.Scan.objects != null
                ? rebuilder.Scan.objects.Count
                : 0;

            Line("replay", boxes > 0,
                 $"boxes={boxes} loadedFromFile={loaded} " +
                 $"anchored={(anchored ? "MRUK_ROOM" : "RAW_WORLD_SPACE")}");
        }

        private void ReportNavMesh()
        {
            var valid = navMeshBuilder != null && navMeshBuilder.HasNavMesh;

            Line("navmesh", valid,
                 $"valid={valid} obstacles={(navMeshBuilder != null ? navMeshBuilder.ObstacleCount : 0)} " +
                 $"agentOnMesh={(agent != null && agent.enabled && agent.isOnNavMesh)}");
        }

        private void ReportCatalogue()
        {
            var count = actionConfigBuilder != null && actionConfigBuilder.LastObjects != null
                ? actionConfigBuilder.LastObjects.Count
                : 0;

            // This is the number the character actually receives. Zero here means it was
            // connected without a catalogue and knows nothing about the room.
            Line("catalogue", count > 0, $"objectsSentToCharacter={count}");
        }

        private void ReportConvai()
        {
            var present = character != null;

            // Presence is the pass condition, not conversation: not talking yet is the
            // normal state at startup, but it does block CommitRescan from pushing.
            Line("convai", present,
                 $"character={present} " +
                 $"inConversation={present && character.IsInConversation}");
        }

        private void Line(string name, bool ok, string detail)
        {
            if (!ok) _problems.Add(name);

            _builder.Append(Prefix).Append(' ').Append(name.PadRight(9))
                    .Append(" ok=").Append(ok).Append("  ").AppendLine(detail);
        }
    }
}
