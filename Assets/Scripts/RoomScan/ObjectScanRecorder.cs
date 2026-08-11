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
        [Tooltip("The EnvironmentRaycastManager in the scene.")]
        public EnvironmentRaycastManager raycastManager;

        [Tooltip("The [BuildingBlock] Passthrough Camera Access object.")]
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

        /// <summary>Floor on any box extent, so a cluster seen edge-on is still visible.</summary>
        private const float MinBoxExtent = 0.05f;

        // ------------------------------------------------------------------

        private class Cluster
        {
            public int Id;
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

        /// <summary>
        /// Read-only snapshot of one cluster, in ROOM-LOCAL space. This is what
        /// LiveScanVisualizer draws, and it is computed the same way the exporter
        /// computes its output -- so a box you can see is a box you will get in the
        /// JSON, once <see cref="Exportable"/> flips true.
        /// </summary>
        public readonly struct ClusterView
        {
            /// <summary>Stable for the life of the cluster; survives merges, unlike the export index.</summary>
            public readonly int Id;

            public readonly string Label;
            public readonly float Confidence;
            public readonly int Observations;
            public readonly Vector3 RoomCenter;
            public readonly Quaternion RoomRotation;
            public readonly Vector3 Size;

            /// <summary>True when this cluster would survive into the exported JSON right now.</summary>
            public readonly bool Exportable;

            public ClusterView(int id, string label, float confidence, int observations,
                               Vector3 roomCenter, Quaternion roomRotation, Vector3 size,
                               bool exportable)
            {
                Id = id;
                Label = label;
                Confidence = confidence;
                Observations = observations;
                RoomCenter = roomCenter;
                RoomRotation = roomRotation;
                Size = size;
                Exportable = exportable;
            }
        }

        /// <summary>Raised when a cluster is created or grows. Payload is room-local.</summary>
        public event Action<ClusterView> OnClusterChanged;

        /// <summary>Raised by <see cref="ClearScan"/> so visualisers can drop their boxes.</summary>
        public event Action OnScanCleared;

        private readonly List<Cluster> _clusters = new List<Cluster>();
        private int _nextClusterId;
        private MRUKRoom _room;

        private void Start()
        {
            if (!EnvironmentRaycastManager.IsSupported)
                Debug.LogError("[ObjectScanRecorder] Depth API unsupported on this device/OS.");

            if (raycastManager == null)
                Debug.LogError("[ObjectScanRecorder] raycastManager is not assigned in the Inspector.");

            if (cameraAccess == null)
                Debug.LogError("[ObjectScanRecorder] cameraAccess is not assigned in the Inspector.");

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
            // Raycast() returns true only when status == Hit, so no status check is needed.
            if (!raycastManager.Raycast(centerRay, out var hit, maxRaycastDistance)) return;

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
                    Id = _nextClusterId++,
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

            OnClusterChanged?.Invoke(Describe(cluster));
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
                // MRUK 205 exposes these as lists -- High Fidelity scene can report more
                // than one floor or ceiling. The schema stores a single height, so take
                // the first, which is what the obsolete singular properties returned.
                if (_room.FloorAnchors.Count > 0)
                    file.room.floorY = WorldToRoom(_room.FloorAnchors[0].transform.position).y;
                if (_room.CeilingAnchors.Count > 0)
                    file.room.ceilingY = WorldToRoom(_room.CeilingAnchors[0].transform.position).y;

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
                var view = Describe(c);
                if (!view.Exportable) continue;

                file.objects.Add(new ScannedObject
                {
                    id = $"obj_{index++:D3}",
                    label = view.Label,
                    confidence = view.Confidence,
                    observations = view.Observations,
                    position = new Vec3(view.RoomCenter),
                    rotation = new Quat(view.RoomRotation),
                    size = new Vec3(view.Size),
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

        public void ClearScan()
        {
            _clusters.Clear();
            OnScanCleared?.Invoke();
        }

        public int PendingClusterCount => _clusters.Count;

        // ==================================================================
        // LIVE VIEW
        // ==================================================================

        /// <summary>
        /// Replaces the contents of <paramref name="destination"/> with the current
        /// cluster set, room-local. Takes a caller-owned list so a per-frame visualiser
        /// does not allocate.
        /// </summary>
        public void SnapshotClusters(List<ClusterView> destination)
        {
            if (destination == null) return;

            destination.Clear();
            foreach (var c in _clusters) destination.Add(Describe(c));
        }

        /// <summary>
        /// The single definition of "what this cluster currently amounts to". Both the
        /// exporter and the live visualiser go through here, so the two can never
        /// disagree about a box's centre, size, or whether it counts yet.
        /// </summary>
        private ClusterView Describe(Cluster c)
        {
            var raw = c.RoomBounds.size;

            // A runaway box is almost certainly scattered depth hits, not a real object.
            var oversized = raw.x > maxObjectSize || raw.y > maxObjectSize || raw.z > maxObjectSize;

            var size = new Vector3(
                Mathf.Max(raw.x, MinBoxExtent),
                Mathf.Max(raw.y, MinBoxExtent),
                Mathf.Max(raw.z, MinBoxExtent));

            return new ClusterView(
                c.Id, c.Label, c.BestConfidence, c.Observations,
                c.RoomBounds.center, c.Orientation, size,
                exportable: c.Observations >= minObservations && !oversized);
        }

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

        /// <summary>
        /// Room-local -> world. Public because the live visualiser needs it, and keeping
        /// it here means the pipeline has exactly one definition of "room space".
        /// </summary>
        public Vector3 RoomToWorld(Vector3 local)
            => _room == null ? local : _room.transform.TransformPoint(local);

        public Quaternion RoomToWorld(Quaternion local)
            => _room == null ? local : _room.transform.rotation * local;
    }
}
