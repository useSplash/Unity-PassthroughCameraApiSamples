using System.Collections.Generic;
using RoomScan;
using UnityEngine;
using UnityEngine.AI;

namespace ConvaiRoom
{
    /// <summary>
    /// Bakes a walkable NavMesh from a replayed room scan: the MRUK floor polygon
    /// becomes walkable surface, and anything the scanner found sitting ON that floor
    /// is punched out of it.
    ///
    /// Geometry is fed to the builder as <see cref="NavMeshBuildSource"/> entries rather
    /// than physics colliders. The scan is static once replayed, so there is nothing for
    /// a collider to do that a bake cannot -- and it keeps the room free of collision
    /// geometry the passthrough app never asked for. If you later need physical
    /// collision (throwing something at the couch, say), that is a separate concern to
    /// add on top; do not repurpose this.
    /// </summary>
    public class RoomScanNavMeshBuilder : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Leave empty to find one on this GameObject.")]
        public RoomScanRebuilder rebuilder;

        [Header("Agent")]
        [Tooltip("The stock 0.5m Humanoid radius will not fit between real furniture. " +
                 "This is a half-width, so 0.25 means the character needs a 50cm gap.")]
        public float agentRadius = 0.25f;

        public float agentHeight = 1.7f;

        [Tooltip("Height the agent can step up. Keep small -- a large climb lets paths " +
                 "walk up onto low furniture.")]
        public float agentClimb = 0.2f;

        public float agentSlope = 20f;

        [Header("What counts as an obstacle")]
        [Tooltip("An object blocks the floor if its underside reaches within this distance " +
                 "of floor level (or below it). A laptop on a desk is a conversation " +
                 "target, not a wall.")]
        public float groundThreshold = 0.15f;

        [Tooltip("Objects flatter than this are stepped over, not walked around. Rugs, " +
                 "cables, and floor-plane misdetections all land here.")]
        public float minObstacleHeight = 0.1f;

        [Tooltip("Scanned walls are stored ~5cm thick, which is under one voxel at this " +
                 "agent radius and bakes unreliably. Inflated to this for the bake only.")]
        public float wallThickness = 0.2f;

        [Header("Debug")]
        public bool verboseLogging = true;

        private NavMeshDataInstance _instance;
        private readonly List<NavMeshBuildSource> _sources = new List<NavMeshBuildSource>();
        private readonly List<Mesh> _floorMeshes = new List<Mesh>();
        private readonly List<int> _triangleScratch = new List<int>();

        /// <summary>Area index 1 is Unity's built-in "Not Walkable".</summary>
        private const int NotWalkableArea = 1;

        public bool HasNavMesh => _instance.valid;

        /// <summary>Scanned objects that were punched out of the floor on the last bake.</summary>
        public int ObstacleCount { get; private set; }

        private void Awake()
        {
            if (rebuilder == null) rebuilder = GetComponent<RoomScanRebuilder>();
        }

        private void OnDisable() => Clear();

        /// <summary>
        /// Rebuilds the NavMesh from whatever the rebuilder currently holds. Safe to call
        /// repeatedly; the previous bake is discarded first.
        /// </summary>
        public bool Build()
        {
            Clear();

            if (rebuilder == null)
            {
                Debug.LogError("[RoomScanNavMeshBuilder] No RoomScanRebuilder assigned.");
                return false;
            }

            var scan = rebuilder.Scan;
            if (scan == null)
            {
                Debug.LogWarning("[RoomScanNavMeshBuilder] No scan loaded, so there is no " +
                                 "floor to bake. The character will not be able to move.");
                return false;
            }

            _sources.Clear();
            ObstacleCount = 0;

            var bounds = new Bounds();
            var hasBounds = false;

            if (!TryAddFloor(scan, ref bounds, ref hasBounds))
            {
                Debug.LogError("[RoomScanNavMeshBuilder] Could not derive a floor from MRUK " +
                               "or from the scan shell. Nothing to bake.");
                return false;
            }

            AddWalls(scan, ref bounds, ref hasBounds);
            AddGroundObstacles(scan, ref bounds, ref hasBounds);

            // Give the builder headroom above the floor, or a tall agent gets clipped by
            // the volume it is being built inside.
            bounds.Expand(new Vector3(agentRadius * 4f, agentHeight * 2f, agentRadius * 4f));

            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentClimb = agentClimb;
            settings.agentSlope = agentSlope;

            var data = NavMeshBuilder.BuildNavMeshData(
                settings, _sources, bounds, Vector3.zero, Quaternion.identity);

            if (data == null)
            {
                Debug.LogError("[RoomScanNavMeshBuilder] BuildNavMeshData returned nothing.");
                return false;
            }

            _instance = NavMesh.AddNavMeshData(data);

            if (verboseLogging)
            {
                Debug.Log($"[RoomScanNavMeshBuilder] Baked from {_sources.Count} sources " +
                          $"({ObstacleCount} floor obstacles) over {bounds.size.x:F1}x" +
                          $"{bounds.size.z:F1}m. Valid: {_instance.valid}");
            }

            return _instance.valid;
        }

