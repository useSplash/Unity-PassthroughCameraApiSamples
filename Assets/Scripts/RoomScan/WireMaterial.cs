using UnityEngine;
using UnityEngine.Rendering;

namespace RoomScan
{
    /// <summary>
    /// The one material and LineRenderer setup every wireframe in the scan pipeline shares.
    ///
    /// Sprites/Default is unlit, alpha-blended and does not write depth, which is what you
    /// want drawing over passthrough. It is also listed in this project's Always Included
    /// Shaders, so Shader.Find resolves it in a player build and not just in the editor.
    /// </summary>
    internal static class WireMaterial
    {
        private static Material _shared;

        public static Material Shared
        {
            get
            {
                if (_shared != null) return _shared;

                var shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogError("[WireMaterial] Sprites/Default not found. Assign a material " +
                                   "explicitly, or add the shader to Project Settings > Graphics > " +
                                   "Always Included Shaders.");
                    return null;
                }

                _shared = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return _shared;
            }
        }

        /// <summary>Applies the settings every wireframe LineRenderer wants.</summary>
        public static void Configure(LineRenderer line, Material material, float width)
        {
            line.useWorldSpace = false;
            line.alignment = LineAlignment.View;      // ribbons always face the headset
            line.textureMode = LineTextureMode.Stretch;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = LightProbeUsage.Off;
            line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            line.sharedMaterial = material != null ? material : Shared;
            line.widthMultiplier = Mathf.Max(width, 1e-4f);
        }
    }
}
