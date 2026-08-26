using RoomScan;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace ConvaiRoom
{
    /// <summary>
    /// Draws the baked NavMesh as a wireframe you can look at in the headset.
    ///
    /// This exists because there is no Navigation window on a Quest. A bake either happened
    /// or it did not, and "valid: true" in logcat says nothing about whether the floor came
    /// out the right shape, whether the couch got punched out, or whether the whole thing
    /// landed a metre to the left. Seeing it is the only way to know.
    ///
    /// One mesh with <see cref="MeshTopology.Lines"/> rather than a LineRenderer per triangle:
    /// a room bakes to tens or low hundreds of triangles, and that many LineRenderers is a
    /// draw call each on hardware that cannot spare them.
    /// </summary>
    public class NavMeshVisualizer : MonoBehaviour
    {
        private const string Tag = "[NavMeshVis]";

        [Tooltip("Colour of the navmesh wireframe.")]
        public Color meshColor = new Color(0.4f, 1f, 0.5f, 0.85f);

        [Tooltip("Lifts the wireframe off the floor so it reads as a surface rather than " +
                 "flickering against one. The navmesh already sits slightly proud of the " +
                 "geometry it was baked from, so this only needs to be small.")]
        public float heightOffset = 0.01f;

        [Tooltip("Above the scan wireframes at 0, well below the panel at 100. Everything " +
                 "here is alpha-blended and writes no depth, so draw order decides this.")]
        public int sortingOrder = 10;

        [Tooltip("Show the wireframe as soon as something bakes.")]
        public bool showOnBake = true;

        private GameObject _go;
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;

        /// <summary>Triangles in the last navmesh this drew. Zero when nothing is baked.</summary>
        public int TriangleCount { get; private set; }

        public bool IsShowing => _go != null && _go.activeSelf;

        /// <summary>
        /// Re-reads the navmesh and rebuilds the wireframe. Call after every bake --
        /// CalculateTriangulation is a snapshot, not a live view.
        /// </summary>
        public void Refresh()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            TriangleCount = triangulation.indices.Length / 3;

            if (TriangleCount == 0)
            {
                Debug.LogWarning($"{Tag} Nothing to draw -- no navmesh is loaded.");
                Clear();
                return;
            }

            EnsureObject();
            BuildLineMesh(triangulation);

            if (showOnBake) Show();

            Debug.Log($"{Tag} Drawing {TriangleCount} navmesh triangles " +
                      $"({triangulation.vertices.Length} vertices).");
        }

        public void Show()
        {
            if (_go != null) _go.SetActive(true);
        }

        public void Hide()
        {
            if (_go != null) _go.SetActive(false);
        }

        public void Toggle()
        {
            if (_go == null) return;
            _go.SetActive(!_go.activeSelf);
        }

        /// <summary>Drops the wireframe entirely. The navmesh itself is untouched.</summary>
        public void Clear()
        {
            TriangleCount = 0;
            if (_mesh != null) _mesh.Clear();
            Hide();
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        private void EnsureObject()
        {
            if (_go != null) return;

            // Its own root object, not a child of whatever this component sits on. The
            // navmesh triangulation is already in world space, so any parent transform that
            // is not identity would shift the drawing off the mesh it is meant to trace.
            _go = new GameObject("NavMesh Wireframe");

            _filter = _go.AddComponent<MeshFilter>();
            _renderer = _go.AddComponent<MeshRenderer>();

            _mesh = new Mesh { name = "NavMesh Wireframe" };

            // A room can bake past 65k vertices once the floor is finely triangulated.
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _filter.sharedMesh = _mesh;

            _renderer.sharedMaterial = WireMaterial.Shared;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.sortingOrder = sortingOrder;

            // Tint through a property block: the wire material is shared with every scan
            // wireframe in the scene, so writing .color would recolour all of them.
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", meshColor);
            _renderer.SetPropertyBlock(block);
        }

        /// <summary>
        /// Turns the triangulation into line segments -- three edges per triangle.
        ///
        /// Interior edges get drawn twice, once for each triangle that shares them. Left that
        /// way on purpose: de-duplicating means hashing every edge, and at room scale the
        /// duplicates cost less than the pass that would remove them.
        /// </summary>
        private void BuildLineMesh(NavMeshTriangulation triangulation)
        {
            var source = triangulation.vertices;
            var indices = triangulation.indices;

            var vertices = new Vector3[source.Length];
            var lift = Vector3.up * heightOffset;

            for (var i = 0; i < source.Length; i++)
                vertices[i] = source[i] + lift;

            var lines = new int[indices.Length * 2];
            var next = 0;

            for (var t = 0; t < indices.Length; t += 3)
            {
                var a = indices[t];
                var b = indices[t + 1];
                var c = indices[t + 2];

                lines[next++] = a; lines[next++] = b;
                lines[next++] = b; lines[next++] = c;
                lines[next++] = c; lines[next++] = a;
            }

            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.SetIndices(lines, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
        }
    }
}
