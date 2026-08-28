using UnityEngine;
using UnityEngine.EventSystems;

namespace ConvaiRoom
{
    /// <summary>
    /// Lets you drag the panel around with the pointer ray, replacing the RECENTER button.
    ///
    /// Sits on the panel's background image rather than on the canvas root, and that is the
    /// whole design. Drag events bubble up from whatever was hit, so putting this on the root
    /// would make every button draggable too -- and with a ray that jitters, a press that
    /// creeps past the drag threshold becomes a drag instead of a click. On the background it
    /// picks up only the parts of the panel with nothing on top: the title bar and the readout.
    /// The panel gets a title bar to drag by, and the buttons keep their presses.
    ///
    /// The panel is grabbed at a distance rather than snapped to the ray. Whatever offset and
    /// range it had when you grabbed it is what it keeps, so it does not leap to the cursor the
    /// moment you take hold of it.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class ConvaiRoomPanelDragger : MonoBehaviour,
                                          IBeginDragHandler, IDragHandler, IEndDragHandler,
                                          IPointerDownHandler, IPointerUpHandler,
                                          IInitializePotentialDragHandler
    {
        private const string Tag = "[ScanPanel]";

        [Tooltip("What actually moves. Left empty, this finds the ConvaiRoomModePanel above " +
                 "it -- the panel owns its transform, and the canvas is only a child of it.")]
        public Transform target;

        [Tooltip("Closest the panel can be dragged. Stops it being shoved into your face.")]
        public float minDistance = 0.4f;

        [Tooltip("Furthest the panel can be dragged. This is the one that matters: without a " +
                 "RECENTER button there is no way to call the panel back, so it must never be " +
                 "draggable out of arm's reach in the first place.")]
        public float maxDistance = 2.5f;

        [Tooltip("Keep the panel turned toward you while it moves. Off, it holds the facing it " +
                 "had when grabbed and you can drag it edge-on to yourself.")]
        public bool facePlayerWhileDragging = true;

        [Tooltip("Logs every step of a drag attempt. On while this is being made to work on " +
                 "device -- the failure modes are indistinguishable from the outside, and each " +
                 "one lives in a different place.")]
        public bool verboseLogging = true;

        private ConvaiRoomModePanel _panel;

        /// <summary>
        /// Where the panel sat relative to the ray when it was grabbed, in the ray's own frame.
        /// Reconstructing from this each frame is what preserves the grab offset.
        /// </summary>
        private Vector3 _grabOffsetInRaySpace;

        private bool _dragging;

        /// <summary>
        /// The pointer that took hold, kept for the length of the hold so the drag can be driven
        /// from Update rather than from the module's drag events.
        ///
        /// Safe to hold onto: the input module keeps one event-data object per pointer id and
        /// mutates it in place each frame, so this reference stays live and its ray is the
        /// current one rather than the one from the frame it was grabbed on.
        /// </summary>
        private PointerEventData _dragPointer;

        private void Awake()
        {
            _panel = GetComponentInParent<ConvaiRoomModePanel>();

            if (target == null && _panel != null) target = _panel.transform;

            if (target == null)
                Debug.LogError($"{Tag} The dragger has no target and found no " +
                               $"ConvaiRoomModePanel above it, so the panel cannot be moved.");
            else if (verboseLogging)
                Debug.Log($"{Tag} Dragger awake on '{name}', moving '{target.name}'.");
        }

        /// <summary>
        /// Only here to prove the background is receiving pointer events at all. If this logs
        /// and OnBeginDrag never does, the press is arriving and the drag is being lost between
        /// the module and here; if this never logs, the ray is not hitting the background and
        /// nothing downstream matters.
        /// </summary>
        public void OnPointerDown(PointerEventData eventData)
        {
            // Only the press that opens a hold. The module re-presses at frame rate while the
            // button is down, and a line per frame buries everything else in the log.
            if (!verboseLogging || _dragging) return;

            Debug.Log($"{Tag} Background pressed (vrPointer={eventData.IsVRPointer()} " +
                      $"drag={(eventData.pointerDrag != null ? eventData.pointerDrag.name : "NONE")} " +
                      $"cam={(eventData.pressEventCamera != null ? eventData.pressEventCamera.name : "NULL")}).");
        }

        /// <summary>
        /// Lets go, and is the reason the release is trustworthy.
        ///
        /// OnEndDrag is not on its own. If the module ever reports a press and a release on the
        /// same frame -- which it can, PressedAndReleased is a state it has -- ProcessMousePress
        /// runs its press half first and sets dragging back to false, so the release half finds
        /// nothing to end and never calls the end-drag handler. The grab would then be held
        /// after the button came up and the panel would follow the ray around the room.
        ///
        /// The pointer-up handler is executed unconditionally on whatever took the press, so it
        /// is the one signal that arrives however the frame is shaped -- as long as the ray is
        /// still on the panel. When it is not, the module has no object to deliver it to and
        /// Update's own check is what ends the hold.
        /// </summary>
        public void OnPointerUp(PointerEventData eventData) => ReleaseHold();

        /// <summary>
        /// Lets go of the panel, from whichever of the three routes noticed first: the end-drag
        /// event, the pointer-up event, or Update seeing the press go away. Releasing twice is
        /// not a problem worth guarding against beyond this early return -- what would be a
        /// problem is releasing zero times, which is why there are three.
        /// </summary>
        private void ReleaseHold()
        {
            if (!_dragging) return;

            _dragging = false;
            _dragPointer = null;
            _lastRefusal = null;

            if (verboseLogging) Debug.Log($"{Tag} Panel dragged to {target.position:F2}.");
        }

        /// <summary>
        /// Takes the drag threshold out of the decision, which is what stopped the panel moving.
        ///
        /// OVRInputModule does not use a pixel threshold for a tracked ray. It compares the angle
        /// between where you pressed and where you are pointing now, and it reads both off
        /// pointerCurrentRaycast.worldPosition -- which is only a real point while the ray is
        /// still ON a canvas. Dragging is the one gesture that takes the ray off the panel, and
        /// the moment it leaves, that raycast goes invalid and its worldPosition collapses to the
        /// world origin. The angle is then measured to (0,0,0) rather than to anything you are
        /// pointing at, and the gate never opens. Presses are unaffected because they are settled
        /// in ProcessMousePress before any of this runs, which is why the buttons always worked
        /// and the panel never moved.
        ///
        /// ShouldStartDrag returns true immediately when useDragThreshold is false, and this
        /// handler is invoked straight after the module sets it, so this is the last word on it.
        /// Nothing is lost by dropping the threshold: the panel is grabbed at its offset rather
        /// than snapped to the ray, so a press that turns into a one-pixel drag moves it by one
        /// pixel, and the buttons are separate objects that never see these events at all.
        /// </summary>
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            eventData.useDragThreshold = false;
        }

