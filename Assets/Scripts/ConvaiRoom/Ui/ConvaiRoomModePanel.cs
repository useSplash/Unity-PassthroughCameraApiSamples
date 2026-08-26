using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Meta.XR.BuildingBlocks.AIBlocks;
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

        /// <summary>
        /// Which half of the flow the room is in.
        ///
        /// Not a mode in the old Scan/Talk sense -- these are sequential. Scanning produces the
        /// floor the character stands on, so the character phase cannot be entered until there
        /// is one, and going back to Scan takes the character away again rather than leaving it
        /// standing in a room that is being re-measured underneath it.
        /// </summary>
        private enum Phase
        {
            Scan,
            Character
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

        [Tooltip("The growing yellow/green wireframes. Switched off when scanning stops, " +
                 "which is what actually clears them -- it destroys its boxes in OnDisable.")]
        public LiveScanVisualizer liveVisualizer;

        [Tooltip("The YOLO runner. Switched off when scanning stops so inference ends at the " +
                 "source rather than producing detections nobody is looking at.")]
        public ObjectDetectionAgent detectionAgent;

        [Tooltip("Puts the phase 2 character in the room. Drives the phase button -- without " +
                 "one the panel stays in phase 1 and says so.")]
        public RoomCharacterSpawner characterSpawner;

        [Tooltip("Optional, and read only. Opens the Convai session for the spawned character; " +
                 "this is where the panel gets 'connecting' and 'listening' from. Nothing here " +
                 "drives it -- it follows the spawner on its own.")]
        public RoomCharacterVoice characterVoice;

        [Tooltip("Optional, and read only. Tells the character what the scan found; the panel " +
                 "reports how many objects she was told about, which is the only way to see " +
                 "from inside the headset that she knows the room at all.")]
        public RoomScanContext roomContext;

        [Tooltip("The task plan, when one is being worked through. Optional -- without one " +
                 "the plan block simply never appears and the three plan buttons say so.")]
        public RoomTaskPlan plan;

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

        [Tooltip("Replays whatever room_scan.json is on disk, without touching the live scan.")]
        [SerializeField] private Button _loadButton;

        [Tooltip("Bakes the NavMesh from whatever the rebuilder is currently holding.")]
        [SerializeField] private Button _bakeButton;

        [Tooltip("Quits the app. Sits away from the others in the title bar, and takes two " +
                 "presses -- see ExitApplication.")]
        [SerializeField] private Button _exitButton;

        [Tooltip("Starts and stops scanning. Its label is the action it will take, not the " +
                 "state it is in -- see ToggleScanning.")]
        [SerializeField] private Button _scanToggleButton;

        [Tooltip("Moves between the scan and the character phase. Its label is the direction " +
                 "it will take you, not the phase you are in -- see UpdatePhaseLabel.")]
        [SerializeField] private Button _nextPhaseButton;

        [Tooltip("Steps back through the plan. Does the same thing as saying 'go back', and " +
                 "exists because a plan you are working through is the one thing here you " +
                 "touch repeatedly -- saying it every time gets old, and voice fails in a " +
                 "noisy room.")]
        [SerializeField] private Button _planBackButton;

        [Tooltip("Steps forward through the plan. The most-pressed control on the panel " +
                 "whenever a plan is up.")]
        [SerializeField] private Button _planNextButton;

        [Tooltip("Throws the plan away. She keeps whatever she has been told, but stops being " +
                 "asked about it and the readout goes back to the room.")]
        [SerializeField] private Button _planClearButton;

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

        [Header("Exit")]
        [Tooltip("How long the first EXIT press stays armed. Press it again inside this to " +
                 "quit; let it lapse and the next press starts over.")]
        public float exitConfirmSeconds = 5f;

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
        /// False when the panel shares a GameObject with the rebuilder, where moving it would
        /// drag every replayed box along too. Public because the dragger has to honour the
        /// same refusal Recenter does -- there is no point guarding one route and not the other.
        /// </summary>
        public bool CanMove => _canMove;

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

        // Convai session, as last drawn. Only here so a change can be spotted -- the line
        // itself is built from the voice component each redraw.
        private RoomCharacterVoice.VoiceState _voiceState = RoomCharacterVoice.VoiceState.Idle;
        private bool _voiceSpeaking;

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

        /// <summary>
        /// While this is in the future, a second EXIT press quits. Deliberately shorter-lived
        /// than the status line that announces it, so the button can never still be armed
        /// after the message telling you so has gone.
        /// </summary>
        private float _exitArmedUntil = float.NegativeInfinity;

        /// <summary>
        /// Whether detection is running. Starts true because phase 1 opens straight into a
        /// scan -- see ApplyScanning in Awake, which pushes this onto the scene rather than
        /// trusting the components to already agree with it.
        /// </summary>
        private bool _scanning = true;

        /// <summary>
        /// Which phase the room is in. Starts in Scan, and nothing moves it but
        /// <see cref="TogglePhase"/> -- the panel is the only thing that decides this, so
        /// there is exactly one place a wrong phase can come from.
        /// </summary>
        private Phase _phase = Phase.Scan;

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
            if (liveVisualizer == null) liveVisualizer = FindAnyObjectByType<LiveScanVisualizer>();
            if (detectionAgent == null) detectionAgent = FindAnyObjectByType<ObjectDetectionAgent>();
            if (characterSpawner == null) characterSpawner = FindAnyObjectByType<RoomCharacterSpawner>();
            if (characterVoice == null) characterVoice = FindAnyObjectByType<RoomCharacterVoice>();
            if (roomContext == null) roomContext = FindAnyObjectByType<RoomScanContext>();

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

            // Pushes the scene into whatever _scanning says rather than assuming it already
            // agrees. The three components have their own enabled flags in the scene, and a
            // panel that says SCANNING while the agent is switched off is worse than useless.
            ApplyScanning(_scanning);

            // Pushed onto the prefab rather than trusted, for the same reason ApplyScanning is:
            // the label is authored text and nothing stops it having been left saying something
            // else. This is the one that says which phase pressing it moves you to.
            UpdatePhaseLabel();

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
            if (_loadButton == null) missing.Add(nameof(_loadButton));
            if (_bakeButton == null) missing.Add(nameof(_bakeButton));
            if (_exitButton == null) missing.Add(nameof(_exitButton));
            if (_scanToggleButton == null) missing.Add(nameof(_scanToggleButton));
            if (_nextPhaseButton == null) missing.Add(nameof(_nextPhaseButton));
            if (_planBackButton == null) missing.Add(nameof(_planBackButton));
            if (_planNextButton == null) missing.Add(nameof(_planNextButton));
            if (_planClearButton == null) missing.Add(nameof(_planClearButton));

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
            _loadButton.onClick.AddListener(LoadSavedScan);
            _bakeButton.onClick.AddListener(BakeNavMesh);
            _exitButton.onClick.AddListener(ExitApplication);
            _scanToggleButton.onClick.AddListener(ScanToggleOrRespawn);
            _nextPhaseButton.onClick.AddListener(TogglePhase);
            _planBackButton.onClick.AddListener(PlanBack);
            _planNextButton.onClick.AddListener(PlanNext);
            _planClearButton.onClick.AddListener(PlanClear);
        }

        // -----------------------------------------------------------------
        // Plan
        // -----------------------------------------------------------------

        /// <summary>
        /// Steps the plan from the panel.
        ///
        /// These do the same thing as saying "next", and they exist because voice is a poor
        /// sole control for the one action you take over and over: a plan is stepped through
        /// once per step, in a room that may be noisy, often while your hands are busy with the
        /// thing the step is about. What they deliberately do NOT do is make her say the step
        /// out loud -- the panel is the quiet route through a plan, and the spoken route is
        /// still there for anyone who wants it.
        /// </summary>
        private void PlanNext() => StepPlan(1, "next step");

        private void PlanBack() => StepPlan(-1, "previous step");

        private void StepPlan(int delta, string label)
        {
            if (plan == null || !plan.HasPlan)
            {
                _lastAction = "no plan to step through";
                return;
            }

            _lastAction = plan.TryMove(delta, out var step)
                ? $"{label}: {step.Number}/{plan.Steps.Count}"
                : delta > 0 ? "already at the last step" : "already at the first step";
        }

        private void PlanClear()
        {
            if (plan == null || !plan.HasPlan)
            {
                _lastAction = "no plan to clear";
                return;
            }

            plan.Clear();
            _lastAction = "plan cleared";
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

            // Polled rather than evented, and cheap enough to do every frame: the session moves
            // through mic -> connecting -> listening in the first couple of seconds of the
            // character phase, and a readout that only catches up on the once-a-second forced
            // redraw spends that whole window showing the wrong step.
            PollVoice();

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

        /// <summary>Marks the panel dirty when the Convai session moves on.</summary>
        private void PollVoice()
        {
            if (characterVoice == null) return;

            var state = characterVoice.State;
            var speaking = characterVoice.IsSpeaking;

            if (state == _voiceState && speaking == _voiceSpeaking) return;

            _voiceState = state;
            _voiceSpeaking = speaking;
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
        /// Starts and stops scanning.
        ///
        /// Stops it at the source rather than just hiding the result. Left running, the YOLO
        /// agent costs an inference pass every frame producing detections nobody is looking
        /// at, and on a headset that is heat and battery you can feel.
        ///
        /// Three things move together, and each is load-bearing:
        ///   - the detection agent, which is where the cost actually is;
        ///   - LiveScanVisualizer, whose OnDisable destroys its boxes -- switching it off is
        ///     what clears the yellow and green wireframes rather than merely stopping new
        ///     ones appearing;
        ///   - the recorder, which stops accumulating.
        ///
        /// What is deliberately NOT touched: RoomScanRebuilder and its replayed boxes, the
        /// navmesh, and the scan already held in memory. Stopping is a pause, not a discard --
        /// SAVE SCAN still writes exactly what was collected up to the moment you stopped,
        /// which is the whole reason you would want to stop before saving.
        /// </summary>
        public void ToggleScanning()
        {
            ApplyScanning(!_scanning);

            Report(_scanning
                ? "scanning resumed"
                : $"scanning stopped -- {_ready} ready / {_tracked} tracked kept");
        }

        private void ApplyScanning(bool scanning)
        {
            _scanning = scanning;

            if (detectionAgent != null) detectionAgent.enabled = scanning;
            if (liveVisualizer != null) liveVisualizer.enabled = scanning;
            if (recorder != null) recorder.enabled = scanning;

            UpdateScanToggleLabel();
            _dirty = true;

            Debug.Log($"{Tag} Scanning -> {scanning} " +
                      $"(agent={(detectionAgent != null ? scanning.ToString() : "absent")} " +
                      $"liveBoxes={(liveVisualizer != null ? scanning.ToString() : "absent")} " +
                      $"recorder={(recorder != null ? scanning.ToString() : "absent")}).");
        }

        /// <summary>
        /// Labels the button with what pressing it will DO, not what the state currently is.
        ///
        /// An earlier panel avoided toggles entirely, using separate Scan and Talk buttons so
        /// there was no question which way one would flip. That worry was right; the answer
        /// here is to name the action instead, which cannot be read two ways. The current
        /// state is on the status line, where state belongs.
        /// </summary>
        private void UpdateScanToggleLabel()
        {
            if (_scanToggleButton == null) return;

            var label = _scanToggleButton.GetComponentInChildren<Text>();
            if (label == null) return;

            label.text = _phase == Phase.Character
                ? "RESPAWN"
                : _scanning ? "STOP SCANNING" : "START SCANNING";
        }

        /// <summary>
        /// Quits the app, on the second press.
        ///
        /// Two presses rather than one because of what a misfire costs here. A hand ray
        /// jitters, the panel is the only way to drive anything without controllers, and the
        /// thing an accidental exit throws away is a scan you may have spent ten minutes
        /// walking around a room to collect. The first press says how many objects are sitting
        /// in memory so the number you would lose is in front of you before you confirm.
        ///
        /// There is no dialog because a world-space modal is another thing to aim at. The
        /// existing transient status line does the job and disarms itself.
        /// </summary>
        public void ExitApplication()
        {
            if (Time.unscaledTime > _exitArmedUntil)
            {
                _exitArmedUntil = Time.unscaledTime + exitConfirmSeconds;

                Report(_ready > 0
                    ? $"press EXIT again to quit -- {_ready} objects in memory"
                    : "press EXIT again to quit");
                return;
            }

            _exitArmedUntil = float.NegativeInfinity;

            Debug.Log($"{Tag} Exit confirmed; quitting with {_ready} ready / {_tracked} " +
                      $"tracked in memory.");

            Quit();
        }

        /// <summary>
        /// Application.Quit does nothing in the Editor, which makes the button look broken on
        /// the one platform where you are most likely to be testing it.
        /// </summary>
        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>
        /// Moves between the scan phase and the character phase.
        ///
        /// One button rather than two because the two directions are never both available:
        /// you are in one phase or the other, and a pair of buttons would spend all its time
        /// with one of them greyed out. The label says which way it goes -- see
        /// <see cref="UpdatePhaseLabel"/>.
        /// </summary>
        public void TogglePhase()
        {
            if (_phase == Phase.Scan) EnterCharacterPhase();
            else ReturnToScanPhase();
        }

        /// <summary>
        /// Stops scanning and puts the character in the room.
        ///
        /// Scanning is stopped BEFORE the spawn, not after. The YOLO pass and a rig being
        /// animated are the two most expensive things in the scene and the transition is
        /// exactly the moment they would overlap; stopping first also means a spawn that fails
        /// leaves the room quiet rather than half-transitioned.
        ///
        /// A refused spawn does not change phase. The panel would otherwise say ROOM PHASE
        /// with nothing standing in the room, which is the most confusing thing it could do.
        /// </summary>
        public void EnterCharacterPhase()
        {
            if (characterSpawner == null)
            {
                Report("no character spawner in the scene");
                Debug.LogError($"{Tag} Cannot enter the character phase: there is no " +
                               $"RoomCharacterSpawner in the scene, so nothing can put a " +
                               $"character in the room.");
                return;
            }

            if (_scanning) ApplyScanning(false);

            if (!characterSpawner.Spawn())
            {
                // The spawner has already logged the detail; the panel carries the summary so
                // it is readable without a logcat attached.
                Report($"character phase refused: {characterSpawner.LastFailure}");
                return;
            }

            _phase = Phase.Character;
            UpdatePhaseLabel();
            UpdateScanToggleLabel();
            _dirty = true;

            // Not "she is listening" -- the session takes a couple of seconds to open, and on
            // a first run it stops to ask for the microphone. The voice line reports the
            // actual step; this only says the phase changed.
            Report("character phase -- connecting her");
            Debug.Log($"{Tag} Entered the character phase at " +
                      $"{characterSpawner.LastSpawnPoint:F2}.");
        }

        /// <summary>
        /// Takes the character away and goes back to scanning controls.
        ///
        /// The character is despawned rather than hidden: the scan phase can re-bake the
        /// navmesh out from under it, and a NavMeshAgent standing on a surface that has been
        /// removed is a warning per frame and a character that cannot move once you come back.
        ///
        /// Scanning is deliberately NOT restarted here. Coming back to look at the scan is not
        /// the same as wanting to collect more of it, and the YOLO pass is expensive enough
        /// that it should only ever start because someone asked.
        /// </summary>
        public void ReturnToScanPhase()
        {
            if (characterSpawner != null) characterSpawner.Despawn();

            _phase = Phase.Scan;
            UpdatePhaseLabel();
            UpdateScanToggleLabel();
            _dirty = true;

            Report("back to scan phase -- character removed");
            Debug.Log($"{Tag} Returned to the scan phase.");
        }

        /// <summary>
        /// Puts the character back on the navmesh, in front of you.
        ///
        /// Worth its own button because the spawn point is a guess: it picks the first walkable
        /// spot on a ladder outward from where you were looking, and in a cluttered room that
        /// can be behind you or on the far side of a couch. Re-spawning is faster than walking
        /// over to find out where it went.
        /// </summary>
        public void RespawnCharacter()
        {
            if (characterSpawner == null)
            {
                Report("no character spawner in the scene");
                return;
            }

            if (!characterSpawner.Spawn())
            {
                Report($"respawn refused: {characterSpawner.LastFailure}");
                return;
            }

            Report("character respawned");
            _dirty = true;
        }

        /// <summary>
        /// The scan-toggle button does two different jobs, one per phase.
        ///
        /// Overloading a button is normally worth avoiding, but START/STOP SCANNING has no
        /// meaning once the scan has been handed to the navmesh, and the alternative is a
        /// button sitting inert through the whole second half of the flow. The label is
        /// rewritten to match, so what it does is never in question -- only what it did a
        /// phase ago.
        /// </summary>
        private void ScanToggleOrRespawn()
        {
            if (_phase == Phase.Character) RespawnCharacter();
            else ToggleScanning();
        }

        /// <summary>
        /// Labels the phase button with the direction it will go, matching how the scan toggle
        /// names its action rather than its state.
        /// </summary>
        private void UpdatePhaseLabel()
        {
            if (_nextPhaseButton == null) return;

            var label = _nextPhaseButton.GetComponentInChildren<Text>();
            if (label != null)
                label.text = _phase == Phase.Character ? "BACK TO SCAN" : "NEXT PHASE";
        }

        /// <summary>
        /// Drops the panel in front of the player, facing them.
        ///
        /// No longer on a button -- the panel is dragged instead -- but still how it gets
        /// placed the first time, once MRUK reports its room. Kept public so a controller
        /// binding or a later phase can call it without rebuilding the plumbing.
        /// </summary>
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
            _builder.AppendLine("<color=#9a9aa0>face buttons need controllers; on hand " +
                                "tracking use the buttons below</color>");

            // Says so because nothing else does. RECENTER used to be a visible button; drag
            // is invisible until someone tells you it is there.
            _builder.Append("<color=#9a9aa0>drag this panel by its title or readout to move " +
                            "it</color>");

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
            _builder.AppendLine(_scanning
                ? "scanning  : <color=#7fd97f>ON</color> - boxes grow as objects are seen"
                : "scanning  : <color=#ffc44d>STOPPED</color> - nothing new is being recorded");

            if (recorder != null)
                _builder.AppendLine($"ready = seen {recorder.minObservations}+ times, " +
                                    $"under {recorder.maxObjectSize} m");
            else
                _builder.AppendLine("<color=#ff8080>no recorder in the scene</color>");

            _builder.AppendLine(DiskLine());
            _builder.AppendLine(AnchorLine());
            _builder.AppendLine(NavMeshLine());
            _builder.AppendLine(CharacterLine());
            AppendPlan();

            // Only while it can be acted on. Nothing on this panel moves her -- she is driven
            // by the conversation -- so the hint says the one thing that is not discoverable
            // from the readout: that talking to her is the interaction.
            if (_phase == Phase.Character)
                _builder.AppendLine("<color=#9a9aa0>just talk to her -- where she goes is the " +
                                    "conversation's business</color>");

            _builder.AppendLine();
            _builder.AppendLine($"last: {_lastAction}");

            _statusText.text = _builder.ToString();

            UpdatePlanButtons();
        }

        /// <summary>
        /// Most steps drawn at once. Past this the list is windowed around the current step --
        /// a nine-step plan drawn in full pushes the buttons off the panel, and the steps you
        /// can see are worth more than the ones you have already done.
        /// </summary>
        private const int PlanWindow = 6;

        /// <summary>
        /// Draws the plan: what is being done, and every step numbered with the current one
        /// marked.
        ///
        /// The enumeration is the whole point of drawing it. She reads the plan out once and
        /// then speaks one step at a time, which is the right way to be walked through a task
        /// and a hopeless way to keep track of where you are in it -- speech has no scrollback.
        /// The panel is the part you can look at, so it shows the shape of the whole thing and
        /// where in it you have got to.
        /// </summary>
        private void AppendPlan()
        {
            if (plan == null || !plan.HasPlan) return;

            var steps = plan.Steps;
            var current = plan.CurrentIndex;

            _builder.AppendLine();
            _builder.AppendLine($"<color=#7fd9d9>plan</color>      : {plan.Task} " +
                                $"({current + 1}/{steps.Count})");

            // Windowed around the current step, kept inside the list at both ends so the last
            // steps of a long plan still fill the window rather than trailing off it.
            var first = 0;
            var last = steps.Count - 1;

            if (steps.Count > PlanWindow)
            {
                first = Mathf.Clamp(current - PlanWindow / 2, 0, steps.Count - PlanWindow);
                last = first + PlanWindow - 1;
            }

            if (first > 0) _builder.AppendLine("  <color=#6a6a70>...</color>");

            for (var i = first; i <= last; i++)
            {
                var step = steps[i];
                var here = i == current;

                var where = step.HasPlace ? $" <color=#9a9aa0>[{step.Where}]</color>" : "";
                var line = $"{step.Number} {step.Text}{where}";

                _builder.AppendLine(here
                    ? $"<color=#ffc44d>&gt; {line}</color>"
                    : $"  <color=#9a9aa0>{line}</color>");
            }

            if (last < steps.Count - 1) _builder.AppendLine("  <color=#6a6a70>...</color>");
        }

        /// <summary>
        /// Greys the plan buttons out when they would do nothing.
        ///
        /// Interactable rather than hidden, and the ends of the plan grey out too. A button
        /// that vanishes takes the layout with it, and on a headset a control that moves is
        /// worse than one that is visibly unavailable -- you aim at where it was.
        /// </summary>
        private void UpdatePlanButtons()
        {
            var has = plan != null && plan.HasPlan;

            _planBackButton.interactable = has && plan.CurrentIndex > 0;
            _planNextButton.interactable = has && !plan.AtLastStep;
            _planClearButton.interactable = has;
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

        /// <summary>
        /// Where the character is and whether her session is up.
        ///
        /// Both on one line, and deliberately: the status block is budgeted at seven lines in
        /// the prefab baker and the character phase already fills it, so a second line about
        /// the session would push the whole block down over the controls readout underneath.
        /// They belong together anyway -- standing there and being connected are the two halves
        /// of the same question, and it is the pair that tells you which of them has failed.
        ///
        /// Distance is the useful number rather than a position: a coordinate means nothing to
        /// someone wearing the headset, and "4.2 m away" tells you whether to turn around and
        /// look for her. Silent in the scan phase -- a line about a character that has
        /// deliberately not been spawned yet is noise on the panel through all of phase 1.
        /// </summary>
        private string CharacterLine()
        {
            if (_phase != Phase.Character) return "phase     : <color=#9a9aa0>SCAN</color>";

            if (characterSpawner == null || !characterSpawner.IsSpawned)
                return "character : <color=#ff8080>GONE</color> - press RESPAWN";

            // The reason takes the distance's place rather than following it. A session that
            // did not open is the only thing worth reading on this line, and how far away she
            // is standing while it failed is not going to help.
            if (characterVoice != null && characterVoice.State == RoomCharacterVoice.VoiceState.Failed)
                return $"character : <color=#ff8080>FAILED</color> - {characterVoice.LastFailure}";

            var head = Camera.main;
            var distance = head != null
                ? Vector3.Distance(head.transform.position,
                                   characterSpawner.Character.transform.position)
                : -1f;

            var where = distance >= 0f ? $"{distance:F1} m away" : "somewhere in the room";

            // Whether she is WALKING is deliberately not reported. That is Convai's business
            // now -- the panel has no hand in where she goes, so a state it does not drive is
            // a state it should not claim to know.
            //
            // The object count rides along on the same line rather than taking one of its own,
            // for the same reason the session state does: the block is out of lines. It is
            // silent at zero, which is honest -- an empty room is what a character who was
            // told nothing looks like, and "0 objects" says so more clearly than nothing at all
            // would, but only once there is a component that could have counted them.
            var known = roomContext != null && roomContext.DescribedCount > 0
                ? $", knows {roomContext.DescribedCount} objects"
                : "";

            return $"character : {VoiceWord()} - {where}{known}";
        }

        /// <summary>
        /// The one word for how far the conversation has got, coloured by whether it needs
        /// anything from you.
        ///
        /// This is the only place the session is visible from inside the headset, and the
        /// states it separates all look identical through the visor: a character standing in
        /// silence is either still connecting, connected and waiting for you to speak, or
        /// connected with no microphone and unable to hear you at all.
        /// </summary>
        private string VoiceWord()
        {
            // Presence only, which is what the panel could say before there was a session to
            // report. Worth keeping rather than complaining: the spawn and the placement are
            // still worth looking at with the voice component missing.
            if (characterVoice == null) return "<color=#7fd97f>HERE</color>";

            switch (characterVoice.State)
            {
                case RoomCharacterVoice.VoiceState.WaitingForMicrophone:
                    return "<color=#ffc44d>MIC PROMPT</color>";

                case RoomCharacterVoice.VoiceState.Connecting:
                    return "<color=#ffc44d>CONNECTING</color>";

                case RoomCharacterVoice.VoiceState.Ready:
                    if (characterVoice.IsSpeaking) return "<color=#7fd97f>SPEAKING</color>";

                    return characterVoice.HasMicrophone
                        ? "<color=#7fd97f>LISTENING</color>"
                        : "<color=#ffc44d>DEAF (no mic)</color>";

                default:
                    return "<color=#ffc44d>NO SESSION</color>";
            }
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
