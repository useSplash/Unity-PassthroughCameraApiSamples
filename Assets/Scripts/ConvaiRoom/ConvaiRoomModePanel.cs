using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ConvaiRoom
{
    /// <summary>
    /// The phase 1 control panel: a world-space readout of what the room scan has picked up
    /// so far, plus a button to write it to disk.
    ///
    /// The class name is historical -- this was the Scan/Talk mode panel before the flow was
    /// split into phases. It keeps the old name so the scene's script GUID reference survives
    /// a rewrite; the rename is a separate commit. Log lines carry <see cref="Tag"/> so greps
    /// do not have to change when that happens.
    ///
    /// The panel is placed once, when MRUK reports its scene, and then left alone. An earlier
    /// version followed the head every frame, which is unreadable to look at and makes the
    /// panel impossible to aim at with a laser -- it moves with the thing you are aiming.
    /// Use Recenter to call it back instead.
    /// </summary>
    public class ConvaiRoomModePanel : MonoBehaviour
    {
        private const string Tag = "[ScanPanel]";

        // The canvas is authored in these units and then scaled down to metres, so the
        // layout numbers below read like ordinary UI pixels instead of millimetres.
        private const float CanvasWidth = 420f;
        private const float CanvasHeight = 340f;

        /// <summary>What the scan file on disk is currently worth.</summary>
        private enum DiskState
        {
            Missing,
            Stale,
            Ready
        }

        [Header("Wiring (left empty, this is found in the scene)")]
        public ObjectScanRecorder recorder;

        [Header("Panel placement")]
        [Tooltip("Drop the panel in front of the player once MRUK reports its scene. Once " +
                 "placed it stays put -- it never follows your head.")]
        public bool placeOnStart = true;

        public float distanceFromPlayer = 1.2f;
        public float heightOffset = -0.25f;

        [Tooltip("Physical width of the panel in metres. Height follows the same scale.")]
        public float panelWidth = 0.42f;

        [Tooltip("How long to wait for MRUK before placing the panel anyway. Lets the scene " +
                 "run in the Editor with no headset attached.")]
        public float mrukTimeoutSeconds = 5f;

        [Header("Input")]
        [Tooltip("Clicks a panel button with the laser. The index trigger rather than A, " +
                 "because RoomScanController already owns A/B/X/Y. Secondary is the right " +
                 "hand on Touch controllers, matching the hand the laser is on.")]
        public OVRInput.Button clickButton = OVRInput.Button.SecondaryIndexTrigger;

        [Header("Refresh")]
        [Tooltip("Seconds between cluster-count polls.")]
        public float countsRefreshInterval = 0.25f;

        [Tooltip("Seconds between scan-file checks on disk.")]
        public float diskRefreshInterval = 1f;

        private static readonly Color ActionButton = new Color(0.22f, 0.28f, 0.34f, 0.92f);
        private static readonly Color LockedButton = new Color(0.16f, 0.16f, 0.18f, 0.75f);
        private static readonly Color LockedLabel = new Color(0.55f, 0.55f, 0.58f);

        private Text _titleText;
        private Text _countsText;
        private Text _statusText;
        private GameObject _canvasGo;
        private OVRRaycaster _raycaster;
        private ConvaiRoomLaserCursor _cursor;

        private bool _canMove = true;
        private bool _hasSpawned;

        // Counts. Both come from one snapshot pass so they can never disagree.
        private readonly List<ObjectScanRecorder.ClusterView> _snapshot =
            new List<ObjectScanRecorder.ClusterView>();

        private int _ready;
        private int _tracked;
        private float _nextCountsPoll;

        // Disk.
        private DiskState _diskState = DiskState.Missing;
        private long _diskBytes;
        private int _diskObjects = -1;          // -1 = not known
        private DateTime _diskWriteUtc;
        private DateTime? _savedThisSessionUtc;
        private DateTime? _clearedAtUtc;
        private float _nextDiskPoll;

        private string _lastAction = "none yet";
        private float _lastActionExpiresAt = float.PositiveInfinity;

        // Redraw is gated: the text mesh is rebuilt when something displayed actually
        // changed, and otherwise at most once a second so the "2m ago" age still ticks.
        private bool _dirty = true;
        private float _nextForcedRedraw;

        private readonly StringBuilder _builder = new StringBuilder();

        private void Awake()
        {
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();

            // The panel owns its transform and moves it on every recenter. Rebuilt boxes are
            // parented to the rebuilder's transform, so sharing a GameObject with it would
            // drag the entire replayed room along -- which looks exactly like an anchoring
            // bug and is miserable to diagnose on a headset.
            if (GetComponent<RoomScanRebuilder>() != null)
            {
                Debug.LogError($"{Tag} This is on the same GameObject as the RoomScanRebuilder, " +
                               $"and moving the panel would move every replayed box with it. " +
                               $"Put the panel on its own empty GameObject. Placement is " +
                               $"disabled for now.");
                _canMove = false;
            }

            BuildUi();

            // Hidden until MRUK reports in, so the panel does not flash up somewhere
            // arbitrary and then jump once the room arrives.
            _canvasGo.SetActive(false);
        }

        private void OnEnable()
        {
            if (recorder != null) recorder.OnScanCleared += HandleScanCleared;
        }

        private void OnDisable()
        {
            if (recorder != null) recorder.OnScanCleared -= HandleScanCleared;
        }

        private void Start()
        {
            ReadDiskObjectCountOnce();
            RefreshDiskState();
            PollCounts();

            if (!placeOnStart)
            {
                SpawnNow();
                return;
            }

            if (MRUK.Instance == null)
            {
                Debug.LogWarning($"{Tag} No MRUK in the scene; placing immediately. Scan poses " +
                                 $"will be raw world space rather than room-local.");
                SpawnNow();
                return;
            }

            MRUK.Instance.RegisterSceneLoadedCallback(SpawnNow);
            StartCoroutine(SpawnIfMrukNeverReports());
        }

        /// <summary>
        /// MRUK only raises SceneLoadedEvent once it actually has room data. In the Editor with
        /// no headset attached -- or on a device where Space Setup was never run -- it stays
        /// silent forever, and the panel would never appear at all.
        /// </summary>
        private IEnumerator SpawnIfMrukNeverReports()
        {
            yield return new WaitForSeconds(mrukTimeoutSeconds);
            if (_hasSpawned) yield break;

            Debug.LogWarning($"{Tag} MRUK reported no scene within {mrukTimeoutSeconds}s; " +
                             $"placing anyway. On a headset this means Space Setup has not " +
                             $"been run.");
            SpawnNow();
        }

        /// <summary>
        /// Shows the panel and drops it in front of the player. Idempotent, and it has to be:
        /// RegisterSceneLoadedCallback invokes immediately when MRUK is already initialised
        /// AND leaves the listener attached, so it can fire twice.
        /// </summary>
        private void SpawnNow()
        {
            if (_hasSpawned) return;
            _hasSpawned = true;

            _canvasGo.SetActive(true);

            // Deliberately after SetActive. OVRRaycaster.Start assigns the canvas world
            // camera, and Start does not run on an inactive GameObject -- wiring the input
            // module to a raycaster that has not started yet is how the first click of the
            // session ends up going nowhere.
            EnsurePointer(_raycaster);

            Recenter();

            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            Debug.Log($"{Tag} Panel placed at {transform.position:F2} " +
                      $"(mruk={MRUK.Instance != null} " +
                      $"room={(room != null ? room.Anchor.Uuid.ToString() : "none")})");
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextCountsPoll)
            {
                _nextCountsPoll = Time.unscaledTime + countsRefreshInterval;
                PollCounts();
            }

            if (Time.unscaledTime >= _nextDiskPoll)
            {
                _nextDiskPoll = Time.unscaledTime + diskRefreshInterval;
                RefreshDiskState();
            }

            if (Time.time >= _lastActionExpiresAt)
            {
                _lastActionExpiresAt = float.PositiveInfinity;
                _lastAction = "none yet";
                _dirty = true;
            }

            if (_dirty || Time.unscaledTime >= _nextForcedRedraw)
            {
                _dirty = false;
                _nextForcedRedraw = Time.unscaledTime + 1f;
                Redraw();
            }
        }

        // -----------------------------------------------------------------
        // Counts
        // -----------------------------------------------------------------

        /// <summary>
        /// Reads both counts from a single snapshot.
        ///
        /// Polled rather than driven by OnClusterChanged: that event fires on every accepted
        /// detection, over a hundred times a second, and never fires on the transition that
        /// actually matters here -- a cluster crossing minObservations and becoming
        /// exportable.
        ///
        /// Note that "tracked" can go DOWN. MergeOverlapping collapses two clusters into one
        /// and the id simply disappears, with no event. That is correct behaviour, not a lost
        /// object.
        /// </summary>
        private void PollCounts()
        {
            if (recorder == null) return;

            recorder.SnapshotClusters(_snapshot);

            var ready = 0;
            foreach (var view in _snapshot)
                if (view.Exportable) ready++;

            if (ready == _ready && _snapshot.Count == _tracked) return;

            _ready = ready;
            _tracked = _snapshot.Count;
            _dirty = true;
        }

        // -----------------------------------------------------------------
        // Disk
        // -----------------------------------------------------------------

        /// <summary>
        /// Parses the scan file exactly once, at startup, to learn how many objects a scan
        /// left over from a previous run holds. RoomScanIO.Load logs on every call, so doing
        /// this on the refresh timer would flood logcat.
        /// </summary>
        private void ReadDiskObjectCountOnce()
        {
            if (!File.Exists(RoomScanIO.DefaultPath)) return;

            try
            {
                var file = RoomScanIO.Load();
                _diskObjects = file != null && file.objects != null ? file.objects.Count : -1;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Could not read the existing scan file: {ex.Message}");
                _diskObjects = -1;
            }
        }

        /// <summary>
        /// Size and existence only -- never a parse. Mirrors what ConvaiRoomHealthProbe does
        /// for the same reason: this runs on a timer and the file is the only thing here that
        /// costs disk IO to inspect properly.
        /// </summary>
        private void RefreshDiskState()
        {
            var previousState = _diskState;
            var previousBytes = _diskBytes;

            var path = RoomScanIO.DefaultPath;

            if (!File.Exists(path))
            {
                _diskState = DiskState.Missing;
                _diskBytes = 0;
            }
            else
            {
                var info = new FileInfo(path);
                _diskBytes = info.Length;
                _diskWriteUtc = info.LastWriteTimeUtc;

                if (_diskBytes <= 0)
                {
                    _diskState = DiskState.Missing;
                }
                else
                {
                    // Staleness is decided from what happened this session, not from the
                    // file's timestamp. Android filesystem timestamps are only accurate to a
                    // second, so a clear immediately after a save cannot be ordered against
                    // it -- and a file left by a PREVIOUS run is a perfectly good scan, not a
                    // stale one.
                    var stale = _savedThisSessionUtc.HasValue
                                && _clearedAtUtc.HasValue
                                && _clearedAtUtc.Value > _savedThisSessionUtc.Value;

                    _diskState = stale ? DiskState.Stale : DiskState.Ready;
                }
            }

            if (_diskState != previousState || _diskBytes != previousBytes) _dirty = true;
        }

        private void HandleScanCleared()
        {
            _clearedAtUtc = DateTime.UtcNow;
            _ready = 0;
            _tracked = 0;

            RefreshDiskState();
            _dirty = true;
        }

        // -----------------------------------------------------------------
        // Actions
        // -----------------------------------------------------------------

        /// <summary>
        /// Writes the current scan to disk.
        ///
        /// Goes through BuildScanFile + RoomScanIO.Save rather than the recorder's
        /// ExportToJson wrapper, for two reasons: the write is unguarded in there, and
        /// building the file here means the count reported on the panel is the count that
        /// actually landed rather than a second, separately-built estimate.
        /// </summary>
        public void SaveScan()
        {
            if (recorder == null)
            {
                Report("save failed: no recorder in the scene");
                Debug.LogError($"{Tag} No ObjectScanRecorder in the scene; nothing to save.");
                return;
            }

            RoomScanFile file;
            try
            {
                file = recorder.BuildScanFile();
            }
            catch (Exception ex)
            {
                Report($"save failed: {ex.Message}");
                Debug.LogError($"{Tag} Could not build the scan file: {ex}");
                return;
            }

            // An empty but syntactically valid file would turn the indicator green over
            // something the next phase cannot use. Refusing, and saying why, is honest.
            if (file.objects.Count == 0)
            {
                Report($"nothing ready to save ({_tracked} tracked, 0 ready)");
                Debug.LogWarning($"{Tag} Save refused: no cluster has reached " +
                                 $"{recorder.minObservations} observations yet.");
                return;
            }

            try
            {
                RoomScanIO.Save(file, null);
            }
            catch (Exception ex)
            {
                Report($"save FAILED: {ex.Message}");
                Debug.LogError($"{Tag} Save failed writing {RoomScanIO.DefaultPath}: {ex}");
                RefreshDiskState();
                return;
            }

            _savedThisSessionUtc = DateTime.UtcNow;
            _diskObjects = file.objects.Count;

            Report($"saved {file.objects.Count} objects");
            Debug.Log($"{Tag} Saved {file.objects.Count} objects -> {RoomScanIO.DefaultPath}");

            // Straight away rather than on the next tick: the indicator flipping a second
            // after the button press reads as a bug.
            RefreshDiskState();
        }

        /// <summary>
        /// Deliberately not wired. Phase 2 does not exist yet, and a button that silently does
        /// nothing is indistinguishable from a broken one -- so this acknowledges the press
        /// and says why. Do not delete this thinking it is dead code; delete it when you
        /// replace it with the real phase transition.
        /// </summary>
        private void NextPhaseNotWired()
        {
            Report("next phase is not wired up yet");
            Debug.Log($"{Tag} Next-phase button pressed; deliberately not wired up yet " +
                      $"(phase 1 only).");
        }

        /// <summary>Drops the panel back in front of the player, facing them.</summary>
        public void Recenter()
        {
            if (!_canMove)
            {
                Report("recenter refused: panel shares the rebuilder's object");
                return;
            }

            var head = Camera.main;
            if (head == null)
            {
                Debug.LogWarning($"{Tag} No main camera, so the panel cannot be placed " +
                                 $"relative to the player.");
                return;
            }

            var forward = head.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();

            transform.position = head.transform.position + forward * distanceFromPlayer
                                                         + Vector3.up * heightOffset;

            // The canvas draws on its +Z face, so matching the player's forward turns the
            // readable side toward them rather than away.
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            Report("panel recentered");
        }

        /// <summary>Posts a transient message to the panel's last-action line.</summary>
        private void Report(string message)
        {
            _lastAction = message;
            _lastActionExpiresAt = Time.time + 6f;
            _dirty = true;
        }

        // -----------------------------------------------------------------
        // Panel construction
        // -----------------------------------------------------------------

        private void BuildUi()
        {
            _canvasGo = new GameObject("Panel Canvas", typeof(RectTransform));
            _canvasGo.transform.SetParent(transform, false);

            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRect = (RectTransform)_canvasGo.transform;
            canvasRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            canvasRect.localScale = Vector3.one * (panelWidth / CanvasWidth);

            // OVRRaycaster rather than the stock GraphicRaycaster: the stock one only
            // understands a screen-space mouse and cannot be driven by a tracked ray.
            _raycaster = _canvasGo.AddComponent<OVRRaycaster>();

            var background = MakeRect(canvasRect, "Background", 0f, 0f, CanvasWidth, CanvasHeight)
                .gameObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.07f, 0.10f, 0.85f);

            _titleText = MakeText(canvasRect, "Title", 16f, 12f, CanvasWidth - 32f, 28f,
                                  24, TextAnchor.MiddleLeft);
            _titleText.text = "PHASE 1 - SCAN";
            _titleText.color = new Color(1f, 0.85f, 0.3f);

            _countsText = MakeText(canvasRect, "Counts", 16f, 44f, CanvasWidth - 32f, 44f,
                                   34, TextAnchor.MiddleLeft);

            _statusText = MakeText(canvasRect, "Status", 16f, 92f, CanvasWidth - 32f, 118f,
                                   16, TextAnchor.UpperLeft);

            // 54 units tall rather than 46. On a 0.42 m canvas that is roughly 2.5 cm in the
            // world -- worth the extra height because a hand ray jitters noticeably more than
            // a controller ray, and a button you cannot reliably hit reads as a broken one.
            MakeButton(canvasRect, "Save Button", "SAVE SCAN",
                       16f, 214f, 186f, 54f, SaveScan);
            MakeButton(canvasRect, "Recenter Button", "RECENTER",
                       218f, 214f, 186f, 54f, Recenter);

            var nextPhase = MakeButton(canvasRect, "Next Phase Button",
                                       "NEXT PHASE <color=#7a7a80>(not wired)</color>",
                                       16f, 274f, CanvasWidth - 32f, 54f, NextPhaseNotWired);

            // Left interactable on purpose. A non-interactable Selectable receives no pointer
            // events at all -- no hover, no press, nothing -- which is exactly how a broken
            // button behaves. It is styled as locked instead, and says so when pressed.
            var nextPhaseImage = nextPhase.targetGraphic as Image;
            if (nextPhaseImage != null) nextPhaseImage.color = LockedButton;

            var nextPhaseLabel = nextPhase.GetComponentInChildren<Text>();
            if (nextPhaseLabel != null) nextPhaseLabel.color = LockedLabel;

            _cursor = ConvaiRoomLaserCursor.Create();
        }

        /// <summary>
        /// Makes sure something in the scene is actually driving UI events from a tracked
        /// controller. Without an OVRInputModule the buttons render but never receive a click,
        /// which is indistinguishable from the panel being broken.
        /// </summary>
        private void EnsurePointer(OVRRaycaster raycaster)
        {
            // OVRInputModule.instance FIRST, and it matters. OVRHand and OVRControllerHelper
            // register themselves as input sources against that static singleton, and
            // Process() drives the cursor from whichever registered source is active --
            // preferring them over the legacy rayTransform path entirely.
            //
            // The previous version looked the module up with FindAnyObjectByType, which skips
            // inactive objects. Miss the SDK's module that way and we build a SECOND one:
            // the hands and controllers stay registered with the first, while our cursor and
            // raycaster get attached to the second. Nothing then drives the beam and no click
            // ever lands, from controllers or hands alike -- which is exactly the "no rays at
            // all" this used to produce on device.
            var module = OVRInputModule.instance;
            var created = false;

            if (module == null)
                module = FindAnyObjectByType<OVRInputModule>(FindObjectsInactive.Include);

            if (module == null)
            {
                var eventSystem = FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);
                var host = eventSystem != null ? eventSystem.gameObject : new GameObject("EventSystem");

                if (eventSystem == null) host.AddComponent<EventSystem>();

                // Two input modules on one EventSystem fight over the pointer, and the
                // standalone one only understands mouse and keyboard.
                var standalone = host.GetComponent<StandaloneInputModule>();
                if (standalone != null) standalone.enabled = false;

                module = host.AddComponent<OVRInputModule>();
                created = true;
            }

            // A module on a deactivated object never runs Process(), so it would look wired
            // up and still do nothing.
            if (!module.gameObject.activeInHierarchy) module.gameObject.SetActive(true);
            module.enabled = true;

            // Still set, but only as the fallback for when no controller or hand has
            // registered. Registered sources supply their own pointer pose and press --
            // the controller's trigger, or a hand's index pinch -- so hands need no
            // separate wiring here.
            module.rayTransform = ControllerAnchor();
            module.joyPadClickButton = clickButton;
            module.m_Cursor = _cursor;

            // Assigned here rather than left to OVRRaycaster's OnPointerEnter: until some
            // pointer has entered a canvas the module holds no raycaster, so the very first
            // click of the session has nothing to hit.
            module.activeGraphicRaycaster = raycaster;
            raycaster.pointer = _cursor.gameObject;

            Debug.Log($"{Tag} Pointer bound to OVRInputModule on '{module.gameObject.name}' " +
                      $"(created={created} isSingleton={ReferenceEquals(module, OVRInputModule.instance)} " +
                      $"active={module.gameObject.activeInHierarchy} ray={module.rayTransform?.name ?? "NONE"}).");
        }

        private Transform ControllerAnchor()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();

            if (rig == null)
            {
                Debug.LogWarning($"{Tag} No OVRCameraRig, so the laser falls back to the head. " +
                                 $"Panel buttons will follow your gaze.");
                return Camera.main != null ? Camera.main.transform : null;
            }

            if (rig.rightHandAnchor != null) return rig.rightHandAnchor;
            if (rig.leftHandAnchor != null) return rig.leftHandAnchor;
            return rig.centerEyeAnchor;
        }

        private RectTransform MakeRect(Transform parent, string name,
                                       float x, float y, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;

            // Anchored to the top-left corner so the layout numbers above read as an ordinary
            // top-down stack rather than offsets from the centre.
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private Text MakeText(Transform parent, string name, float x, float y,
                              float width, float height, int fontSize, TextAnchor alignment)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = ScanLabel.BuiltinFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // Labels never take the click; the button underneath them does.
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// Builds one panel button. Returns the Button rather than its Image so callers can
        /// reach both the tint target and the label -- the locked next-phase button needs to
        /// recolour both.
        /// </summary>
        private Button MakeButton(Transform parent, string name, string label,
                                  float x, float y, float width, float height, UnityAction onClick)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var image = rect.gameObject.AddComponent<Image>();
            image.color = ActionButton;

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = ButtonColors();
            button.onClick.AddListener(onClick);

            MakeText(rect, "label", 0f, 0f, width, height, 20, TextAnchor.MiddleCenter).text = label;
            return button;
        }

        private static ColorBlock ButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;

            // ColorTint multiplies the graphic's own colour, so white leaves the base tint
            // alone and the per-button colours above still show through.
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.06f;
            return colors;
        }

        // -----------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------

        private void Redraw()
        {
            if (_countsText != null)
            {
                _countsText.text = $"{_ready} ready / {_tracked} tracked";
                _countsText.color = _ready > 0
                    ? new Color(0.5f, 0.9f, 1f)
                    : new Color(0.75f, 0.75f, 0.78f);
            }

            if (_statusText == null) return;

            _builder.Clear();

            // Read the thresholds off the recorder rather than hardcoding them, so this line
            // cannot go stale if someone tunes the clustering.
            if (recorder != null)
                _builder.AppendLine($"ready = seen {recorder.minObservations}+ times, " +
                                    $"under {recorder.maxObjectSize} m");
            else
                _builder.AppendLine("<color=#ff8080>no recorder in the scene</color>");

            _builder.AppendLine(DiskLine());
            _builder.AppendLine(AnchorLine());
            _builder.AppendLine();
            _builder.AppendLine($"last: {_lastAction}");

            _statusText.text = _builder.ToString();
        }

        private string DiskLine()
        {
            switch (_diskState)
            {
                case DiskState.Missing:
                    return "scan file : <color=#ff8080>NO</color> - nothing saved yet";

                case DiskState.Stale:
                    return $"scan file : <color=#ffc44d>STALE</color> - saved before you " +
                           $"cleared ({Kilobytes()})";

                default:
                    var objects = _diskObjects >= 0 ? $"{_diskObjects} objects, " : "";
                    return $"scan file : <color=#7fd97f>YES</color> - {objects}" +
                           $"{Kilobytes()}, {Age()}";
            }
        }

        private string AnchorLine()
        {
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;

            return room != null
                ? "anchored  : <color=#7fd97f>MRUK room</color>"
                : "anchored  : <color=#ffc44d>RAW WORLD SPACE</color>";
        }

        private string Kilobytes() => $"{_diskBytes / 1024f:0.0} KB";

        private string Age()
        {
            var age = DateTime.UtcNow - _diskWriteUtc;

            if (age.TotalSeconds < 60) return "just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
            return $"{(int)age.TotalDays}d ago";
        }
    }
}
