using System;
using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Components;
using RoomScan;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;

namespace ConvaiRoom
{
    /// <summary>
    /// Sends the phase 2 character walking: point at the floor, pull the trigger, it walks
    /// there around the scanned furniture.
    ///
    /// This is the movement spine, and it is deliberately local. <see cref="SendTo"/> is the
    /// single way anything gets the character to move, so the Convai session -- when it is
    /// wired -- drives the character through exactly the same call a trigger press does.
    /// Keeping a manual route to it is not scaffolding to delete later: it is the only way to
    /// tell "the character will not walk there" apart from "the backend did not ask it to",
    /// and those two failures look identical from inside a headset.
    ///
    /// Aiming does not use physics. The navmesh is built from
    /// <see cref="NavMeshBuildSource"/> entries rather than colliders -- see
    /// <see cref="RoomScanNavMeshBuilder"/> -- so there is nothing in the room for a
    /// Physics.Raycast to hit. The pointer ray is intersected with the floor plane instead and
    /// the result sampled onto the navmesh, which also gives the marker its meaning: it is
    /// green where the character can actually stand, not merely where you are pointing.
    /// </summary>
    public class RoomCharacterDirector : MonoBehaviour
    {
        private const string Tag = "[RoomDirector]";

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Holds the live character. Nothing here does anything until it has spawned one.")]
        public RoomCharacterSpawner spawner;

        [Tooltip("Optional. Supplies the floor height before a character exists, so the aim " +
                 "marker works the moment phase 2 opens rather than only after the first spawn.")]
        public RoomScanRebuilder rebuilder;

        [Header("Input")]
        [Tooltip("Sends the character to wherever the marker is. The same trigger the panel " +
                 "uses for its buttons -- a press that lands on the panel presses the button " +
                 "and does not also send the character. See PointerOverUI.")]
        public OVRInput.Button sendButton = OVRInput.Button.SecondaryIndexTrigger;

        [Tooltip("How far the aim ray reaches. Past this the floor is not worth pointing at " +
                 "-- rooms are smaller than this and the marker would sit through a wall.")]
        public float maxAimDistance = 12f;

        [Header("Marker")]
        [Tooltip("Radius of the ring drawn where the character would walk to.")]
        public float markerRadius = 0.22f;

        [Tooltip("Ring drawn where the character CAN walk to.")]
        public Color walkableColor = new Color(0.5f, 0.9f, 0.55f, 0.95f);

        [Tooltip("Ring drawn where it cannot -- off the bake, or inside furniture that was " +
                 "punched out of the floor.")]
        public Color blockedColor = new Color(1f, 0.65f, 0.3f, 0.9f);

        [Tooltip("Draw order. Matches the scan wireframes so the marker sits with the room " +
                 "rather than over the panel.")]
        public int sortingOrder = 10;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>Raised when the character is sent somewhere, with the destination.</summary>
        public event Action<Vector3> OnSent;

        /// <summary>
        /// Raised when a move ends. True means it arrived, false means it was stopped or
        /// cancelled -- the same meaning <see cref="ConvaiNavMeshLocomotion.MoveEnded"/> gives.
        /// </summary>
        public event Action<bool> OnArrived;

        /// <summary>Where the character was last sent. Meaningless until <see cref="HasSent"/>.</summary>
        public Vector3 LastDestination { get; private set; }

        public bool HasSent { get; private set; }

        /// <summary>True while the character is walking somewhere.</summary>
        public bool IsMoving => Locomotion != null && Locomotion.IsMoving;

        /// <summary>
        /// Why the last <see cref="SendTo"/> was refused, ready to put on the panel. Empty
        /// after a send that was accepted.
        /// </summary>
        public string LastRefusal { get; private set; } = "";

        private ConvaiNavMeshLocomotion Locomotion => spawner != null ? spawner.Locomotion : null;

        private Transform _aim;
        private WireLoop _marker;
        private readonly List<Vector3> _ring = new List<Vector3>();

        /// <summary>Whether the aim currently lands somewhere the character could stand.</summary>
        private bool _aimValid;
        private Vector3 _aimPoint;

        /// <summary>
        /// The locomotion we attached <see cref="HandleMoveEnded"/> to. Held so the handler can
        /// be detached from the right object: the spawner replaces its character on every
        /// re-spawn, and unsubscribing from the NEW locomotion would leave the old one holding
        /// a reference to a destroyed listener.
        /// </summary>
        private ConvaiNavMeshLocomotion _subscribed;

