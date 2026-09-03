using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Records one user-test session to disk, and lends the panel three buttons to drive it.
    ///
    /// The app measures nothing on its own, and several of the things a study wants to know
    /// are destroyed as they happen: room_scan.json lives at one fixed path and the next
    /// participant overwrites it, and nothing anywhere writes down when a scan began, so
    /// firstSeenUtc cannot be turned into a time-to-detect. This component is the thing that
    /// keeps them.
    ///
    /// It is a LISTENER, almost entirely. Two events were added to the panel for it
    /// (<see cref="ConvaiRoomModePanel.OnStageChanged"/> and
    /// <see cref="ConvaiRoomModePanel.OnReported"/>) and everything else here is polling on
    /// the same cadences the panel already polls at. That is deliberate: instrumentation that
    /// changes the thing it measures is worse than no instrumentation, and the scan phase in
    /// particular is the one whose accuracy is being reported on.
    ///
    /// An event log rather than running totals. The analysis wants a recall curve at 30, 60
    /// and 120 seconds, and a summary computed on device would bake those three cut points in
    /// before anybody had seen a single session. Every question nobody thought to ask in
    /// advance would then be unanswerable, and there is exactly one shot per participant.
    ///
    /// Absent from an ordinary build. The panel finds this in Awake and offers the study slot
    /// only when it is there, so a scene without it behaves exactly as it did before any of
    /// this existed.
    /// </summary>
    public class StudySessionRecorder : MonoBehaviour
    {
        private const string Tag = "[Study]";

        /// <summary>How much of the panel the study is currently borrowing.</summary>
        private enum Mode
        {
            /// <summary>Not involved. The panel draws its own flow.</summary>
            Off,

            /// <summary>Choosing participant, room and switches, before anything is recorded.</summary>
            Setup,

            /// <summary>Recording.</summary>
            Session,

            /// <summary>Recording, and the truth marker has the buttons.</summary>
            Truth,

            /// <summary>Recording, and the reference-trial runner has the buttons.</summary>
            Reference
        }

        /// <summary>
        /// What the session screen's one cycled control can do.
        ///
        /// The session screen has four jobs and three slots, so it borrows the setup screen's
        /// idiom: one control names the next action, one performs it. MARK NOTE keeps a slot of
        /// its own rather than joining the ring, because it stamps an instant -- a control you
        /// have to cycle to is one whose timestamp is however long the cycling took.
        /// </summary>
        private enum Tool
        {
            Reference,
            Truth,
            End,
            Leave
        }

        /// <summary>
        /// One thing that can be dialled in before a session starts.
        ///
        /// A cycled field rather than a button each, because the panel has three slots and
        /// this needs six or seven values -- and because a keyboard is not an option here.
        /// One control picks the field, one changes it, which scales to as many settings as
        /// the protocol grows without ever needing a fourth button or a re-baked prefab.
        /// </summary>
        private enum Field
        {
            Participant,
            Room,

            /// <summary>Arms the raw observation log. See <see cref="ScanObservationLog"/>.</summary>
            ObservationLog,

            /// <summary>How many Convai turns this participant is allowed. See <see cref="StudyRequestBudget"/>.</summary>
            Budget,

            /// <summary>Whether that budget refuses turns or merely reports them.</summary>
            Enforce,

            /// <summary>Not a setting -- the way back out of setup. See <see cref="SetupSlotLabel"/>.</summary>
            Leave
        }

        /// <summary>
        /// The budgets the panel offers, with 0 for "count but never refuse".
        ///
        /// A fixed ring rather than an increment, because the field is cycled with one button
        /// and stepping from 0 to 40 one at a time is forty presses in a headset. The values
        /// bracket the protocol's 30-40 planning figure on both sides so a pilot that finds the
        /// real ceiling elsewhere can be dialled in without a rebuild.
        /// </summary>
        private static readonly int[] BudgetSteps = { 0, 20, 30, 40, 50, 60, 80 };

        [Header("Wiring (left empty, these are found in the scene)")]
        public ConvaiRoomModePanel panel;
        public ObjectScanRecorder recorder;

        [Tooltip("Optional. Without one the MARK TRUTH button says so rather than disappearing " +
                 "-- a control that vanishes is one you go looking for in the code.")]
        public RoomTruthMarker truthMarker;

        [Tooltip("Optional. Only touched when a session arms it, and only to open and close " +
                 "its file; the recorder itself decides what goes in.")]
        public ScanObservationLog observationLog;

        [Tooltip("Optional. Runs the reference-resolution block -- the study's primary outcome. " +
                 "Without one the REF BLOCK control says so rather than disappearing.")]
        public ReferenceTrialRunner trials;

        [Header("Participants")]
        [Tooltip("How many participant ids to offer, as P01..Pnn. Cycling wraps.")]
        public int participantCount = 16;

        [Tooltip("Room labels to offer. The ground-truth file is keyed by whichever is chosen, " +
                 "so these must stay stable across a study -- renaming R2 halfway through " +
                 "orphans every measurement taken in it.")]
        public List<string> roomLabels = new List<string> { "R1", "R2", "R3", "R4", "R5", "R6" };

        [Header("Convai request budget")]
        [Tooltip("How many conversation turns one participant is allowed, or 0 for no budget. " +
                 "Also settable in the headset from the study setup screen.\n\n" +
                 "A turn is one utterance the Convai backend processed. Pointing at an object " +
                 "and asking for a plan both cost nothing -- pointing only stages context, and " +
                 "the planner talks to Anthropic or Ollama and never touches Convai.")]
        public int convaiBudget = 40;

        [Tooltip("Refuse turns once the budget is spent, by holding the microphone shut.\n\n" +
                 "LEAVE THIS OFF unless a pilot has established what the real Convai ceiling " +
                 "is. There are two ceilings and only one of them is real: the backend enforces " +
                 "an actual quota and announces it, but never reports how much is left, while " +
                 "the number above is a planning figure. Enforcing a guessed ceiling ends a " +
                 "participant's session while quota remained, which costs a slot and buys " +
                 "nothing. Off, the budget still counts and still warns on the panel.")]
        public bool enforceBudget;

        [Header("Sampling")]
        [Tooltip("Seconds between cluster polls, which is what resolves the time-to-detect " +
                 "curve. The default matches the panel's own count poll.\n\n" +
                 "Finer buys nothing: the curve is read at 30, 60 and 120 seconds.")]
        public float clusterPollInterval = 0.25f;

        [Tooltip("Seconds between scan-file checks. Mirrors the panel's disk poll, and is " +
                 "what catches a save made with the A button rather than through the panel.")]
        public float diskPollInterval = 1f;

        [Header("Debug")]
        public bool verboseLogging = true;

        // -----------------------------------------------------------------

        private Mode _mode = Mode.Off;
        private StudySession _session;
        private float _t0;

        private Field _field = Field.Participant;
        private Tool _tool = Tool.Reference;
        private int _participant = 1;
        private int _room;
        private bool _obsArmed;

        private string _status = "";

        /// <summary>The run currently open, or null between runs.</summary>
        private ScanRunEntry _openRun;

        /// <summary>
        /// The Convai turn counter. Owned rather than a component of its own, so the scene
        /// setup stays the list of AddComponents it already is -- see the remark on
        /// <see cref="StudyRequestBudget"/>. It is also the only thing in the study that talks
        /// to the Convai SDK, which is what keeps this class the listener it claims to be.
        /// </summary>
        private readonly StudyRequestBudget _budget = new StudyRequestBudget();

        /// <summary>
        /// Utterance counts and timings. Owned here for the same reason the budget is -- it has
        /// no Inspector surface and no scene presence, so making it a component would grow the
        /// scene-setup list for nothing. Counts and instants only; it has nowhere to put text.
        /// </summary>
        private readonly StudyTranscriptWatch _speech = new StudyTranscriptWatch();

        // Cluster polling. The set is what makes a milestone fire once: a cluster stays
        // exportable for the rest of the scan, so without it every poll would write a row.
        private readonly List<ObjectScanRecorder.ClusterView> _snapshot =
            new List<ObjectScanRecorder.ClusterView>();

        private readonly HashSet<int> _milestoned = new HashSet<int>();
        private float _nextClusterPoll;

        // Disk watching.
        private DateTime _lastScanWrite = DateTime.MinValue;
        private long _lastScanBytes = -1;
        private float _nextDiskPoll;
        private int _saveIndex;

        /// <summary>
        /// When the panel last announced a save.
        ///
        /// Used to tell a panel save from an A-button one. The A button calls
        /// ExportToJson directly, which skips every guard the panel's SaveScan applies and
        /// announces nothing at all -- so the file simply changes under us. A change with no
        /// announcement beside it is that bypass, and worth recording as the protocol
        /// deviation it is rather than silently attributing to the panel.
        /// </summary>
        private float _panelSaidSavedAt = float.NegativeInfinity;

        private float _lastWriteAt;
        private readonly StringBuilder _builder = new StringBuilder(256);

        // -----------------------------------------------------------------
        // Panel seam
        // -----------------------------------------------------------------

        /// <summary>Whether the study currently owns the three action slots.</summary>
        public bool OwnsPanel => _mode != Mode.Off;

        /// <summary>Whether a session is open and being written.</summary>
        public bool HasSession => _session != null;

        /// <summary>Seconds since the session opened. Zero when there is none.</summary>
        private float T => _session != null ? Time.realtimeSinceStartup - _t0 : 0f;

        /// <summary>
        /// What the panel's spare slot says. Bound to slot 2, which is empty at both Home and
        /// Character.
        /// </summary>
        public string EntryLabel => _session == null ? "STUDY SETUP" : "STUDY";

        /// <summary>
        /// Opens the study's own screen: setup before a session, the session controls during
        /// one.
        ///
        /// One entry point rather than two, because slot 2 is one button and which screen it
        /// should open is never ambiguous -- there is either a session or there is not.
        /// </summary>
        public void OpenStudy()
        {
            if (_session == null)
            {
                _mode = Mode.Setup;
                _field = Field.Participant;
                _status = "pick participant and room, then start";
                return;
            }

            _mode = Mode.Session;
            _status = "recording";
        }

        /// <summary>Kept for the panel's older binding. See <see cref="OpenStudy"/>.</summary>
        public void OpenSetup() => OpenStudy();

        public string SlotLabel(int slot)
        {
            switch (_mode)
            {
                case Mode.Setup: return SetupSlotLabel(slot);
                case Mode.Session: return SessionSlotLabel(slot);
                case Mode.Truth: return truthMarker != null ? truthMarker.SlotLabel(slot) : null;
                case Mode.Reference: return trials != null ? trials.SlotLabel(slot) : null;
                default: return null;
            }
        }

        public string SlotBlocked(int slot)
        {
            if (_mode == Mode.Truth)
                return truthMarker != null ? truthMarker.SlotBlocked(slot) : "";

            if (_mode == Mode.Reference)
                return trials != null ? trials.SlotBlocked(slot) : "";

            if (_mode == Mode.Session && slot == 2) return ToolBlocked();

            return "";
        }

        public void PressSlot(int slot)
        {
            switch (_mode)
            {
                case Mode.Setup: PressSetup(slot); break;
                case Mode.Session: PressSession(slot); break;
                case Mode.Truth: PressTruth(slot); break;
                case Mode.Reference: PressReference(slot); break;
            }
        }

        // -----------------------------------------------------------------
        // Setup
        // -----------------------------------------------------------------

        private string SetupSlotLabel(int slot)
        {
            switch (slot)
            {
                case 0:
                    return $"FIELD: {FieldName(_field)}";

                case 1:
                    // Leave is not a setting, so its action slot reads as the way out rather
                    // than as a value to change. One control cycles and one acts, on every
                    // field including this one -- the pattern does not break for the exit.
                    return _field == Field.Leave ? "LEAVE SETUP" : $"CHANGE: {FieldValue(_field)}";

                case 2:
                    return "START SESSION";

                default:
                    return null;
            }
        }

        private void PressSetup(int slot)
        {
            switch (slot)
            {
                case 0:
                    _field = (Field)(((int)_field + 1) % Enum.GetValues(typeof(Field)).Length);
                    break;

                case 1:
                    if (_field == Field.Leave)
                    {
                        _mode = Mode.Off;
                        _status = "";
                        return;
                    }

                    Advance(_field);
                    break;

                case 2:
                    StartSession();
                    break;
            }
        }

        private static string FieldName(Field field)
        {
            switch (field)
            {
                case Field.Participant: return "PARTICIPANT";
                case Field.Room: return "ROOM";
                case Field.ObservationLog: return "OBS LOG";
                case Field.Budget: return "REQ BUDGET";
                case Field.Enforce: return "AT BUDGET";
                default: return "(exit)";
            }
        }

        private string FieldValue(Field field)
        {
            switch (field)
            {
                case Field.Participant: return ParticipantId;
                case Field.Room: return RoomLabel;
                case Field.ObservationLog: return _obsArmed ? "ARMED" : "OFF";
                case Field.Budget: return convaiBudget <= 0 ? "NONE" : convaiBudget.ToString();

                // Spelled as what happens rather than as on/off. "AT BUDGET: WARN" says what
                // the session will do at the line; "ENFORCE: OFF" needs you to remember which
                // way round it reads, in a headset, with a participant waiting.
                case Field.Enforce: return enforceBudget ? "HARD STOP" : "WARN ONLY";

                default: return "";
            }
        }

        private void Advance(Field field)
        {
            switch (field)
            {
                case Field.Participant:
                    _participant = _participant % Mathf.Max(1, participantCount) + 1;
                    break;

                case Field.Room:
                    if (roomLabels != null && roomLabels.Count > 0)
                        _room = (_room + 1) % roomLabels.Count;
                    break;

                case Field.ObservationLog:
                    _obsArmed = !_obsArmed;
                    break;

                case Field.Budget:
                    convaiBudget = NextBudget(convaiBudget);
                    break;

                case Field.Enforce:
                    enforceBudget = !enforceBudget;
                    break;
            }
        }

        /// <summary>
        /// The next budget in the ring.
        ///
        /// A value set in the Inspector that is not one of the steps -- which is allowed, and
        /// is how an odd ceiling found in a pilot gets used -- lands on the first step above
        /// it rather than being snapped away, so one press never silently discards it.
        /// </summary>
        private static int NextBudget(int current)
        {
            foreach (var step in BudgetSteps)
                if (step > current) return step;

            return BudgetSteps[0];
        }

        public string ParticipantId => $"P{_participant:D2}";

        public string RoomLabel =>
            roomLabels != null && roomLabels.Count > 0
                ? roomLabels[Mathf.Clamp(_room, 0, roomLabels.Count - 1)]
                : "R1";

        // -----------------------------------------------------------------
        // Session slots
        // -----------------------------------------------------------------

        private string SessionSlotLabel(int slot)
        {
            switch (slot)
            {
                // One press, always, and first in the row. This is the only control here whose
                // value is the instant it was pressed at.
                case 0: return "MARK NOTE";

                case 1: return $"NEXT: {ToolName(_tool)}";
                case 2: return ToolAction(_tool);
                default: return null;
            }
        }

        private static string ToolName(Tool tool)
        {
            switch (tool)
            {
                case Tool.Reference: return "REF BLOCK";
                case Tool.Truth: return "MARK TRUTH";
                case Tool.End: return "END SESSION";
                default: return "LEAVE STUDY";
            }
        }

        private static string ToolAction(Tool tool)
        {
            switch (tool)
            {
                case Tool.Reference: return "OPEN TRIALS";
                case Tool.Truth: return "OPEN MARKING";
                case Tool.End: return "END IT";
                default: return "LEAVE";
            }
        }

        /// <summary>Why the action slot is greyed, or empty when it can be pressed.</summary>
        private string ToolBlocked()
        {
            switch (_tool)
            {
                case Tool.Reference:
                    if (trials == null) return "no runner in scene";
                    return "";

                case Tool.Truth:
                    return truthMarker == null ? "no marker in scene" : "";

                default:
                    return "";
            }
        }

        private void PressSession(int slot)
        {
            switch (slot)
            {
                case 0:
                    // A marker with no text. What happened is written on the facilitator's
                    // sheet against the clock; what this supplies is the instant, on the same
                    // clock as everything else in the file, which is the part paper cannot do.
                    Note("note", $"marked at {T:F1}s");
                    _status = "note marked";
                    break;

                case 1:
                    _tool = (Tool)(((int)_tool + 1) % Enum.GetValues(typeof(Tool)).Length);
                    break;

                case 2:
                    RunTool(_tool);
                    break;
            }
        }

        private void RunTool(Tool tool)
        {
            switch (tool)
            {
                case Tool.Reference:
                    OpenReference();
                    break;

                case Tool.Truth:
                    if (truthMarker == null)
                    {
                        _status = "no RoomTruthMarker in the scene";
                        return;
                    }

                    truthMarker.Begin(RoomLabel);
                    _mode = Mode.Truth;
                    _status = "marking ground truth";
                    break;

                case Tool.End:
                    EndSession();
                    break;

                default:
                    // Back to the app's own flow, with the session still recording underneath.
                    // This is the way to reach BRING IN CHARACTER without ending anything.
                    _mode = Mode.Off;
                    _status = "recording";
                    break;
            }
        }

        // -----------------------------------------------------------------
        // Reference trials
        // -----------------------------------------------------------------

        /// <summary>
        /// Hands the panel to the trial runner, building the block first if it has none.
        ///
        /// Built on entry rather than at session start, because the block is generated from the
        /// objects in the REPLAYED scan -- and at session start there usually is not one yet.
        /// The participant scans the room first.
        /// </summary>
        private void OpenReference()
        {
            if (trials == null)
            {
                _status = "no ReferenceTrialRunner in the scene";
                return;
            }

            if (!trials.IsRunning && trials.Block.planned == 0 && !trials.Build(ParticipantId))
            {
                // Build already put the reason on its own status line, and says more about it
                // in the console. Left here rather than started anyway: a block that cannot be
                // generated is one the room cannot support, and finding that out mid-trial is
                // worse than finding it out now.
                _status = "could not build the block - see the console";
                return;
            }

            // Copied as soon as it exists, not only when the block finishes. The seed is what
            // makes the trial order reproducible, and a session abandoned halfway would
            // otherwise have run a block nobody can reconstruct.
            _session.referenceBlock = trials.Block;
            Flush();

            trials.ClearLeaveRequest();
            _mode = Mode.Reference;
            _status = "reference trials";
        }

        private void PressReference(int slot)
        {
            if (trials == null)
            {
                _mode = Mode.Session;
                return;
            }

            trials.PressSlot(slot);

            // The runner decides when it is finished with the buttons, the same way the truth
            // marker does. Asking it after each press keeps the two screens from disagreeing
            // about who currently owns the row.
            if (!trials.WantsToLeave) return;

            trials.ClearLeaveRequest();
            _mode = Mode.Session;
            _status = "recording";
        }

        /// <summary>Copies the block's shape into the session the moment it is generated.</summary>
        private void HandleTrialFinished(ReferenceTrialEntry entry)
        {
            if (_session == null || entry == null) return;

            _session.referenceTrials.Add(entry);

            // Flushed per trial, unlike a Convai turn. A trial is the study's primary outcome
            // and they arrive about twice a minute, so the write is neither hot nor frequent --
            // and a block interrupted by a headset coming off keeps everything up to the last
            // completed trial rather than up to the last coarse event.
            Flush();
        }

        private void HandleTrialAttempt(ReferenceAttemptEntry entry)
        {
            if (_session == null || entry == null) return;

            _session.referenceAttempts.Add(entry);
        }

        private void HandleBlockFinished(ReferenceBlock block)
        {
            if (_session == null || block == null) return;

            _session.referenceBlock = block;

            Note("ref-block", $"{block.completed} of {block.planned} trials, seed {block.seed}");
            Flush();
        }

        private void PressTruth(int slot)
        {
            if (truthMarker == null)
            {
                _mode = Mode.Session;
                return;
            }

            truthMarker.PressSlot(slot);

            // The marker decides when it is finished; asking it each press is simpler than
            // having it call back, and it cannot get out of step with the panel this way.
            if (!truthMarker.IsMarking)
            {
                _mode = Mode.Session;
                _status = $"truth: {truthMarker.Count} objects marked";

                if (_session != null) _session.summary.truthObjects = truthMarker.Count;
                Flush();
            }
        }

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Awake()
        {
            if (panel == null) panel = FindAnyObjectByType<ConvaiRoomModePanel>();
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (truthMarker == null) truthMarker = FindAnyObjectByType<RoomTruthMarker>();
            if (observationLog == null) observationLog = FindAnyObjectByType<ScanObservationLog>();
            if (trials == null) trials = FindAnyObjectByType<ReferenceTrialRunner>();

            // One clock for the whole file. The runner would otherwise stamp its rows with
            // realtimeSinceStartup while everything else here is seconds since the session
            // opened, and two clocks in one file is how an offline join produces an ordering
            // that never happened.
            if (trials != null) trials.TimeSource = () => T;

            // Wired here rather than in OnEnable because these are the counter's own events,
            // not the SDK's -- the counter attaches and detaches from Convai on its own
            // schedule, and re-subscribing to it on every enable would double the handlers.
            _budget.verboseLogging = verboseLogging;
            _budget.OnTurn += HandleConvaiTurn;
            _budget.OnBudgetSpent += HandleBudgetSpent;
            _budget.OnQuotaExhausted += HandleQuotaExhausted;

            // Quiet even when the recorder is verbose: this fires four or five times per
            // exchange, and a line each would bury the entries that matter during a session.
            _speech.verboseLogging = false;

            // The same clock the trial runner is given, and the same reason: two clocks in one
            // file is how an offline join produces an ordering that never happened.
            _speech.TimeSource = () => T;
            _speech.OnSpeechEvent += HandleSpeechEvent;
        }

        private void OnEnable()
        {
            // The two subscriptions are independent. Guarding both behind the panel would mean a
            // scene with no panel silently recorded no trials either, which is a much larger
            // loss than the missing buttons that caused it.
            if (panel != null)
            {
                panel.OnStageChanged += HandleStageChanged;
                panel.OnReported += HandleReported;
            }

            if (trials == null) return;

            trials.OnTrialFinished += HandleTrialFinished;
            trials.OnAttempt += HandleTrialAttempt;
            trials.OnBlockFinished += HandleBlockFinished;
        }

        private void OnDisable()
        {
            if (panel != null)
            {
                panel.OnStageChanged -= HandleStageChanged;
                panel.OnReported -= HandleReported;
            }

            if (trials != null)
            {
                trials.OnTrialFinished -= HandleTrialFinished;
                trials.OnAttempt -= HandleTrialAttempt;
                trials.OnBlockFinished -= HandleBlockFinished;
            }

            // Before the flush, because a hold that outlives the thing holding it leaves the
            // app with a muted microphone and nothing left to open it -- a state you can only
            // get out of by restarting.
            _budget.Detach();
            _speech.Detach();

            // A session that is still open when this is torn down has whatever it collected
            // still in memory. Writing it is the difference between a short session and no
            // session at all.
            if (_session != null) Flush();
        }

        /// <summary>
        /// Writes on pause, which on a Quest is what taking the headset off looks like.
        ///
        /// The proximity sensor pauses the app, and that -- not a clean quit through the
        /// panel's EXIT -- is how a participant session actually ends. Without this the last
        /// stretch of every session is lost.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (paused && _session != null) Flush();
        }

        private void OnApplicationQuit()
        {
            if (_session != null) EndSession();
        }

        // -----------------------------------------------------------------
        // Recording
        // -----------------------------------------------------------------

        public void StartSession()
        {
            if (_session != null)
            {
                _status = "already recording";
                return;
            }

            var startUtc = DateTime.UtcNow;
            var run = StudySessionIO.NextRun(ParticipantId);

            _t0 = Time.realtimeSinceStartup;
            _milestoned.Clear();
            _saveIndex = 0;
            _openRun = null;

            _session = new StudySession
            {
                sessionId = StudySessionIO.MakeSessionId(ParticipantId, RoomLabel, run, startUtc),
                participantId = ParticipantId,
                roomLabel = RoomLabel,
                run = run,
                startedUtc = startUtc.ToString("o"),
                appVersion = Application.version,
                deviceModel = SystemInfo.deviceModel,
                build = CaptureBuild()
            };

            // Established before anything can change it, so a save made a second later is
            // measured as a change rather than as the first one.
            RefreshDiskBaseline();

            ArmObservationLog();

            // The budget is per participant, so the count starts again here. What Reset does
            // NOT clear is a backend quota already reported gone -- that is per account, not
            // per participant, and a session started after one was hit is a session with no
            // conversation in it. The panel keeps saying so.
            _budget.Budget = convaiBudget;
            _budget.Enforce = enforceBudget;
            _budget.Reset();
            _speech.Reset();

            Note("session-start", $"{ParticipantId} {RoomLabel} run {run}");
            Note("budget", $"{(convaiBudget <= 0 ? "no budget" : convaiBudget + " requests")}, " +
                           $"{(enforceBudget ? "enforced" : "warn only")}");

            if (_budget.QuotaExhausted)
            {
                // Worth a line in the file of its own. The turn count will read zero and every
                // conversation measure will be empty, and this is the only thing that says the
                // session was doomed before it opened rather than merely quiet.
                Note("quota-exhausted", $"already gone at session start ({_budget.QuotaType})");

                Debug.LogError($"{Tag} Starting a session with the Convai quota already " +
                               $"exhausted. She will not answer. Nothing conversational will " +
                               $"be recorded.");
            }

            // The panel goes BACK TO ITS OWN FLOW here, and the session records underneath it.
            //
            // It used to stay on Mode.Session, which held all three action slots for the whole
            // session -- so START NEW SCAN, PROCEED and BRING IN CHARACTER were unreachable
            // from the moment recording began, and a participant could not actually be taken
            // through the protocol they were recording. The study screen is reached again from
            // the spare slot at Home and at Character; see EntryLabel.
            _mode = Mode.Off;
            _tool = Tool.Reference;
            _status = "recording";

            Debug.Log($"{Tag} Session {_session.sessionId} started " +
                      $"(obs log {(_obsArmed ? "ARMED" : "off")}).");

            Flush();
        }

        public void EndSession()
        {
            if (_session == null) return;

            CloseRun();

            if (observationLog != null && observationLog.IsOpen)
            {
                observationLog.Close();
                if (recorder != null) recorder.recordObservations = false;
            }

            // Enforcement is lifted rather than the counter detached: the count is still worth
            // showing on the panel between participants, and the next Tick gives the
            // microphone back. Leaving a hold in place past END SESSION would mute the app for
            // whoever picks the headset up next.
            _budget.Enforce = false;

            Note("session-end", $"{T:F1}s");
            _session.endedUtc = DateTime.UtcNow.ToString("o");

            UpdateSummary();
            Flush();

            Debug.Log($"{Tag} Session {_session.sessionId} ended -- " +
                      $"{_session.scanRuns.Count} scan runs, {_session.saves.Count} saves, " +
                      $"{_session.milestones.Count} milestones, " +
                      $"{_session.summary.truthObjects} truth objects, " +
                      $"{_session.summary.convaiRequests} Convai turns.");

            _session = null;
            _mode = Mode.Off;
            _status = "";
        }

        private StudyBuild CaptureBuild()
        {
            var build = new StudyBuild { observationLogArmed = _obsArmed };

            if (recorder == null) return build;

            build.minObservations = recorder.minObservations;
            build.extentPercentile = recorder.extentPercentile;
            build.extentSampleCount = recorder.extentSampleCount;
            build.mergeRadius = recorder.mergeRadius;
            build.mergeOverlap = recorder.mergeOverlap;
            build.maxObjectSize = recorder.maxObjectSize;
            build.minConfidence = recorder.minConfidence;

            if (recorder.ignoredLabels != null)
                build.ignoredLabels = new List<string>(recorder.ignoredLabels);

            return build;
        }

        private void ArmObservationLog()
        {
            if (!_obsArmed) return;

            if (observationLog == null || recorder == null)
            {
                Debug.LogWarning($"{Tag} Observation log was armed but there is no " +
                                 $"ScanObservationLog or ObjectScanRecorder in the scene. " +
                                 $"Nothing will be recorded, and the extent ablation cannot " +
                                 $"be computed from this session.");

                _session.build.observationLogArmed = false;
                return;
            }

            recorder.recordObservations = true;
            observationLog.Open(ScanObservationLog.PathForStem(_session.sessionId),
                                recorder.DescribeSettings());
        }

        // -----------------------------------------------------------------
        // Panel events
        // -----------------------------------------------------------------

        /// <summary>
        /// Opens and closes scan runs as the flow moves.
        ///
        /// Scanning and Saved are treated as ONE run between them. Saving mid-scan bounces the
        /// panel into Saved to ask whether to carry on, and answering "keep scanning" comes
        /// straight back -- so counting that as two runs would split one continuous collection
        /// period in half and put a spurious gap in the middle of the time-to-detect curve.
        /// </summary>
        private void HandleStageChanged(ConvaiRoomModePanel.Stage from, ConvaiRoomModePanel.Stage to)
        {
            if (_session == null) return;

            var wasScanning = IsScanStage(from);
            var nowScanning = IsScanStage(to);

            if (!wasScanning && nowScanning) OpenRun();
            else if (wasScanning && !nowScanning) CloseRun();

            Flush();
        }

        private static bool IsScanStage(ConvaiRoomModePanel.Stage stage) =>
            stage == ConvaiRoomModePanel.Stage.Scanning || stage == ConvaiRoomModePanel.Stage.Saved;

        private void OpenRun()
        {
            if (_openRun != null) return;

            // The milestone set is cleared per run, not per session. Starting a new scan
            // clears the recorder's clusters and its ids begin again, so a stale set would
            // suppress the first few milestones of the second scan -- which is precisely the
            // part of the curve being measured.
            _milestoned.Clear();

            _openRun = new ScanRunEntry { tStart = T };
            _session.scanRuns.Add(_openRun);

            if (verboseLogging) Debug.Log($"{Tag} Scan run {_session.scanRuns.Count} opened.");
        }

        private void CloseRun()
        {
            if (_openRun == null) return;

            _openRun.tStop = T;
            _openRun.exportedAtStop = CountExportable();
            _openRun = null;
        }

        /// <summary>
        /// Keeps every panel message, and watches for the one that says a save landed.
        ///
        /// The text is kept verbatim rather than classified. These strings are the app's own
        /// account of what it did -- "nothing ready to save (12 tracked, 0 ready)" carries
        /// both counts and the refusal in one line -- and a classifier written now would be a
        /// guess at which distinctions matter to an analysis that has not been run yet.
        /// </summary>
        private void HandleReported(string message)
        {
            if (_session == null || string.IsNullOrEmpty(message)) return;

            _session.reports.Add(new ReportEntry
            {
                t = T,
                stage = "",
                text = message
            });

            if (message.StartsWith("saved ", StringComparison.OrdinalIgnoreCase))
                _panelSaidSavedAt = Time.realtimeSinceStartup;
        }

        // -----------------------------------------------------------------
        // Convai turns
        // -----------------------------------------------------------------

        /// <summary>
        /// One participant turn, counted.
        ///
        /// Not flushed. Turns arrive while somebody is talking, a flush re-serialises the whole
        /// session, and writing to disk on the conversation's own cadence is exactly the kind
        /// of instrumentation cost this recorder was built to avoid. The entries ride along to
        /// the next coarse event, and OnApplicationPause catches the headset coming off, which
        /// is how a session really ends.
        /// </summary>
        private void HandleConvaiTurn(int index, string messageId)
        {
            if (_session == null) return;

            _session.convaiTurns.Add(new ConvaiTurnEntry
            {
                t = T,
                index = index,
                messageId = messageId ?? ""
            });
        }

        /// <summary>
        /// One speech boundary.
        ///
        /// Not flushed, for the same reason a turn is not: these arrive several times per
        /// exchange while somebody is talking, and a flush re-serialises the whole session.
        /// They ride along to the next coarse event, and OnApplicationPause catches the headset
        /// coming off, which is how a session really ends.
        /// </summary>
        private void HandleSpeechEvent(SpeechEventEntry entry)
        {
            if (_session == null || entry == null) return;

            _session.speech.Add(entry);
        }

        /// <summary>
        /// The app's own budget line, reached.
        ///
        /// Flushed, unlike a turn: this is rare, it is the moment the session's character
        /// changes, and if the headset comes off in the confusion that follows it is the last
        /// thing anyone would want missing from the file.
        /// </summary>
        private void HandleBudgetSpent()
        {
            _status = enforceBudget
                ? $"BUDGET SPENT ({convaiBudget}) - mic held, no more turns"
                : $"budget spent ({convaiBudget}) - turns continue, not enforced";

            if (_session == null) return;

            Note("budget-spent", $"{_budget.Used} turns, " +
                                 $"{(enforceBudget ? "enforced" : "warn only")}");
            Flush();
        }

        /// <summary>
        /// The backend's ceiling, reached. Everything after this is a session with no
        /// conversation in it, so it is written down immediately and said as loudly as the
        /// panel allows.
        /// </summary>
        private void HandleQuotaExhausted(string quotaType, string message)
        {
            _status = $"CONVAI QUOTA GONE ({quotaType}) - she is offline";

            if (_session == null) return;

            Note("quota-exhausted", $"{quotaType}: {message}");
            Flush();
        }

        // -----------------------------------------------------------------
        // Polling
        // -----------------------------------------------------------------

        private void Update()
        {
            // Ticked before the session check, and so counting outside a session too. Two
            // reasons: a hold taken during a session has to be released after END SESSION,
            // which is a moment when there is no session to gate on; and the panel showing a
            // live count during a pilot turn is how anyone finds out the counter is attached
            // at all before it matters.
            _budget.Tick();
            _speech.Tick();

            if (_session == null) return;

            if (Time.unscaledTime >= _nextClusterPoll)
            {
                _nextClusterPoll = Time.unscaledTime + Mathf.Max(0.05f, clusterPollInterval);
                PollClusters();
            }

            if (Time.unscaledTime >= _nextDiskPoll)
            {
                _nextDiskPoll = Time.unscaledTime + Mathf.Max(0.25f, diskPollInterval);
                PollDisk();
            }
        }

        /// <summary>
        /// Writes down the first moment each cluster became exportable.
        ///
        /// The crossing of minObservations rather than first sighting, deliberately: that is
        /// the threshold the precision figure is scored at, so the recall curve and the
        /// precision number describe the same event instead of two different ones. First
        /// sighting is still recoverable -- firstSeenUtc rides along in the scan copy.
        ///
        /// Polled rather than evented. The recorder raises OnClusterChanged on every accepted
        /// detection, a hundred times a second, and never on this transition at all -- so an
        /// event here would mean adding a listener to the hottest path in the app to learn
        /// something a quarter-second poll answers just as well for a curve read at 30, 60
        /// and 120 seconds.
        /// </summary>
        private void PollClusters()
        {
            if (recorder == null) return;

            recorder.SnapshotClusters(_snapshot);

            foreach (var view in _snapshot)
            {
                if (!view.Exportable) continue;
                if (!_milestoned.Add(view.Id)) continue;

                _session.milestones.Add(new MilestoneEntry
                {
                    t = T,
                    clusterId = view.Id,
                    observations = view.Observations,
                    label = view.Label,
                    roomPosition = new Vec3(view.RoomCenter)
                });
            }
        }

        /// <summary>
        /// Notices that room_scan.json changed, and takes a copy of it.
        ///
        /// The copy is the whole point. Every scan-accuracy measure -- recall, precision,
        /// position error, duplicate rate -- is computed against the exported objects, and
        /// the file holding them is at one fixed path that the next participant overwrites.
        /// Without a copy taken at the moment of the save, the subject of the measurement is
        /// gone by the time anybody sits down to measure it.
        ///
        /// Size and timestamp only, never a parse: this runs on a timer, and RoomScanIO.Load
        /// logs on every call.
        /// </summary>
        private void PollDisk()
        {
            var path = RoomScanIO.DefaultPath;
            if (!File.Exists(path)) return;

            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc == _lastScanWrite && info.Length == _lastScanBytes) return;

            _lastScanWrite = info.LastWriteTimeUtc;
            _lastScanBytes = info.Length;

            var viaPanel = Time.realtimeSinceStartup - _panelSaidSavedAt < 2f;
            var copiedTo = CopyScan(path, ++_saveIndex);

            _session.saves.Add(new ScanSaveEntry
            {
                t = T,
                objects = CountExportable(),
                bytes = info.Length,
                copiedTo = copiedTo,
                viaPanel = viaPanel
            });

            if (!viaPanel)
            {
                // The A button on the controller calls ExportToJson directly, skipping every
                // guard SaveScan applies and announcing nothing. Recording it as a deviation
                // is more useful than quietly filing it as a normal save.
                Note("bypass", "scan file changed with no panel save -- A button");
                Debug.LogWarning($"{Tag} The scan file changed without a panel save. That is " +
                                 $"the A-button export, which bypasses the panel's checks.");
            }

            Flush();
        }

        private string CopyScan(string path, int index)
        {
            var target = Path.Combine(StudySessionIO.Folder,
                                      $"{_session.sessionId}.scan.{index}.json");

            try
            {
                StudySessionIO.EnsureFolder();
                File.Copy(path, target, overwrite: true);

                if (verboseLogging) Debug.Log($"{Tag} Copied the scan -> {target}");
                return Path.GetFileName(target);
            }
            catch (Exception ex)
            {
                // Loud, because this is the failure that costs a participant. Everything else
                // in the file is context for the objects in this copy.
                Debug.LogError($"{Tag} COULD NOT COPY THE SCAN to {target}: {ex.Message}. " +
                               $"This session's scan accuracy cannot be scored.");

                Note("error", $"scan copy failed: {ex.Message}");
                return "";
            }
        }

        private int CountExportable()
        {
            if (recorder == null) return 0;

            recorder.SnapshotClusters(_snapshot);

            var ready = 0;
            foreach (var view in _snapshot)
                if (view.Exportable) ready++;

            return ready;
        }

        // -----------------------------------------------------------------
        // Writing
        // -----------------------------------------------------------------

        private void Note(string kind, string text)
        {
            if (_session == null) return;

            _session.notes.Add(new NoteEntry { t = T, kind = kind, text = text });
        }

        private void UpdateSummary()
        {
            if (_session == null) return;

            var s = _session.summary;
            s.scanRuns = _session.scanRuns.Count;
            s.scanSaves = _session.saves.Count;
            s.milestones = _session.milestones.Count;
            s.objectsExported = _session.saves.Count > 0
                ? _session.saves[_session.saves.Count - 1].objects
                : 0;

            if (truthMarker != null) s.truthObjects = truthMarker.Count;

            if (observationLog != null)
            {
                s.obsRecords = observationLog.Written;
                s.obsDropped = observationLog.Dropped;
            }

            // Read off the counter rather than from convaiTurns.Count. The two are equal by
            // construction -- the count restarts when the session opens and every turn after
            // that appends a row -- and taking it from the counter is what keeps them equal:
            // the number reported here and the number enforcement acts on cannot drift apart
            // if there is only one of them.
            s.convaiRequests = _budget.Used;
            s.convaiBudget = _budget.Budget;
            s.convaiBudgetEnforced = _budget.Enforce;
            s.convaiQuotaExhausted = _budget.QuotaExhausted;
            s.convaiQuotaType = _budget.QuotaType ?? "";

            s.participantUtterances = _speech.ParticipantUtterances;
            s.characterUtterances = _speech.CharacterUtterances;
            s.characterInterruptions = _speech.Interruptions;
            s.llmNoResponses = _speech.NoResponses;
        }

        /// <summary>
        /// Re-serialises the whole session over the top of its file.
        ///
        /// Rewriting rather than appending, because the file is one JsonUtility object and
        /// there is no append that keeps it valid. It is cheap -- a long session is well under
        /// a hundred kilobytes and this runs on coarse events, not per frame -- and it means
        /// the file on disk is always a complete, parseable session rather than a fragment
        /// that needs repairing if the headset is put down mid-run.
        /// </summary>
        private void Flush()
        {
            if (_session == null) return;

            UpdateSummary();

            try
            {
                StudySessionIO.Save(_session);
                _lastWriteAt = Time.realtimeSinceStartup;
            }
            catch (Exception ex)
            {
                _status = $"WRITE FAILED: {ex.Message}";
                Debug.LogError($"{Tag} Could not write the session file: {ex}");
            }
        }

        /// <summary>
        /// The block drawn on the details panel.
        ///
        /// Exists so the facilitator can see the session is alive before the participant takes
        /// the headset off. The write age is the load-bearing number: a session that has not
        /// been written for minutes is one that is not recording, and this is the only place
        /// that shows.
        /// </summary>
        public string DetailsBlock()
        {
            _builder.Clear();

            if (_session == null) return "";

            _builder.Append("study     : ").Append(_session.sessionId).AppendLine();

            _builder.Append("collected : ")
                    .Append(_session.scanRuns.Count).Append(" runs, ")
                    .Append(_session.saves.Count).Append(" saves, ")
                    .Append(_session.milestones.Count).Append(" objects, ")
                    .Append(_session.summary.truthObjects).Append(" truth")
                    .AppendLine();

            _builder.AppendLine(_budget.Describe());

            // Only once somebody has spoken. Before that it is a row saying nothing has
            // happened yet, on a readout with five other things competing for the same space.
            if (_speech.ParticipantUtterances > 0 || _speech.CharacterUtterances > 0)
                _builder.AppendLine(_speech.Describe());

            // Only once there is a block. Before that the line would be a permanent "trials:
            // none built" on a readout with four other things competing for the same few rows.
            if (trials != null && (trials.IsRunning || trials.Block.planned > 0))
                _builder.AppendLine(trials.DetailsBlock());

            if (_session.build.observationLogArmed && observationLog != null)
            {
                _builder.Append("obs log   : ")
                        .Append(observationLog.IsOpen ? "recording " : "closed ")
                        .Append(observationLog.Written).Append(" records");

                if (observationLog.Dropped > 0)
                    _builder.Append(", ").Append(observationLog.Dropped)
                            .Append(" DROPPED - ablation void");

                _builder.AppendLine();
            }

            var age = Time.realtimeSinceStartup - _lastWriteAt;
            _builder.Append("written   : ").Append(age < 1f ? "just now" : $"{age:F0}s ago");

            if (!string.IsNullOrEmpty(_status)) _builder.AppendLine().Append("note      : ").Append(_status);

            return _builder.ToString();
        }

        /// <summary>Reads the scan file's current state without counting it as a save.</summary>
        private void RefreshDiskBaseline()
        {
            var path = RoomScanIO.DefaultPath;

            if (!File.Exists(path))
            {
                _lastScanWrite = DateTime.MinValue;
                _lastScanBytes = -1;
                return;
            }

            var info = new FileInfo(path);
            _lastScanWrite = info.LastWriteTimeUtc;
            _lastScanBytes = info.Length;
        }
    }
}
