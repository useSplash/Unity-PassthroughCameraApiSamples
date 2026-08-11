using UnityEngine;
using UnityEngine.Rendering;

namespace RoomScan
{
    /// <summary>
    /// A twelve-edge wireframe box drawn with a single LineRenderer.
    ///
    /// Solid cubes are useless over passthrough -- they hide the very object you
    /// are trying to look at -- so everything the scan pipeline shows is an outline.
    ///
    /// The transform is deliberately never scaled: extents are baked into the
    /// LineRenderer's local points instead. Scaling the transform would scale the
    /// line WIDTH with it, so a wide flat box (a table, say) would come out with
    /// wildly uneven edges.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class WireBox : MonoBehaviour
    {
        /// <summary>
        /// One continuous stroke covering all 12 edges of a unit cube. Four edges get
        /// retraced -- unavoidable, since every corner of a cube has odd degree -- and
        /// costs nothing visually because the retrace lands exactly on the first pass.
        /// </summary>
        private static readonly Vector3[] UnitPath =
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
        };

        private static Material _sharedMaterial;

        private LineRenderer _line;
        private readonly Vector3[] _points = new Vector3[UnitPath.Length];

        private LineRenderer Line => _line != null ? _line : (_line = GetComponent<LineRenderer>());

        /// <summary>Full extents in metres, matching the size the exporter writes.</summary>
        public Vector3 Size { get; private set; } = Vector3.one;

        /// <summary>Creates a ready-to-use wire box. Pass a null material to use the built-in one.</summary>
        public static WireBox Create(string name, Transform parent, Material material, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var box = go.AddComponent<WireBox>();   // RequireComponent brings the LineRenderer
            box.Configure(material, width);
            return box;
        }

        public void Configure(Material material, float width)
        {
            var line = Line;
            line.useWorldSpace = false;
            line.loop = false;
            line.alignment = LineAlignment.View;    // ribbons always face the headset
            line.textureMode = LineTextureMode.Stretch;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = LightProbeUsage.Off;
            line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            line.positionCount = UnitPath.Length;
            line.sharedMaterial = material != null ? material : SharedMaterial;

            SetWidth(width);
            SetSize(Size);
        }

        public void SetSize(Vector3 size)
        {
            Size = size;
            for (var i = 0; i < UnitPath.Length; i++)
                _points[i] = Vector3.Scale(UnitPath[i], size);

            Line.SetPositions(_points);
        }

        public void SetColor(Color color)
        {
            var line = Line;
            line.startColor = color;
            line.endColor = color;
        }

        public void SetWidth(float width) => Line.widthMultiplier = Mathf.Max(width, 1e-4f);

        /// <summary>
        /// Sprites/Default is unlit, alpha-blended and depth-write-free, which is what
        /// you want over passthrough. It is also listed in this project's Always Included
        /// Shaders, so Shader.Find resolves it in a player build and not just in the editor.
        /// </summary>
        private static Material SharedMaterial
        {
            get
            {
                if (_sharedMaterial != null) return _sharedMaterial;

                var shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogError("[WireBox] Sprites/Default not found. Assign a material " +
                                   "explicitly, or add the shader to Project Settings > " +
                                   "Graphics > Always Included Shaders.");
                    return null;
                }

                _sharedMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return _sharedMaterial;
            }
        }
    }
}
