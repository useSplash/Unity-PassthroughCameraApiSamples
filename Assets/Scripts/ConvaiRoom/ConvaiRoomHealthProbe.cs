using System.Collections.Generic;
using System.IO;
using System.Text;
using Convai.Modules.Vision;
using Convai.Runtime.Components;
using Convai.Runtime.Vision.Sources;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;

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
    /// nothing was ever exported; a scan file that replays no boxes means the export was
    /// empty. Each of those looks identical from the outside -- the room simply comes up
    /// bare.
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
        public ConvaiCharacter character;

        [Tooltip("Publishes passthrough frames to Convai. Optional -- without one the vision " +
                 "line simply says the room is running blind, which is a valid setup.")]
        public ConvaiVisionPublisher visionPublisher;

        private readonly StringBuilder _builder = new StringBuilder();
        private readonly List<string> _problems = new List<string>();
        private float _nextReportTime;
        private string _lastVerdict;

        private void Awake()
        {
            if (bridge == null) bridge = FindAnyObjectByType<ObjectDetectionScanBridge>();
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (character == null) character = FindAnyObjectByType<ConvaiCharacter>();
            if (visionPublisher == null) visionPublisher = FindAnyObjectByType<ConvaiVisionPublisher>();
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
            ReportConvai();
            ReportVision();

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

        private void ReportConvai()
        {
            var present = character != null;

            // Presence is the pass condition, not conversation: not talking yet is the
            // normal state at startup.
            Line("convai", present,
                 $"character={present} " +
                 $"inConversation={present && character.IsInConversation}");
        }

        /// <summary>
        /// Whether the character is being fed passthrough frames.
        ///
        /// This is the line that exists because vision fails silently and in several different
        /// ways that are indistinguishable from inside the headset: the components can be
        /// missing, the frame source can fail to find PassthroughCameraAccess, the camera
        /// permission can be refused, or everything can be wired and simply not publishing
        /// because no session is open yet. She looks equally oblivious in all four cases.
        ///
        /// The frame count is the number that settles it. Publishing with frames climbing is
        /// the only combination that means she is actually seeing the room.
        ///
        /// Passing on presence rather than on publishing, deliberately: not connected yet is
        /// the normal state for most of a session's life, and a probe that shouts PROBLEM
        /// through the whole scan phase is a probe people stop reading.
        /// </summary>
        private void ReportVision()
        {
            if (visionPublisher == null)
            {
                Line("vision", true, "publisher=none - running blind, scan metadata only");
                return;
            }

            var source = visionPublisher.FrameSource;

            var frames = source != null ? source.FrameCount : 0;
            var capturing = source != null && source.IsCapturing;

            // The status message is the frame source's own account of why it is not capturing --
            // "PassthroughCameraAccess not found", a permission refusal, the wrong platform. It
            // is the one string worth forwarding verbatim rather than summarising.
            var status = source is IVisionFrameSourceStatusProvider reporter &&
                         !string.IsNullOrEmpty(reporter.StatusMessage)
                ? $" status={reporter.StatusMessage}"
                : "";

            Line("vision", source != null,
                 $"publisher=yes source={(source != null ? source.SourceId : "MISSING")} " +
                 $"capturing={capturing} publishing={visionPublisher.IsPublishing} " +
                 $"frames={frames}{status}");
        }

        private void Line(string name, bool ok, string detail)
        {
            if (!ok) _problems.Add(name);

            _builder.Append(Prefix).Append(' ').Append(name.PadRight(9))
                    .Append(" ok=").Append(ok).Append("  ").AppendLine(detail);
        }
    }
}
