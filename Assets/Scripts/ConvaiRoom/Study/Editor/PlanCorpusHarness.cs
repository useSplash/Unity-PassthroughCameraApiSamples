using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConvaiRoom;
using RoomScan;
using UnityEditor;
using UnityEngine;

namespace ConvaiRoomEditor
{
    /// <summary>
    /// Drives the planner unattended across saved scans, tasks, backends and the
    /// grounded/ungrounded ablation, with no headset and no Play Mode.
    ///
    /// WHY THIS CARRIES THE STUDY'S WEIGHT. At n = 4 the participant session supports a
    /// within-participant probe and a qualitative account of one task each -- four
    /// observations, not a sample. Nothing about THIS corpus depends on how many participants
    /// there are: a room does not need a person in it to be planned about. Effort spent here
    /// buys real statistical power; effort spent shaving the participant session does not.
    ///
    /// WHY IT DOES NOT NEED A SCENE. RoomTaskVocabulary.Collect -- what the live app calls --
    /// searches for ConvaiActionTarget components in a loaded scene, so it always returns
    /// nothing here. RoomScanContext.PlacesFor and .SummaryFor are the offline replacements: the
    /// same naming and description code the headset runs, reading only the scan file. Their
    /// existence is what makes this class possible; see the remarks there for why they are
    /// trustworthy substitutes rather than a second implementation that could drift.
    ///
    /// WHY IT REUSES OnPlanAttempt RATHER THAN TIMING ITS OWN CALLS. That event was built in the
    /// same change that added it here, for exactly this reason: the study recorder and this
    /// harness want the same numbers about the same kind of event, and two measurement paths
    /// are two things that could disagree about what a "plan attempt" is.
    ///
    /// WHY IT IS AN EditorWindow rather than a plain menu item. Every other Editor tool in this
    /// folder is a static class with a file picker, because their configuration lives in the
    /// picked file itself -- the ablation tool reads capacity and percentile out of the log's own
    /// header. There is no file to read the backend, the repeat count, or the task list out of
    /// here; those are decisions for THIS run, and a window is the plain way to take them.
    ///
    /// DOMAIN RELOAD. A script recompile while a run is active destroys every field on this
    /// window and abandons whatever request was in flight. Nothing here can prevent that, so
    /// the corpus is flushed to disk after every completed plan rather than once at the end --
    /// the worst a reload costs is the one job that was running, not the run. Don't edit scripts
    /// while this is going.
    /// </summary>
    public class PlanCorpusHarness : EditorWindow
    {
        private const string Tag = "[PlanHarness]";

        [MenuItem("Tools/Convai Room/Plan Corpus Harness")]
        public static void Open() => GetWindow<PlanCorpusHarness>("Plan Corpus");

        // -----------------------------------------------------------------
        // Configuration. Serialized so the window keeps it across a close and reopen within
        // the same Editor session -- not across a domain reload, which wipes it like everything
        // else on this object. See the class remark.
        // -----------------------------------------------------------------

        [SerializeField] private List<string> _scanPaths = new List<string>();
        [SerializeField] private string _tasksText = "";

        [SerializeField] private bool _runGrounded = true;
        [SerializeField] private bool _runUngrounded = true;
        [SerializeField] private bool _runAnthropic = true;
        [SerializeField] private bool _runOllama = true;

        [SerializeField] private string _anthropicModel = "claude-haiku-4-5";
        [SerializeField] private string _anthropicEffort = "";

        [SerializeField] private string _ollamaUrl = "http://localhost:11434";
        [SerializeField] private string _ollamaModel = "qwen2.5:7b";
        [SerializeField] private float _ollamaTemperature = 0.2f;

        [SerializeField] private int _maxTokens = 8000;
        [SerializeField] private int _timeoutSeconds = 40;

        [Tooltip("Matches RoomScanContext.maxObjects -- which objects get named at all.")]
        [SerializeField] private int _maxObjects = 40;

        [Tooltip("Matches RoomTaskPlanner.maxPlaces -- how many named objects the planner is " +
                 "actually offered, nearest the room centre first. See PlacesFor's remark on " +
                 "why the centre stands in for the player here.")]
        [SerializeField] private int _maxPlaces = 30;

        [Tooltip("1 for a full-factorial sweep. 10+ for the consistency mode -- repeats of the " +
                 "SAME (room, task, condition, backend), reporting how much the plan agrees " +
                 "with itself. Running both at once is possible but the request count " +
                 "multiplies fast; narrow the scans, tasks or backends first.")]
        [SerializeField] private int _repeats = 1;