        /// <summary>
        /// Takes hold of the panel, once per hold.
        ///
        /// The guard is the whole of it. OVRInputModule reports a HELD button as pressed on
        /// every frame rather than only on the frame it went down -- see rayData.IsActive =
        /// pressed in GetMouseStateFromInputSource -- so ProcessMousePress treats every frame
        /// as a fresh press, sets dragging back to false, and ProcessDrag then begins a whole
        /// new drag. OnPointerDown and OnBeginDrag both fire at frame rate for one hold.
        ///
        /// Re-anchoring on each of those is what pinned the panel in place. The offset is
        /// stored relative to the ray and rebuilt from the ray in OnDrag, so capturing it
        /// again from the panel's CURRENT position, on the same frame that OnDrag rebuilds it,
        /// makes the round trip an identity: OnDrag assigns the panel exactly the position it
        /// already had. It moved by nothing, every frame, and no refusal was logged because
        /// nothing had refused.
        ///
        /// So the grab is taken on the first begin of a hold and kept until the release clears
        /// it. Every later begin is the module repeating itself and is ignored.
        /// </summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            // Already holding: this is the module re-beginning the same drag, not a new grab.
            if (_dragging) return;

            if (!CanDrag(eventData, out var ray)) return;

            _dragging = true;
            _dragPointer = eventData;
            _grabOffsetInRaySpace =
                Quaternion.Inverse(Quaternion.LookRotation(ray.direction)) *
                (target.position - ray.origin);

