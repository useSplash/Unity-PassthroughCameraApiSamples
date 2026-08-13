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

        [Tooltip("Two same-label clusters collapse into one when the smaller box is at least " +
                 "this fraction swallowed by the other. Catches splits that mergeRadius misses. " +
                 "0 disables overlap merging.")]
        [Range(0f, 1f)] public float mergeOverlap = 0.25f;

        [Header("Filtering")]
        [Tooltip("YOLO classes to ignore, case-insensitive. 'person' is here by default: people " +
                 "move, and a cluster that smears along someone's path is meaningless.")]
        public List<string> ignoredLabels = new List<string> { "person" };

        [Header("Size estimation")]
        [Tooltip("Fraction of observations kept when measuring extents. 0.8 discards the most " +
                 "extreme 10% at each end -- that is what stops one stray depth hit from " +
                 "inflating a box permanently. 1 keeps everything (the old behaviour).")]
        [Range(0.2f, 1f)] public float extentPercentile = 0.8f;

        [Tooltip("How many recent observations feed the size estimate. Older ones are overwritten.")]
        [Range(8, 256)] public int extentSampleCount = 64;

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

            /// <summary>
            /// The cluster's own frame, fixed when the cluster is created and never changed.
            /// Extents are measured in it, so the exported rotation and size finally describe
            /// the SAME box -- the old code measured a room-axis-aligned box and then drew it
            /// rotated, which is not a valid oriented box. Re-aiming the frame later would
            /// invalidate every extent already accumulated in it, which is why a better-
            /// confidence observation no longer overwrites it.
            /// </summary>
            public Vector3 Origin;
            public Quaternion Rotation;

            public Vector3 CentroidSum;     // room-local, for merge-distance tests
            public DateTime FirstSeen;
            public DateTime LastSeen;

            /// <summary>
            /// Ring buffer of per-observation AABBs in CLUSTER-LOCAL space. Taking percentiles
            /// across these is what replaced the old monotonic Encapsulate(): a union only ever
            /// grows, so a single bad depth hit was baked into the box forever.
            /// </summary>
            public readonly List<Vector3> Mins = new List<Vector3>();
            public readonly List<Vector3> Maxs = new List<Vector3>();
            public int Cursor;

            public bool ExtentsDirty = true;
            public Bounds CachedLocal;

            public Vector3 Centroid => CentroidSum / Mathf.Max(1, Observations);

            public Vector3 ToLocal(Vector3 roomPoint) => Quaternion.Inverse(Rotation) * (roomPoint - Origin);
            public Vector3 ToRoom(Vector3 localPoint) => Origin + Rotation * localPoint;

            public void AddObservation(Vector3 min, Vector3 max, int capacity)
            {
                capacity = Mathf.Max(1, capacity);

                // The Inspector can shrink the capacity mid-scan; drop the overflow rather
                // than let stale samples keep feeding the percentiles.
                while (Mins.Count > capacity)
                {
                    Mins.RemoveAt(Mins.Count - 1);
                    Maxs.RemoveAt(Maxs.Count - 1);
                }

                if (Mins.Count < capacity)
                {
                    Mins.Add(min);
                    Maxs.Add(max);
                    Cursor = Mins.Count % capacity;
                }
                else
                {
                    Mins[Cursor] = min;
                    Maxs[Cursor] = max;
                    Cursor = (Cursor + 1) % capacity;
                }

                ExtentsDirty = true;
            }
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

        // Scratch, reused every detection. ProcessDetection runs up to 100+ times a second,
        // so allocating here would be a steady GC drip on device.
        private readonly Vector2[] _pixelCorners = new Vector2[4];
        private readonly Vector3[] _roomCorners = new Vector3[4];
        private readonly List<float> _axisScratch = new List<float>();
        private readonly List<Cluster> _mergeScratch = new List<Cluster>();
        private HashSet<string> _ignoredSet;

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

        /// <summary>Inspector tweaks mid-scan need the cached lookups thrown away.</summary>
        private void OnValidate()
        {
            _ignoredSet = null;                                     // ignoredLabels changed
            foreach (var c in _clusters) c.ExtentsDirty = true;     // extentPercentile changed
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
            if (IsIgnored(label)) return;
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
            _pixelCorners[0] = new Vector2(boxPixels.xMin, boxPixels.yMin);
            _pixelCorners[1] = new Vector2(boxPixels.xMax, boxPixels.yMin);
            _pixelCorners[2] = new Vector2(boxPixels.xMin, boxPixels.yMax);
            _pixelCorners[3] = new Vector2(boxPixels.xMax, boxPixels.yMax);

            for (var i = 0; i < _pixelCorners.Length; i++)
            {
                if (!TryBuildWorldRay(_pixelCorners[i], capturePose, out var r)) return;
                _roomCorners[i] = WorldToRoom(IntersectPlane(r, hitPoint, planeNormal));
            }

            var roomCenter = WorldToRoom(hitPoint);

            // Yaw the box to face the camera, flattened to horizontal.
            var toCam = capturePose.position - hitPoint;
            toCam.y = 0f;
            var orientation = toCam.sqrMagnitude > 1e-4f
                ? WorldToRoom(Quaternion.LookRotation(toCam.normalized, Vector3.up))
                : Quaternion.identity;

            // --- 4. Merge into an existing cluster or start a new one ---
            var cluster = FindCluster(label, roomCenter);
            var now = DateTime.UtcNow;

            if (cluster == null)
            {
                cluster = new Cluster
                {
                    Id = _nextClusterId++,
                    Label = label,
                    BestConfidence = confidence,
                    Origin = roomCenter,
                    Rotation = orientation,
                    FirstSeen = now
                };
                _clusters.Add(cluster);
            }

            // --- 5. Record this observation's extent in the CLUSTER's own frame ---
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            Accumulate(cluster.ToLocal(roomCenter), ref min, ref max);
            for (var i = 0; i < _roomCorners.Length; i++)
                Accumulate(cluster.ToLocal(_roomCorners[i]), ref min, ref max);

            cluster.AddObservation(min, max, extentSampleCount);
            cluster.Observations++;
            cluster.CentroidSum += roomCenter;
            cluster.LastSeen = now;
            if (confidence > cluster.BestConfidence) cluster.BestConfidence = confidence;

            // --- 6. Collapse any split that has since drifted together ---
            cluster = MergeOverlapping(cluster);

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
            var raw = LocalBoundsOf(c);

            // A runaway box is almost certainly scattered depth hits, not a real object.
            var oversized = raw.size.x > maxObjectSize ||
                            raw.size.y > maxObjectSize ||
                            raw.size.z > maxObjectSize;

            var size = new Vector3(
                Mathf.Max(raw.size.x, MinBoxExtent),
                Mathf.Max(raw.size.y, MinBoxExtent),
                Mathf.Max(raw.size.z, MinBoxExtent));

            return new ClusterView(
                c.Id, c.Label, c.BestConfidence, c.Observations,
                c.ToRoom(raw.center), c.Rotation, size,
                exportable: c.Observations >= minObservations && !oversized);
        }

        // ==================================================================
        // SIZE ESTIMATION
        // ==================================================================

        /// <summary>
        /// The cluster's box in its own frame, taken as a percentile band over recent
        /// observations rather than their union. Each observation contributes a quad
        /// perpendicular to the view ray with no thickness along it, so depth jitter used
        /// to accumulate straight into the box: a 3 cm keyboard came out 60 cm tall.
        /// Trimming the tails collapses that back toward the real object.
        /// </summary>
        private Bounds LocalBoundsOf(Cluster c)
        {
            if (!c.ExtentsDirty) return c.CachedLocal;

            var lower = Vector3.zero;
            var upper = Vector3.zero;
            var trim = Mathf.Clamp01(1f - Mathf.Clamp(extentPercentile, 0.2f, 1f)) * 0.5f;

            for (var axis = 0; axis < 3; axis++)
            {
                lower[axis] = PercentileOf(c.Mins, axis, trim);
                upper[axis] = PercentileOf(c.Maxs, axis, 1f - trim);

                // With few, wildly disagreeing observations the two bands can cross.
                // Collapse to the midpoint rather than emit a negative extent.
                if (upper[axis] < lower[axis])
                {
                    var mid = (upper[axis] + lower[axis]) * 0.5f;
                    lower[axis] = mid;
                    upper[axis] = mid;
                }
            }

            var bounds = new Bounds();
            bounds.SetMinMax(lower, upper);

            c.CachedLocal = bounds;
            c.ExtentsDirty = false;
            return bounds;
        }

        private float PercentileOf(List<Vector3> samples, int axis, float t)
        {
            if (samples.Count == 0) return 0f;

            _axisScratch.Clear();
            foreach (var s in samples) _axisScratch.Add(s[axis]);
            _axisScratch.Sort();

            var index = Mathf.Clamp(Mathf.RoundToInt(t * (_axisScratch.Count - 1)), 0, _axisScratch.Count - 1);
            return _axisScratch[index];
        }

        private static void Accumulate(Vector3 point, ref Vector3 min, ref Vector3 max)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        // ==================================================================
        // MERGING
        // ==================================================================

        /// <summary>
        /// Collapses same-label clusters that turned out to be one object. FindCluster
        /// compares against a running centroid, so two clusters can be seeded apart and
        /// only later drift together -- that is how one monitor became four. Runs on the
        /// cluster just touched and returns whichever cluster survived.
        /// </summary>
        private Cluster MergeOverlapping(Cluster cluster)
        {
            _mergeScratch.Clear();

            foreach (var other in _clusters)
            {
                if (ReferenceEquals(other, cluster)) continue;
                if (!string.Equals(other.Label, cluster.Label, StringComparison.Ordinal)) continue;
                if (ShouldMerge(cluster, other)) _mergeScratch.Add(other);
            }

            foreach (var other in _mergeScratch)
            {
                // Keep the older cluster so its id, and therefore its live box, persists.
                var keep = other.Id < cluster.Id ? other : cluster;
                var drop = ReferenceEquals(keep, other) ? cluster : other;

                Absorb(keep, drop, extentSampleCount);
                _clusters.Remove(drop);
                cluster = keep;
            }

            return cluster;
        }

        private bool ShouldMerge(Cluster a, Cluster b)
        {
            var boxA = RoomBoundsOf(a);
            var boxB = RoomBoundsOf(b);

            // The same test FindCluster uses, but against the settled box centres instead
            // of the running centroid -- that alone recovers the centroid-drift splits.
            if (Vector3.Distance(boxA.center, boxB.center) < mergeRadius) return true;

            if (mergeOverlap <= 0f) return false;

            var overlap = IntersectionVolume(boxA, boxB);
            if (overlap <= 0f) return false;

            // Measured against the SMALLER box: the duplicate case is a small cluster sitting
            // inside a larger one, which a symmetric IoU would score too low to catch.
            var smaller = Mathf.Min(VolumeOf(boxA), VolumeOf(boxB));
            return smaller > 1e-6f && overlap / smaller >= mergeOverlap;
        }

        private static void Absorb(Cluster keep, Cluster drop, int capacity)
        {
            // Re-express the dropped cluster's observations in the survivor's frame, so the
            // percentile estimate keeps the evidence rather than just the resulting box.
            for (var i = 0; i < drop.Mins.Count; i++)
            {
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                for (var corner = 0; corner < 8; corner++)
                {
                    var local = CornerOf(drop.Mins[i], drop.Maxs[i], corner);
                    Accumulate(keep.ToLocal(drop.ToRoom(local)), ref min, ref max);
                }

                keep.AddObservation(min, max, capacity);
            }

            keep.Observations += drop.Observations;
            keep.CentroidSum += drop.CentroidSum;
            keep.BestConfidence = Mathf.Max(keep.BestConfidence, drop.BestConfidence);

            if (drop.FirstSeen < keep.FirstSeen) keep.FirstSeen = drop.FirstSeen;
            if (drop.LastSeen > keep.LastSeen) keep.LastSeen = drop.LastSeen;
        }

        /// <summary>The cluster's oriented box as a room-axis-aligned box, for overlap tests.</summary>
        private Bounds RoomBoundsOf(Cluster c)
        {
            var local = LocalBoundsOf(c);

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (var corner = 0; corner < 8; corner++)
                Accumulate(c.ToRoom(CornerOf(local.min, local.max, corner)), ref min, ref max);

            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }

        private static Vector3 CornerOf(Vector3 min, Vector3 max, int index)
            => new Vector3(
                (index & 1) == 0 ? min.x : max.x,
                (index & 2) == 0 ? min.y : max.y,
                (index & 4) == 0 ? min.z : max.z);

        private static float VolumeOf(Bounds b)
            => Mathf.Max(b.size.x, 0f) * Mathf.Max(b.size.y, 0f) * Mathf.Max(b.size.z, 0f);

        private static float IntersectionVolume(Bounds a, Bounds b)
        {
            var min = Vector3.Max(a.min, b.min);
            var max = Vector3.Min(a.max, b.max);
            var size = max - min;

            if (size.x <= 0f || size.y <= 0f || size.z <= 0f) return 0f;
            return size.x * size.y * size.z;
        }

        // ==================================================================
        // HELPERS
        // ==================================================================

        private bool IsIgnored(string label)
        {
            if (ignoredLabels == null || ignoredLabels.Count == 0) return false;

            _ignoredSet ??= new HashSet<string>(ignoredLabels, StringComparer.OrdinalIgnoreCase);
            return _ignoredSet.Contains(label);
        }

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

        private Cluster FindCluster(string label, Vector3 roomCenter)
        {
            Cluster best = null;
            var bestDist = mergeRadius;

            foreach (var c in _clusters)
            {
                if (c.Label != label) continue;
                var d = Vector3.Distance(c.Centroid, roomCenter);
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
