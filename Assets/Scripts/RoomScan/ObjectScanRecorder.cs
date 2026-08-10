using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;                       // EnvironmentRaycastManager, PassthroughCameraAccess
using Meta.XR.MRUtilityKit;          // MRUK room anchor (origin)

namespace RoomScan
{
    /// <summary>
    /// Turns per-frame YOLO 2D boxes into a deduplicated set of 3D objects
    /// in room-local space, then serialises to JSON.
    ///
    /// ObjectDetectionScanBridge calls ProcessDetection(...) once per detection
    /// per frame. Call ExportToJson() when done.
    /// </summary>
    public class ObjectScanRecorder : MonoBehaviour
    {
        [Header("References")]
        public EnvironmentRaycastManager raycastManager;

        [Tooltip("Left blank, this is found in the scene at Start.")]
        public PassthroughCameraAccess cameraAccess;

        [Header("Clustering")]
        [Tooltip("Detections of the same label within this radius merge into one object.")]
        public float mergeRadius = 0.5f;

        [Tooltip("An object must be seen this many times before it is exported.")]
        public int minObservations = 5;

        [Tooltip("Extra confidence gate. ObjectDetectionAgent.minConfidence and the provider's " +
                 "scoreThreshold already filter upstream, so this is normally left at 0.")]
        [Range(0f, 1f)] public float minConfidence = 0f;

        [Header("Sanity limits")]
        [Tooltip("Ignore depth hits further away than this (metres).")]
        public float maxRaycastDistance = 6f;

        [Tooltip("Discard a cluster if its box grows larger than this on any axis.")]
        public float maxObjectSize = 3f;

        // ------------------------------------------------------------------

        private class Cluster
        {
            public string Label;
            public float BestConfidence;
            public int Observations;
            public Bounds RoomBounds;       // grown in room-local space
            public Vector3 CentroidSum;     // for a stable centre estimate
            public Quaternion Orientation;
            public DateTime FirstSeen;
            public DateTime LastSeen;

            public Vector3 Centroid => CentroidSum / Mathf.Max(1, Observations);
        }

        private readonly List<Cluster> _clusters = new List<Cluster>();
        private MRUKRoom _room;

        private void Start()
        {
            if (raycastManager == null)
                raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>();

            if (cameraAccess == null)
                cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();

            if (!EnvironmentRaycastManager.IsSupported)
                Debug.LogError("[ObjectScanRecorder] Depth API unsupported on this device/OS.");

            if (raycastManager == null)
                Debug.LogError("[ObjectScanRecorder] No EnvironmentRaycastManager in the scene.");

            if (cameraAccess == null)
                Debug.LogError("[ObjectScanRecorder] No PassthroughCameraAccess in the scene.");

            if (MRUK.Instance != null)
            {
                MRUK.Instance.RegisterSceneLoadedCallback(() =>
                {
                    _room = MRUK.Instance.GetCurrentRoom();
                    if (_room == null)
                        Debug.LogError("[ObjectScanRecorder] MRUK loaded but there is no current room. " +
                                       "Run Space Setup on the headset. Recording will fall back to " +
                                       "WORLD space and will not survive a recenter.");
                });
            }
            else
            {
                Debug.LogError("[ObjectScanRecorder] No MRUK in the scene. Coordinates will be recorded " +
                               "in WORLD space and will not survive a recenter or restart.");
            }
        }

        // ==================================================================
        // MAIN ENTRY POINT
        // ==================================================================

        /// <param name="label">YOLO class name, e.g. "chair".</param>
        /// <param name="confidence">0..1 from the model.</param>
        /// <param name="boxPixels">
        /// Bounding box in CAMERA IMAGE pixels (PassthroughCameraAccess.GetTexture()
        /// resolution), origin TOP-LEFT with y increasing downward.
        /// </param>
        /// <param name="capturePose">
        /// Camera pose at the moment the frame was grabbed -- NOT the current pose. At 30fps
        /// with 40-60ms latency, using the live pose smears boxes badly when the user
        /// turns their head. ObjectDetectionScanBridge sources this from
        /// DepthTextureAccess.DepthFrameData.CameraPose.
        /// </param>
        public void ProcessDetection(string label, float confidence, Rect boxPixels, Pose capturePose)
        {
            if (confidence < minConfidence) return;
            if (raycastManager == null) return;   // already logged in Start

            // --- 1. Centre pixel -> world ray, built from the CAPTURE pose ---
            if (!TryBuildWorldRay(boxPixels.center, capturePose, out var centerRay)) return;

            // --- 2. Depth raycast to find how far away it actually is ---
            if (!raycastManager.Raycast(centerRay, out var hit)) return;

            var hitPoint = hit.point;
            var depth = Vector3.Distance(centerRay.origin, hitPoint);
            if (depth <= 0.05f || depth > maxRaycastDistance) return;

            // --- 3. Project the box corners onto the plane at that depth ---
            // This converts pixel extents into metric extents.
            var planeNormal = -centerRay.direction;
            // Order is irrelevant -- these only feed Encapsulate().
            var corners = new[]
            {
                new Vector2(boxPixels.xMin, boxPixels.yMin),
                new Vector2(boxPixels.xMax, boxPixels.yMin),
                new Vector2(boxPixels.xMin, boxPixels.yMax),
                new Vector2(boxPixels.xMax, boxPixels.yMax),
            };

            var worldCorners = new List<Vector3>(4);
            foreach (var c in corners)
            {
                if (!TryBuildWorldRay(c, capturePose, out var r)) return;
                worldCorners.Add(IntersectPlane(r, hitPoint, planeNormal));
            }

            // --- 4. Convert everything to room-local space ---
            var localCenter = WorldToRoom(hitPoint);
            var localCorners = new List<Vector3>(4);
            foreach (var wc in worldCorners) localCorners.Add(WorldToRoom(wc));

            // Yaw the box to face the camera, flattened to horizontal.
            var toCam = capturePose.position - hitPoint;
            toCam.y = 0f;
            var orientation = toCam.sqrMagnitude > 1e-4f
                ? WorldToRoom(Quaternion.LookRotation(toCam.normalized, Vector3.up))
                : Quaternion.identity;

            // --- 5. Merge into an existing cluster or start a new one ---
            var cluster = FindCluster(label, localCenter);
            var now = DateTime.UtcNow;

            if (cluster == null)
            {
                cluster = new Cluster
                {
                    Label = label,
                    BestConfidence = confidence,
                    Observations = 0,
                    RoomBounds = new Bounds(localCenter, Vector3.zero),
                    CentroidSum = Vector3.zero,
                    Orientation = orientation,
                    FirstSeen = now
                };
                _clusters.Add(cluster);
            }

            cluster.Observations++;
            cluster.CentroidSum += localCenter;
            cluster.LastSeen = now;
            if (confidence > cluster.BestConfidence)
            {
                cluster.BestConfidence = confidence;
                cluster.Orientation = orientation;   // trust the clearest view
            }

            // Growing the bounds across viewpoints is what recovers the THIRD
            // dimension -- a single 2D box can never give you depth extent.
            foreach (var lc in localCorners) cluster.RoomBounds.Encapsulate(lc);
            cluster.RoomBounds.Encapsulate(localCenter);
        }

