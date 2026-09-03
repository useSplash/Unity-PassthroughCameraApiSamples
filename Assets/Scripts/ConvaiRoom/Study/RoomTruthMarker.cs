using System;
using System.Collections.Generic;
using Meta.XR;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Measures what is really in the room, by hand, in the headset.
    ///
    /// Every scan-accuracy number needs something to be accurate ABOUT -- recall, precision,
    /// position error, extent error and duplicate rate are all comparisons against a list of
    /// what is actually there and where. Nothing in the app has ever known that.
    ///
    /// Marked here rather than measured with a tape, and the reason is coordinate frames. A
    /// tape gives you distances in the room's own terms and then somebody has to get those
    /// into the frame the scan was recorded in, which is the hard part, the part that is
    /// quietly wrong, and the part that would make a 10 cm position figure meaningless. The
    /// controller is already tracked in exactly that frame, so touching a corner IS the
    /// measurement -- it goes through <see cref="ObjectScanRecorder.WorldToRoom"/>, the same
    /// conversion the scanner itself uses, and lands in the same space as the boxes it will
    /// be subtracted from.
    ///
    /// One object is two corners of its bounding box, diagonally opposite, plus a label. Two
    /// ways to place a corner:
    ///
    ///   index trigger  the controller's tip, where it is. Sub-centimetre, and the default.
    ///   grip           a raycast forward, for a corner you cannot reach. Carries the depth
    ///                  pass's own error, so it is flagged and never pooled with the others.
    ///
    /// The result is per ROOM, not per session -- the furniture does not move between
    /// participants -- so it is written to truth_&lt;room&gt;.json and appended to on later visits.
    /// </summary>
    public class RoomTruthMarker : MonoBehaviour
    {
        private const string Tag = "[Truth]";

        /// <summary>
        /// The label used for something with no COCO class at all.
        ///
        /// Not a formality. Recall is scored over objects the model could possibly name, and
        /// room coverage is reported separately, so that the vocabulary's limits are not
        /// counted against the pipeline: a bookshelf that YOLO has no word for is a fact
        /// about COCO, while a missed chair is a fact about the scanner. Without a way to say
        /// "this is not in the vocabulary" the two collapse into the same number.
        /// </summary>
        public const string OutOfVocabulary = "OUT OF VOCAB";

        [Header("Wiring (left empty, these are found in the scene)")]
        [Tooltip("Supplies the room frame. Truth is recorded through its WorldToRoom so it " +
                 "lands in the same space as the scan it will be compared against.")]
        public ObjectScanRecorder recorder;

        [Tooltip("Optional, and only used for the grip-trigger route. Without one, corners " +
                 "that cannot be reached simply cannot be marked.")]
        public EnvironmentRaycastManager raycastManager;

        [Header("Vocabulary")]
        [Tooltip("The detector's own class list. Drag in SentisYoloClasses.txt from\n" +
                 "Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/.\n\n" +
                 "Reading the model's list rather than a hand-copied one is what stops " +
                 "'in vocabulary' drifting away from what the model can actually emit. " +
                 "Marking refuses to start without it, because guessing here would corrupt " +
                 "the recall figure rather than merely inconvenience anyone.")]
        public TextAsset labelsAsset;

        [Header("Capture")]
        [Tooltip("Which controller does the marking. The left hand by default, so the right " +
                 "stays free to aim the laser at the panel.")]
        public OVRInput.Controller markingHand = OVRInput.Controller.LTouch;

        [Tooltip("Places a corner at the controller's tip. The accurate route -- the tracked " +
                 "position IS the measurement.")]
        public OVRInput.Button captureButton = OVRInput.Button.PrimaryIndexTrigger;

        [Tooltip("Places a corner by raycasting forward, for corners out of arm's reach. " +
                 "Flagged in the file, because it carries the depth pass's error rather than " +
                 "the tracker's.")]
        public OVRInput.Button raycastButton = OVRInput.Button.PrimaryHandTrigger;

        [Tooltip("How far ahead of the hand anchor the controller's tip sits, in metres. " +
                 "The anchor is roughly at the grip, so this is what turns 'where the " +
                 "controller is' into 'where I am pointing it at'.")]
        public float tipOffset = 0.04f;

        [Tooltip("Ignore a raycast further away than this.")]
        public float maxRaycastDistance = 6f;

        [Header("Debug")]
        public bool verboseLogging = true;

        // -----------------------------------------------------------------

        private RoomTruthFile _file;
        private string _roomLabel = "";

        private readonly List<string> _labels = new List<string>();
        private int _labelIndex;

        private bool _haveFirstCorner;
        private Vector3 _cornerA;
        private bool _cornerAViaRaycast;

        private OVRCameraRig _rig;
        private string _status = "";

        /// <summary>Whether the marker currently owns the panel's buttons.</summary>
        public bool IsMarking { get; private set; }

        /// <summary>How many objects the room's truth file holds.</summary>
        public int Count => _file != null && _file.objects != null ? _file.objects.Count : 0;

        private string CurrentLabel =>
            _labels.Count > 0 ? _labels[Mathf.Clamp(_labelIndex, 0, _labels.Count - 1)] : OutOfVocabulary;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Awake()
        {
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (raycastManager == null) raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>();

            LoadLabels();
        }

        /// <summary>
        /// Reads the detector's class list.
        ///
        /// Split on newline and trimmed, the same way SentisInferenceUiManager reads it --
        /// the file ships with CRLF endings and an entry of "chair\r" would never match a
        /// scanned label, which is the sort of thing that reads as a recall failure.
        /// </summary>
        private void LoadLabels()
        {
            _labels.Clear();

            if (labelsAsset == null) return;

            _labels.AddRange(ParseLabels(labelsAsset.text));

            if (verboseLogging)
                Debug.Log($"{Tag} Loaded {_labels.Count - 1} classes from {labelsAsset.name}.");
        }

        /// <summary>
        /// Turns the class-list file into the cycle order, with the out-of-vocabulary entry
        /// last.
        ///
        /// Static and public so StudySelfCheck can exercise it without a scene. The CRLF trim
        /// is the part worth testing: the asset ships with Windows line endings, and a label
        /// of "chair\r" matches no scanned label at all -- which would show up in the results
        /// as a recall failure rather than as the string bug it is.
        /// </summary>
        public static List<string> ParseLabels(string text)
        {
            var labels = new List<string>();

            // The early-out only skips the split/trim below -- it must not skip the
            // OutOfVocabulary add past it, or an empty/missing labels asset leaves the marker
            // with zero cycle entries instead of just the one it should always offer.
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var raw in text.Split('\n'))
                {
                    var label = raw.Trim();
                    if (label.Length > 0) labels.Add(label);
                }
            }

            // Always last, so cycling through the vocabulary reaches it without it sitting in
            // the middle of the real classes.
            labels.Add(OutOfVocabulary);
            return labels;
        }

        /// <summary>
        /// The axis-aligned box two opposite corners describe.
        ///
        /// Static and public for the same reason as <see cref="ParseLabels"/>. Order must not
        /// matter -- somebody marking bottom-left-then-top-right and somebody doing the
        /// reverse have measured the same object -- which is what the Min/Max pair guarantees
        /// and what the self-check asserts.
        /// </summary>
        public static void BoxFromCorners(Vector3 a, Vector3 b, out Vector3 center, out Vector3 size)
        {
            var min = Vector3.Min(a, b);
            var max = Vector3.Max(a, b);

            center = (min + max) * 0.5f;
            size = max - min;
        }

        // -----------------------------------------------------------------
        // Panel seam
        // -----------------------------------------------------------------

        /// <summary>
        /// Starts marking the named room, loading whatever has already been measured in it.
        ///
        /// Refuses in two cases, both loudly, because both produce data that looks fine and
        /// is not: no class list means "in vocabulary" would be a guess, and no MRUK room
        /// means the coordinates are raw world space that will not survive the restart
        /// between this and the scan being compared against.
        /// </summary>
        public void Begin(string roomLabel)
        {
            if (_labels.Count == 0)
            {
                _status = "no class list assigned";
                Debug.LogError($"{Tag} No labels asset assigned, so nothing can be scored as " +
                               $"in-vocabulary or not. Drag SentisYoloClasses.txt (in " +
                               $"Assets/PassthroughCameraApiSamples/MultiObjectDetection/" +
                               $"SentisInference/Model/) onto this component. Not marking.", this);
                return;
            }

            if (recorder == null || !recorder.HasRoom)
            {
                _status = "no MRUK room";
                Debug.LogError($"{Tag} There is no MRUK room, so every corner would be " +
                               $"recorded in raw world space and would not line up with a " +
                               $"scan taken at any other time. Run Space Setup. Not marking.",
                               this);
                return;
            }

            _roomLabel = roomLabel;
            _file = RoomTruthIO.LoadOrCreate(roomLabel);
            _file.anchorUuid = AnchorUuid();

            _haveFirstCorner = false;
            IsMarking = true;
            _status = Count > 0 ? $"{Count} already marked" : "touch a corner to start";

            Debug.Log($"{Tag} Marking {roomLabel}; {Count} objects already on file.");
        }

        public string SlotLabel(int slot)
        {
            if (!IsMarking) return null;

            switch (slot)
            {
                case 0: return $"LABEL: {CurrentLabel}";
                case 1: return _haveFirstCorner ? "CANCEL CORNER" : "UNDO LAST";
                case 2: return "DONE";
                default: return null;
            }
        }

        public string SlotBlocked(int slot)
        {
            if (slot == 1 && !_haveFirstCorner && Count == 0) return "nothing marked";
            return "";
        }

        public void PressSlot(int slot)
        {
            switch (slot)
            {
                case 0:
                    // The index is NOT reset after a commit, which is the whole ergonomics of
                    // this control: a room with four chairs in it is one cycle and four
                    // commits, not four trips through eighty classes.
                    if (_labels.Count > 0) _labelIndex = (_labelIndex + 1) % _labels.Count;
                    break;

                case 1:
                    if (_haveFirstCorner)
                    {
                        _haveFirstCorner = false;
                        _status = "corner cancelled";
                    }
                    else
                    {
                        UndoLast();
                    }
                    break;

                case 2:
                    Finish();
                    break;
            }
        }

        private void UndoLast()
        {
            if (_file == null || _file.objects.Count == 0)
            {
                _status = "nothing to undo";
                return;
            }

            var last = _file.objects[_file.objects.Count - 1];
            _file.objects.RemoveAt(_file.objects.Count - 1);

            Save();
            _status = $"removed {last.label}";
        }

        private void Finish()
        {
            if (_file != null) Save();

            IsMarking = false;
            _haveFirstCorner = false;
            _status = "";

            Debug.Log($"{Tag} Finished {_roomLabel} with {Count} objects.");
        }

        // -----------------------------------------------------------------
        // Capture
        // -----------------------------------------------------------------

        private void Update()
        {
            if (!IsMarking) return;

            if (OVRInput.GetDown(captureButton, markingHand)) Capture(viaRaycast: false);
            else if (OVRInput.GetDown(raycastButton, markingHand)) Capture(viaRaycast: true);
        }

        private void Capture(bool viaRaycast)
        {
            if (!TryPoint(viaRaycast, out var world))
            {
                _status = viaRaycast ? "raycast hit nothing" : "no controller pose";
                return;
            }

            var local = recorder.WorldToRoom(world);

            if (!_haveFirstCorner)
            {
                _cornerA = local;
                _cornerAViaRaycast = viaRaycast;
                _haveFirstCorner = true;
                _status = $"corner 1 set - now the opposite corner of the {CurrentLabel}";
                return;
            }

            Commit(_cornerA, local, _cornerAViaRaycast || viaRaycast);
            _haveFirstCorner = false;
        }

        /// <summary>
        /// Where the marking hand is pointing, in world space.
        ///
        /// The anchor sits near the grip rather than at the tip, so the offset is what makes
        /// "touch the corner" mean the thing the hand is actually touching. Under hand
        /// tracking there is no controller and this returns false rather than silently
        /// recording the wrist -- a corner measured from a wrist is off by the length of a
        /// hand, which is most of the error budget.
        /// </summary>
        private bool TryPoint(bool viaRaycast, out Vector3 world)
        {
            world = default;

            if (_rig == null) _rig = FindAnyObjectByType<OVRCameraRig>();
            if (_rig == null) return false;

            var anchor = markingHand == OVRInput.Controller.RTouch
                ? _rig.rightHandAnchor
                : _rig.leftHandAnchor;

            if (anchor == null) return false;

            if (!viaRaycast)
            {
                world = anchor.position + anchor.forward * tipOffset;
                return true;
            }

            if (raycastManager == null) return false;

            var ray = new Ray(anchor.position, anchor.forward);
            if (!raycastManager.Raycast(ray, out var hit, maxRaycastDistance)) return false;

            world = hit.point;
            return true;
        }

        /// <summary>
        /// Turns two opposite corners into a box and files it.
        ///
        /// Axis-aligned in ROOM space, and the rotation is left at identity to say so rather
        /// than left unset. Two touched corners describe a box aligned to the room, which is
        /// what almost all furniture is; an oriented truth capture would need a third point
        /// and is not worth the extra press until something in a room is measurably skew.
        /// </summary>
        private void Commit(Vector3 a, Vector3 b, bool viaRaycast)
        {
            BoxFromCorners(a, b, out var center, out var size);

            // A box with no depth is two presses at the same place -- a double-fire, or a
            // corner marked twice by mistake. Filing it would put a zero into an extent-error
            // denominator, which is worse than making somebody press again.
            if (size.x < 0.02f && size.y < 0.02f && size.z < 0.02f)
            {
                _status = "those two corners are the same point";
                Debug.LogWarning($"{Tag} Ignored a box with no size ({size:F3} m).");
                return;
            }

            var label = CurrentLabel;
            var inVocab = label != OutOfVocabulary;

            _file.objects.Add(new TruthObject
            {
                id = $"truth_{_file.objects.Count:D3}",
                label = label,
                inVocabulary = inVocab,
                position = new Vec3(center),
                size = new Vec3(size),
                cornerA = new Vec3(a),
                cornerB = new Vec3(b),
                viaRaycast = viaRaycast,
                markedUtc = DateTime.UtcNow.ToString("o")
            });

            Save();

            _status = $"{label} #{Count} ({size.x:F2} x {size.y:F2} x {size.z:F2} m)";

            if (verboseLogging)
                Debug.Log($"{Tag} Marked {label} at {center:F2}, size {size:F2}" +
                          $"{(viaRaycast ? " (raycast)" : "")}.");
        }

        /// <summary>
        /// Writes after every object, not at the end.
        ///
        /// Marking a room is ten minutes of somebody's time and the app is one proximity
        /// sensor away from being paused. Losing the lot to an unwritten buffer is not a
        /// trade worth taking for a file this small.
        /// </summary>
        private void Save()
        {
            try
            {
                RoomTruthIO.Save(_file);
            }
            catch (Exception ex)
            {
                _status = $"WRITE FAILED: {ex.Message}";
                Debug.LogError($"{Tag} Could not write the truth file: {ex}");
            }
        }

        private string AnchorUuid()
        {
            var mruk = Meta.XR.MRUtilityKit.MRUK.Instance;
            var room = mruk != null ? mruk.GetCurrentRoom() : null;

            return room != null ? room.Anchor.Uuid.ToString() : "none";
        }

        /// <summary>What the marker is doing, for the panel's details block.</summary>
        public string Status => _status;
    }
}
