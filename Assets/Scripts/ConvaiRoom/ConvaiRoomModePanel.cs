using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;
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
    ///
    /// The layout lives in the Scan Panel prefab, not in here. This class only reads and
    /// writes the pieces it is handed: an earlier version built the whole canvas in code,
    /// which meant every spacing tweak was a recompile and a redeploy to see. Restyle the
    /// prefab freely -- move things, resize them, recolour them, swap the fonts. The only
    /// contract is that the references below stay assigned.
    ///
    /// Tools > Convai Room > Bake Scan Panel Prefab regenerates a stock panel if the prefab
    /// is ever lost.
    /// </summary>
    public class ConvaiRoomModePanel : MonoBehaviour
    {
        private const string Tag = "[ScanPanel]";

        /// <summary>What the scan file on disk is currently worth.</summary>
        private enum DiskState
        {
            Missing,
            Stale,
            Ready
        }

        [Header("Wiring (left empty, this is found in the scene)")]
        public ObjectScanRecorder recorder;

        [Tooltip("Replays room_scan.json. Drives the LOAD SAVED SCAN button.")]
        public RoomScanRebuilder rebuilder;

        [Tooltip("Read only, and only for its button bindings -- the controls readout takes " +
                 "the labels from here so it cannot go stale if someone rebinds them.")]
        public RoomScanController scanController;

        [Tooltip("Bakes a walkable NavMesh from the replayed scan. Drives BAKE NAVMESH.")]
        public RoomScanNavMeshBuilder navMeshBuilder;

        [Tooltip("Optional. Draws the baked NavMesh so you can see it in the headset.")]
        public NavMeshVisualizer navMeshVisualizer;

        [Header("Panel UI (all from the prefab, all required)")]
        [Tooltip("The world-space canvas. This is also the object that gets hidden until MRUK " +
                 "reports its room, so it must be the panel's canvas rather than a child of it.")]
        [SerializeField] private OVRRaycaster _raycaster;

        // The title is deliberately not referenced here. It is a static label -- the prefab
        // owns its text and colour outright, and phase 2 can add a field when it actually
        // needs to change it.

        [Tooltip("The big 'N ready / N tracked' line. Overwritten every redraw.")]
        [SerializeField] private Text _countsText;

        [Tooltip("The multi-line status block. Overwritten every redraw, rich text and all.")]
        [SerializeField] private Text _statusText;

        [Tooltip("The controller-bindings block. Written once on startup from the actual " +
                 "bindings, not every frame -- they cannot change while the scene runs.")]
        [SerializeField] private Text _controlsText;

        [SerializeField] private Button _saveButton;
        [SerializeField] private Button _recenterButton;

        [Tooltip("Replays whatever room_scan.json is on disk, without touching the live scan.")]
        [SerializeField] private Button _loadButton;

        [Tooltip("Bakes the NavMesh from whatever the rebuilder is currently holding.")]
        [SerializeField] private Button _bakeButton;

        [Tooltip("Styled as locked but left interactable on purpose -- see NextPhaseNotWired.")]
        [SerializeField] private Button _nextPhaseButton;

        [Header("Panel placement")]
        [Tooltip("Drop the panel in front of the player once MRUK reports its scene. Once " +
                 "placed it stays put -- it never follows your head.")]
        public bool placeOnStart = true;

        public float distanceFromPlayer = 1.2f;
        public float heightOffset = -0.25f;

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

        [Header("Counts colour")]
        [Tooltip("The counts line is the one piece of styling the panel still drives, because " +
                 "the colour carries meaning: this one when something is ready to export...")]
        [SerializeField] private Color countsReadyColor = new Color(0.5f, 0.9f, 1f);

        [Tooltip("...and this one when nothing is. Setting the colour on the Text in the " +
                 "prefab will not stick -- change these instead.")]
        [SerializeField] private Color countsIdleColor = new Color(0.75f, 0.75f, 0.78f);

        private ConvaiRoomLaserCursor _cursor;

        /// <summary>The canvas GameObject, which is always the raycaster's own.</summary>
        private GameObject _canvasGo;

        private bool _canMove = true;
        private bool _hasSpawned;

        /// <summary>
        /// False when the prefab references are not all assigned. Everything downstream is
        /// skipped rather than throwing a null reference per frame, which on a headset buries
        /// the one error that says what is actually wrong.
        /// </summary>
        private bool _wired;

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
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (scanController == null) scanController = FindAnyObjectByType<RoomScanController>();
            if (navMeshBuilder == null) navMeshBuilder = FindAnyObjectByType<RoomScanNavMeshBuilder>();
            if (navMeshVisualizer == null) navMeshVisualizer = FindAnyObjectByType<NavMeshVisualizer>();

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

            _wired = ValidateWiring();
            if (!_wired) return;

            // The raycaster requires a Canvas on its own GameObject, so these can never be two
            // different objects -- one field instead of two, and no way to wire them apart.
            _canvasGo = _raycaster.gameObject;

            BindButtons();
            WriteControls();
            _cursor = ConvaiRoomLaserCursor.Create();

            // Hidden until MRUK reports in, so the panel does not flash up somewhere
            // arbitrary and then jump once the room arrives.
            _canvasGo.SetActive(false);
        }

        /// <summary>
        /// Reports every unassigned reference in one go, naming the fields.
        ///
        /// One message rather than a guard at each use site: a panel that is missing its
        /// prefab wiring is not partially broken, it is broken, and the useful thing to know
        /// on a headset is the whole list at once rather than whichever field the next frame
        /// happened to touch first.
        /// </summary>
        private bool ValidateWiring()
        {
            var missing = new List<string>();

            if (_raycaster == null) missing.Add(nameof(_raycaster));
            if (_countsText == null) missing.Add(nameof(_countsText));
            if (_statusText == null) missing.Add(nameof(_statusText));
            if (_controlsText == null) missing.Add(nameof(_controlsText));
            if (_saveButton == null) missing.Add(nameof(_saveButton));
            if (_recenterButton == null) missing.Add(nameof(_recenterButton));
            if (_loadButton == null) missing.Add(nameof(_loadButton));
            if (_bakeButton == null) missing.Add(nameof(_bakeButton));
            if (_nextPhaseButton == null) missing.Add(nameof(_nextPhaseButton));

            if (missing.Count == 0) return true;

            Debug.LogError($"{Tag} The panel is not wired up: {string.Join(", ", missing)} " +
                           $"{(missing.Count == 1 ? "is" : "are")} unassigned. This component " +
                           $"expects to live on the Scan Panel prefab. The panel will not be " +
                           $"shown. Re-bake a stock one from Tools > Convai Room > Bake Scan " +
                           $"Panel Prefab if it has been lost.", this);
            return false;
        }

        /// <summary>
        /// Binds the button handlers here rather than through the Inspector's onClick lists.
        /// A UnityEvent wired in the editor holds the method by name, so renaming one of these
        /// silently unbinds the button and the only symptom is a press that does nothing on a
        /// headset. Bound this way, the same rename is a compile error.
        /// </summary>
        private void BindButtons()
        {
            _saveButton.onClick.AddListener(SaveScan);
            _recenterButton.onClick.AddListener(Recenter);
            _loadButton.onClick.AddListener(LoadSavedScan);
            _bakeButton.onClick.AddListener(BakeNavMesh);
            _nextPhaseButton.onClick.AddListener(NextPhaseNotWired);
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
            if (!_wired) return;

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
            if (!_wired) return;

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
        /// Replays whatever room_scan.json is on disk.
        ///
        /// Available before and during a scan on purpose. The rebuilt boxes are the
        /// rebuilder's own objects, separate from anything the recorder is accumulating, so
        /// this disturbs nothing -- it just shows what is already saved, which is the only
        /// way to judge whether a previous scan is worth keeping before overwriting it.
        /// </summary>
        public void LoadSavedScan()
        {
            if (rebuilder == null)
            {
                Report("load failed: no rebuilder in the scene");
                Debug.LogError($"{Tag} No RoomScanRebuilder in the scene, so there is nothing " +
                               $"that can replay the scan file.");
                return;
            }

            rebuilder.Rebuild();

            if (rebuilder.Scan == null)
            {
                Report("nothing to load: no scan file on disk");
                return;
            }

            var count = rebuilder.Rebuilt.Count;
            var alignment = rebuilder.Alignment;

            // Whether the boxes were corrected onto the current walls or dropped in on the
            // file's own coordinates is worth saying: the two look identical right up until
            // you notice the whole room is rotated.
            Report(alignment.Applied
                ? $"loaded {count} objects, aligned to walls ({alignment.Error:F2} m)"
                : $"loaded {count} objects, file coordinates");

            Debug.Log($"{Tag} Loaded {count} objects from {RoomScanIO.DefaultPath} " +
                      $"(alignment: {alignment.Summary}).");
        }

        /// <summary>
        /// Bakes a walkable NavMesh from whatever the rebuilder is currently holding.
        ///
        /// Takes the replayed scan rather than the live one on purpose: the bake needs the
        /// room's floor polygon and a settled set of objects, and a cluster that is still
        /// gaining observations is neither. Load a scan first -- and the refusal below says
        /// so rather than baking an empty floor and reporting success.
        /// </summary>
        public void BakeNavMesh()
        {
            if (navMeshBuilder == null)
            {
                Report("bake failed: no navmesh builder in the scene");
                Debug.LogError($"{Tag} No RoomScanNavMeshBuilder in the scene; nothing can bake.");
                return;
            }

            if (rebuilder == null || rebuilder.Scan == null)
            {
                Report("bake needs a loaded scan -- press LOAD SAVED SCAN first");
                Debug.LogWarning($"{Tag} Bake refused: the rebuilder is holding no scan, so " +
                                 $"there is no floor to bake and nothing to walk around.");
                return;
            }

            if (!navMeshBuilder.Build())
            {
                Report("bake FAILED -- see the log");
                return;
            }

            // Redrawn straight after, not on the next tick. The wireframe is the only way to
            // tell a good bake from a bad one in a headset, and a bake you cannot see yet
            // reads as one that did not happen.
            if (navMeshVisualizer != null) navMeshVisualizer.Refresh();

            var triangles = navMeshVisualizer != null ? navMeshVisualizer.TriangleCount : 0;

            Report($"baked navmesh: {navMeshBuilder.ObstacleCount} obstacles" +
                   (triangles > 0 ? $", {triangles} tris" : ""));
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

        /// <summary>
        /// Writes the controller bindings into the panel, once.
        ///
        /// The labels come from RoomScanController's own binding fields rather than being
        /// typed out here, so rebinding one cannot leave this readout quietly lying about
        /// which button does what. Written once rather than per redraw because those fields
        /// are serialized -- they cannot change while the scene is running.
        /// </summary>
        private void WriteControls()
        {
            _builder.Clear();
            _builder.AppendLine("<color=#ffd54d>CONTROLS</color>");

            if (scanController != null)
                _builder.AppendLine($"{ButtonLabel(scanController.exportButton)} save   " +
                                    $"{ButtonLabel(scanController.clearButton)} clear   " +
                                    $"{ButtonLabel(scanController.rebuildButton)} load   " +
                                    $"{ButtonLabel(scanController.shellToggleButton)} outline");
            else
                _builder.AppendLine("<color=#ff8080>no RoomScanController in the scene</color>");

            // Worth its own line. OVRInput promotes hand tracking to the active controller,
            // and under it every face button above resolves to nothing -- so on hands the
            // panel is not a convenience, it is the only way to drive any of this.
            _builder.Append("<color=#9a9aa0>face buttons need controllers; on hand tracking " +
                            "use the buttons below</color>");

            _controlsText.text = _builder.ToString();
        }

        /// <summary>
        /// The name printed on the headset's controller for a binding. OVRInput's enum names
        /// are positional -- One, Two, Three, Four -- and nobody wearing the thing knows which
        /// of those is the button under their thumb.
        /// </summary>
        private static string ButtonLabel(OVRInput.Button button)
        {
            switch (button)
            {
                case OVRInput.Button.One: return "A";
                case OVRInput.Button.Two: return "B";
                case OVRInput.Button.Three: return "X";
                case OVRInput.Button.Four: return "Y";
                case OVRInput.Button.PrimaryIndexTrigger: return "L trigger";
                case OVRInput.Button.SecondaryIndexTrigger: return "R trigger";
                case OVRInput.Button.PrimaryHandTrigger: return "L grip";
                case OVRInput.Button.SecondaryHandTrigger: return "R grip";
                case OVRInput.Button.Start: return "menu";
                default: return button.ToString();
            }
        }

        // -----------------------------------------------------------------
        // Pointer plumbing
        // -----------------------------------------------------------------

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

            // THE reason there was no laser on device. OVRInputModule.IsModuleSupported()
            // returns "allowActivationOnMobileDevice || Input.mousePresent", and EventSystem
            // will only ever select a module that reports itself supported. The field is
            // false by default and the only thing that sets it true is the SDK's Reset(),
            // which is #if UNITY_EDITOR and only runs when a human adds the component in the
            // Inspector -- AddComponent at runtime never calls it.
            //
            // So on a headset, where there is no mouse, the module we built was never chosen,
            // Process() never ran, and nothing drove the cursor or delivered a click. In the
            // Editor Input.mousePresent is true, which is why this looked fine right up until
            // it was tried on device. Forced on every module we touch, found or created,
            // because an authored one with the box unticked is the same dead end.
            module.allowActivationOnMobileDevice = true;

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
                      $"active={module.gameObject.activeInHierarchy} ray={module.rayTransform?.name ?? "NONE"} " +
                      $"supported={module.IsModuleSupported()} mouse={Input.mousePresent}).");
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

        // -----------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------

        private void Redraw()
        {
            _countsText.text = $"{_ready} ready / {_tracked} tracked";
            _countsText.color = _ready > 0 ? countsReadyColor : countsIdleColor;

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
            _builder.AppendLine(NavMeshLine());
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

        /// <summary>
        /// Whether anything is baked, and how much of the room it covers.
        ///
        /// Obstacle count is the useful number rather than triangles: it is the one that says
        /// the scan actually reached the bake. A floor with zero obstacles bakes perfectly
        /// happily and means the furniture never made it through.
        /// </summary>
        private string NavMeshLine()
        {
            if (navMeshBuilder == null)
                return "navmesh   : <color=#9a9aa0>no builder in the scene</color>";

            if (!navMeshBuilder.HasNavMesh)
                return "navmesh   : <color=#ffc44d>NO</color> - not baked yet";

            var triangles = navMeshVisualizer != null && navMeshVisualizer.TriangleCount > 0
                ? $", {navMeshVisualizer.TriangleCount} tris"
                : "";

            return $"navmesh   : <color=#7fd97f>YES</color> - " +
                   $"{navMeshBuilder.ObstacleCount} obstacles{triangles}";
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