        public void Clear()
        {
            if (_instance.valid) _instance.Remove();
            _instance = default;

            foreach (var mesh in _floorMeshes)
                if (mesh != null) Destroy(mesh);

            _floorMeshes.Clear();
            ObstacleCount = 0;
        }

        // -----------------------------------------------------------------
        // Floor
        // -----------------------------------------------------------------

        /// <summary>
        /// Prefers MRUK's real floor polygon, which is the only source that knows the
        /// room is L-shaped. Falls back to a rectangle derived from the scan's own wall
        /// records so a scan captured without Space Setup still produces something.
        /// </summary>
        private bool TryAddFloor(RoomScanFile scan, ref Bounds bounds, ref bool hasBounds)
        {
            var room = rebuilder.Room;
            var added = false;

            if (room != null)
            {
                foreach (var floor in room.FloorAnchors)
                    if (TryAddFloorAnchor(floor, ref bounds, ref hasBounds)) added = true;
            }

            if (added) return true;

            if (verboseLogging)
            {
                Debug.Log("[RoomScanNavMeshBuilder] No usable MRUK floor anchor; falling back " +
                          "to a rectangle from the scan's wall records.");
            }

            return TryAddFallbackFloor(scan, ref bounds, ref hasBounds);
        }

        private bool TryAddFloorAnchor(Meta.XR.MRUtilityKit.MRUKAnchor anchor,
                                       ref Bounds bounds, ref bool hasBounds)
        {
            if (anchor == null) return false;

            // Same convention the shell visualiser uses: plane-local XY, +Z along the normal.
            var outline = new List<Vector2>();
            var boundary = anchor.PlaneBoundary2D;

            if (boundary != null && boundary.Count >= 3)
            {
                outline.AddRange(boundary);
            }
            else if (anchor.PlaneRect.HasValue)
            {
                var r = anchor.PlaneRect.Value;
                outline.Add(new Vector2(r.xMin, r.yMin));
                outline.Add(new Vector2(r.xMax, r.yMin));
                outline.Add(new Vector2(r.xMax, r.yMax));
                outline.Add(new Vector2(r.xMin, r.yMax));
            }
            else
            {
                return false;
            }

            if (!TryTriangulate(outline, _triangleScratch)) return false;

            var mesh = new Mesh { name = "RoomScan Floor" };
            var vertices = new Vector3[outline.Count];
            for (var i = 0; i < outline.Count; i++)
                vertices[i] = new Vector3(outline[i].x, outline[i].y, 0f);

            var localToWorld = anchor.transform.localToWorldMatrix;
            EnsureUpwardWinding(vertices, _triangleScratch, localToWorld);

            mesh.vertices = vertices;
            mesh.triangles = _triangleScratch.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            _floorMeshes.Add(mesh);

            _sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform = localToWorld,
                area = 0
            });

            Encapsulate(ref bounds, ref hasBounds,
                        localToWorld.MultiplyPoint3x4(mesh.bounds.center), mesh.bounds.size);

            return true;
        }

        /// <summary>
        /// A rectangle spanning the scanned walls, at the recorded floor height. Crude by
        /// design -- it exists so a room without Space Setup still gets a walkable plane.
        /// </summary>
        private bool TryAddFallbackFloor(RoomScanFile scan, ref Bounds bounds, ref bool hasBounds)
        {
            if (scan.room == null || scan.room.walls == null || scan.room.walls.Count == 0)
                return false;

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);

            foreach (var wall in scan.room.walls)
            {
                var c = wall.center.ToVector3();
                var halfWidth = wall.size.ToVector3().x * 0.5f;

                // The wall's local right axis spans its width; project that onto the floor.
                var right = wall.rotation.ToQuaternion() * Vector3.right * halfWidth;

                foreach (var end in new[] { c + right, c - right })
                {
                    min = Vector2.Min(min, new Vector2(end.x, end.z));
                    max = Vector2.Max(max, new Vector2(end.x, end.z));
                }
            }

            if (max.x <= min.x || max.y <= min.y) return false;

            var centerLocal = new Vector3((min.x + max.x) * 0.5f, scan.room.floorY,
                                          (min.y + max.y) * 0.5f);
            var size = new Vector3(max.x - min.x, 0.05f, max.y - min.y);

            var worldCenter = rebuilder.RoomToWorld(centerLocal);
            var worldRot = rebuilder.RoomToWorld(Quaternion.identity);

            _sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = size,
                transform = Matrix4x4.TRS(worldCenter, worldRot, Vector3.one),
                area = 0
            });

            Encapsulate(ref bounds, ref hasBounds, worldCenter, size);
            return true;
        }

        // -----------------------------------------------------------------
        // Obstacles
        // -----------------------------------------------------------------

        private void AddWalls(RoomScanFile scan, ref Bounds bounds, ref bool hasBounds)
        {
            if (scan.room?.walls == null) return;

            foreach (var wall in scan.room.walls)
            {
                var size = wall.size.ToVector3();
                if (size.x <= 0f || size.y <= 0f) continue;

                size.z = Mathf.Max(size.z, wallThickness);

                var worldCenter = rebuilder.RoomToWorld(wall.center.ToVector3());
                var worldRot = rebuilder.RoomToWorld(wall.rotation.ToQuaternion());

                _sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = size,
                    transform = Matrix4x4.TRS(worldCenter, worldRot, Vector3.one),
                    area = NotWalkableArea
                });

                Encapsulate(ref bounds, ref hasBounds, worldCenter, size);
            }
        }

        /// <summary>
        /// Only objects actually resting on the floor become holes. Everything else stays
        /// a conversation target but leaves the floor walkable underneath it.
        /// </summary>
        private void AddGroundObstacles(RoomScanFile scan, ref Bounds bounds, ref bool hasBounds)
        {
            var floorY = scan.room?.floorY ?? 0f;

            foreach (var entry in rebuilder.Rebuilt)
            {
                var obj = entry.Data;
                if (obj?.position == null || obj.size == null) continue;

                var size = obj.size.ToVector3();
                if (size.y < minObstacleHeight) continue;
                if (!IsOnFloor(obj, floorY, groundThreshold)) continue;

                var worldCenter = rebuilder.RoomToWorld(obj.position.ToVector3());
                var worldRot = rebuilder.RoomToWorld(obj.rotation.ToQuaternion());

                _sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = size,
                    transform = Matrix4x4.TRS(worldCenter, worldRot, Vector3.one),
                    area = NotWalkableArea
                });

                Encapsulate(ref bounds, ref hasBounds, worldCenter, size);
                ObstacleCount++;
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Whether a scanned object blocks the floor rather than sitting on something else.
        /// Both values are room-local, so this needs no transform.
        ///
        /// The test is "does the box reach down to floor level", not "is its underside
        /// exactly at floor level". Estimated extents routinely overshoot downward -- in a
        /// real scan four of five chairs had undersides 0.2-0.6 m BELOW the floor plane --
        /// and a symmetric near-the-floor test rejects every one of them, letting the
        /// character path straight through the furniture. Anything reaching the floor is
        /// on the floor, however far it overshoots.
        ///
        /// Shared with the action-config builder so a thing the character is told is
        /// "standing on the floor" is exactly a thing it has to walk around.
        /// </summary>
        public static bool IsOnFloor(ScannedObject obj, float floorY, float threshold)
        {
            if (obj?.position == null || obj.size == null) return false;

            var underside = obj.position.ToVector3().y - obj.size.ToVector3().y * 0.5f;
            return underside <= floorY + threshold;
        }

        private static void Encapsulate(ref Bounds bounds, ref bool hasBounds,
                                        Vector3 center, Vector3 size)
        {
            // A rotated box's world extent is larger than its local size, so use the
            // diagonal. Overshooting the build volume is harmless; undershooting clips it.
            var radius = size.magnitude * 0.5f;
            var padded = new Bounds(center, Vector3.one * (radius * 2f));

            if (!hasBounds)
            {
                bounds = padded;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(padded);
            }
        }

        /// <summary>
        /// Flips triangle winding if the polygon came out facing down. Deriving the
        /// correct winding from the boundary's own order means reasoning about MRUK's
        /// plane convention and Unity's handedness at once; checking the result instead
        /// is shorter and cannot be subtly wrong.
        /// </summary>
        private static void EnsureUpwardWinding(Vector3[] vertices, List<int> triangles,
                                                Matrix4x4 localToWorld)
        {
            if (triangles.Count < 3) return;

            var a = localToWorld.MultiplyPoint3x4(vertices[triangles[0]]);
            var b = localToWorld.MultiplyPoint3x4(vertices[triangles[1]]);
            var c = localToWorld.MultiplyPoint3x4(vertices[triangles[2]]);

            var normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, Vector3.up) >= 0f) return;

            for (var i = 0; i < triangles.Count; i += 3)
                (triangles[i], triangles[i + 2]) = (triangles[i + 2], triangles[i]);
        }

        /// <summary>
        /// Ear-clipping triangulation of a simple polygon. Rooms are frequently L-shaped,
        /// so a triangle fan would invent walkable floor where a wall stands.
        /// </summary>
        private static bool TryTriangulate(IReadOnlyList<Vector2> polygon, List<int> triangles)
        {
            triangles.Clear();

            var n = polygon.Count;
            if (n < 3) return false;

            // Index ring, normalised to counter-clockwise so the ear test has one meaning.
            var remaining = new List<int>(n);
            if (SignedArea(polygon) >= 0f)
                for (var i = 0; i < n; i++) remaining.Add(i);
            else
                for (var i = n - 1; i >= 0; i--) remaining.Add(i);

            var guard = n * n;

            while (remaining.Count > 2 && guard-- > 0)
            {
                var clipped = false;

                for (var i = 0; i < remaining.Count; i++)
                {
                    var count = remaining.Count;
                    var i0 = remaining[(i + count - 1) % count];
                    var i1 = remaining[i];
                    var i2 = remaining[(i + 1) % count];

                    if (!IsEar(polygon, remaining, i0, i1, i2)) continue;

                    triangles.Add(i0);
                    triangles.Add(i1);
                    triangles.Add(i2);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }

                // Self-intersecting or degenerate outline. Keep whatever was clipped --
                // a partial floor beats no floor at all.
                if (!clipped) break;
            }

            return triangles.Count >= 3;
        }

        private static float SignedArea(IReadOnlyList<Vector2> polygon)
        {
            var area = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.x * b.y - b.x * a.y;
            }

            return area * 0.5f;
        }

        private static bool IsEar(IReadOnlyList<Vector2> polygon, List<int> remaining,
                                  int i0, int i1, int i2)
        {
            var a = polygon[i0];
            var b = polygon[i1];
            var c = polygon[i2];

            // Reflex corners cannot be ears.
            if (Cross(a, b, c) <= 0f) return false;

            foreach (var index in remaining)
            {
                if (index == i0 || index == i1 || index == i2) continue;
                if (PointInTriangle(polygon[index], a, b, c)) return false;
            }

            return true;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = Cross(a, b, p);
            var d2 = Cross(b, c, p);
            var d3 = Cross(c, a, p);

            var hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            var hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;

            return !(hasNegative && hasPositive);
        }
    }
}