        private const int RingSegments = 28;

        private void Awake()
        {
            if (spawner == null) spawner = FindAnyObjectByType<RoomCharacterSpawner>();
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
        }

        private void OnDisable()
        {
            Unsubscribe();
            if (_marker != null) _marker.gameObject.SetActive(false);
        }

        private void OnDestroy() => Unsubscribe();

        private void Update()
        {
            SyncSubscription();

            var aiming = TryAim(out _aimPoint, out _aimValid);
            DrawMarker(aiming);

            if (!aiming || !_aimValid) return;
            if (!OVRInput.GetDown(sendButton)) return;

            // A press that landed on the panel is a button press. Without this the same pull
            // that says BAKE NAVMESH also sends the character to whatever floor happens to lie
            // behind the panel, which looks like the button doing something it does not.
            if (PointerOverUI()) return;

            SendTo(_aimPoint);
        }

        // -----------------------------------------------------------------
        // Commanding
        // -----------------------------------------------------------------

        /// <summary>
        /// Sends the character to a world position. The one entry point for movement --
        /// trigger presses, panel buttons and, later, Convai actions all arrive here.
        ///
        /// Returns false and sets <see cref="LastRefusal"/> when the character cannot go,
        /// which is not the same as it choosing not to: no character, no navmesh under it, or
        /// no path. The distinction is what the panel reports.
        /// </summary>
        public bool SendTo(Vector3 destination)
        {
            LastRefusal = "";

            var locomotion = Locomotion;
            if (locomotion == null)
            {
                LastRefusal = "no character to send";
                Debug.LogWarning($"{Tag} Nothing to send: no character is spawned, or the one " +
                                 $"that is has no ConvaiNavMeshLocomotion.");
                return false;
            }

            // MoveTo logs its own reason -- not on a navmesh, nothing walkable near the
            // destination, or no path between the two -- so this only has to say that it
            // refused, not re-derive why.
            if (!locomotion.MoveTo(destination))
            {
                LastRefusal = "character cannot reach there";
                return false;
            }

            LastDestination = destination;
            HasSent = true;

            if (verboseLogging)
                Debug.Log($"{Tag} Sent '{locomotion.name}' to {destination:F2}.");

            OnSent?.Invoke(destination);
            return true;
        }

        /// <summary>
        /// Ends the current walk the way a person would -- by walking the last stride out
        /// rather than stopping dead. Returns false when there was nothing to stop.
        /// </summary>
        public bool Halt()
        {
            var locomotion = Locomotion;
            if (locomotion == null || !locomotion.IsMoving) return false;

            locomotion.StopGracefully();
            if (verboseLogging) Debug.Log($"{Tag} Halted '{locomotion.name}'.");
            return true;
        }

        /// <summary>
        /// Keeps the arrival handler pointed at whatever character is currently spawned.
        /// Polled rather than driven by an event because the spawner replaces its character
        /// wholesale and this is a reference compare, not work.
        /// </summary>
        private void SyncSubscription()
        {
            var current = Locomotion;
            if (ReferenceEquals(current, _subscribed)) return;

            Unsubscribe();

            _subscribed = current;
            if (_subscribed != null) _subscribed.MoveEnded += HandleMoveEnded;
        }

        private void Unsubscribe()
        {
            if (_subscribed == null) return;

            _subscribed.MoveEnded -= HandleMoveEnded;
            _subscribed = null;
        }

        private void HandleMoveEnded(bool arrived)
        {
            if (verboseLogging)
                Debug.Log($"{Tag} Move ended: {(arrived ? "arrived" : "stopped short")}.");

            OnArrived?.Invoke(arrived);
        }

        // -----------------------------------------------------------------
        // Aiming
        // -----------------------------------------------------------------

