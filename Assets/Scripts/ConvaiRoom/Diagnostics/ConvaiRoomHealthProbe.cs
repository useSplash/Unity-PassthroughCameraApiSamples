using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Convai.Modules.Vision;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Vision.Sources;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Periodic health report for the headset and the scan pipeline, written as one
    /// greppable block.
    ///
    /// Every line carries the same prefix, so `adb logcat | grep RoomHealth` shows the
    /// whole picture at once, and every line ends with ok=True/False so a single failing
    /// subsystem can be found with `grep "ok=False"`.
    ///
    /// The ladder matters more than any individual line. Batches arriving with no capture
    /// pose means depth is not being delivered; detections forwarded with no pending
    /// clusters means the raycasts are missing the room; clusters with no scan file means
    /// nothing was ever exported; a scan file that replays no boxes means the export was
    /// empty. Each of those looks identical from the outside -- the room simply comes up
    /// bare.
    /// </summary>
    public class ConvaiRoomHealthProbe : MonoBehaviour
    {
        private const string Prefix = "[RoomHealth]";

        [Header("Reporting")]
        [Tooltip("Seconds between reports. Zero or less reports once on start and stops.")]
        public float intervalSeconds = 5f;

        [Tooltip("Only log when a subsystem flips between ok and not ok, rather than on " +
                 "every interval. Quieter for long sessions, but you lose the heartbeat " +
                 "that tells you the probe is still alive.")]
        public bool logOnlyOnChange;

        [Header("Wiring (left empty, these are found in the scene)")]
        public ObjectDetectionScanBridge bridge;
        public ObjectScanRecorder recorder;
        public RoomScanRebuilder rebuilder;
        public ConvaiCharacter character;

        [Tooltip("Publishes passthrough frames to Convai. Optional -- without one the vision " +
                 "line simply says the room is running blind, which is a valid setup.")]
        public ConvaiVisionPublisher visionPublisher;

        [Tooltip("Makes the task-planning request. Optional -- without one the planner line " +
                 "says task planning is not set up, which is a valid setup.")]
        public RoomPlannerClient planner;

        [Tooltip("The plan being worked through, so the report can say where in it we are.")]
        public RoomTaskPlan plan;

        [Tooltip("Owns the session and the microphone permission. Without it the convai line " +
                 "cannot say whether she can actually hear you, which is the difference " +
                 "between a quiet character and a deaf one.")]
        public RoomCharacterVoice voice;

        private readonly StringBuilder _builder = new StringBuilder();
        private readonly List<string> _problems = new List<string>();
        private float _nextReportTime;
        private string _lastVerdict;

        private void Awake()
        {
            if (bridge == null) bridge = FindAnyObjectByType<ObjectDetectionScanBridge>();
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (character == null) character = FindAnyObjectByType<ConvaiCharacter>();
            if (visionPublisher == null) visionPublisher = FindAnyObjectByType<ConvaiVisionPublisher>();
            if (planner == null) planner = FindAnyObjectByType<RoomPlannerClient>();
            if (plan == null) plan = FindAnyObjectByType<RoomTaskPlan>();
            if (voice == null) voice = FindAnyObjectByType<RoomCharacterVoice>();
        }

        private void Start() => Report();

        private void Update()
        {
            if (intervalSeconds <= 0f) return;
            if (Time.unscaledTime < _nextReportTime) return;

            _nextReportTime = Time.unscaledTime + intervalSeconds;
            Report();
        }

        /// <summary>Writes one report immediately. Public so a button can force one.</summary>
        public void Report()
        {
            _problems.Clear();
            _builder.Clear();

            ReportHeadset();
            ReportMruk();
            ReportDetection();
            ReportRecorder();
            ReportScanFile();
            ReportReplay();
            ReportConvai();
            ReportActions();
            ReportVision();
            ReportPlanner();

            var verdict = _problems.Count == 0
                ? "ALL OK"
                : "PROBLEM: " + string.Join(",", _problems);

            _builder.Append(Prefix).Append(" verdict   ").Append(verdict);

            if (logOnlyOnChange && verdict == _lastVerdict) return;
            _lastVerdict = verdict;

            if (_problems.Count == 0) Debug.Log(_builder.ToString());
            else Debug.LogWarning(_builder.ToString());
        }

        // -----------------------------------------------------------------

        private void ReportHeadset()
        {
            var head = Camera.main;
            var hmd = OVRManager.isHmdPresent;
            var controller = OVRInput.GetActiveController();

            Line("headset", hmd && head != null,
                 $"hmd={hmd} controller={controller} inputFocus={OVRManager.hasInputFocus} " +
                 $"camera={(head != null ? head.name : "NONE")}");
        }

        private void ReportMruk()
        {
            var instance = MRUK.Instance;
            var room = instance != null ? instance.GetCurrentRoom() : null;

            // No room means Space Setup was never run on this headset, and every replayed
            // box lands relative to wherever the app started instead of the real room.
            Line("mruk", room != null,
                 $"instance={instance != null} " +
                 $"room={(room != null ? room.Anchor.Uuid.ToString() : "NONE_RUN_SPACE_SETUP")}");
        }

        private void ReportDetection()
        {
            var present = bridge != null;
            var batches = present ? bridge.BatchesSeen : 0;
            var forwarded = present ? bridge.DetectionsForwarded : 0;
            var pose = present && bridge.HasCapturePose;

            Line("detect", present && batches > 0 && pose,
                 $"bridge={present} batches={batches} forwarded={forwarded} capturePose={pose}");
        }

        private void ReportRecorder()
        {
            var present = recorder != null;

            Line("recorder", present,
                 $"present={present} enabled={present && recorder.enabled} " +
                 $"pendingClusters={(present ? recorder.PendingClusterCount : 0)}");
        }

        private void ReportScanFile()
        {
            var path = RoomScanIO.DefaultPath;
            var exists = File.Exists(path);
            long bytes = 0;

            if (exists)
            {
                // Size rather than a parse: this runs every few seconds and the file is
                // the only thing here that costs disk IO to inspect properly.
                bytes = new FileInfo(path).Length;
            }

            Line("scanfile", exists && bytes > 0, $"exists={exists} bytes={bytes} path={path}");
        }

        private void ReportReplay()
        {
            var boxes = rebuilder != null && rebuilder.Rebuilt != null ? rebuilder.Rebuilt.Count : 0;
            var anchored = rebuilder != null && rebuilder.Room != null;
            var loaded = rebuilder != null && rebuilder.Scan != null && rebuilder.Scan.objects != null
                ? rebuilder.Scan.objects.Count
                : 0;

            Line("replay", boxes > 0,
                 $"boxes={boxes} loadedFromFile={loaded} " +
                 $"anchored={(anchored ? "MRUK_ROOM" : "RAW_WORLD_SPACE")}");
        }

        /// <summary>
        /// Whether there is a character, whether she is in conversation, and -- the part this
        /// line was missing -- whether she can hear you.
        ///
        /// The microphone belongs on this line because speech is the ONLY way into any of this.
        /// Every action she can take is invoked by the backend deciding you asked for it, so a
        /// denied permission does not degrade the room, it disconnects you from all of it: she
        /// connects, reports ready, stands there, and never responds to a word. Without this
        /// field that state is indistinguishable from a character who is simply ignoring you,
        /// and the probe was reporting ok=True through the whole of it.
        ///
        /// Reported, not failed on. A character with no microphone is a real state rather than
        /// a broken one -- she still speaks, and the debug console can still drive her with
        /// text -- so it is said plainly and left to you to decide whether it is the problem.
        /// </summary>
        private void ReportConvai()
        {
            var live = Character();
            var present = live != null;

            // Read from the voice component rather than the character: the permission is asked
            // for there, and it is the only thing that knows whether the answer was yes.
            var mic = voice != null ? voice.HasMicrophone : false;
            var state = voice != null ? voice.State.ToString() : "no RoomCharacterVoice";

            // Presence is the pass condition, not conversation: not talking yet is the
            // normal state at startup.
            Line("convai", present,
                 $"character={present} " +
                 $"inConversation={present && live.IsInConversation} " +
                 $"session={state} " +
                 $"mic={(mic ? "yes" : "NO - she cannot hear you")}");
        }

        /// <summary>
        /// The character now standing in the room, or null when there is none.
        ///
        /// Resolved per report rather than cached in <c>Awake</c>, and that is a fix rather
        /// than a preference. The character is SPAWNED, so Awake here runs during phase 1 when
        /// there is none: the cached lookup found nothing, held that nothing for the rest of
        /// the session, and went on reporting <c>character=False</c> against a session that was
        /// Ready and audibly talking. The verdict then read PROBLEM on all 390 reports of a
        /// working run, which is how a probe stops being read at exactly the moment it matters.
        ///
        /// The voice's own character is preferred over a scene sweep because that is the one
        /// the session was actually opened for. A sweep answers with whichever ConvaiCharacter
        /// the scene happens to contain, which is the same answer right up until it is not.
        /// </summary>
        private ConvaiCharacter Character()
        {
            if (voice != null && voice.Character != null) return voice.Character;
            if (character != null) return character;

            return character = FindAnyObjectByType<ConvaiCharacter>();
        }

        /// <summary>
        /// Which actions the Convai backend has actually been offered.
        ///
        /// THE LINE THIS PROBE WAS MISSING, and the reason three test sessions went on guesses.
        /// An action authored on the character but dropped on the way to the wire is dropped
        /// SILENTLY: <c>ConvaiActionConfigSource.BuildActionConfig</c> filters out every
        /// definition whose executor will not run and says nothing about what it removed. From
        /// inside the headset the result is a character who never performs that action, which
        /// is indistinguishable from a backend that had it and chose something else -- and
        /// those two have nothing in common as far as the fix goes. One is a prefab that needs
        /// repairing, the other is wording.
        ///
        /// So this asks the SDK the same question the connect payload asks, through the same
        /// public method, and prints the answer. A name that is authored on this character and
        /// missing from <c>offered</c> is one the Convai Character can never be asked to
        /// perform, however it is phrased at her.
        ///
        /// The validator is run alongside it because it is the half that says WHY. A dropped
        /// action is nearly always an unbound executor, and the diagnostic names both the
        /// action and the repair. Reported as an error only when something authored actually
        /// failed to make it: a character in a scene with no action targets yet produces
        /// warnings that are true and not worth a red line for the whole scan phase.
        ///
        /// Costs a config build and a validation pass every <see cref="intervalSeconds"/>.
        /// That is real but small, it is the same order as the place sweep the planner line
        /// already does, and neither is on a per-frame path.
        /// </summary>
        private void ReportActions()
        {
            var live = Character();

            if (live == null)
            {
                Line("actions", true, "no character yet - nothing has been offered");
                return;
            }

            var source = live.GetActionConfigSource();

            if (source == null)
            {
                Line("actions", false,
                     "the character has no Convai Actions component, so it offers no actions " +
                     "at all and every one of them will be improvised in conversation");
                return;
            }

            // Exactly what goes on the wire at connect, built by the SDK rather than guessed at.
            var config = source.BuildActionConfig();

            var offered = new List<string>();
            if (config?.Actions != null)
            {
                foreach (var action in config.Actions)
                {
                    var name = CanonicalName(action);
                    if (!string.IsNullOrEmpty(name)) offered.Add(name);
                }
            }

            var dropped = new List<string>();
            foreach (var definition in source.Definitions)
            {
                var name = definition != null ? (definition.ActionName ?? "").Trim() : "";
                if (name.Length == 0) continue;

                if (!Offers(offered, name)) dropped.Add(name);
            }

            var detail = $"offered={offered.Count} [{string.Join(", ", offered)}]";

            if (dropped.Count > 0)
                detail += $"  DROPPED=[{string.Join(", ", dropped)}]";

            var problem = FirstError(source);
            if (!string.IsNullOrEmpty(problem)) detail += $"  why={problem}";

            Line("actions", dropped.Count == 0 && offered.Count > 0, detail);
        }

        /// <summary>
        /// Whether the wire config carries this authored name. Case-insensitive, because the
        /// SDK matches action names that way and a case difference is not a dropped action.
        /// </summary>
        private static bool Offers(List<string> offered, string name)
        {
            foreach (var candidate in offered)
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// The first thing the SDK's own validator says is broken about this character's
        /// actions, or empty when it says nothing is.
        ///
        /// Errors only. Warnings here are mostly "this action wants an object target and the
        /// character knows none yet", which is simply true for the whole of phase 1 and would
        /// put a reason on a line that has no problem to explain.
        /// </summary>
        private static string FirstError(ConvaiActionConfigSource source)
        {
            var diagnostics = ConvaiActionConfigValidator.Validate(source);

            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic == null) continue;
                if (diagnostic.Severity != ConvaiActionConfigDiagnosticSeverity.Error) continue;

                return diagnostic.Message;
            }

            return "";
        }

        /// <summary>
        /// The action name out of a rendered wire string -- everything before the first
        /// parameter slot or the description separator, whichever comes first.
        ///
        /// Written here rather than called because the SDK's own ConvaiActionWireGrammar is
        /// internal to the Convai assembly. It is only two delimiters, and the SDK's validator
        /// refuses to let either appear inside an authored action name, so reading a name back
        /// this way is unambiguous for anything that reached the wire at all.
        /// </summary>
        private static string CanonicalName(string renderedAction)
        {
            if (string.IsNullOrWhiteSpace(renderedAction)) return "";

            var value = renderedAction.Trim();

            var slot = value.IndexOf(" {", StringComparison.Ordinal);
            var description = value.IndexOf(" - ", StringComparison.Ordinal);

            var cut = slot;
            if (description >= 0 && (cut < 0 || description < cut)) cut = description;

            return cut >= 0 ? value.Substring(0, cut).Trim() : value;
        }

        /// <summary>
        /// Whether the character is being fed passthrough frames.
        ///
        /// This is the line that exists because vision fails silently and in several different
        /// ways that are indistinguishable from inside the headset: the components can be
        /// missing, the frame source can fail to find PassthroughCameraAccess, the camera
        /// permission can be refused, or everything can be wired and simply not publishing
        /// because no session is open yet. She looks equally oblivious in all four cases.
        ///
        /// The frame count is the number that settles it. Publishing with frames climbing is
        /// the only combination that means she is actually seeing the room.
        ///
        /// Passing on presence rather than on publishing, deliberately: not connected yet is
        /// the normal state for most of a session's life, and a probe that shouts PROBLEM
        /// through the whole scan phase is a probe people stop reading.
        /// </summary>
        private void ReportVision()
        {
            if (visionPublisher == null)
            {
                Line("vision", true, "publisher=none - running blind, scan metadata only");
                return;
            }

            var source = visionPublisher.FrameSource;

            var frames = source != null ? source.FrameCount : 0;
            var capturing = source != null && source.IsCapturing;

            // The status message is the frame source's own account of why it is not capturing --
            // "PassthroughCameraAccess not found", a permission refusal, the wrong platform. It
            // is the one string worth forwarding verbatim rather than summarising.
            var status = source is IVisionFrameSourceStatusProvider reporter &&
                         !string.IsNullOrEmpty(reporter.StatusMessage)
                ? $" status={reporter.StatusMessage}"
                : "";

            Line("vision", source != null,
                 $"publisher=yes source={(source != null ? source.SourceId : "MISSING")} " +
                 $"capturing={capturing} publishing={visionPublisher.IsPublishing} " +
                 $"frames={frames}{status}");
        }

        /// <summary>
        /// Whether task planning can actually happen, and how much of the room it has to work
        /// with.
        ///
        /// Three separate things have to be true and all three fail the same way from inside
        /// the headset -- you ask "how do I do this?" and she answers as if you had just asked
        /// a normal question. The planner can be missing from the scene, it can be unconfigured
        /// for whichever backend it is set to, or the room can have no groundable places at
        /// all, and none of those says anything out loud.
        ///
        /// The place count is the number worth watching. A configured planner with zero places
        /// still works, but every step comes back unlocated, which is the difference between a
        /// plan about this room and a plan about rooms in general.
        ///
        /// What "configured" means depends on the backend, which is why this asks the planner
        /// rather than checking for a key: Anthropic needs one and Ollama has none, so a line
        /// hardcoded to report a key would call a perfectly healthy local setup broken.
        ///
        /// Reachability is deliberately not tested. The probe runs on a timer and pinging a LAN
        /// address every few seconds to colour a status line is a bad trade, so a sleeping PC
        /// still reads as ok here and announces itself the moment a plan is asked for.
        ///
        /// A missing planner is not a problem, only a missing capability: the room ran without
        /// one for its whole life before this, and a probe that shouts about an unconfigured
        /// optional feature is a probe people stop reading. A planner that is present but not
        /// configured IS a problem, because that combination is always a mistake.
        /// </summary>
        private void ReportPlanner()
        {
            if (planner == null)
            {
                Line("planner", true, "none - she can talk about the room but not plan tasks");
                return;
            }

            var places = RoomTaskVocabulary.Collect().Count;
            var ready = planner.IsConfigured;

            var state = plan != null && plan.HasPlan
                ? $"step {plan.CurrentIndex + 1}/{plan.Steps.Count}"
                : "no plan";

            // Named per backend, because the thing you would go and fix is different. An
            // Anthropic planner is missing a key; an Ollama one is missing an address or a
            // model, and saying "key=MISSING" at somebody running locally sends them hunting
            // for a file that was never meant to exist.
            var config = planner.backend == RoomPlannerClient.PlannerBackend.Anthropic
                ? $"key={(ready ? "yes" : "MISSING")}"
                : $"at={planner.Endpoint}";

            Line("planner", ready,
                 $"via={planner.BackendName} {config} model={planner.ActiveModel} " +
                 $"places={places} {state}");
        }

        private void Line(string name, bool ok, string detail)
        {
            if (!ok) _problems.Add(name);

            _builder.Append(Prefix).Append(' ').Append(name.PadRight(9))
                    .Append(" ok=").Append(ok).Append("  ").AppendLine(detail);
        }
    }
}
