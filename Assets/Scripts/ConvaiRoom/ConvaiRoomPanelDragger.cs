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
                                          IBeginDragHandler, IDragHandler, IEndDragHandler
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

        private ConvaiRoomModePanel _panel;

        /// <summary>
        /// Where the panel sat relative to the ray when it was grabbed, in the ray's own frame.
        /// Reconstructing from this each frame is what preserves the grab offset.
        /// </summary>
        private Vector3 _grabOffsetInRaySpace;

        private bool _dragging;

        private void Awake()
        {
            _panel = GetComponentInParent<ConvaiRoomModePanel>();

            if (target == null && _panel != null) target = _panel.transform;

            if (target == null)
                Debug.LogError($"{Tag} The dragger has no target and found no " +
                               $"ConvaiRoomModePanel above it, so the panel cannot be moved.");
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!CanDrag(eventData, out var ray)) return;

            _dragging = true;
            _grabOffsetInRaySpace =
                Quaternion.Inverse(Quaternion.LookRotation(ray.direction)) *
                (target.position - ray.origin);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging || !CanDrag(eventData, out var ray)) return;

            var offset = Quaternion.LookRotation(ray.direction) * _grabOffsetInRaySpace;

            // Clamped along the grab direction rather than per-axis, so the panel slides
            // toward or away from you along the ray instead of being squashed flat against
            // an invisible box.
            var distance = Mathf.Clamp(offset.magnitude, minDistance, maxDistance);
            if (offset.sqrMagnitude > 1e-6f) offset = offset.normalized * distance;

            target.position = ray.origin + offset;

            if (facePlayerWhileDragging) FacePlayer();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragging) return;

            _dragging = false;
            Debug.Log($"{Tag} Panel dragged to {target.position:F2}.");
        }

        private bool CanDrag(PointerEventData eventData, out Ray ray)
        {
            ray = default;

            if (target == null) return false;

            // The panel refuses to move when it shares a GameObject with the rebuilder,
            // because moving it would drag every replayed box along with it. Dragging has to
            // honour that for the same reason Recenter does.
            if (_panel != null && !_panel.CanMove) return false;

            // A mouse in the Editor produces a plain PointerEventData with no world ray, and
            // there is nothing sensible to drag a world-space panel by in that case.
            if (!eventData.IsVRPointer()) return false;

            ray = eventData.GetRay();
            return ray.direction.sqrMagnitude > 1e-6f;
        }

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
