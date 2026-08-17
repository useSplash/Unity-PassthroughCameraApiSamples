using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// The controller laser and its cursor dot, built in code so the panel needs no prefab.
    ///
    /// OVRInputModule owns the raycast and calls in here through OVRCursor; this only draws.
    /// The beam is in world space because the module reports world-space hit points, and it
    /// hides itself whenever no controller is tracked -- otherwise the beam freezes at the
    /// last known pose the moment the controllers are put down or hand tracking takes over.
    /// </summary>
    public class ConvaiRoomLaserCursor : OVRCursor
    {
        [Tooltip("How far the beam is drawn when it is not hitting any UI.")]
        public float idleLength = 2.5f;

        public float beamWidth = 0.004f;
        public float cursorSize = 0.012f;
        public Color beamColor = new Color(0.45f, 0.85f, 1f, 0.85f);

        private LineRenderer _line;
        private Transform _dot;

        /// <summary>Creates the laser on its own root GameObject.</summary>
        public static ConvaiRoomLaserCursor Create()
        {
            var go = new GameObject("Convai Room Laser");
            return go.AddComponent<ConvaiRoomLaserCursor>();
        }

        private void Awake()
        {
            _line = gameObject.AddComponent<LineRenderer>();
            WireMaterial.Configure(_line, null, beamWidth);

            // Configure parks the line in local space, which is what the wireframe boxes
            // want. The input module hands this one world-space endpoints instead.
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.startColor = beamColor;
            _line.endColor = beamColor;

            _dot = BuildDot();
        }

        private Transform BuildDot()
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            dot.name = "cursor";
            dot.transform.SetParent(transform, false);
            dot.transform.localScale = Vector3.one * cursorSize;

            // The primitive ships with a collider, which would sit exactly in the path of
            // the ray it marks the end of and swallow the hits it exists to point at.
            Destroy(dot.GetComponent<Collider>());

            var renderer = dot.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = WireMaterial.Shared;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Tint through a property block: the wire material is shared with every
            // scan wireframe in the scene, so writing .color would recolour all of them.
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", beamColor);
            renderer.SetPropertyBlock(block);

            dot.AddComponent<FaceCamera>();
            return dot.transform;
        }

        public override void SetCursorRay(Transform ray)
        {
            Draw(ray.position, ray.position + ray.forward * idleLength);
        }

        public override void SetCursorStartDest(Vector3 start, Vector3 dest, Vector3 normal)
        {
            Draw(start, dest);
        }

        private void Draw(Vector3 start, Vector3 end)
        {
            if (_line == null) return;

            _line.SetPosition(0, start);
            _line.SetPosition(1, end);

            if (_dot != null) _dot.position = end;
        }

        private void LateUpdate()
        {
            var hasController = OVRInput.GetActiveController() != OVRInput.Controller.None;

            if (_line != null) _line.enabled = hasController;
            if (_dot != null) _dot.gameObject.SetActive(hasController);
        }
    }
}
