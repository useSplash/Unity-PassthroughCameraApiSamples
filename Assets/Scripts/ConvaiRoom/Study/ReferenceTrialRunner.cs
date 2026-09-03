using System;
using System.Collections.Generic;
using System.Text;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Runs the reference-resolution block: cue an object, ask the participant to indicate it
    /// by naming or by pointing, and time how long the app takes to agree with them.
    ///
    /// This is the study's primary outcome, and the whole design of it is shaped by two things
    /// that cannot be negotiated with: a Convai request budget that a runaway block would eat,
    /// and fifteen to twenty minutes in a headset.
    ///
    /// WHAT IS MEASURED, precisely. Not "did she understand", but "did THIS APP resolve the
    /// referring expression to the object that was cued". Both modalities end at the same
    /// place -- RoomScanPointer for a point, RoomAttentionExecutor for a name -- and both raise
    /// their event before the character is consulted, so nothing here is timing a network round
    /// trip. A trial is scored on the scan file's id, never on the display name: display names
    /// are invented at rebuild time by a greedy landmark pass and are not stable across
    /// rebuilds, so "chair by the couch" can be a different chair tomorrow.
    ///
    /// T0 IS THE END OF THE CUE. The target is highlighted for about two seconds and the clock
    /// starts when the highlight goes out. That is a machine-precise instant on the same clock
    /// as everything else in the session file, and it costs nothing to defend: the alternative,
    /// a facilitator deciding when the participant "started", is a judgement made in a headset
    /// under time pressure and would differ between participants and between conditions.
    ///
    /// NAMING TRIALS ARE CAPPED AT TWO ATTEMPTS. This is the single thing that makes the
    /// request budget bounded rather than hopeful. Every naming attempt is a Convai turn and
    /// therefore a request; an uncapped block of twelve naming trials could spend the entire
    /// participant's quota on one stubborn chair. Pointing costs nothing -- it never reaches
    /// Convai at all -- so pointing trials run to the timeout instead, and the asymmetry is a
    /// deliberate consequence of what the two modalities actually cost.
    ///
    /// HOW A DISTRACTOR COUNT IS REALISED. Not by hiding objects: the room is the room, and a
    /// study that removed furniture between trials would be measuring a different app. A trial
    /// with N distractors is one whose TARGET has N competitors sharing its label -- four
    /// chairs make a three-distractor target. That is what makes the condition a real
    /// difficulty gradient in both modalities at once: naming gets harder because the label
    /// stops being a unique description, and pointing gets harder because the candidates are
    /// alike and often close together. A room that cannot supply a condition says so at
    /// generation time, in the file and on the panel, rather than quietly producing an easier
    /// trial that would be analysed as a hard one.
    ///
    /// It is driven from the panel and the controller only, like everything else here.
    /// </summary>
    public class ReferenceTrialRunner : MonoBehaviour
    {
        private const string Tag = "[RefTrials]";

        public enum Modality
        {
            Naming,
            Pointing
        }

        /// <summary>Where one trial has got to.</summary>
        private enum Phase
        {
            /// <summary>No block, or the block is finished.</summary>
            Idle,

            /// <summary>The target is highlighted. The clock has not started.</summary>
            Cue,

            /// <summary>Cue over, clock running, waiting for the participant to indicate.</summary>
            Waiting,

            /// <summary>Trial over, a breath before the next cue.</summary>
            Between,

            /// <summary>Every trial has been run.</summary>
            Finished
        }

        [Header("Wiring (left empty, these are found in the scene)")]
        public RoomScanRebuilder rebuilder;

        [Tooltip("The pointing modality. Its OnAttentionChanged is what a pointing trial is " +
                 "scored on.")]
        public RoomScanPointer pointer;

        [Tooltip("The naming modality -- the executor behind the character's Look At action. " +
                 "Without one in the scene there is no naming condition and the block says so " +
                 "rather than running half a design.")]
        public RoomAttentionExecutor attention;

        [Tooltip("Optional. Only used to name the target on the panel so the facilitator can " +
                 "read out the cue if the participant missed the highlight.")]
        public RoomScanContext context;

        [Header("Design")]
        [Tooltip("Repetitions of each modality x distractor-count cell.\n\n" +
                 "Three gives 24 trials and 96 observations at n = 4; two gives 16 and 64. " +
                 "Three costs roughly three more minutes in the headset and about six more " +
                 "Convai requests, since only the naming half spends any.")]
        [Range(1, 6)] public int reps = 3;

        [Tooltip("Distractor counts to test, from 1 up to this. A trial's target is chosen to " +
                 "have this many competitors sharing its label.")]
        [Range(1, 6)] public int maxDistractors = 4;

        [Tooltip("How long the target is highlighted. The clock starts when this ends.")]
        public float cueSeconds = 2f;

        [Tooltip("How long a trial may run before it is scored as a timeout.")]
        public float timeoutSeconds = 60f;

        [Tooltip("A breath between trials, so the next cue does not appear on the same frame " +
                 "the last one was answered.")]
        public float betweenSeconds = 1.5f;

        [Tooltip("Attempts allowed in a NAMING trial before it is scored incorrect and the " +
                 "block moves on.\n\n" +
                 "This is what bounds the Convai request budget: every naming attempt is a " +
                 "turn, and therefore a request. Pointing is not capped because it costs " +
                 "nothing -- it never reaches Convai.")]
        [Range(1, 5)] public int namingAttemptCap = 2;

        [Header("Cue")]
        [Tooltip("The colour the target is highlighted with. Deliberately not the pointer's " +
                 "highlight colour -- 'this is the target' and 'you are pointing at this' must " +
                 "never look the same, or the cue teaches the answer.")]
        public Color cueColor = new Color(0.3f, 0.9f, 1f, 1f);

        [Header("Debug")]
        public bool verboseLogging = true;

        // -----------------------------------------------------------------

        /// <summary>One trial, as generated. The proxy is resolved when the block is built.</summary>
        private class Trial
        {
            public Modality Modality;
            public int Distractors;
            public int ActualDistractors;
            public int Rep;

            public string TargetId = "";
            public string TargetLabel = "";
            public GameObject Proxy;

            public ReferenceTrialEntry Entry;
        }

        private readonly List<Trial> _trials = new List<Trial>();
        private readonly List<string> _unavailable = new List<string>();

        private Phase _phase = Phase.Idle;
        private int _index = -1;
        private float _phaseUntil;

        /// <summary>Realtime at which the current trial's cue ended. t0.</summary>
        private float _t0;

        private int _attempts;
        private int _seed;
        private string _status = "";

        private readonly StringBuilder _builder = new StringBuilder(192);

        // The cue highlight, restored the way RoomScanPointer restores its own: read the colour
        // back off the renderer rather than assuming it, so the box gets back whatever it had.
        private WireBox _cued;
        private Color _cuedWas;

        /// <summary>
        /// Whether the pointer's own highlight was on before the cue borrowed the box.
        ///
        /// The two highlights would otherwise fight over one LineRenderer: the pointer stores
        /// the box's colour when the ray settles, the cue stores it too, and whichever restores
        /// second writes back a colour the other one had already replaced -- leaving a box
        /// permanently cue-coloured, which on the next trial is a second target. Suppressing the
        /// pointer for the length of the cue removes the overlap rather than trying to order it.
        /// </summary>
        private bool _pointerHighlightWas;
        private bool _suppressedPointer;

        /// <summary>Raised as each trial finishes, so the recorder can write it down.</summary>
        public event Action<ReferenceTrialEntry> OnTrialFinished;

        /// <summary>Raised per indication within a trial.</summary>
        public event Action<ReferenceAttemptEntry> OnAttempt;

        /// <summary>Raised when the block ends, however it ended.</summary>
        public event Action<ReferenceBlock> OnBlockFinished;

        /// <summary>Whether a block is running, which is what the recorder hands the panel for.</summary>
        public bool IsRunning => _phase != Phase.Idle && _phase != Phase.Finished;

        /// <summary>How the block was generated. Copied into the session file.</summary>
        public ReferenceBlock Block { get; private set; } = new ReferenceBlock();

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (pointer == null) pointer = FindAnyObjectByType<RoomScanPointer>();
            if (attention == null) attention = FindAnyObjectByType<RoomAttentionExecutor>();
            if (context == null) context = FindAnyObjectByType<RoomScanContext>();
        }

        private void OnEnable()
        {
            if (pointer != null) pointer.OnAttentionChanged += HandlePointed;

            if (attention != null)
            {
                attention.OnAttentionChanged += HandleNamed;
                attention.OnAttentionUnresolved += HandleNameUnresolved;
            }
        }

        private void OnDisable()
        {
            if (pointer != null) pointer.OnAttentionChanged -= HandlePointed;

            if (attention != null)
            {
                attention.OnAttentionChanged -= HandleNamed;
                attention.OnAttentionUnresolved -= HandleNameUnresolved;
            }

            // A cue left on the box would outlive the block and read as a target on whatever
            // runs next. Restoring here covers the scene being torn down mid-trial.
            ClearCue();
        }

        // -----------------------------------------------------------------
        // Building the block
        // -----------------------------------------------------------------

        /// <summary>
        /// Generates the block for one participant, deterministically.
        ///
        /// Returns false, with a reason on the panel, when the room cannot furnish a block at
        /// all -- which is a thing to find out while the participant is still being briefed
        /// rather than four trials in.
        /// </summary>
        public bool Build(string participantId)
        {
            _trials.Clear();
            _unavailable.Clear();

            if (rebuilder == null || rebuilder.Scan == null)
            {
                _status = "no scan replayed - load a scan first";
                Debug.LogWarning($"{Tag} No scan is loaded, so there are no objects to cue.");
                return false;
            }

            _seed = SeedFor(participantId);

            // System.Random, seeded, rather than UnityEngine.Random: the latter is a global the
            // rest of the app also draws from, so an unrelated effect taking one number would
            // silently change this participant's trial order and the recorded seed would no
            // longer reproduce it.
            var random = new System.Random(_seed);

            var byLabel = GroupByLabel();

            for (var distractors = 1; distractors <= maxDistractors; distractors++)
            {
                var candidates = CandidatesFor(byLabel, distractors);

                if (candidates.Count == 0)
                {
                    // Recorded once per condition rather than once per trial it cost, because
                    // the fact is about the room and not about any particular trial.
                    _unavailable.Add($"naming/{distractors}");
                    _unavailable.Add($"pointing/{distractors}");

                    Debug.LogWarning($"{Tag} The room has no object with {distractors} " +
                                     $"same-label competitors, so the {distractors}-distractor " +
                                     $"condition cannot run here. Its cells will be empty.");
                    continue;
                }

                foreach (Modality modality in Enum.GetValues(typeof(Modality)))
                {
                    if (modality == Modality.Naming && attention == null) continue;

                    for (var rep = 1; rep <= reps; rep++)
                    {
                        var pick = candidates[random.Next(candidates.Count)];

                        _trials.Add(new Trial
                        {
                            Modality = modality,
                            Distractors = distractors,
                            ActualDistractors = pick.Competitors,
                            Rep = rep,
                            TargetId = pick.Id,
                            TargetLabel = pick.Label,
                            Proxy = pick.Proxy
                        });
                    }
                }
            }

            if (attention == null)
            {
                for (var d = 1; d <= maxDistractors; d++) _unavailable.Add($"naming/{d}");

                Debug.LogWarning($"{Tag} There is no RoomAttentionExecutor in the scene, so the " +
                                 $"naming condition cannot run and this block is pointing only. " +
                                 $"Add one to the room manager and author the Look At action.");
            }

            Shuffle(_trials, random);

            Block = new ReferenceBlock
            {
                ran = false,
                seed = _seed,
                seedSource = participantId ?? "",
                reps = reps,
                cueSeconds = cueSeconds,
                timeoutSeconds = timeoutSeconds,
                namingAttemptCap = namingAttemptCap,
                scanId = ScanId(),
                planned = _trials.Count,
                completed = 0,
                unavailable = new List<string>(_unavailable)
            };

            if (_trials.Count == 0)
            {
                _status = "the room cannot furnish any trial";
                return false;
            }

            _status = $"{_trials.Count} trials ready (seed {_seed})";

            Debug.Log($"{Tag} Built {_trials.Count} trials for {participantId} from seed {_seed} " +
                      $"over scan '{Block.scanId}'" +
                      $"{(_unavailable.Count > 0 ? $" -- {_unavailable.Count} cells unavailable" : "")}.");

            return true;
        }

        /// <summary>
        /// A seed derived from the participant id, stable forever.
        ///
        /// FNV-1a rather than string.GetHashCode, which is the trap here: .NET randomises string
        /// hashing per process on some runtimes and has changed the algorithm between versions,
        /// so a seed taken from it would differ between two runs of the SAME build and the
        /// recorded number would reproduce nothing. This is eight lines and cannot drift.
        ///
        /// Derived rather than entered so that re-running a participant reproduces their block
        /// exactly -- which is what makes a repeated session comparable rather than a new one.
        /// </summary>
        public static int SeedFor(string participantId)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;

                var hash = offset;
                var text = participantId ?? "";

                foreach (var c in text)
                {
                    hash ^= c;
                    hash *= prime;
                }

                // Masked to non-negative: System.Random rejects int.MinValue outright, and a
                // negative seed is merely awkward to read back off a panel.
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        /// <summary>One object that could be a target, with how crowded it is.</summary>
        private readonly struct Candidate
        {
            public readonly string Id;
            public readonly string Label;
            public readonly GameObject Proxy;

            /// <summary>How many OTHER objects share this one's label.</summary>
            public readonly int Competitors;

            public Candidate(string id, string label, GameObject proxy, int competitors)
            {
                Id = id;
                Label = label;
                Proxy = proxy;
                Competitors = competitors;
            }
        }

        private Dictionary<string, List<RoomScanRebuilder.RebuiltObject>> GroupByLabel()
        {
            var byLabel = new Dictionary<string, List<RoomScanRebuilder.RebuiltObject>>();

            foreach (var entry in rebuilder.Rebuilt)
            {
                if (entry.Proxy == null || entry.Data == null) continue;

                var label = string.IsNullOrEmpty(entry.Data.label) ? "object" : entry.Data.label;

                if (!byLabel.TryGetValue(label, out var list))
                {
                    list = new List<RoomScanRebuilder.RebuiltObject>();
                    byLabel[label] = list;
                }

                list.Add(entry);
            }

            return byLabel;
        }

        /// <summary>
        /// Objects with exactly this many same-label competitors, or failing that the least
        /// crowded objects that have at least this many.
        ///
        /// The fallback is what keeps a real room usable. Exact groups of five are not something
        /// a living room reliably contains, and refusing the condition outright would empty a
        /// quarter of the design over a technicality. The trial records what it actually got,
        /// so the analysis reads the real number rather than the requested one.
        /// </summary>
        private static List<Candidate> CandidatesFor(
            Dictionary<string, List<RoomScanRebuilder.RebuiltObject>> byLabel, int distractors)
        {
            var exact = new List<Candidate>();
            var over = new List<Candidate>();
            var bestOver = int.MaxValue;

            foreach (var pair in byLabel)
            {
                var competitors = pair.Value.Count - 1;
                if (competitors < distractors) continue;

                foreach (var entry in pair.Value)
                {
                    var candidate = new Candidate(entry.Data.id, pair.Key, entry.Proxy, competitors);

                    if (competitors == distractors)
                    {
                        exact.Add(candidate);
                        continue;
                    }

                    // Only the least crowded of the over-supplied groups, so a four-distractor
                    // trial does not silently become a nine-distractor one because the room
                    // happens to contain ten books.
                    if (competitors < bestOver)
                    {
                        bestOver = competitors;
                        over.Clear();
                    }

                    if (competitors == bestOver) over.Add(candidate);
                }
            }

            return exact.Count > 0 ? exact : over;
        }

        /// <summary>Fisher-Yates, on the seeded generator, so the order is part of the seed.</summary>
        private static void Shuffle(List<Trial> trials, System.Random random)
        {
            for (var i = trials.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (trials[i], trials[j]) = (trials[j], trials[i]);
            }
        }

        private string ScanId()
        {
            var scan = rebuilder != null ? rebuilder.Scan : null;
            if (scan == null) return "";

            return string.IsNullOrEmpty(scan.capturedUtc) ? "unknown" : scan.capturedUtc;
        }

        // -----------------------------------------------------------------
        // Running
        // -----------------------------------------------------------------

        /// <summary>Starts the block that <see cref="Build"/> generated.</summary>
        public void Begin()
        {
            if (_trials.Count == 0)
            {
                _status = "no trials - build the block first";
                return;
            }

            Block.ran = true;
            _index = -1;
            Advance();
        }

        /// <summary>
        /// Ends the block early. The trials already run are kept -- a block abandoned at trial
        /// eighteen is eighteen usable observations, and throwing them away because the last six
        /// did not happen would be the worst possible response to a participant needing to stop.
        /// </summary>
        public void Abort(string why)
        {
            if (!IsRunning) return;

            if (_phase == Phase.Waiting || _phase == Phase.Cue) Finish("gave-up");

            ClearCue();
            _phase = Phase.Finished;
            _status = $"block ended early: {why}";

            Debug.Log($"{Tag} Block ended early after {Block.completed} trials: {why}");
            OnBlockFinished?.Invoke(Block);
        }

        /// <summary>Scores the current trial as given up and moves on.</summary>
        public void GiveUp()
        {
            if (_phase != Phase.Waiting && _phase != Phase.Cue) return;

            Finish("gave-up");
            Advance();
        }

        private void Update()
        {
            switch (_phase)
            {
                case Phase.Cue:
                    if (Time.realtimeSinceStartup < _phaseUntil) return;
                    EndCue();
                    return;

                case Phase.Waiting:
                    if (Time.realtimeSinceStartup - _t0 < timeoutSeconds) return;

                    Finish("timeout");
                    Advance();
                    return;

                case Phase.Between:
                    if (Time.realtimeSinceStartup < _phaseUntil) return;

                    Advance();
                    return;
            }
        }

        /// <summary>Moves to the next trial, or finishes the block.</summary>
        private void Advance()
        {
            _index++;

            if (_index >= _trials.Count)
            {
                _phase = Phase.Finished;
                _status = $"block finished - {Block.completed} of {Block.planned} trials";

                Debug.Log($"{Tag} Block finished: {Block.completed} of {Block.planned} trials.");
                OnBlockFinished?.Invoke(Block);
                return;
            }

            var trial = _trials[_index];

            // A proxy that has gone is a scan that was rebuilt underneath the block. The trial
            // cannot be run and is recorded as skipped rather than dropped, so the block's
            // planned count and its rows still reconcile.
            if (trial.Proxy == null)
            {
                trial.Entry = NewEntry(trial);
                trial.Entry.outcome = "skipped";
                trial.Entry.tCueStart = -1f;
                trial.Entry.tCueEnd = -1f;

                OnTrialFinished?.Invoke(trial.Entry);

                Debug.LogWarning($"{Tag} Trial {_index + 1} skipped -- its target no longer " +
                                 $"exists. The scan was rebuilt during the block.");

                Advance();
                return;
            }

            _attempts = 0;
            trial.Entry = NewEntry(trial);
            trial.Entry.tCueStart = Now();

            ShowCue(trial.Proxy);

            _phase = Phase.Cue;
            _phaseUntil = Time.realtimeSinceStartup + Mathf.Max(0.1f, cueSeconds);

            _status = $"trial {_index + 1}/{_trials.Count}: cue";
        }

        private void EndCue()
        {
            ClearCue();

            _t0 = Time.realtimeSinceStartup;
            _phase = Phase.Waiting;

            var trial = Current;
            if (trial != null) trial.Entry.tCueEnd = Now();

            _status = $"trial {_index + 1}/{_trials.Count}: {Describe(trial)}";

            if (verboseLogging && trial != null)
                Debug.Log($"{Tag} Trial {_index + 1} ({trial.Modality}, " +
                          $"{trial.ActualDistractors} distractors) -> '{trial.TargetId}'.");
        }

        private ReferenceTrialEntry NewEntry(Trial trial) => new ReferenceTrialEntry
        {
            index = _index + 1,
            modality = trial.Modality == Modality.Naming ? "naming" : "pointing",
            distractors = trial.Distractors,
            actualDistractors = trial.ActualDistractors,
            rep = trial.Rep,
            targetId = trial.TargetId,
            targetLabel = trial.TargetLabel,
            targetName = DisplayName(trial.Proxy)
        };

        private Trial Current =>
            _index >= 0 && _index < _trials.Count ? _trials[_index] : null;

        // -----------------------------------------------------------------
        // Scoring
        // -----------------------------------------------------------------

        private void HandlePointed(string name, GameObject proxy) =>
            Score(Modality.Pointing, ScanIdOf(proxy));

        private void HandleNamed(string name, GameObject proxy) =>
            Score(Modality.Naming, ScanIdOf(proxy));

        /// <summary>
        /// A name that reached the app and matched nothing.
        ///
        /// Scored as an attempt with no object, which costs a naming trial one of its two
        /// tries. That is the honest accounting: the participant produced a referring
        /// expression, it cost a Convai request, and this app failed to resolve it. Not
        /// counting it would make the cap protect the budget less than it claims to.
        /// </summary>
        private void HandleNameUnresolved(string spoken) => Score(Modality.Naming, "");

        /// <summary>
        /// One indication, from whichever modality raised it.
        ///
        /// Indications from the modality this trial is NOT testing are ignored rather than
        /// scored. Pointing runs continuously -- the pointer commits attention whenever the ray
        /// settles, with no button -- so a participant resting their hand during a naming trial
        /// would otherwise answer it by accident, and answer it correctly about as often as the
        /// distractor count allows.
        /// </summary>
        private void Score(Modality modality, string indicatedId)
        {
            if (_phase != Phase.Waiting) return;

            var trial = Current;
            if (trial == null || trial.Modality != modality) return;

            _attempts++;

            var correct = !string.IsNullOrEmpty(indicatedId) && indicatedId == trial.TargetId;
            var latency = Time.realtimeSinceStartup - _t0;

            trial.Entry.attempts = _attempts;

            OnAttempt?.Invoke(new ReferenceAttemptEntry
            {
                trialIndex = _index + 1,
                attempt = _attempts,
                t = Now(),
                latency = latency,
                modality = trial.Entry.modality,
                indicatedId = indicatedId ?? "",
                correct = correct
            });

            if (correct)
            {
                trial.Entry.latency = latency;
                Finish("correct");
                Advance();
                return;
            }

            // The cap applies to naming only, and is the reason the request budget is bounded.
            // Pointing runs to the timeout because it costs nothing to keep trying.
            if (modality == Modality.Naming && _attempts >= Mathf.Max(1, namingAttemptCap))
            {
                Finish("wrong");
                Advance();
                return;
            }

            _status = $"trial {_index + 1}/{_trials.Count}: attempt {_attempts}, try again";
        }

        private void Finish(string outcome)
        {
            var trial = Current;
            if (trial == null || trial.Entry == null) return;

            ClearCue();

            trial.Entry.outcome = outcome;
            trial.Entry.correct = outcome == "correct";

            Block.completed++;

            OnTrialFinished?.Invoke(trial.Entry);

            if (verboseLogging)
                Debug.Log($"{Tag} Trial {trial.Entry.index}: {outcome} " +
                          $"after {trial.Entry.attempts} attempt(s)" +
                          $"{(trial.Entry.latency >= 0f ? $", {trial.Entry.latency:F2}s" : "")}.");

            _phase = Phase.Between;
            _phaseUntil = Time.realtimeSinceStartup + Mathf.Max(0f, betweenSeconds);
        }

        /// <summary>
        /// The scan id behind a replayed proxy.
        ///
        /// Looked up through the rebuilder rather than parsed out of the GameObject's name.
        /// The name is "{id}_{label}" and would parse, but a label containing an underscore --
        /// or a rename anywhere -- would turn a scoring function into a string bug that reads
        /// as a participant getting a trial wrong.
        /// </summary>
        private string ScanIdOf(GameObject proxy)
        {
            if (proxy == null || rebuilder == null) return "";

            foreach (var entry in rebuilder.Rebuilt)
                if (entry.Proxy == proxy && entry.Data != null)
                    return entry.Data.id;

            return "";
        }

        private static string DisplayName(GameObject proxy) =>
            proxy != null && proxy.TryGetComponent<Convai.Runtime.Actions.ConvaiActionTarget>(out var t)
                ? t.TargetName
                : "";

        // -----------------------------------------------------------------
        // The cue
        // -----------------------------------------------------------------

        private void ShowCue(GameObject proxy)
        {
            ClearCue();

            if (proxy == null || !proxy.TryGetComponent<WireBox>(out var box)) return;

            // Suppressed for the length of the cue so the two highlights cannot both be holding
            // a "colour it used to be" for the same box. See the field's own remark.
            if (pointer != null)
            {
                _pointerHighlightWas = pointer.highlightAimed;
                pointer.highlightAimed = false;
                _suppressedPointer = true;
            }

            var line = box.GetComponent<LineRenderer>();
            _cuedWas = line != null ? line.startColor : Color.white;
            _cued = box;

            box.SetColor(cueColor);
        }

        private void ClearCue()
        {
            if (_cued != null)
            {
                _cued.SetColor(_cuedWas);
                _cued = null;
            }

            if (!_suppressedPointer) return;

            _suppressedPointer = false;
            if (pointer != null) pointer.highlightAimed = _pointerHighlightWas;
        }

        /// <summary>Seconds since the session opened, supplied by the recorder that owns it.</summary>
        private float Now() => TimeSource != null ? TimeSource() : Time.realtimeSinceStartup;

        /// <summary>
        /// Where session-relative time comes from.
        ///
        /// Injected rather than recomputed, so every row this writes shares one clock with the
        /// scan milestones, the notes and the Convai turns. Two clocks in one file is how an
        /// offline join silently produces an ordering that never happened.
        /// </summary>
        public Func<float> TimeSource;

        // -----------------------------------------------------------------
        // Panel
        // -----------------------------------------------------------------

        public string SlotLabel(int slot)
        {
            switch (slot)
            {
                case 0:
                    return IsRunning ? "GIVE UP" : "START BLOCK";

                case 1:
                    return IsRunning ? "END BLOCK" : "LEAVE";

                case 2:
                    return null;

                default:
                    return null;
            }
        }

        public string SlotBlocked(int slot)
        {
            if (slot != 0 || IsRunning) return "";

            if (rebuilder == null || rebuilder.Scan == null) return "no scan";

            return _trials.Count == 0 ? "no trials" : "";
        }

        /// <summary>True once the runner is done with the panel and the recorder can take it back.</summary>
        public bool WantsToLeave { get; private set; }

        public void PressSlot(int slot)
        {
            switch (slot)
            {
                case 0:
                    if (IsRunning) GiveUp();
                    else Begin();
                    break;

                case 1:
                    if (IsRunning) Abort("ended from the panel");
                    else WantsToLeave = true;
                    break;
            }
        }

        /// <summary>Clears the leave request. Called by the recorder once it has taken over.</summary>
        public void ClearLeaveRequest() => WantsToLeave = false;

        /// <summary>
        /// The block drawn on the details panel while trials are running.
        ///
        /// The target's display name is on it deliberately. The cue is a two-second highlight
        /// and a participant who blinked has no way back to it; this is what lets the
        /// facilitator say "the chair by the couch" out loud rather than abandoning the trial.
        /// It is a deviation worth noting on the sheet when it happens, not a reason to leave
        /// the facilitator with nothing.
        /// </summary>
        public string DetailsBlock()
        {
            _builder.Clear();

            _builder.Append("trials    : ");

            if (_trials.Count == 0)
            {
                _builder.Append("none built");
                if (!string.IsNullOrEmpty(_status)) _builder.Append(" - ").Append(_status);
                return _builder.ToString();
            }

            _builder.Append(Block.completed).Append('/').Append(_trials.Count)
                    .Append(" done, seed ").Append(_seed);

            if (Block.unavailable.Count > 0)
                _builder.Append(", ").Append(Block.unavailable.Count).Append(" cells empty");

            var trial = Current;

            if (IsRunning && trial != null)
            {
                _builder.AppendLine();
                _builder.Append("now       : ").Append(trial.Entry.modality)
                        .Append(", ").Append(trial.ActualDistractors).Append(" distractors -> ")
                        .Append(string.IsNullOrEmpty(trial.Entry.targetName)
                                    ? trial.TargetLabel
                                    : trial.Entry.targetName);

                if (_phase == Phase.Waiting)
                    _builder.Append(" (").Append((Time.realtimeSinceStartup - _t0).ToString("F0"))
                            .Append("s)");
            }

            if (!string.IsNullOrEmpty(_status))
            {
                _builder.AppendLine();
                _builder.Append("block     : ").Append(_status);
            }

            return _builder.ToString();
        }

        private string Describe(Trial trial)
        {
            if (trial == null) return "";

            return trial.Modality == Modality.Naming ? "NAME it" : "POINT at it";
        }
    }
}
