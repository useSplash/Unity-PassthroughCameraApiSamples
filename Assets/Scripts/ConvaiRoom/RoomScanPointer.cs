using Convai.Runtime;
using Convai.Runtime.Actions;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Lets you point at something and then talk about it as "that".
    ///
    /// Names alone cannot carry a room. Four chairs are four chairs however carefully they are
    /// named, and the moment you want the one you are looking at rather than the one by the
    /// couch, the only honest way to say which is to point at it.
    ///
    /// Convai has a first-class place to put that: the current attention object, which is what
    /// the backend resolves "that", "there" and "it" against. Set it to the thing under your ray
    /// and ordinary speech starts working -- "go to that one", "what is that?", "sit over there"
    /// -- with no new vocabulary on either side of the conversation.
    ///
    /// Aim is continuous and settles rather than being pressed. There is no button because there
    /// is no hand free to press one: the whole point is to be pointing at a thing while saying a
    /// sentence about it. <see cref="dwellSeconds"/> is what stops a ray sweeping across the room
    /// on the way to somewhere from renaming "that" four times as it passes.
    ///
    /// Attention is deliberately never cleared by looking away. You point, you lower your hand,
    /// and you say the sentence -- an attention object that expired the moment your arm dropped
    /// would be a feature you could never actually use.
    ///
    /// The boxes are hit with maths rather than physics. They are LineRenderers with no colliders
    /// and the navmesh is baked from the scan data rather than from the scene, so there is no
    /// physics in this room at all -- adding some, and a layer to keep it off the character's
    /// agent and the hand rays, would be a lot of scene surface for a ray against forty boxes.
    /// </summary>
    public class RoomScanPointer : MonoBehaviour
    {
        private const string Tag = "[RoomPointer]";

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Supplies the boxes to point at.")]
        public RoomScanRebuilder rebuilder;

        [Tooltip("Supplies the character whose attention this sets. Nothing is sent until she " +
                 "is connected and ready.")]
        public RoomCharacterVoice voice;

        [Header("Aiming")]
        [Tooltip("Where the ray comes from. Left empty, the right hand is used -- the hand's own " +
                 "pointer pose under hand tracking, the controller anchor otherwise.")]
        public Transform rayOrigin;

        [Tooltip("How far the ray reaches, in metres. Beyond this, pointing at the far wall of a " +
                 "large room stops picking up furniture behind it.")]
        public float maxDistance = 8f;

        [Tooltip("How much to grow each box when testing the ray against it, in metres.\n\n" +
                 "A scan box is a wireframe a centimetre thick around a chair, and holding a ray " +
                 "steady enough to intersect one is not something anyone should have to do. This " +
                 "is the difference between pointing at a chair and threading a needle.")]
        public float aimPadding = 0.08f;

        [Tooltip("How long the ray has to rest on something before it becomes 'that'. Stops a " +
                 "ray crossing the room on its way somewhere from claiming everything it passes.")]
        public float dwellSeconds = 0.3f;

        [Header("Feedback")]
        [Tooltip("Recolour the box being pointed at. This is the only way to know what 'that' " +
                 "currently means, so switching it off makes the feature blind.")]
        public bool highlightAimed = true;

        public Color highlightColor = new Color(1f, 0.85f, 0.3f, 1f);

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>What "that" currently means, or null when nothing has been pointed at.</summary>
        public string AttentionName { get; private set; }

        /// <summary>The box the ray is resting on right now, which may not have settled yet.</summary>
        private GameObject _candidate;
        private float _candidateSince;

        /// <summary>The box that actually won, and the colour it had before it was highlighted.</summary>
        private GameObject _committed;
        private WireBox _highlighted;
        private Color _highlightedWas;

        private OVRCameraRig _rig;

        /// <summary>Seconds between re-scans for hand components. See ActiveHandPointer.</summary>
        private const float HandSearchInterval = 2f;

        private OVRHand[] _hands;
        private float _nextHandSearch;

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (voice == null) voice = FindAnyObjectByType<RoomCharacterVoice>();

            if (rebuilder == null)
                Debug.LogError($"{Tag} No RoomScanRebuilder in the scene, so there are no boxes " +
                               $"to point at.", this);
        }

        private void OnDisable() => ClearHighlight();

        private void Update()
        {
            // Only while there is someone to tell. Outside the character phase this would be
            // recolouring the scan's own boxes for no reason, on top of a per-frame cost nothing
            // is asking for.
            if (rebuilder == null || voice == null || voice.Character == null)
            {
                if (_committed != null) Forget();
                return;
            }

            var ray = BuildRay();
            if (!ray.HasValue) return;

            var aimed = FindAimed(ray.Value);

            if (aimed != _candidate)
            {
                _candidate = aimed;
                _candidateSince = Time.unscaledTime;
            }

            // Only ever committed to something. Losing the ray is not a decision to forget what
            // "that" meant -- see the class summary.
            if (aimed == null || aimed == _committed) return;
            if (Time.unscaledTime - _candidateSince < dwellSeconds) return;

            Commit(aimed);
        }

        // -----------------------------------------------------------------
        // Aiming
        // -----------------------------------------------------------------

        /// <summary>
        /// Where the ray comes from, preferring whatever the player is actually pointing with.
        ///
        /// Hand tracking gets its own pose rather than the controller anchor: under hands that
        /// anchor sits at the wrist and faces wherever the wrist faces, which is not where anyone
        /// thinks they are pointing. OVRHand publishes the aim ray the system itself uses for
        /// hand rays, so pointing agrees with the beam the player can see.
        /// </summary>
        private Ray? BuildRay()
        {
            if (rayOrigin != null)
                return new Ray(rayOrigin.position, rayOrigin.forward);

            var hand = ActiveHandPointer();
            if (hand != null) return new Ray(hand.position, hand.forward);

            if (_rig == null) _rig = FindAnyObjectByType<OVRCameraRig>();

            if (_rig != null)
            {
                var anchor = _rig.rightHandAnchor != null ? _rig.rightHandAnchor : _rig.leftHandAnchor;
                if (anchor != null) return new Ray(anchor.position, anchor.forward);
                if (_rig.centerEyeAnchor != null)
                    return new Ray(_rig.centerEyeAnchor.position, _rig.centerEyeAnchor.forward);
            }

            // Gaze, as the last resort. Worse than a hand -- you cannot look at her and point at
            // a chair at the same time -- but better than the feature silently doing nothing on a
            // rig this did not expect.
            var head = Camera.main;
            return head != null ? new Ray(head.transform.position, head.transform.forward) : (Ray?)null;
        }

        /// <summary>
        /// The pointer pose of whichever hand is currently tracked, or null on controllers.
        ///
        /// The hands are cached rather than searched every frame -- this runs in Update and a
        /// scene-wide type search per frame is a real cost on a headset. The cache is refreshed
        /// on a slow timer instead of once, because the hand rig is built by a Meta building
        /// block and may not exist on the frame this first looks.
        /// </summary>
        private Transform ActiveHandPointer()
        {
            if (_hands == null || _hands.Length == 0 || Time.unscaledTime >= _nextHandSearch)
            {
                _hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude);
                _nextHandSearch = Time.unscaledTime + HandSearchInterval;
            }

            foreach (var hand in _hands)
            {
                if (hand != null && hand.IsTracked && hand.IsPointerPoseValid)
                    return hand.PointerPose;
            }

            return null;
        }

        /// <summary>The nearest box the ray passes through, or null.</summary>
        private GameObject FindAimed(Ray ray)
        {
            GameObject best = null;
            var bestDistance = float.PositiveInfinity;

            foreach (var entry in rebuilder.Rebuilt)
            {
                if (entry.Proxy == null || entry.Data == null) continue;

                var size = entry.Data.size.ToVector3() + Vector3.one * (aimPadding * 2f);

                if (!RayHitsBox(ray, entry.Proxy.transform.position, entry.Proxy.transform.rotation,
                                size, out var distance))
                    continue;

                if (distance > maxDistance || distance >= bestDistance) continue;

                bestDistance = distance;
                best = entry.Proxy;
            }

            return best;
        }

        /// <summary>
        /// Slab test against one oriented box.
        ///
        /// The ray is taken into the box's own space rather than the box being turned into world
        /// axes, because the boxes are rotated to match the furniture they wrap and an
        /// axis-aligned test against a chair at forty degrees would claim a volume half again
        /// its size.
        /// </summary>
        private static bool RayHitsBox(Ray ray, Vector3 centre, Quaternion rotation, Vector3 size,
                                       out float distance)
        {
            distance = 0f;

            var inverse = Quaternion.Inverse(rotation);
            var origin = inverse * (ray.origin - centre);
            var direction = inverse * ray.direction;
            var extents = size * 0.5f;

            var near = 0f;
            var far = float.PositiveInfinity;

            for (var axis = 0; axis < 3; axis++)
            {
                var o = origin[axis];
                var d = direction[axis];
                var e = Mathf.Max(extents[axis], 1e-4f);

                // Parallel to this pair of faces: either inside the slab for the whole ray, or
                // outside it for the whole ray, and dividing by d would be an infinity.
                if (Mathf.Abs(d) < 1e-6f)
                {
                    if (o < -e || o > e) return false;
                    continue;
                }

                var first = (-e - o) / d;
                var second = (e - o) / d;
                if (first > second) (first, second) = (second, first);

                near = Mathf.Max(near, first);
                far = Mathf.Min(far, second);

                if (near > far) return false;
            }

            distance = near;
            return true;
        }

        // -----------------------------------------------------------------
        // Attention
        // -----------------------------------------------------------------

        private void Commit(GameObject aimed)
        {
            var name = TargetName(aimed);
            if (string.IsNullOrEmpty(name)) return;

            _committed = aimed;
            AttentionName = name;
            Highlight(aimed);

            var character = voice.Character;
            if (character == null || !character.IsInConversation) return;

            // Silent: pointing at something is not a remark about it. She should know what you
            // mean by "that" without announcing that she noticed you point.
            character.DynamicContext.SetCurrentAttentionObject(name, ConvaiRespondMode.Silent);

            if (verboseLogging) Debug.Log($"{Tag} Attention -> '{name}'");
        }

        /// <summary>
        /// What the backend knows this box as.
        ///
        /// Read off the box rather than looked up in RoomScanContext, so this stays correct by
        /// construction: the name sent as attention is literally the name registered as a walk
        /// target, because it is the same field on the same component.
        /// </summary>
        private static string TargetName(GameObject proxy)
        {
            if (proxy != null && proxy.TryGetComponent<ConvaiActionTarget>(out var target))
                return target.TargetName;

            return null;
        }

        private void Forget()
        {
            _committed = null;
            _candidate = null;
            AttentionName = null;
            ClearHighlight();
        }

        // -----------------------------------------------------------------
        // Feedback
        // -----------------------------------------------------------------

        private void Highlight(GameObject proxy)
        {
            ClearHighlight();

            if (!highlightAimed || proxy == null) return;
            if (!proxy.TryGetComponent<WireBox>(out var box)) return;

            // The colour is read back from the renderer rather than assumed, so a restore puts
            // back whatever the box actually had -- the rebuilder's colour today, something else
            // if the boxes are ever styled per object.
            var line = box.GetComponent<LineRenderer>();
            _highlightedWas = line != null ? line.startColor : Color.white;
            _highlighted = box;

            box.SetColor(highlightColor);
        }

        private void ClearHighlight()
        {
            if (_highlighted == null)
            {
                _highlighted = null;
                return;
            }

            _highlighted.SetColor(_highlightedWas);
            _highlighted = null;
        }
    }
}
