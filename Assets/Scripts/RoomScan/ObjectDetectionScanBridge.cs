using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Meta.XR.BuildingBlocks.AIBlocks;   // ObjectDetectionAgent, BoxData, DepthTextureAccess

namespace RoomScan
{
    /// <summary>
    /// Feeds the Object Detection building block's YOLO output into ObjectScanRecorder.
    ///
    /// Put this on the [BuildingBlock] Object Detection GameObject, alongside
    /// ObjectDetectionAgent and DepthTextureAccess.
    ///
    /// Two things happen here that are easy to get wrong elsewhere:
    ///   1. The capture pose comes from DepthTextureAccess, not from the live head pose.
    ///   2. BoxData's fields are decoded correctly -- see HandleBatch.
    /// </summary>
    [RequireComponent(typeof(ObjectDetectionAgent))]
    public class ObjectDetectionScanBridge : MonoBehaviour
    {
        [Tooltip("The object carrying ObjectScanRecorder.")]
        public ObjectScanRecorder recorder;

        [Tooltip("Log every detection with its capture pose. Noisy -- for bring-up only.")]
        public bool verboseLogging;

        private ObjectDetectionAgent _agent;
        private DepthTextureAccess _depth;
        private Pose _capturePose;
        private bool _hasPose;
        private bool _warnedNoPose;

        /// <summary>Most recent capture pose, for debug overlays.</summary>
        public Pose LastCapturePose => _capturePose;

        private void Awake()
        {
            _agent = GetComponent<ObjectDetectionAgent>();
            _depth = GetComponent<DepthTextureAccess>();

            if (recorder == null)
                Debug.LogError("[ScanBridge] recorder is not assigned in the Inspector.");

            if (_depth == null)
                Debug.LogError("[ScanBridge] No DepthTextureAccess on this GameObject. The Object " +
                               "Detection building block normally supplies it; without it there is " +
                               "no capture pose and nothing will be recorded.");
        }

        private void OnEnable()
        {
            if (_agent != null) _agent.OnBoxesUpdated += HandleBatch;
            if (_depth != null) _depth.OnDepthTextureUpdateCPU += OnDepth;
        }

        private void OnDisable()
        {
            if (_agent != null) _agent.OnBoxesUpdated -= HandleBatch;
            if (_depth != null) _depth.OnDepthTextureUpdateCPU -= OnDepth;
        }

        /// <summary>
        /// The pose PassthroughCameraAccess reported when this depth frame was sampled.
        /// ObjectDetectionAgent calls RequestDepthSample() immediately before grabbing the
        /// camera texture, so this tracks the frame being inferred.
        ///
        /// Caveat: the GPU readback is async and is skipped while one is already in flight,
        /// so this can trail the detection by a frame or two. ObjectDetectionVisualizer
        /// has exactly the same behaviour -- it is the SDK's accuracy ceiling, not a bug.
        /// </summary>
        private void OnDepth(DepthTextureAccess.DepthFrameData frame)
        {
            _capturePose = frame.CameraPose;
            _hasPose = true;
        }

        private void HandleBatch(List<BoxData> batch)
        {
            if (recorder == null) return;

            if (!_hasPose)
            {
                if (!_warnedNoPose)
                {
                    _warnedNoPose = true;
                    Debug.LogWarning("[ScanBridge] Detections are arriving but no depth frame has " +
                                     "been delivered yet, so there is no capture pose. If this " +
                                     "persists, check that EnvironmentDepthManager is enabled and " +
                                     "that depth is supported on this device.");
                }
                return;
            }

            foreach (var box in batch)
            {
                SplitLabel(box.label, out var className, out var confidence);

                // BoxData.position is (xmin, ymin) and .scale is (xmax, ymax) -- opposite
                // CORNERS, not position+size, despite the field names and the XML docs.
                // Units are camera-texture pixels with a top-left origin.
                var boxPixels = Rect.MinMaxRect(
                    box.position.x, box.position.y,
                    box.scale.x, box.scale.y);

                if (verboseLogging)
                    Debug.Log($"[ScanBridge] {className} {confidence:0.00} px={boxPixels} " +
                              $"capturePos={_capturePose.position} " +
                              $"captureRot={_capturePose.rotation.eulerAngles}");

                recorder.ProcessDetection(className, confidence, boxPixels, _capturePose);
            }
        }

        /// <summary>
        /// ObjectDetectionAgent packs the score into the label as $"{label} {score:0.00}",
        /// so "dining table 0.87". COCO class names contain spaces, so split on the LAST
        /// one. If there is no parseable trailing score, keep the whole string as the
        /// label rather than dropping the detection.
        /// </summary>
        internal static void SplitLabel(string raw, out string className, out float confidence)
        {
            className = raw;
            confidence = 1f;

            if (string.IsNullOrEmpty(raw)) return;

            var split = raw.LastIndexOf(' ');
            if (split <= 0 || split == raw.Length - 1) return;

            var tail = raw.Substring(split + 1);

            // The SDK formats under the ambient culture, so try that before invariant.
            if (!float.TryParse(tail, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
                !float.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return;

            className = raw.Substring(0, split);
            confidence = parsed;
        }
    }
}