        // -----------------------------------------------------------------
        // Run state. Deliberately not serialized -- none of it means anything after a reload,
        // and a stale "running" flag surviving into a reopened window would show a progress
        // bar for a run that no longer exists.
        // -----------------------------------------------------------------

        private bool _running;
        private int _jobIndex;
        private int _jobTotal;
        private string _statusLine = "";
        private string _lastCorpusPath = "";
        private string _lastReportPath = "";

        private CancellationTokenSource _cts;
        private GameObject _clientHost;
        private RoomPlannerClient _client;

        private Vector2 _scroll;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeReload;
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeReload;
            CleanUpClient();
        }

        /// <summary>
        /// The one thing this can do about a reload: say so, loudly, before the state it is
        /// about to lose becomes impossible to explain. The corpus itself is safe -- it was
        /// flushed after the last completed job -- only the in-flight request and this window's
        /// run state are gone.
        /// </summary>
        private void HandleBeforeReload()
        {
            if (!_running) return;

            Debug.LogWarning($"{Tag} A script reload is interrupting the run at job " +
                             $"{_jobIndex}/{_jobTotal}. Everything completed so far is saved " +
                             $"in {_lastCorpusPath}. Re-open this window and run again for the " +
                             $"remainder -- there is no auto-resume.");
        }