            if (verboseLogging)
                Debug.Log($"{Tag} Drag started, holding '{target.name}' at " +
                          $"{_grabOffsetInRaySpace.magnitude:F2} m.");
        }

        /// <summary>
        /// Deliberately empty. The panel is moved from Update instead -- see there for why --
        /// but the interface has to stay implemented regardless, because the module finds the
        /// drag target with GetEventHandler&lt;IDragHandler&gt; and would never begin a drag on
        /// a background that did not claim to handle one.
        /// </summary>
        public void OnDrag(PointerEventData eventData)
        {
        }

        /// <summary>
        /// Moves the panel, and does it here rather than in OnDrag because OnDrag stops arriving
        /// part way through exactly the drags that need it most.
        ///
        /// The held button re-presses every frame, so ProcessMousePress rebuilds pointerDrag
        /// every frame from pointerCurrentRaycast.gameObject -- and the frame the ray slips off
        /// the panel, that is null, so pointerDrag is null and ProcessDrag skips both of its
        /// blocks. No drag event, no movement, and the panel sits still until the ray happens
        /// back onto it. That is the stutter, and it shows up on long drags because the ray
        /// leaving the panel is something long drags do.
        ///
        /// They do it by construction, not by accident: the grab offset holds the panel's root
        /// fixed in the ray's frame while FacePlayer turns the panel toward the head every
        /// frame. A fixed root under a rotating plane means the ray's intersection creeps across
        /// the surface, and far enough into a sweep it creeps off the edge.
        ///
        /// Driving from Update decouples the two. Where the ray points decides where the panel
        /// goes; it no longer also decides whether the panel is allowed to move at all.
        /// </summary>
        private void Update()
        {
            if (!_dragging) return;

            // The one signal that survives the ray leaving the panel. eligibleForClick is set
            // true on every frame of a hold, before the module looks at what is under the ray,
            // and cleared on release wherever the ray happens to be pointing -- so it says
            // "still held" when nothing else here can, and says it honestly on the way out.
            if (_dragPointer == null || !_dragPointer.eligibleForClick)
            {
                ReleaseHold();
                return;
            }

            if (!CanDrag(_dragPointer, out var ray)) return;

            var offset = Quaternion.LookRotation(ray.direction) * _grabOffsetInRaySpace;

            // Clamped along the grab direction rather than per-axis, so the panel slides
            // toward or away from you along the ray instead of being squashed flat against
            // an invisible box.
            var distance = Mathf.Clamp(offset.magnitude, minDistance, maxDistance);
            if (offset.sqrMagnitude > 1e-6f) offset = offset.normalized * distance;

            target.position = ray.origin + offset;

            if (facePlayerWhileDragging) FacePlayer();
        }

        public void OnEndDrag(PointerEventData eventData) => ReleaseHold();

        private bool CanDrag(PointerEventData eventData, out Ray ray)
        {
            ray = default;

            if (target == null) return Refuse("no target");

            // The panel refuses to move when it shares a GameObject with the rebuilder,
            // because moving it would drag every replayed box along with it. Dragging has to
            // honour that for the same reason Recenter does.
            if (_panel != null && !_panel.CanMove) return Refuse("panel reports CanMove false");

            // A mouse in the Editor produces a plain PointerEventData with no world ray, and
            // there is nothing sensible to drag a world-space panel by in that case.
            if (!eventData.IsVRPointer()) return Refuse("not a VR pointer");

            ray = eventData.GetRay();

            if (ray.direction.sqrMagnitude <= 1e-6f) return Refuse("ray has no direction");
            return true;
        }

        /// <summary>
        /// Logs why a drag was turned down, once rather than every frame -- OnDrag fires
        /// continuously and a refusal repeated at frame rate buries the log it is meant to help.
        /// </summary>
        private bool Refuse(string reason)
        {
            if (verboseLogging && reason != _lastRefusal)
            {
                _lastRefusal = reason;
                Debug.LogWarning($"{Tag} Drag refused: {reason}.");
            }

            return false;
        }

        private string _lastRefusal;

        /// <summary>
        /// Turns the readable face toward the player. Uses the direction from the head to the
        /// panel rather than the head's own forward, which matters once the panel has been
        /// dragged off to one side -- the two stop agreeing the moment it is not straight ahead.
        /// </summary>
        private void FacePlayer()
        {
            var head = Camera.main;
            if (head == null) return;

            var forward = target.position - head.transform.position;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;

            target.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
    }
}
