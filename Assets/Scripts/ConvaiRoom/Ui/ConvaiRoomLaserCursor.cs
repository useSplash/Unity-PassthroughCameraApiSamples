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

        [Tooltip("Draw order against the rest of the scan visuals. The scan wireframes sit at " +
                 "0 and the panel canvas at 100, so the beam and its dot go above both.\n\n" +
                 "It has to be above the panel, not just above the boxes: the dot marks where " +
                 "you are pointing ON the panel, and a dot sorted underneath it is invisible " +
                 "exactly when it matters. Everything here is alpha-blended and writes no " +
                 "depth, so draw order is what decides this rather than distance.")]
        public int sortingOrder = 200;

        private LineRenderer _line;
        private Transform _dot;

        /// <summary>
        /// When the input module last pushed a pose into us. The beam is shown only while
        /// this is fresh.
        ///
        /// This replaces an earlier check on OVRInput.GetActiveController(), which was wrong
        /// in both directions: it hid the beam whenever hand tracking was driving the pointer
        /// (hands are not a "controller"), and it kept the beam visible whenever a controller
        /// was merely connected, even if nothing was actually driving the cursor. Keying off
        /// the drive itself works for controllers and hands alike, and still parks the beam
        /// when the pointer genuinely stops.
        /// </summary>
        private float _lastDrivenTime = float.NegativeInfinity;

        [Tooltip("How long the beam keeps drawing after the last pose it was given. Covers " +
                 "the odd dropped frame without freezing a stale beam in mid-air.")]
        public float driveTimeout = 0.25f;

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
            _line.sortingOrder = sortingOrder;

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
            renderer.sortingOrder = sortingOrder;

            // Tint through a property block: the wire material is shared with every
            // scan wireframe in the scene, so writing .color would recolour all of them.
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", beamColor);
            renderer.SetPropertyBlock(block);

            dot.AddComponent<FaceCamera>();
            return dot.transform;
        }

        /// <summary>
        /// Keeps the ray transform rather than the point it was at, so LateUpdate can read the
        /// pose again after everything else has moved this frame.
        ///
        /// The input module calls in here from Process(), which runs on EventSystem's Update.
        /// Nothing orders that against OVRHand's Update, and OVRHand is what writes the hand's
        /// PointerPose. Draw straight from the value handed over and roughly half the time it
        /// is last frame's pose -- which sits still when your hand does and lags behind it when
        /// it moves. Re-reading the transform in LateUpdate is always the freshest pose
        /// available, whatever order the Updates ran in.
        ///
        /// Controllers never showed this because OVRControllerHelper's ray transform is its own
        /// GameObject under the camera rig, and the rig updates its anchors before LateUpdate
        /// either way.
        /// </summary>
        private Transform _ray;

        // Set when the module reports an actual UI hit this frame, which is a real endpoint
        // rather than the arbitrary idle length.
        private bool _hitThisFrame;
        private Vector3 _hitEnd;

        public override void SetCursorRay(Transform ray)
        {
            _ray = ray;
            _lastDrivenTime = Time.unscaledTime;
        }

        public override void SetCursorStartDest(Vector3 start, Vector3 dest, Vector3 normal)
        {
            _hitThisFrame = true;
            _hitEnd = dest;
            _lastDrivenTime = Time.unscaledTime;

            // start is deliberately dropped. It is the ray origin as it was when the raycast
            // ran, and the whole point here is to take that from the transform instead.
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
            var driven = Time.unscaledTime - _lastDrivenTime <= driveTimeout;

            if (driven && _ray != null)
            {
                // The endpoint from a hit is one frame old at worst, which costs a little
                // angular accuracy at the far end. The origin is what has to be right: a beam
                // that does not start at your hand reads as broken, one whose far end is a
                // few millimetres out does not.
                var start = _ray.position;
                Draw(start, _hitThisFrame ? _hitEnd : start + _ray.forward * idleLength);
            }

            _hitThisFrame = false;

            if (_line != null) _line.enabled = driven;
            if (_dot != null) _dot.gameObject.SetActive(driven);
        }
    }
}