        // ==================================================================
        // EXPORT
        // ==================================================================

        public RoomScanFile BuildScanFile()
        {
            var file = new RoomScanFile
            {
                originAnchorUuid = _room != null ? _room.Anchor.Uuid.ToString() : "none"
            };

            if (_room != null)
            {
                if (_room.FloorAnchor != null)
                    file.room.floorY = WorldToRoom(_room.FloorAnchor.transform.position).y;
                if (_room.CeilingAnchor != null)
                    file.room.ceilingY = WorldToRoom(_room.CeilingAnchor.transform.position).y;

                foreach (var wall in _room.WallAnchors)
                {
                    var size = wall.PlaneRect.HasValue
                        ? new Vector3(wall.PlaneRect.Value.size.x, wall.PlaneRect.Value.size.y, 0.05f)
                        : Vector3.zero;

                    file.room.walls.Add(new WallRecord
                    {
                        center = new Vec3(WorldToRoom(wall.transform.position)),
                        rotation = new Quat(WorldToRoom(wall.transform.rotation)),
                        size = new Vec3(size)
                    });
                }
            }

            int index = 0;
            foreach (var c in _clusters)
            {
                if (c.Observations < minObservations) continue;

                var size = c.RoomBounds.size;
                if (size.x > maxObjectSize || size.y > maxObjectSize || size.z > maxObjectSize)
                    continue;   // runaway cluster, almost certainly bad depth hits

                // Give flat clusters a minimum thickness so the box is visible.
                size = new Vector3(
                    Mathf.Max(size.x, 0.05f),
                    Mathf.Max(size.y, 0.05f),
                    Mathf.Max(size.z, 0.05f));

                file.objects.Add(new ScannedObject
                {
                    id = $"obj_{index++:D3}",
                    label = c.Label,
                    confidence = c.BestConfidence,
                    observations = c.Observations,
                    position = new Vec3(c.RoomBounds.center),
                    rotation = new Quat(c.Orientation),
                    size = new Vec3(size),
                    firstSeenUtc = c.FirstSeen.ToString("o"),
                    lastSeenUtc = c.LastSeen.ToString("o")
                });
            }

            return file;
        }

        public void ExportToJson(string path = null)
        {
            RoomScanIO.Save(BuildScanFile(), path);
        }

        public void ClearScan() => _clusters.Clear();

        public int PendingClusterCount => _clusters.Count;

        // ==================================================================
        // HELPERS
        // ==================================================================

        /// <summary>
        /// Builds a world-space ray for an image pixel using a stored pose.
        /// The pose argument is what makes this correct: passing the LIVE pose would
        /// aim the ray where the head is now, not where it was when the frame was
        /// captured 40-60ms ago.
        /// </summary>
        private bool TryBuildWorldRay(Vector2 pixel, Pose capturePose, out Ray worldRay)
        {
            worldRay = default;

            var tex = cameraAccess != null ? cameraAccess.GetTexture() : null;
            if (tex == null) return false;

            // Image pixels are top-left origin; viewport points are bottom-left origin.
            var viewport = new Vector2(pixel.x / tex.width, 1f - pixel.y / tex.height);

            worldRay = cameraAccess.ViewportPointToRay(viewport, capturePose);
            return true;
        }

        private static Vector3 IntersectPlane(Ray ray, Vector3 planePoint, Vector3 planeNormal)
        {
            var denom = Vector3.Dot(ray.direction, planeNormal);
            if (Mathf.Abs(denom) < 1e-6f) return ray.origin;
            var t = Vector3.Dot(planePoint - ray.origin, planeNormal) / denom;
            return ray.origin + ray.direction * t;
        }

        private Cluster FindCluster(string label, Vector3 localCenter)
        {
            Cluster best = null;
            var bestDist = mergeRadius;

            foreach (var c in _clusters)
            {
                if (c.Label != label) continue;
                var d = Vector3.Distance(c.Centroid, localCenter);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        private Vector3 WorldToRoom(Vector3 world)
            => _room == null ? world : _room.transform.InverseTransformPoint(world);

        private Quaternion WorldToRoom(Quaternion world)
            => _room == null ? world : Quaternion.Inverse(_room.transform.rotation) * world;
    }
}
