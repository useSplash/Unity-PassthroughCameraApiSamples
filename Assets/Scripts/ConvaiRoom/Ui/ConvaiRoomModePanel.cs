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
    /// The control panel, and the flow: where the room has got to, and the two or three things
    /// you can do from there.
    ///
    /// It runs as a small state machine -- see <see cref="Stage"/> -- rather than as a board of
    /// every control at once. The app opens on a choice between scanning a room and loading the
    /// last one, and each answer leads to the next question: a loaded room asks whether it is
    /// the right one, a saved scan asks whether to carry on or move on. Nothing was removed to
    /// get there. Every action the old panel had is still here and still public; they are dealt
    /// out a few at a time instead of all at once, so the order to do them in stops being
    /// something you have to already know.
    ///
    /// There are TWO panels, and the split is the same one. The main panel carries the flow and
    /// nothing else -- where you are, one number, the question, the buttons -- because a flow
    /// you have to read is a flow that has not been designed. Everything you would go LOOKING
    /// for is on the details panel beside it, switched on with INFO: why something refused,
    /// whether the walls lined up, what the controller buttons do, and the plan when there is
    /// one. Both hang off this transform, so dragging either moves the pair.
    ///
    /// Colours and corner radii come from a <see cref="ScanPanelTheme"/> asset and are applied
    /// at runtime, so a restyle is an asset edit rather than a re-bake.
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

        /// <summary>
        /// How many main-action buttons the panel has.
        ///
        /// Three, because the busiest stage needs three -- bake, bring her in, start over --
        /// and no stage needs four. Every stage draws from the same three slots in the same
        /// three places, so a control you used a moment ago is still where you last aimed even
        /// though it now says something else.
        ///
        /// Public because ScanPanelPrefabBaker lays out exactly this many buttons and binds
        /// them as one array. Two places agreeing on a number by coincidence is how a re-bake
        /// quietly produces a panel with a slot the flow never fills.
        /// </summary>
        public const int SlotCount = 3;

        /// <summary>What the scan file on disk is currently worth.</summary>
        private enum DiskState
        {
            Missing,
            Stale,
            Ready
        }

        /// <summary>
        /// Where in the flow the room is.
        ///
        /// Sequential, not modes: each one produces what the next needs. Scanning produces the
        /// floor the character stands on, so the character cannot be brought in until there is
        /// one, and coming back takes her away again rather than leaving her standing in a room
        /// being re-measured underneath her.
        ///
        /// The panel used to open straight into <see cref="Scanning"/> with every button in the
        /// flow on screen at once, and the order to press them in was something you either knew
        /// or did not. Each stage now shows only the two or three things that make sense to do
        /// from it, which is the same set of actions arranged so the flow explains itself.
        ///
        /// Two of the five are questions rather than places to work from --
        /// <see cref="Review"/> and <see cref="Saved"/> put a line of text up and offer the two
        /// answers to it. They are stages rather than a separate dialog system for two reasons:
        /// a world-space modal is one more thing to find and aim at, and it would cover the
        /// readout that says what you are deciding about. Asking in place, in the same three
        /// button positions, leaves the counts and the scan-file line readable while you choose.
        /// </summary>
        private enum Stage
        {
            /// <summary>Nothing running. Scan a room, or load the one already saved.</summary>
            Home,

            /// <summary>Collecting. The live boxes grow as objects are seen.</summary>
            Scanning,

            /// <summary>Still collecting, and just written to disk: carry on, or move on?</summary>
            Saved,

            /// <summary>A saved scan is on show, being judged: is this the right room?</summary>
            Review,

            /// <summary>A layout has been accepted. Bake it, then hand it to the character.</summary>
            Ready,

            /// <summary>She is standing in the room.</summary>
            Character
        }

        /// <summary>
        /// What one action slot currently is: what it says, what it does, and whether it can be
        /// pressed yet.
        ///
        /// The handler is a delegate rather than a name looked up later, so a renamed method is
        /// a compile error here exactly as it is for the buttons bound in
        /// <see cref="BindButtons"/> -- which is the whole reason none of this goes through the
        /// Inspector's onClick lists.
        /// </summary>
        private readonly struct SlotAction
        {
            public readonly string Label;
            public readonly Action Press;

            /// <summary>Why the slot is greyed out, or empty when it is not.</summary>
            public readonly string Blocked;

            public SlotAction(string label, Action press, string blocked = "")
            {
                Label = label;
                Press = press;
                Blocked = blocked;
            }

            public bool Exists => Press != null;
            public bool Enabled => string.IsNullOrEmpty(Blocked);

            /// <summary>An empty slot, which is drawn as no button at all.</summary>
            public static SlotAction None => new SlotAction(null, null);
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

        [Header("Look")]
        [Tooltip("Every colour and corner radius the panel draws itself with. Applied at " +
                 "runtime, so changing it needs no re-bake -- but the Scene view keeps showing " +
                 "whatever the prefab was baked with until you press Play.\n\nLeave it empty " +
                 "and the panel uses the theme's own defaults, which is the shipped look.")]
        [SerializeField] private ScanPanelTheme _theme;

        [Header("Main panel (all from the prefab, all required)")]
        [Tooltip("The world-space canvas. This is also the object that gets hidden until MRUK " +
                 "reports its room, so it must be the panel's canvas rather than a child of it.")]
        [SerializeField] private OVRRaycaster _raycaster;

        [Tooltip("The panel's own name for where you are. It used to be a static 'PHASE 1 - " +
                 "SCAN' the prefab owned outright; now that the flow has more than two steps " +
                 "it is the cheapest possible orientation and the panel writes it.")]
        [SerializeField] private Text _titleText;

        [Tooltip("The big headline number. Overwritten every redraw, and what it counts " +
                 "depends on the stage -- see Headline.")]
        [SerializeField] private Text _countsText;

        [Tooltip("The question line above the action buttons. Empty except while the panel is " +
                 "waiting on an answer.")]
        [SerializeField] private Text _promptText;

        [Tooltip("The stack of main actions, top to bottom. These are SLOTS rather than named " +
                 "buttons: what each one says and does is decided by the stage, and unused " +
                 "slots are hidden. See LayOutActions, which is the whole flow in one place.")]
        [SerializeField] private Button[] _actionButtons = new Button[SlotCount];

        [Tooltip("Shows and hides the details panel. Its label is the action it will take.")]
        [SerializeField] private Button _infoButton;

        [Tooltip("Quits the app. Sits away from the others in the title bar, and takes two " +
                 "presses -- see ExitApplication.")]
        [SerializeField] private Button _exitButton;

        [Header("Details panel (all from the prefab, all required)")]
        [Tooltip("The whole second panel, switched on and off by the INFO button. Everything " +
                 "below lives on it.")]
        [SerializeField] private GameObject _detailsRoot;

        [Tooltip("The details panel's own raycaster. It has buttons on it, so it needs one of " +
                 "its own -- a canvas without one is a canvas the laser goes straight through.")]
        [SerializeField] private OVRRaycaster _detailsRaycaster;

        [Tooltip("The multi-line status block. Overwritten every redraw, rich text and all.")]
        [SerializeField] private Text _statusText;

        [Tooltip("The controller-bindings block. Written once on startup from the actual " +
                 "bindings, not every frame -- they cannot change while the scene runs.")]
        [SerializeField] private Text _controlsText;

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

        /// <summary>
        /// The theme actually in use: the assigned asset, or a default instance when there is
        /// none. Resolved once in Awake so nothing downstream has to null-check it, and so a
        /// panel with no theme assigned still looks like the panel rather than like a bug.
        /// </summary>
        private ScanPanelTheme _skin;

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
        /// Whether detection is running. Starts false: the app opens on a choice rather than
        /// mid-scan, and a YOLO pass burning battery behind a menu nobody has answered yet is
        /// exactly the sort of thing you do not notice until the headset is warm. Pushed onto
        /// the scene by ApplyScanning in Awake rather than trusting the components to already
        /// agree with it.
        /// </summary>
        private bool _scanning;

        /// <summary>
        /// Where in the flow the room is. Starts at <see cref="Stage.Home"/>, and nothing moves
        /// it but <see cref="EnterStage"/> -- so there is exactly one place a wrong stage can
        /// come from.
        /// </summary>
        private Stage _stage = Stage.Home;

        /// <summary>
        /// Whether the details panel is up. Starts hidden: it is reference material, and the
        /// whole point of moving the readout onto it is that the flow does not need reading.
        /// </summary>
        private bool _detailsShown;

        /// <summary>
        /// Whether a plan was up on the last redraw, so the arrival of one can be spotted. The
        /// plan is drawn on the details panel, and a plan nobody can see is worse than no plan
        /// -- see the auto-open in <see cref="Redraw"/>.
        /// </summary>
        private bool _hadPlan;

        /// <summary>
        /// What the three action slots currently are. Rebuilt by <see cref="LayOutActions"/>
        /// whenever the stage or anything that greys a slot out changes.
        /// </summary>
        private readonly SlotAction[] _slots = new SlotAction[SlotCount];

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

            // The only reference that had no fallback, which mattered once the prefab started
            // being re-baked: an instance whose overrides are re-pointed loses the ones the
            // scene set, and every other field here can find itself again.
            if (plan == null) plan = FindAnyObjectByType<RoomTaskPlan>();

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

            // Before anything is written into the panel: the theme decides the palette the
            // readout quotes, so a redraw that ran first would come out in the wrong colours
            // and stay that way until something else marked it dirty.
            _skin = _theme != null ? _theme : ScriptableObject.CreateInstance<ScanPanelTheme>();
            ApplyTheme();

            BindButtons();
            WriteControls();

            // Hidden from the start, and switched rather than assumed: the prefab is authored
            // with it visible so it can be laid out, and shipping that way would put the whole
            // readout back in front of you at launch.
            SetDetailsShown(false);

            // Awake rather than Start, and that ordering is the point: every Awake runs before
            // any Start, so this lands before the rebuilder's own Start would have replayed the
            // scan. The panel owns when a saved room appears now -- the app opens on an empty
            // room and a question, and a room that has already filled itself with the last
            // scan's boxes has answered that question on your behalf. ConvaiRoomBootstrap is
            // held off by its own replayScanOnStart, which ships off for the same reason.
            if (rebuilder != null) rebuilder.rebuildOnStart = false;

            // Pushes the scene into whatever _scanning says rather than assuming it already
            // agrees. The three components have their own enabled flags in the scene, and a
            // panel that says SCANNING while the agent is switched off is worse than useless.
            ApplyScanning(_scanning);

            // Pushed onto the prefab rather than trusted, for the same reason ApplyScanning is:
            // every label here is authored text and nothing stops it having been left saying
            // something else.
            EnterStage(Stage.Home);

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
            if (_titleText == null) missing.Add(nameof(_titleText));
            if (_countsText == null) missing.Add(nameof(_countsText));
            if (_promptText == null) missing.Add(nameof(_promptText));
            if (_infoButton == null) missing.Add(nameof(_infoButton));
            if (_exitButton == null) missing.Add(nameof(_exitButton));
            if (_detailsRoot == null) missing.Add(nameof(_detailsRoot));
            if (_detailsRaycaster == null) missing.Add(nameof(_detailsRaycaster));
            if (_statusText == null) missing.Add(nameof(_statusText));
            if (_controlsText == null) missing.Add(nameof(_controlsText));
            if (_planBackButton == null) missing.Add(nameof(_planBackButton));
            if (_planNextButton == null) missing.Add(nameof(_planNextButton));
            if (_planClearButton == null) missing.Add(nameof(_planClearButton));

            // Named individually rather than as one "action buttons" complaint. A half-filled
            // array is the likeliest way this breaks -- a prefab baked before the slot count
            // changed comes back with the right field and the wrong length -- and knowing which
            // one is empty is the difference between re-baking and hunting.
            if (_actionButtons == null || _actionButtons.Length != SlotCount)
                missing.Add($"{nameof(_actionButtons)} (needs exactly {SlotCount})");
            else
                for (var i = 0; i < SlotCount; i++)
                    if (_actionButtons[i] == null) missing.Add($"{nameof(_actionButtons)}[{i}]");

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
            _exitButton.onClick.AddListener(ExitApplication);
            _infoButton.onClick.AddListener(ToggleDetails);
            _planBackButton.onClick.AddListener(PlanBack);
            _planNextButton.onClick.AddListener(PlanNext);
            _planClearButton.onClick.AddListener(PlanClear);

            // Bound once, to the slot rather than to the action. What each slot does changes
            // with the stage, and re-binding onClick on every stage change is how a button ends
            // up carrying two handlers and firing the one you retired three stages ago.
            for (var i = 0; i < SlotCount; i++)
            {
                var slot = i;
                _actionButtons[i].onClick.AddListener(() => PressSlot(slot));
            }
        }

        /// <summary>
        /// Runs whatever the stage has put in a slot.
        ///
        /// Guarded rather than trusted: a press can arrive in the same frame the stage changed
        /// underneath it -- Unity delivers the click after the layout has been rewritten -- and
        /// an empty or greyed slot firing whatever it held a moment ago is the one way this
        /// arrangement could do something nobody asked for.
        /// </summary>
        private void PressSlot(int index)
        {
            if (index < 0 || index >= SlotCount) return;

            var slot = _slots[index];
            if (!slot.Exists || !slot.Enabled) return;

            slot.Press();

            // The press has almost certainly changed what the slots should say -- most of them
            // move the stage on -- and waiting for the next poll to notice leaves the button
            // you just hit still offering the thing it has already done.
            LayOutActions();
            _dirty = true;
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

            // The details panel's own raycaster only needs the cursor handed to it. Which
            // raycaster the module is pointed AT is settled by OVRRaycaster on pointer enter,
            // so the two canvases hand off between themselves once the first click has landed
            // -- but the cursor is not something either of them finds on its own, and without
            // it the plan buttons over there take presses that go nowhere visible.
            _detailsRaycaster.pointer = _cursor.gameObject;

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
        // Look
        // -----------------------------------------------------------------

        /// <summary>
        /// Paints the whole panel from <see cref="_skin"/>.
        ///
        /// Driven off the <see cref="ScanPanelSkin"/> tags the baker leaves on each graphic
        /// rather than off a reference per piece. That is what makes the theme reach parts the
        /// panel has no field for -- both backgrounds, six button faces, every label -- and what
        /// lets something added to the prefab later be themed by dropping a tag on it.
        ///
        /// Run once, in Awake. The colours do not change while the app runs; a theme edited in
        /// the Inspector during Play shows up on the next entry to Play, which is the same deal
        /// as every other serialized default here.
        /// </summary>
        private void ApplyTheme()
        {
            var panelSprite = RoundedRectSprite.Get(_skin.panelCornerRadius);
            var buttonSprite = RoundedRectSprite.Get(_skin.buttonCornerRadius);

            // Includes inactive, and it has to: the details panel is switched off a few lines
            // later in Awake, and on some paths was never on to begin with. An unthemed panel
            // that only appears once you press INFO is the worst possible time to notice.
            foreach (var skin in GetComponentsInChildren<ScanPanelSkin>(true))
            {
                switch (skin.role)
                {
                    case ScanPanelSkin.Role.PanelBackground:
                        Paint(skin, _skin.panelBackground, panelSprite);
                        break;

                    case ScanPanelSkin.Role.ButtonFace:
                        Paint(skin, _skin.buttonFace, buttonSprite);
                        break;

                    case ScanPanelSkin.Role.ExitFace:
                        Paint(skin, _skin.exitFace, buttonSprite);
                        break;

                    case ScanPanelSkin.Role.Title:
                        Write(skin, _skin.title);
                        break;

                    case ScanPanelSkin.Role.BodyText:
                        Write(skin, _skin.bodyText);
                        break;

                    case ScanPanelSkin.Role.ButtonLabel:
                        Write(skin, _skin.buttonLabel);
                        break;
                }
            }
        }

        /// <summary>
        /// Colours an image and gives it its rounded corners.
        ///
        /// Sliced only when there is a sprite. An Image set to Sliced with none logs a warning
        /// every frame it is drawn, and a zero radius is a legitimate answer -- it means someone
        /// wants the square panel back.
        /// </summary>
        private static void Paint(ScanPanelSkin skin, Color color, Sprite sprite)
        {
            var image = skin.GetComponent<Image>();
            if (image == null) return;

            image.color = color;
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        }

        private static void Write(ScanPanelSkin skin, Color color)
        {
            var text = skin.GetComponent<Text>();
            if (text != null) text.color = color;
        }

        /// <summary>
        /// Shows and hides the details panel.
        ///
        /// The readout lives over there now, which is the point: the main panel is a flow and a
        /// flow you have to read is a flow that has not been designed. What is on the details
        /// panel is the stuff you go looking for -- why something refused, whether the walls
        /// lined up, what the controller buttons do -- and going looking for it is a press.
        /// </summary>
        public void ToggleDetails() => SetDetailsShown(!_detailsShown);

        public void SetDetailsShown(bool shown)
        {
            _detailsShown = shown;
            _detailsRoot.SetActive(shown);

            var label = _infoButton.GetComponentInChildren<Text>();
            if (label != null) label.text = shown ? "HIDE INFO" : "INFO";

            // The readout is not built while it is hidden -- see Redraw -- so whatever is on it
            // is as old as the moment it was switched off. Forcing a redraw is what stops it
            // coming back showing the room as it was several presses ago.
            _dirty = true;
        }

        // -----------------------------------------------------------------
        // Flow
        // -----------------------------------------------------------------

        /// <summary>
        /// Moves the panel to a stage and redraws everything that depends on it.
        ///
        /// The only way <see cref="_stage"/> ever changes, so there is exactly one place a
        /// wrong stage can come from and exactly one thing to read to know what happens on the
        /// way in.
        /// </summary>
        private void EnterStage(Stage stage)
        {
            _stage = stage;

            LayOutActions();
            _dirty = true;
        }

        /// <summary>
        /// Starts a fresh scan of the room, from any stage that offers it.
        ///
        /// Three things are cleared first, and each matters. The replayed boxes go because
        /// scanning past a previous scan's blue wireframes is unreadable -- you cannot tell what
        /// you have just picked up from what you loaded a minute ago, which is the exact
        /// judgement the live boxes exist to support. Any character in the room goes because the
        /// floor she is standing on is about to be re-measured. And the recorder's own clusters
        /// go because "scan this room again" means this room, not this room added to the last
        /// one.
        /// </summary>
        public void StartNewScan()
        {
            if (rebuilder != null) rebuilder.Clear();
            if (characterSpawner != null) characterSpawner.Despawn();
            if (recorder != null) recorder.ClearScan();

            ApplyScanning(true);
            EnterStage(Stage.Scanning);

            Report("scanning -- walk the room, then SAVE");
            Debug.Log($"{Tag} Started a new scan; replayed boxes and pending clusters cleared.");
        }

        /// <summary>
        /// Replays the saved scan and asks whether it is the room you meant.
        ///
        /// The question is the whole point of this route. A scan file is anonymous -- it has a
        /// size and a date and nothing else that says which room it is of -- so the only honest
        /// way to answer "is this the right one" is to put the boxes in front of you and let you
        /// look at where they landed.
        /// </summary>
        public void LoadForReview()
        {
            LoadSavedScan();

            // Nothing to judge, so nothing to ask. The report from LoadSavedScan already says
            // why, and staying put leaves the two Home choices where they were.
            if (rebuilder == null || rebuilder.Scan == null) return;

            EnterStage(Stage.Review);
        }

        /// <summary>
        /// Takes the replayed scan as the room to use, and moves on to setting it up.
        ///
        /// Nothing is loaded or re-read here -- the boxes are already in the room and the room
        /// context has already been handed them by the rebuild. This only records that you said
        /// yes.
        /// </summary>
        public void KeepLayout()
        {
            EnterStage(Stage.Ready);
            Report("layout kept -- bake it, then bring her in");
        }

        /// <summary>
        /// Ends the scan and moves on with what was just saved.
        ///
        /// Scanning is stopped first, and then the file that was written a moment ago is
        /// replayed. That replay is not busywork: everything downstream reads the REBUILDER
        /// rather than the recorder -- the navmesh bakes from it, and the room context describes
        /// its objects to the character -- so a scan that is only in memory is a scan nothing
        /// else in the app can see. Doing it here is what turns the old load-then-bake sequence
        /// into one press.
        /// </summary>
        public void ProceedFromScan()
        {
            ApplyScanning(false);
            LoadSavedScan();

            // A replay that came back with nothing leaves nothing to bake and nothing to
            // describe, so moving on would hand you a stage where both actions refuse. Staying
            // put keeps SAVE SCAN in reach, which is the thing worth trying again.
            if (rebuilder == null || rebuilder.Scan == null)
            {
                EnterStage(Stage.Scanning);
                return;
            }

            EnterStage(Stage.Ready);
            Report("room set -- bake it, then bring her in");
        }

        /// <summary>Dismisses the after-save question and carries on collecting.</summary>
        public void KeepScanning()
        {
            if (!_scanning) ApplyScanning(true);

            EnterStage(Stage.Scanning);
            Report("still scanning -- save again whenever you like");
        }

        /// <summary>
        /// Decides what the three action slots say and do, from the stage.
        ///
        /// This is the flow, all of it, in one readable block -- which is the reason the slots
        /// are generic. Spread over a dozen named buttons each with its own show/hide rule, the
        /// question "what can I do from here" had no single answer to read.
        ///
        /// A slot that would refuse is greyed rather than hidden, with the reason on it: the
        /// stage decides WHICH controls exist, and within a stage a control that comes and goes
        /// is one you aim at where it used to be.
        /// </summary>
        private void LayOutActions()
        {
            for (var i = 0; i < SlotCount; i++) _slots[i] = SlotAction.None;

            switch (_stage)
            {
                case Stage.Home:
                    _slots[0] = new SlotAction("START NEW SCAN", StartNewScan);
                    _slots[1] = new SlotAction("LOAD SAVED SCAN", LoadForReview,
                        _diskState == DiskState.Missing ? "nothing saved" : "");
                    break;

                case Stage.Scanning:
                    _slots[0] = new SlotAction(_scanning ? "STOP SCANNING" : "RESUME SCANNING",
                                               ToggleScanning);

                    // Greyed on the count rather than left to be refused after the press. The
                    // refusal in SaveScan stays -- it is the one that knows what BuildScanFile
                    // actually produced -- but being told no is a worse way to learn that
                    // nothing has settled yet than the button saying so before you reach for it.
                    _slots[1] = new SlotAction("SAVE SCAN", SaveScan,
                        _ready == 0 ? "nothing ready yet" : "");
                    break;

                case Stage.Saved:
                    _slots[0] = new SlotAction("KEEP SCANNING", KeepScanning);
                    _slots[1] = new SlotAction("PROCEED", ProceedFromScan);
                    break;

                case Stage.Review:
                    _slots[0] = new SlotAction("YES, KEEP IT", KeepLayout);
                    _slots[1] = new SlotAction("SCAN NEW ROOM", StartNewScan);
                    break;

                case Stage.Ready:
                    _slots[0] = new SlotAction(HasNavMesh ? "RE-BAKE NAVMESH" : "BAKE NAVMESH",
                                               BakeNavMesh);

                    _slots[1] = new SlotAction("BRING IN CHARACTER", EnterCharacterPhase,
                        HasNavMesh ? "" : "bake first");

                    _slots[2] = new SlotAction("SCAN NEW ROOM", StartNewScan);
                    break;

                case Stage.Character:
                    _slots[0] = new SlotAction("RESPAWN", RespawnCharacter);
                    _slots[1] = new SlotAction("BACK TO ROOM SETUP", ReturnToScanPhase);
                    break;
            }

            ApplySlots();
        }

        /// <summary>Whether anything is baked. False with no builder, which is honest.</summary>
        private bool HasNavMesh => navMeshBuilder != null && navMeshBuilder.HasNavMesh;

        /// <summary>Pushes <see cref="_slots"/> onto the actual buttons.</summary>
        private void ApplySlots()
        {
            for (var i = 0; i < SlotCount; i++)
            {
                var button = _actionButtons[i];
                if (button == null) continue;

                var slot = _slots[i];

                if (button.gameObject.activeSelf != slot.Exists)
                    button.gameObject.SetActive(slot.Exists);

                if (!slot.Exists) continue;

                button.interactable = slot.Enabled;

                var label = button.GetComponentInChildren<Text>();
                if (label == null) continue;

                // The reason rides on the button itself. On a headset the alternative is
                // pressing a dead control and going looking for why in a readout somewhere
                // else, and the two words that explain it fit here.
                label.text = slot.Enabled ? slot.Label : $"{slot.Label}\n({slot.Blocked})";
            }

            // The plan is a character-stage thing, and three permanently greyed buttons under
            // the readout through the whole of the scan are three controls to wonder about.
            var planning = _stage == Stage.Character;
            SetPlanRowShown(planning);

            if (planning) UpdatePlanButtons();
        }

        private void SetPlanRowShown(bool shown)
        {
            if (_planBackButton.gameObject.activeSelf != shown)
                _planBackButton.gameObject.SetActive(shown);

            if (_planNextButton.gameObject.activeSelf != shown)
                _planNextButton.gameObject.SetActive(shown);

            if (_planClearButton.gameObject.activeSelf != shown)
                _planClearButton.gameObject.SetActive(shown);
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

            // Only from the stages that scan. SaveScan is public and a controller binding can
            // reach it too, and a question about what to do next has no business appearing on
            // top of a stage that was not asking it.
            if (_stage == Stage.Scanning || _stage == Stage.Saved) EnterStage(Stage.Saved);
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
        /// gaining observations is neither. The flow only offers this from a stage that has
        /// already replayed one, but the refusal below stays: this is public, and baking an
        /// empty floor while reporting success is the worst thing it could do.
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
                Report("bake needs a room loaded first");
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

            // The scan slot is labelled with the action it will take rather than the state it
            // is in -- STOP SCANNING while running, RESUME SCANNING while paused. An earlier
            // panel avoided toggles entirely for fear of which way one would flip; naming the
            // action instead cannot be read two ways, and the state belongs on the status line.
            LayOutActions();
            _dirty = true;

            Debug.Log($"{Tag} Scanning -> {scanning} " +
                      $"(agent={(detectionAgent != null ? scanning.ToString() : "absent")} " +
                      $"liveBoxes={(liveVisualizer != null ? scanning.ToString() : "absent")} " +
                      $"recorder={(recorder != null ? scanning.ToString() : "absent")}).");
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
        /// Moves between the room setup and the character, whichever way round you are.
        ///
        /// Kept for anything driving the panel from outside it -- a controller binding, a later
        /// phase. The panel's own buttons call the two halves directly now, because each stage
        /// only ever offers one of the two directions and naming it is clearer than a toggle.
        /// </summary>
        public void TogglePhase()
        {
            if (_stage == Stage.Character) ReturnToScanPhase();
            else EnterCharacterPhase();
        }

        /// <summary>
        /// Stops scanning and puts the character in the room.
        ///
        /// Scanning is stopped BEFORE the spawn, not after. The YOLO pass and a rig being
        /// animated are the two most expensive things in the scene and the transition is
        /// exactly the moment they would overlap; stopping first also means a spawn that fails
        /// leaves the room quiet rather than half-transitioned.
        ///
        /// A refused spawn does not change stage. The panel would otherwise say she is in the
        /// room with nothing standing in it, which is the most confusing thing it could do.
        /// </summary>
        public void EnterCharacterPhase()
        {
            if (characterSpawner == null)
            {
                Report("no character spawner in the scene");
                Debug.LogError($"{Tag} Cannot bring the character in: there is no " +
                               $"RoomCharacterSpawner in the scene, so nothing can put a " +
                               $"character in the room.");
                return;
            }

            if (_scanning) ApplyScanning(false);

            if (!characterSpawner.Spawn())
            {
                // The spawner has already logged the detail; the panel carries the summary so
                // it is readable without a logcat attached.
                Report($"cannot bring her in: {characterSpawner.LastFailure}");
                return;
            }

            EnterStage(Stage.Character);

            // Not "she is listening" -- the session takes a couple of seconds to open, and on
            // a first run it stops to ask for the microphone. The voice line reports the
            // actual step; this only says the stage changed.
            Report("she is here -- connecting her");
            Debug.Log($"{Tag} Brought the character in at " +
                      $"{characterSpawner.LastSpawnPoint:F2}.");
        }

        /// <summary>
        /// Takes the character away and goes back to the room-setup controls.
        ///
        /// The character is despawned rather than hidden: setup can re-bake the navmesh out
        /// from under her, and a NavMeshAgent standing on a surface that has been removed is a
        /// warning per frame and a character that cannot move once you come back.
        ///
        /// Back to <see cref="Stage.Ready"/> rather than all the way to Home. The layout is
        /// still loaded and still baked -- that is what she was standing on -- so dropping you
        /// at the start of the flow would ask you to redo work that is sitting there done.
        /// Scanning is deliberately NOT restarted for the same reason it never was: coming back
        /// to look at the room is not the same as wanting to collect more of it.
        /// </summary>
        public void ReturnToScanPhase()
        {
            if (characterSpawner != null) characterSpawner.Despawn();

            EnterStage(Stage.Ready);

            Report("she has stepped out -- room kept");
            Debug.Log($"{Tag} Returned to room setup.");
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
            _builder.AppendLine(Accent("CONTROLS"));

            if (scanController != null)
            {
                _builder.AppendLine($"{ButtonLabel(scanController.exportButton)} save   " +
                                    $"{ButtonLabel(scanController.clearButton)} clear   " +
                                    $"{ButtonLabel(scanController.rebuildButton)} load   " +
                                    $"{ButtonLabel(scanController.shellToggleButton)} outline");

                // Said out loud because the panel is a flow now and these are not part of it.
                // They go straight at the scan -- RoomScanController owns them and knows
                // nothing about stages -- so loading with a face button puts boxes in the room
                // without the panel ever asking whether you want to keep them. That is not a
                // bug, it is the raw pipeline, but a control that leaves the readout describing
                // a different room is worth a warning.
                _builder.AppendLine(Muted("these act on the scan itself, not on this flow"));
            }
            else
            {
                _builder.AppendLine(Bad("no RoomScanController in the scene"));
            }

            // Worth its own line. OVRInput promotes hand tracking to the active controller,
            // and under it every face button above resolves to nothing -- so on hands the
            // panel is not a convenience, it is the only way to drive any of this.
            _builder.AppendLine(Muted("no face buttons on hand tracking -- use the panel"));

            // Says so because nothing else does. RECENTER used to be a visible button; drag
            // is invisible until someone tells you it is there.
            _builder.Append(Muted("drag either panel by its background to move both"));

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

        /// <summary>
        /// Redraws the panel.
        ///
        /// Split in two now that the readout has its own panel: the main one is four short
        /// pieces of text and is always drawn, and the readout is only built when someone is
        /// actually looking at it. That split is worth having beyond the saved work -- it is
        /// the same split the panels are: what the flow needs, and what you go looking for.
        /// </summary>
        private void Redraw()
        {
            // Here rather than at each thing that could change a slot. Half the greying rules
            // read polled state -- the ready count, whether a file is on disk, whether anything
            // is baked -- and none of those arrive as an event, so the slots are re-derived
            // alongside the readout that reports the same numbers. Every write below it is a
            // no-op when the value has not moved.
            LayOutActions();

            _titleText.text = Title();

            _countsText.text = Headline();
            _countsText.color = HeadlineIsGood ? _skin.headlineActive : _skin.headlineIdle;

            _promptText.text = PromptLine();

            // A plan opens the details panel on its own. It is drawn over there, it is the one
            // thing on that panel you act on rather than read, and she announces it by speaking
            // -- so without this the first anyone knows of a plan is being told about one they
            // cannot see. Only on the transition, so closing it again stays closed.
            var hasPlan = plan != null && plan.HasPlan;
            if (hasPlan && !_hadPlan && !_detailsShown) SetDetailsShown(true);
            _hadPlan = hasPlan;

            if (_detailsShown) RedrawDetails();
        }

        /// <summary>
        /// Builds the readout on the details panel.
        ///
        /// Written PER STAGE rather than all of it every time. It used to print the same seven
        /// lines throughout -- the navmesh line while there was nothing to bake from, the
        /// character line before there was a character -- and a block where five of the seven
        /// say "not yet" is one nobody reads, so the two that matter go unread with them.
        /// </summary>
        private void RedrawDetails()
        {
            _builder.Clear();

            switch (_stage)
            {
                case Stage.Home:
                    _builder.AppendLine(DiskLine());
                    _builder.AppendLine(AnchorLine());
                    _builder.AppendLine();
                    _builder.AppendLine(Muted("scan the room to measure it, or load the last " +
                                              "scan and look at where its boxes land"));
                    break;

                case Stage.Scanning:
                case Stage.Saved:
                    _builder.AppendLine(_scanning
                        ? $"scanning  : {Good("ON")} - boxes grow as objects are seen"
                        : $"scanning  : {Warn("PAUSED")} - nothing new is recorded");

                    // Read the thresholds off the recorder rather than hardcoding them, so this
                    // line cannot go stale if someone tunes the clustering.
                    if (recorder != null)
                        _builder.AppendLine($"ready     : seen {recorder.minObservations}+ times, " +
                                            $"under {recorder.maxObjectSize} m");
                    else
                        _builder.AppendLine(Bad("no recorder in the scene"));

                    _builder.AppendLine(DiskLine());
                    _builder.AppendLine(AnchorLine());
                    break;

                case Stage.Review:
                    _builder.AppendLine(DiskLine());
                    _builder.AppendLine(AlignmentLine());
                    _builder.AppendLine(AnchorLine());
                    _builder.AppendLine();
                    _builder.AppendLine(Muted("look around: the blue boxes should sit on the " +
                                              "real furniture"));
                    break;

                case Stage.Ready:
                    _builder.AppendLine(DiskLine());
                    _builder.AppendLine(NavMeshLine());
                    _builder.AppendLine(AnchorLine());
                    break;

                case Stage.Character:
                    _builder.AppendLine(CharacterLine());
                    _builder.AppendLine(NavMeshLine());
                    AppendPlan();

                    // Nothing on this panel moves her -- she is driven by the conversation --
                    // so the hint says the one thing that is not discoverable from the readout:
                    // that talking to her is the interaction. Dropped once a plan is up, and
                    // for two reasons: by then you have plainly worked out that talking to her
                    // works, and the block is out of lines.
                    if (plan == null || !plan.HasPlan)
                        _builder.AppendLine(Muted("just talk to her -- where she goes is the " +
                                                  "conversation's business"));
                    break;
            }

            _builder.AppendLine();
            _builder.AppendLine($"last: {_lastAction}");

            // The plan row is not touched here. LayOutActions ran before this and ApplySlots
            // greys the three buttons as part of it, which keeps every button across both
            // panels updated from one place.
            _statusText.text = _builder.ToString();
        }

        /// <summary>
        /// Where you are, in two or three words.
        ///
        /// Kept short enough to sit in the title bar next to INFO and EXIT. "SCANNING" rather
        /// than "SCANNING THE ROOM" for exactly that reason -- the headline underneath is
        /// already counting what is being scanned.
        /// </summary>
        private string Title()
        {
            switch (_stage)
            {
                case Stage.Scanning: return "SCANNING";
                case Stage.Saved: return "SCAN SAVED";
                case Stage.Review: return "SAVED LAYOUT";
                case Stage.Ready: return "ROOM READY";
                case Stage.Character: return "IN THE ROOM";
                default: return "ROOM FLOW";
            }
        }

        // -----------------------------------------------------------------
        // Palette
        // -----------------------------------------------------------------

        // Every coloured word in the readout goes through one of these rather than carrying a
        // hex literal. That is what makes the theme worth having: a light background needs
        // every one of these to move with it, and thirty-nine hardcoded greens and ambers
        // scattered through the readout is a restyle nobody finishes.

        /// <summary>Ready, baked, connected, present.</summary>
        private string Good(string text) => $"<color=#{_skin.GoodHex}>{text}</color>";

        /// <summary>Works, but not the way you wanted -- stale, unaligned, paused.</summary>
        private string Warn(string text) => $"<color=#{_skin.WarnHex}>{text}</color>";

        /// <summary>Missing, or failed.</summary>
        private string Bad(string text) => $"<color=#{_skin.BadHex}>{text}</color>";

        /// <summary>Hints and asides, read once and then ignored.</summary>
        private string Muted(string text) => $"<color=#{_skin.MutedHex}>{text}</color>";

        /// <summary>Quieter still. The ellipses either end of a windowed plan.</summary>
        private string Dim(string text) => $"<color=#{_skin.DimHex}>{text}</color>";

        /// <summary>The question being asked, and the CONTROLS heading.</summary>
        private string Accent(string text) => $"<color=#{_skin.AccentHex}>{text}</color>";

        /// <summary>
        /// The one big number, chosen for the stage.
        ///
        /// This is the line you read from across the room without focusing on the panel, so
        /// each stage puts the number it is actually waiting on in it: how much the scan has
        /// settled while scanning, how much came back while judging a saved one, and how much
        /// she has been told about once she is here.
        /// </summary>
        private string Headline()
        {
            switch (_stage)
            {
                case Stage.Scanning:
                case Stage.Saved:
                    return $"{_ready} ready / {_tracked} tracked";

                case Stage.Review:
                case Stage.Ready:
                    var loaded = rebuilder != null ? rebuilder.Rebuilt.Count : 0;
                    return loaded == 1 ? "1 object in the room" : $"{loaded} objects in the room";

                case Stage.Character:
                    var known = roomContext != null ? roomContext.DescribedCount : 0;
                    return known > 0 ? $"she knows {known} objects" : "she is here";

                default:
                    if (_diskState == DiskState.Missing) return "no saved scan";
                    return _diskObjects >= 0 ? $"{_diskObjects} objects saved" : "scan saved";
            }
        }

        /// <summary>
        /// Whether the headline is reporting something usable, which is what colours it.
        ///
        /// Per stage, because the same zero means different things: no clusters yet while
        /// scanning is a scan that has not got going, and no saved file at Home is a first run.
        /// </summary>
        private bool HeadlineIsGood
        {
            get
            {
                switch (_stage)
                {
                    case Stage.Scanning:
                    case Stage.Saved: return _ready > 0;
                    case Stage.Review:
                    case Stage.Ready: return rebuilder != null && rebuilder.Rebuilt.Count > 0;
                    case Stage.Character: return true;
                    default: return _diskState != DiskState.Missing;
                }
            }
        }

        /// <summary>
        /// The question this stage is asking, if it is asking one. Empty otherwise, which draws
        /// nothing and leaves a gap above the actions rather than moving them.
        /// </summary>
        private string PromptLine()
        {
            switch (_stage)
            {
                case Stage.Review:
                    return Accent("Use this room?");

                case Stage.Saved:
                    // The count comes from the file that was just written rather than from the
                    // live counts, which keep moving: what you are deciding about is what
                    // landed on disk.
                    var saved = _diskObjects >= 0 ? $"Saved {_diskObjects} objects." : "Saved.";
                    return Accent($"{saved} Carry on, or move on?");

                default:
                    return "";
            }
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
            _builder.AppendLine($"<color=#{_skin.PlanHex}>plan</color>      : {plan.Task} " +
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

            if (first > 0) _builder.AppendLine($"  {Dim("...")}");

            for (var i = first; i <= last; i++)
            {
                var step = steps[i];
                var here = i == current;

                var where = step.HasPlace ? $" {Muted($"[{step.Where}]")}" : "";
                var line = $"{step.Number} {step.Text}{where}";

                _builder.AppendLine(here ? Warn($"&gt; {line}") : $"  {Muted(line)}");
            }

            if (last < steps.Count - 1) _builder.AppendLine($"  {Dim("...")}");
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
                    return $"scan file : {Bad("NO")} - nothing saved yet";

                case DiskState.Stale:
                    return $"scan file : {Warn("STALE")} - saved before you " +
                           $"cleared ({Kilobytes()})";

                default:
                    var objects = _diskObjects >= 0 ? $"{_diskObjects} objects, " : "";
                    return $"scan file : {Good("YES")} - {objects}{Kilobytes()}, {Age()}";
            }
        }

        private string AnchorLine()
        {
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;

            return room != null
                ? $"anchored  : {Good("MRUK room")}"
                : $"anchored  : {Warn("RAW WORLD SPACE")}";
        }

        /// <summary>
        /// Whether the replayed boxes were corrected onto the walls you are standing between,
        /// or dropped in on the coordinates the file happened to be written with.
        ///
        /// On the panel only while a loaded scan is being judged, which is the one moment it
        /// decides anything: the two look identical right up until you notice the whole room is
        /// rotated, and knowing the fit was thrown away tells you that a layout that lands wrong
        /// is worth re-scanning rather than nudging.
        /// </summary>
        private string AlignmentLine()
        {
            if (rebuilder == null) return $"aligned   : {Muted("no rebuilder")}";

            var alignment = rebuilder.Alignment;

            if (!alignment.Applied)
                return $"aligned   : {Warn("NO")} - {alignment.Summary}";

            return alignment.Ambiguous
                ? $"aligned   : {Warn("AMBIGUOUS")} - fits more than one way round"
                : $"aligned   : {Good("YES")} - {alignment.Error:F2} m off the walls";
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
                return $"navmesh   : {Muted("no builder in the scene")}";

            if (!navMeshBuilder.HasNavMesh)
                return $"navmesh   : {Warn("NO")} - not baked yet";

            var triangles = navMeshVisualizer != null && navMeshVisualizer.TriangleCount > 0
                ? $", {navMeshVisualizer.TriangleCount} tris"
                : "";

            return $"navmesh   : {Good("YES")} - " +
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
        /// look for her. Only drawn in the character stage -- a line about a character who has
        /// deliberately not been spawned yet is noise on the panel through all of the setup.
        /// </summary>
        private string CharacterLine()
        {
            if (characterSpawner == null || !characterSpawner.IsSpawned)
                return $"character : {Bad("GONE")} - press RESPAWN";

            // The reason takes the distance's place rather than following it. A session that
            // did not open is the only thing worth reading on this line, and how far away she
            // is standing while it failed is not going to help.
            if (characterVoice != null && characterVoice.State == RoomCharacterVoice.VoiceState.Failed)
                return $"character : {Bad("FAILED")} - {characterVoice.LastFailure}";

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
            if (characterVoice == null) return Good("HERE");

            switch (characterVoice.State)
            {
                case RoomCharacterVoice.VoiceState.WaitingForMicrophone:
                    return Warn("MIC PROMPT");

                case RoomCharacterVoice.VoiceState.Connecting:
                    return Warn("CONNECTING");

                case RoomCharacterVoice.VoiceState.Ready:
                    if (characterVoice.IsSpeaking) return Good("SPEAKING");

                    return characterVoice.HasMicrophone
                        ? Good("LISTENING")
                        : Warn("DEAF (no mic)");

                default:
                    return Warn("NO SESSION");
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