        // -----------------------------------------------------------------
        // GUI
        // -----------------------------------------------------------------

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Saved scans", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Every scan gets planned against, independently. Six is the study's own " +
                "target; more or fewer both work.", MessageType.None);
            DrawScanList();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tasks, one per line", EditorStyles.boldLabel);
            _tasksText = EditorGUILayout.TextArea(_tasksText, GUILayout.MinHeight(90));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Conditions and backends", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _runGrounded = EditorGUILayout.ToggleLeft("Grounded", _runGrounded, GUILayout.Width(140));
                _runUngrounded = EditorGUILayout.ToggleLeft("Ungrounded", _runUngrounded, GUILayout.Width(140));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _runAnthropic = EditorGUILayout.ToggleLeft("Anthropic", _runAnthropic, GUILayout.Width(140));
                _runOllama = EditorGUILayout.ToggleLeft("Ollama", _runOllama, GUILayout.Width(140));
            }

            if (_runAnthropic)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Anthropic", EditorStyles.boldLabel);
                _anthropicModel = EditorGUILayout.TextField("Model", _anthropicModel);
                _anthropicEffort = EditorGUILayout.TextField("Effort (blank for Haiku)", _anthropicEffort);

                if (!HasAnthropicKey())
                    EditorGUILayout.HelpBox(
                        "No planner key found (persistentDataPath/planner_key.txt or the " +
                        "bundled Resources asset). Anthropic jobs will fail fast rather than " +
                        "being skipped -- that failure is itself worth having in the corpus.",
                        MessageType.Warning);
            }

            if (_runOllama)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Ollama", EditorStyles.boldLabel);
                _ollamaUrl = EditorGUILayout.TextField("URL", _ollamaUrl);
                _ollamaModel = EditorGUILayout.TextField("Model", _ollamaModel);
                _ollamaTemperature = EditorGUILayout.Slider("Temperature", _ollamaTemperature, 0f, 1f);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shared", EditorStyles.boldLabel);
            _maxTokens = EditorGUILayout.IntField("Max tokens", _maxTokens);
            _timeoutSeconds = EditorGUILayout.IntField("Timeout (s)", _timeoutSeconds);
            _maxObjects = EditorGUILayout.IntField("Max named objects", _maxObjects);
            _maxPlaces = EditorGUILayout.IntField("Max places offered", _maxPlaces);
            _repeats = Mathf.Max(1, EditorGUILayout.IntField("Repeats", _repeats));

            EditorGUILayout.Space();

            var jobs = _running ? _jobTotal : CountJobs();
            EditorGUILayout.LabelField($"This run: {jobs} plan(s).");

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_running || jobs == 0))
            {
                if (GUILayout.Button("Start", GUILayout.Height(28))) RunAsync();
            }

            using (new EditorGUI.DisabledScope(!_running))
            {
                if (GUILayout.Button("Cancel")) _cts?.Cancel();
            }

            if (_running)
                EditorGUILayout.LabelField($"{_jobIndex}/{_jobTotal} -- {_statusLine}");

            if (!string.IsNullOrEmpty(_lastCorpusPath))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last run", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(_lastCorpusPath, GUILayout.Height(18));
                EditorGUILayout.SelectableLabel(_lastReportPath, GUILayout.Height(18));

                if (GUILayout.Button("Reveal")) EditorUtility.RevealInFinder(_lastCorpusPath);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawScanList()
        {
            for (var i = 0; i < _scanPaths.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(Path.GetFileName(_scanPaths[i]));

                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        _scanPaths.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (GUILayout.Button("Add scan file..."))
            {
                var start = Directory.Exists(StudySessionIO.Folder)
                    ? StudySessionIO.Folder
                    : Application.persistentDataPath;

                var path = EditorUtility.OpenFilePanel("Pick a saved room_scan.json", start, "json");
                if (!string.IsNullOrEmpty(path)) _scanPaths.Add(path);

                GUIUtility.ExitGUI();
            }
        }

        private bool HasAnthropicKey()
        {
            var sidecar = Path.Combine(Application.persistentDataPath, "planner_key.txt");
            if (File.Exists(sidecar)) return true;

            return Resources.Load<TextAsset>("planner_key") != null;
        }

        // -----------------------------------------------------------------
        // Job counting
        // -----------------------------------------------------------------

        private List<string> ParseTasks()
        {
            var tasks = new List<string>();
            if (string.IsNullOrEmpty(_tasksText)) return tasks;

            foreach (var line in _tasksText.Split('\n'))
            {
                var trimmed = line.Trim('\r', ' ', '\t');
                if (trimmed.Length > 0) tasks.Add(trimmed);
            }

            return tasks;
        }

        private int ConditionCount() => (_runGrounded ? 1 : 0) + (_runUngrounded ? 1 : 0);
        private int BackendCount() => (_runAnthropic ? 1 : 0) + (_runOllama ? 1 : 0);

        private int CountJobs() =>
            _scanPaths.Count * ParseTasks().Count * ConditionCount() * BackendCount() *
            Mathf.Max(1, _repeats);

        // -----------------------------------------------------------------
        // Running
        // -----------------------------------------------------------------

        /// <summary>One cell of the design, before it has been run.</summary>
        private struct Job
        {
            public string Room;
            public RoomScanFile Scan;
            public string ScanFile;
            public string Task;
            public bool Grounded;
            public RoomPlannerClient.PlannerBackend Backend;
            public int Repeat;
        }

        /// <summary>
        /// Runs the whole configured sweep.
        ///
        /// async void, deliberately, and only because this is a top-level UI event handler --
        /// the one case that pattern is meant for. Every exception a single job can throw is
        /// caught around that job specifically; nothing here should be able to end the run
        /// early except cancellation, and a bug that somehow does end it early still leaves the
        /// corpus holding everything completed before it.
        /// </summary>
        private async void RunAsync()
        {
            if (_running) return;

            var tasks = ParseTasks();
            var jobs = BuildJobs(tasks, out var loadFailures);

            foreach (var failure in loadFailures)
                Debug.LogWarning($"{Tag} {failure}");

            if (jobs.Count == 0)
            {
                Debug.LogWarning($"{Tag} Nothing to run -- check the scan list, the task list, " +
                                 $"and that at least one condition and one backend are ticked.");
                return;
            }

            _running = true;
            _jobIndex = 0;
            _jobTotal = jobs.Count;
            _cts = new CancellationTokenSource();

            EnsureClient();

            var startUtc = DateTime.UtcNow;
            var corpus = new PlanCorpus
            {
                runId = PlanCorpusIO.MakeRunId(startUtc),
                startedUtc = startUtc.ToString("o"),
                planned = jobs.Count,
                repeats = _repeats
            };

            foreach (var job in jobs)
            {
                if (!corpus.rooms.Contains(job.Room)) corpus.rooms.Add(job.Room);
                if (!corpus.tasks.Contains(job.Task)) corpus.tasks.Add(job.Task);
            }

            if (_runAnthropic) corpus.backends.Add("anthropic");
            if (_runOllama) corpus.backends.Add("ollama");

            _lastCorpusPath = PlanCorpusIO.PathFor(corpus.runId);
            PlanCorpusIO.Save(corpus, _lastCorpusPath);

            // Computed once per room rather than once per job -- the naming pass is
            // deterministic, so calling it per job would waste work without changing a single
            // answer. The FULL set (uncapped by maxPlaces) is kept alongside the offered one:
            // scoring a plan's text for room vocabulary has to check against everything the
            // room contains, not just what this particular call was offered, or the ungrounded
            // arm's leakage check would have nothing real to compare against.
            var roomVocab = new Dictionary<string, List<string>>();

            try
            {
                foreach (var job in jobs)
                {
                    if (_cts.IsCancellationRequested) break;

                    _jobIndex++;
                    _statusLine = $"{job.Room} / {Lower(job.Backend)} / " +
                                 $"{(job.Grounded ? "grounded" : "ungrounded")} / rep {job.Repeat}";

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Plan Corpus Harness", $"{_jobIndex}/{_jobTotal}: {_statusLine}",
                            (float)_jobIndex / _jobTotal))
                    {
                        _cts.Cancel();
                    }

                    if (!roomVocab.TryGetValue(job.ScanFile, out var vocab))
                    {
                        vocab = new List<string>();
                        foreach (var place in RoomScanContext.PlacesFor(job.Scan, _maxObjects))
                            vocab.Add(place.Name);

                        roomVocab[job.ScanFile] = vocab;
                    }

                    PlanRecord record;

                    try
                    {
                        record = await RunOneAsync(job, corpus.plans.Count, vocab, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A job that throws for a reason PlanAsync's own guards did not catch is
                        // still one row in the corpus, not a dead run. 400 requests over a real
                        // network WILL include something unexpected eventually.
                        Debug.LogError($"{Tag} Job {_jobIndex}/{_jobTotal} " +
                                       $"({_statusLine}) threw and was recorded as a failure: {ex}");

                        record = FailedRecord(job, corpus.plans.Count, ex.Message);
                    }

                    corpus.plans.Add(record);
                    corpus.completed++;

                    // Flushed after every plan. A run stopped by a reload, a crash, or a
                    // deliberate cancel keeps everything up to the last one that finished.
                    PlanCorpusIO.Save(corpus, _lastCorpusPath);

                    Repaint();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            corpus.endedUtc = DateTime.UtcNow.ToString("o");
            PlanCorpusIO.Save(corpus, _lastCorpusPath);

            _lastReportPath = PlanCorpusReport.Write(corpus, Path.GetDirectoryName(_lastCorpusPath));

            Debug.Log($"{Tag} Run finished: {corpus.completed}/{corpus.planned} plans. " +
                      $"{_lastCorpusPath}");

            CleanUpClient();
            _running = false;
            Repaint();
        }

        private List<Job> BuildJobs(List<string> tasks, out List<string> loadFailures)
        {
            loadFailures = new List<string>();
            var jobs = new List<Job>();

            var loaded = new Dictionary<string, RoomScanFile>();

            foreach (var path in _scanPaths)
            {
                RoomScanFile scan;
                try
                {
                    scan = RoomScanIO.Load(path);
                }
                catch (Exception ex)
                {
                    loadFailures.Add($"Could not load {path}: {ex.Message}. Skipped.");
                    continue;
                }

                if (scan == null)
                {
                    loadFailures.Add($"{path} did not parse as a scan. Skipped.");
                    continue;
                }

                loaded[path] = scan;
            }

            foreach (var path in _scanPaths)
            {
                if (!loaded.TryGetValue(path, out var scan)) continue;

                var room = Path.GetFileNameWithoutExtension(path);

                foreach (var task in tasks)
                {
                    if (_runGrounded) AddJobsFor(jobs, room, scan, path, task, true);
                    if (_runUngrounded) AddJobsFor(jobs, room, scan, path, task, false);
                }
            }

            return jobs;
        }

        private void AddJobsFor(List<Job> jobs, string room, RoomScanFile scan, string path,
                                string task, bool grounded)
        {
            for (var repeat = 1; repeat <= Mathf.Max(1, _repeats); repeat++)
            {
                if (_runAnthropic)
                    jobs.Add(new Job
                    {
                        Room = room, Scan = scan, ScanFile = path, Task = task,
                        Grounded = grounded, Backend = RoomPlannerClient.PlannerBackend.Anthropic,
                        Repeat = repeat
                    });

                if (_runOllama)
                    jobs.Add(new Job
                    {
                        Room = room, Scan = scan, ScanFile = path, Task = task,
                        Grounded = grounded, Backend = RoomPlannerClient.PlannerBackend.Ollama,
                        Repeat = repeat
                    });
            }
        }

        /// <summary>Runs one job and turns the result into a corpus row.</summary>
        private async Task<PlanRecord> RunOneAsync(Job job, int index, List<string> roomVocab,
                                                   CancellationToken token)
        {
            ConfigureClient(job.Backend);

            var places = job.Grounded
                ? RoomScanContext.PlacesFor(job.Scan, _maxObjects, _maxPlaces)
                : new List<RoomTaskVocabulary.Place>();

            var summary = job.Grounded ? RoomScanContext.SummaryFor(job.Scan) : "";

            RoomPlannerClient.PlanAttempt? attempt = null;
            void Capture(RoomPlannerClient.PlanAttempt a) => attempt = a;

            _client.OnPlanAttempt += Capture;

            RoomPlannerClient.PlanResult result;
            try
            {
                result = await _client.PlanAsync(job.Task, places, summary, token);
            }
            finally
            {
                _client.OnPlanAttempt -= Capture;
            }

            return BuildRecord(job, index, attempt, result, roomVocab);
        }

        private PlanRecord BuildRecord(Job job, int index, RoomPlannerClient.PlanAttempt? attempt,
                                       RoomPlannerClient.PlanResult result, List<string> roomVocab)
        {
            var record = new PlanRecord
            {
                index = index,
                room = job.Room,
                scanFile = Path.GetFileName(job.ScanFile),
                task = job.Task,
                condition = job.Grounded ? "grounded" : "ungrounded",
                backend = Lower(job.Backend),
                model = job.Backend == RoomPlannerClient.PlannerBackend.Anthropic
                    ? _anthropicModel : _ollamaModel,
                repeat = job.Repeat,
                ok = result.Ok,
                failure = result.Failure ?? "",
                summary = result.Summary ?? ""
            };

            // Falls back to zero/false if the event somehow never fired -- it always should,
            // since PlanAsync raises it from a finally on every path -- rather than throwing and
            // losing the whole job over a missing timing number.
            if (attempt.HasValue)
            {
                var a = attempt.Value;
                record.cancelled = a.Cancelled;
                record.latency = a.LatencySeconds;
                record.placesOffered = a.PlacesOffered;
                record.hadRoomSummary = a.HadRoomSummary;
                record.groundedSteps = a.GroundedSteps;
                record.droppedLocations = a.DroppedLocations;
            }

            if (result.Steps != null)
            {
                foreach (var step in result.Steps)
                    record.steps.Add(new PlanStepRecord { text = step.Text, where = step.Where ?? "" });
            }

            record.wordsPerStep = PlanScoring.WordsPerStep(record.steps);
            record.roomMentions = PlanScoring.RoomMentions(record.steps, roomVocab);

            return record;
        }

        private static PlanRecord FailedRecord(Job job, int index, string message) => new PlanRecord
        {
            index = index,
            room = job.Room,
            scanFile = Path.GetFileName(job.ScanFile),
            task = job.Task,
            condition = job.Grounded ? "grounded" : "ungrounded",
            backend = Lower(job.Backend),
            repeat = job.Repeat,
            ok = false,
            failure = $"harness exception: {message}"
        };

        private static string Lower(RoomPlannerClient.PlannerBackend backend) =>
            backend == RoomPlannerClient.PlannerBackend.Anthropic ? "anthropic" : "ollama";

        // -----------------------------------------------------------------
        // The client
        // -----------------------------------------------------------------

        /// <summary>
        /// A RoomPlannerClient with nothing else attached, kept alive for the whole run.
        ///
        /// RoomPlannerClient has no Awake, no wiring, and no scene dependency -- it reads its
        /// own public fields and makes a web request, which is what makes standing one up
        /// outside any scene both possible and honest: this is exactly what the live app's
        /// component does too, just with its fields set here instead of in the Inspector.
        /// HideAndDontSave keeps it out of the hierarchy view and out of any save.
        /// </summary>
        private void EnsureClient()
        {
            if (_client != null) return;

            _clientHost = EditorUtility.CreateGameObjectWithHideFlags(
                "Plan Corpus Harness (temporary)", HideFlags.HideAndDontSave,
                typeof(RoomPlannerClient));

            _client = _clientHost.GetComponent<RoomPlannerClient>();
        }

        private void ConfigureClient(RoomPlannerClient.PlannerBackend backend)
        {
            _client.backend = backend;
            _client.model = _anthropicModel;
            _client.effort = _anthropicEffort;
            _client.ollamaUrl = _ollamaUrl;
            _client.ollamaModel = _ollamaModel;
            _client.ollamaTemperature = _ollamaTemperature;
            _client.maxTokens = _maxTokens;
            _client.timeoutSeconds = _timeoutSeconds;
            _client.verboseLogging = false;
        }

        private void CleanUpClient()
        {
            if (_clientHost != null) DestroyImmediate(_clientHost);
            _clientHost = null;
            _client = null;
        }
    }
}