        /// <summary>
        /// Where the pointer ray meets the floor, and whether the character could stand there.
        ///
        /// Returns false when there is nothing to aim with or the ray never reaches the floor
        /// -- pointing at the ceiling, mostly, which is a real thing to do and not worth a
        /// marker.
        /// </summary>
        private bool TryAim(out Vector3 point, out bool walkable)
        {
            point = Vector3.zero;
            walkable = false;

            var aim = AimTransform();
            if (aim == null) return false;

            if (!TryFloorHeight(out var floorY)) return false;

            var origin = aim.position;
            var direction = aim.forward;

            // Parallel to the floor, or pointing away from it. Either way the ray never
            // arrives, and a marker parked at the horizon is worse than none.
            if (Mathf.Abs(direction.y) < 1e-4f) return false;

            var travel = (floorY - origin.y) / direction.y;
            if (travel <= 0f || travel > maxAimDistance) return false;

            var onPlane = origin + direction * travel;

            // Sampled with the same radius the spawner uses, and for the same reason: it
            // forgives aiming a few centimetres into a table leg without silently dragging
            // the destination somewhere else in the room.
            var radius = spawner != null ? spawner.sampleRadius : 0.6f;

            if (NavMesh.SamplePosition(onPlane, out var hit, radius, NavMesh.AllAreas))
            {
                point = hit.position;
                walkable = true;
                return true;
            }

            // Still reported, so the ring appears in its blocked colour. Showing where the
            // character will NOT go is what makes the punched-out furniture legible.
            point = onPlane;
            return true;
        }

        /// <summary>
        /// The pose to aim from. The right hand anchor, matching the hand the panel puts its
        /// laser on -- aiming with one hand and pointing with the other is not something to
        /// discover while wearing the thing.
        /// </summary>
        private Transform AimTransform()
        {
            if (_aim != null) return _aim;

            var rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null)
            {
                if (rig.rightHandAnchor != null) return _aim = rig.rightHandAnchor;
                if (rig.leftHandAnchor != null) return _aim = rig.leftHandAnchor;
                if (rig.centerEyeAnchor != null) return _aim = rig.centerEyeAnchor;
            }

            // Gaze aiming. Poor, but it is what the Editor has with no rig attached, and it
            // makes the whole phase testable at a desk.
            return _aim = Camera.main != null ? Camera.main.transform : null;
        }

        /// <summary>
        /// The height of the walkable floor in world space.
        ///
        /// The character's own feet are the best answer available: it is standing on the
        /// navmesh, so its Y IS the bake's floor height, with no transform to get wrong. The
        /// scan's recorded floor is the fallback for aiming before anything has spawned.
        /// </summary>
        private bool TryFloorHeight(out float floorY)
        {
            floorY = 0f;

            if (spawner != null && spawner.IsSpawned)
            {
                floorY = spawner.Character.transform.position.y;
                return true;
            }

            var scan = rebuilder != null ? rebuilder.Scan : null;
            if (scan?.room == null) return false;

            floorY = rebuilder.RoomToWorld(new Vector3(0f, scan.room.floorY, 0f)).y;
            return true;
        }

        /// <summary>
        /// Whether the pointer is currently over a UI graphic. Asked of the EventSystem rather
        /// than raycast here again, so it answers with whatever the panel's own input module
        /// decided -- the two can never disagree about what is under the laser.
        /// </summary>
        private static bool PointerOverUI()
        {
            var events = EventSystem.current;
            return events != null && events.IsPointerOverGameObject();
        }

        // -----------------------------------------------------------------
        // Marker
        // -----------------------------------------------------------------

        private void DrawMarker(bool aiming)
        {
            // Hidden with no character on purpose. A destination ring with nothing to walk
            // there is an invitation to press a trigger that cannot do anything.
            var show = aiming && spawner != null && spawner.IsSpawned && !PointerOverUI();

            if (!show)
            {
                if (_marker != null) _marker.gameObject.SetActive(false);
                return;
            }

            EnsureMarker();

            _marker.gameObject.SetActive(true);

            // Lifted a little so the ring is not z-fighting the floor it lies on.
            _marker.transform.position = _aimPoint + Vector3.up * 0.01f;
            _marker.SetColor(_aimValid ? walkableColor : blockedColor);
        }

        private void EnsureMarker()
        {
            if (_marker != null) return;

            _marker = WireLoop.Create("Character Destination", null, null, 0.006f);
            _marker.GetComponent<LineRenderer>().sortingOrder = sortingOrder;

            // Built once in local space and then only moved. The ring lies in the XZ plane
            // because it marks a spot on the floor, not a target in the air.
            _ring.Clear();
            for (var i = 0; i < RingSegments; i++)
            {
                var angle = i / (float)RingSegments * Mathf.PI * 2f;
                _ring.Add(new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * markerRadius);
            }

            _marker.SetPoints(_ring);
        }
    }
}
