using System.Collections.Generic;
using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// A closed wireframe polygon of arbitrary point count, drawn with one LineRenderer.
    ///
    /// Used for MRUK surface outlines. A room floor is rarely a rectangle -- yours has ten
    /// walls -- so the shell has to be able to draw an n-gon, which WireBox cannot.
    ///
    /// Points are LOCAL, so parenting this to an MRUK anchor and feeding it the anchor's
    /// PlaneBoundary2D is all that is needed to place it.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class WireLoop : MonoBehaviour
    {
        private LineRenderer _line;
        private Vector3[] _points = System.Array.Empty<Vector3>();

        private LineRenderer Line => _line != null ? _line : (_line = GetComponent<LineRenderer>());

        /// <summary>Creates a ready-to-use loop. Pass a null material to use the built-in one.</summary>
        public static WireLoop Create(string name, Transform parent, Material material, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var loop = go.AddComponent<WireLoop>();   // RequireComponent brings the LineRenderer
            loop.Configure(material, width);
            return loop;
        }

        public void Configure(Material material, float width)
        {
            WireMaterial.Configure(Line, material, width);
            Line.loop = true;
        }

        public void SetPoints(List<Vector3> points)
        {
            var line = Line;

            if (points == null || points.Count < 2)
            {
                line.positionCount = 0;
                return;
            }

            if (_points.Length != points.Count) _points = new Vector3[points.Count];
            points.CopyTo(_points);

            line.positionCount = _points.Length;
            line.SetPositions(_points);
        }

        public void SetColor(Color color)
        {
            var line = Line;
            line.startColor = color;
            line.endColor = color;
        }

        public void SetWidth(float width) => Line.widthMultiplier = Mathf.Max(width, 1e-4f);
    }
}
