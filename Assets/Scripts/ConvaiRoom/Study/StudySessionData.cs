using System;
using System.Collections.Generic;
using System.IO;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    // ---------------------------------------------------------------------
    // What one user-test session leaves behind.
    //
    // Same rules as RoomScanData: plain [Serializable] classes, public fields
    // only, no dictionaries, no properties. JsonUtility fails SILENTLY on any
    // of those -- you get {} and no error -- which is why StudySelfCheck runs
    // a round-trip over every type in here.
    //
    // Poses are ROOM-LOCAL, matching the scan file, so a truth box and a
    // scanned box can be subtracted without going near world space.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every parameter the accuracy numbers depend on, copied at session start.
    ///
    /// This exists because the result is meaningless without it. "Precision was 0.92" is
    /// a claim about a particular minObservations, a particular percentile band and a
    /// particular merge radius, and those are Inspector fields that anybody can nudge
    /// between participants. Recording them here means a condition is read out of the
    /// data rather than reconstructed from a lab notebook six weeks later.
    /// </summary>
    [Serializable]
    public class StudyBuild
    {
        public int minObservations;
        public float extentPercentile;
        public int extentSampleCount;
        public float mergeRadius;
        public float mergeOverlap;
        public float maxObjectSize;
        public float minConfidence;
        public List<string> ignoredLabels = new List<string>();

        /// <summary>
        /// Whether the raw observation log was running. The extent ablation is only
        /// computable for sessions where this was true, and a session where it was on is a
        /// session whose scan timings carry the log's overhead -- so it qualifies the
        /// timing numbers as much as it enables the extent ones.
        /// </summary>
        public bool observationLogArmed;
    }

    /// <summary>
    /// The at-a-glance block, and the only part of this file anybody reads on device.
    ///
    /// It is drawn on the details panel so the facilitator can confirm the session actually
    /// recorded something before the participant takes the headset off. That check is the
    /// whole reason it exists: every other number in this file is for offline analysis, and
    /// by the time anyone looks at those, the chance to re-run the session is gone.
    /// </summary>
    [Serializable]
    public class StudySummary
    {
        public int scanRuns;
        public int scanSaves;
        public int objectsExported;
        public int truthObjects;
        public int milestones;

        /// <summary>
        /// Records the observation log wrote, and records it threw away because the drain
        /// could not keep up. A non-zero drop count disqualifies this session's extent
        /// ablation, which is a thing you want to read off the data rather than infer from
        /// a frame-rate memory.
        /// </summary>
        public int obsRecords;
        public int obsDropped;

        /// <summary>
        /// Participant conversation turns counted this session -- one per utterance the Convai
        /// backend processed, which is the same unit the quota bills for. Pointing and planning
        /// are both off-quota and are deliberately not in here.
        /// </summary>
        public int convaiRequests;

        /// <summary>
        /// The budget those were counted against, and whether it was actually being enforced.
        ///
        /// Both travel with the data for the reason <see cref="StudyBuild"/> exists at all: a
        /// session that ran out of turns and one that was cut off by the app are different
        /// events with the same turn count, and only these two fields tell them apart six
        /// weeks later. Zero means no budget was set.
        /// </summary>
        public int convaiBudget;
        public bool convaiBudgetEnforced;

        /// <summary>
        /// Whether the BACKEND said the quota was gone, as opposed to the app's own budget
        /// being spent. This is the unrecoverable one -- the SDK terminates the pipeline
        /// immediately after the message -- so a session with this set has no conversation
        /// data past <see cref="convaiRequests"/> turns, whatever else is in the file.
        /// </summary>
        public bool convaiQuotaExhausted;
        public string convaiQuotaType = "";

        /// <summary>
        /// The shape of the conversation: how many times each side spoke, how often she was cut
        /// off, and how often the backend chose not to answer at all.
        ///
        /// <c>participantUtterances</c> and <see cref="convaiRequests"/> count the same event
        /// and should agree. They are kept separately on purpose -- one is the bill and one is
        /// the interaction -- and a disagreement between them is a real signal that one of the
        /// two subscriptions missed events.
        /// </summary>
        public int participantUtterances;
        public int characterUtterances;
        public int characterInterruptions;
        public int llmNoResponses;

        /// <summary>
        /// Task and planner totals. <c>planFailures</c> counts attempts that came back with
        /// nothing, which at one task per participant is a count and not a rate -- the write-up
        /// should not turn four of these into a percentage.
        /// </summary>
        public int tasksStarted;
        public int tasksCompleted;
        public int assists;
        public int planAttempts;
        public int planFailures;
    }

    /// <summary>One period of collecting, from entering the scanning stage to leaving it.</summary>
    [Serializable]
    public class ScanRunEntry
    {
        public float tStart;

        /// <summary>Negative while the run is still open, so an interrupted session is obvious.</summary>
        public float tStop = -1f;

        public int exportedAtStop;
    }

    /// <summary>
    /// One write of room_scan.json, and the copy taken of it.
    ///
    /// The copy is the load-bearing artifact of this whole phase. room_scan.json lives at
    /// one fixed path and the next participant overwrites it, so without a per-session copy
    /// every scan-accuracy measure loses its subject the moment the next session starts.
    /// </summary>
    [Serializable]
    public class ScanSaveEntry
    {
        public float t;
        public int objects;
        public long bytes;
        public string copiedTo;

        /// <summary>
        /// False when the file changed on disk with no matching panel save -- which means
        /// the A button was pressed. That route bypasses the panel's guards entirely and
        /// announces nothing, so catching it here turns an invisible protocol deviation
        /// into a recorded one.
        /// </summary>
        public bool viaPanel = true;
    }

    /// <summary>
    /// The first moment a cluster became exportable.
    ///
    /// Deliberately the crossing of minObservations rather than first sighting. That is the
    /// threshold precision is scored at, so the recall-at-30/60/120s curve and the precision
    /// figure end up describing the same event rather than two different ones. First
    /// sighting is still recoverable from firstSeenUtc in the scan copy.
    /// </summary>
    [Serializable]
    public class MilestoneEntry
    {
        public float t;
        public int clusterId;
        public int observations;
        public string label;
        public Vec3 roomPosition;
    }

    /// <summary>
    /// One participant conversation turn: when it landed, which number it was, and the
    /// backend's id for it.
    ///
    /// NO TEXT, and there is no field here that could hold any. Participant speech does not go
    /// on disk anywhere in this study -- that was decided before any of it was built, and the
    /// cheapest way to keep the decision is for the type to have nowhere to put it. The id is
    /// what lets a turn be joined to the utterance counts and timings recorded later without
    /// either side ever storing what was said.
    ///
    /// The instants matter on their own: turns clustered at the start of a block and turns
    /// spread evenly through it are different interactions with identical totals.
    /// </summary>
    [Serializable]
    public class ConvaiTurnEntry
    {
        public float t;

        /// <summary>1-based, and the running count at the moment this turn was counted.</summary>
        public int index;

        /// <summary>The backend message id, or empty when the SDK supplied none.</summary>
        public string messageId;
    }

    /// <summary>
    /// How the reference-resolution block was generated, so it can be regenerated exactly.
    ///
    /// The seed is the load-bearing field. The block is shuffled, and a shuffle nobody wrote
    /// down is a trial order that cannot be reproduced, checked for an ordering artefact, or
    /// compared across participants. With the seed and the scan id, the whole block is
    /// recoverable from four numbers.
    /// </summary>
    [Serializable]
    public class ReferenceBlock
    {
        public bool ran;

        /// <summary>The seed, and the participant id it was derived from.</summary>
        public int seed;
        public string seedSource = "";

        public int reps;
        public float cueSeconds;
        public float timeoutSeconds;
        public int namingAttemptCap;

        /// <summary>Which replayed scan the targets were chosen from.</summary>
        public string scanId = "";

        public int planned;
        public int completed;

        /// <summary>
        /// Conditions the room could not supply a target for, as "modality/distractors".
        ///
        /// A distractor count is realised by choosing a target that HAS that many same-label
        /// competitors, so a room with no label occurring five times cannot furnish the
        /// four-distractor condition at all. That is a fact about the room, and it belongs in
        /// the data rather than in a memory of which participant sat in which room -- an empty
        /// cell in the analysis otherwise looks like attrition.
        /// </summary>
        public List<string> unavailable = new List<string>();
    }

    /// <summary>
    /// One reference-resolution trial: cue a target, see whether the participant can indicate
    /// it, and how long that took.
    ///
    /// Scored on <see cref="targetId"/>, the scan file's id, never on the display name.
    /// Display names are invented at rebuild time and are not stable across rebuilds -- the
    /// landmark assignment is greedy by distance and depends on the object cap -- so a trial
    /// scored on "chair by the couch" is a trial scored against a label that may belong to a
    /// different chair next time the scan is replayed.
    /// </summary>
    [Serializable]
    public class ReferenceTrialEntry
    {
        /// <summary>1-based position in the block, after shuffling.</summary>
        public int index;

        /// <summary>"naming" or "pointing".</summary>
        public string modality = "";

        /// <summary>The condition: how many same-label competitors the target was meant to have.</summary>
        public int distractors;

        /// <summary>
        /// How many it actually had. Equal to <see cref="distractors"/> when the room could
        /// furnish the condition exactly, higher when the nearest available target was more
        /// crowded than asked for. Analyse on this one.
        /// </summary>
        public int actualDistractors;

        public int rep;

        /// <summary>The scan id. This is what correctness is judged on.</summary>
        public string targetId = "";
        public string targetLabel = "";

        /// <summary>The display name at cue time. For reading the log, not for scoring.</summary>
        public string targetName = "";

        /// <summary>When the cue appeared, and when it ended -- which is t0.</summary>
        public float tCueStart;
        public float tCueEnd;

        /// <summary>
        /// Seconds from the end of the cue to the resolving indication, or -1 when there never
        /// was one. Measured from the cue's END so that the two seconds of highlight are not
        /// counted as thinking time, and so t0 is a machine-precise instant rather than a
        /// facilitator's judgement of when the participant started.
        /// </summary>
        public float latency = -1f;

        public int attempts;
        public bool correct;

        /// <summary>"correct", "wrong", "timeout", "gave-up", or "skipped".</summary>
        public string outcome = "";
    }

    /// <summary>
    /// One indication within a trial -- one point, or one name that reached the app.
    ///
    /// A flat array keyed by <see cref="trialIndex"/> rather than a list inside the trial,
    /// matching the rule the rest of this file follows: JsonUtility is unreliable about nested
    /// containers, and every named array here is already a dataframe offline.
    ///
    /// THERE IS NO FIELD FOR WHAT WAS SAID, and that is deliberate rather than an omission.
    /// Participant speech does not go on disk anywhere in this study. The cost is real and
    /// worth stating in the write-up: a naming attempt that resolved to nothing is recorded as
    /// having happened and having failed, and the referring expression that failed cannot be
    /// recovered afterwards to find out why.
    /// </summary>
    [Serializable]
    public class ReferenceAttemptEntry
    {
        public int trialIndex;

        /// <summary>1-based within the trial.</summary>
        public int attempt;

        /// <summary>Seconds since the session opened, on the same clock as everything else.</summary>
        public float t;

        /// <summary>Seconds since this trial's cue ended.</summary>
        public float latency;

        public string modality = "";

        /// <summary>
        /// The scan id indicated, or empty when a spoken name resolved to no object at all.
        /// Empty is a real outcome and not a missing value: "they meant nothing this app knows"
        /// and "they meant the wrong object" are different failures of reference resolution.
        /// </summary>
        public string indicatedId = "";

        public bool correct;
    }

    /// <summary>
    /// One boundary in the conversation: somebody started or stopped speaking, an utterance was
    /// understood, a turn finished.
    ///
    /// AN EVENT LOG, NOT DURATIONS. Nothing here is an interval. Every duration and latency the
    /// analysis could want -- how long she took to start answering, how long an utterance ran,
    /// the gap between a participant finishing and the backend understanding them -- is a
    /// subtraction between two rows that are both here. Storing intervals instead would mean
    /// choosing now which ones matter, before anybody has seen a session, and there is one shot
    /// per participant.
    ///
    /// THERE IS NO TEXT FIELD, and there is nowhere to put one. <see cref="characters"/> is a
    /// length, not a recording: it cannot be read back into words, and it answers whether
    /// people produce longer referring expressions when naming is hard.
    /// </summary>
    [Serializable]
    public class SpeechEventEntry
    {
        public float t;

        /// <summary>"participant" or "character".</summary>
        public string speaker = "";

        /// <summary>
        /// "started", "stopped", "final", "turn-done", "interrupted", "no-response".
        ///
        /// A loose string rather than an enum, for the reason <see cref="NoteEntry"/> gives: a
        /// protocol grows new kinds mid-study, and a new enum member is a rebuild and a redeploy
        /// to a headset on another machine.
        ///
        /// "stopped" and "final" are deliberately different events. The first is when they
        /// finished speaking, the second is when the backend had finished understanding it;
        /// folding them together would put the recognition delay into either the participant's
        /// thinking time or her response time depending which end you measured from.
        /// </summary>
        public string kind = "";

        /// <summary>
        /// The backend id, where the event carries one. Empty otherwise. This is the join to
        /// <see cref="ConvaiTurnEntry.messageId"/>, which is how utterance timings and the
        /// request count line up without either side counting for the other.
        /// </summary>
        public string messageId = "";

        /// <summary>
        /// How long the utterance was, in characters. Zero on events that carry no text.
        /// A measure, not a recording -- see the remark on this class.
        /// </summary>
        public int characters;
    }

    /// <summary>
    /// One attempt at a plan, however it ended.
    ///
    /// Failures and cancellations are rows here too. A latency distribution built only from the
    /// successes describes a faster planner than the one anybody used: the slow attempts are
    /// exactly the ones that time out or get abandoned, so dropping them makes the number look
    /// better the worse the planner behaves.
    ///
    /// <see cref="placesOffered"/> and <see cref="hadRoomSummary"/> together record the
    /// condition, not just the outcome. The ungrounded arm is an empty place list AND a
    /// withheld summary -- a summary naming the furniture reimports the vocabulary the ablation
    /// was removing -- so one field could not describe it.
    /// </summary>
    [Serializable]
    public class PlanAttemptEntry
    {
        public float t;

        public bool ok;

        /// <summary>The caller gave up. Neither a success nor a failure of the planner.</summary>
        public bool cancelled;

        /// <summary>Short reason, from the planner. Empty when <see cref="ok"/>.</summary>
        public string failure = "";

        public string backend = "";
        public string model = "";

        /// <summary>
        /// How long the participant actually waited, including the attempts that never
        /// answered. This was previously not measured anywhere -- only the request timeout
        /// bounded it -- and it is the number a person in a headset cares about most.
        /// </summary>
        public float latency;

        public int placesOffered;
        public bool hadRoomSummary;

        public int steps;

        /// <summary>Steps that came back with a place the room actually has.</summary>
        public int groundedSteps;

        /// <summary>
        /// Locations thrown away because the room no longer had them. Counted apart from
        /// ungrounded steps on purpose: a dropped location is a stale scan or an invented
        /// place, and a step with no location is the planner saying "nowhere". The step alone
        /// cannot tell them apart, and before this they were both simply absent.
        /// </summary>
        public int droppedLocations;

        /// <summary>
        /// How long the task request was, in characters -- NOT the task itself.
        ///
        /// In a participant session the task arrives as the player's own words, through the
        /// Plan Task action's parameter. That makes it participant speech, and participant
        /// speech does not go on disk anywhere in this study. The offline plan harness writes
        /// its tasks in full, because those are researcher-authored prompts rather than
        /// anybody's utterance.
        /// </summary>
        public int taskCharacters;
    }

    /// <summary>
    /// One task attempt, bounded by the facilitator.
    ///
    /// Started and ended by hand rather than inferred, because there is no signal in the app
    /// that means "the task began" -- the participant says a sentence and the planner may or
    /// may not be involved. What the panel supplies is the instant, on the same clock as
    /// everything else, which is the part a paper sheet cannot do.
    /// </summary>
    [Serializable]
    public class TaskEntry
    {
        public float tStart;

        /// <summary>Negative while the task is still open, so an interrupted session is obvious.</summary>
        public float tEnd = -1f;

        /// <summary>
        /// Times the facilitator had to step in. Not a quality score and not comparable across
        /// facilitators -- it is a marker for the interview to be about, and a count of them is
        /// as much as one task per participant can support.
        /// </summary>
        public int assists;

        /// <summary>Whether the facilitator called it finished rather than abandoned.</summary>
        public bool completed;
    }

    /// <summary>One line from the panel's own outcome channel, success or refusal.</summary>
    [Serializable]
    public class ReportEntry
    {
        public float t;
        public string stage;
        public string text;
    }

    /// <summary>
    /// Something a human decided, stamped with the same clock as everything else.
    ///
    /// Kinds in use: "note" (free marker), "session-start", "session-end". Later phases add
    /// "assist", "task-start", "task-done", "rotated". Kept as a loose string rather than an
    /// enum on purpose -- a study protocol grows new kinds of remark mid-study, and a new
    /// enum member is a rebuild and a redeploy to a headset on another machine.
    /// </summary>
    [Serializable]
    public class NoteEntry
    {
        public float t;
        public string kind;
        public string text;
    }

    /// <summary>
    /// One session, as one file.
    ///
    /// Typed arrays rather than one generic {kind, a, b} event bag, for two reasons.
    /// JsonUtility cannot serialise a polymorphic array at all, so the bag would have to
    /// flatten every payload into shared untyped slots; and each named array here is already
    /// a dataframe offline, with field names that say what they hold. Ordering across arrays
    /// is recovered from t, which every entry carries.
    ///
    /// t is seconds since the session opened, not since app start. Sessions are what get
    /// compared, and Time.realtimeSinceStartup would put an offset in every one of them that
    /// depends on how long the app sat on the home screen first.
    /// </summary>
    [Serializable]
    public class StudySession
    {
        /// <summary>
        /// 2 since the Convai turn counter landed.
        ///
        /// Bumped rather than left alone because JsonUtility cannot tell an absent field from a
        /// zero one: a version-1 session and a version-2 session where nobody spoke both read
        /// back as convaiRequests = 0. One of those is a session with no counter in the build
        /// at all, and the difference decides whether the turn count means anything.
        /// </summary>
        public int schemaVersion = 2;

        public string capturedUtc;

        public string sessionId;
        public string participantId;
        public string roomLabel;
        public int run;

        public string startedUtc;
        public string endedUtc;

        public string appVersion;
        public string deviceModel;

        public StudyBuild build = new StudyBuild();
        public StudySummary summary = new StudySummary();

        public List<ScanRunEntry> scanRuns = new List<ScanRunEntry>();
        public List<ScanSaveEntry> saves = new List<ScanSaveEntry>();
        public List<MilestoneEntry> milestones = new List<MilestoneEntry>();
        public List<ReportEntry> reports = new List<ReportEntry>();
        public List<NoteEntry> notes = new List<NoteEntry>();
        public List<ConvaiTurnEntry> convaiTurns = new List<ConvaiTurnEntry>();

        public List<SpeechEventEntry> speech = new List<SpeechEventEntry>();
        public List<TaskEntry> tasks = new List<TaskEntry>();
        public List<PlanAttemptEntry> plans = new List<PlanAttemptEntry>();

        public ReferenceBlock referenceBlock = new ReferenceBlock();
        public List<ReferenceTrialEntry> referenceTrials = new List<ReferenceTrialEntry>();
        public List<ReferenceAttemptEntry> referenceAttempts = new List<ReferenceAttemptEntry>();
    }

    // ---------------------------------------------------------------------
    // Disk IO. Mirrors RoomScanIO deliberately, including its refusal to
    // catch: the write is unguarded here and the CALLER wraps it, so the
    // caller can put the failure on the panel where somebody in a headset
    // will actually see it.
    // ---------------------------------------------------------------------

    public static class StudySessionIO
    {
        /// <summary>
        /// Everything the study writes goes in one subfolder, and that is the point: it is
        /// the bulk-pull unit. One `adb pull .../files/study` takes the sessions, the scan
        /// copies, the observation logs and the room truth in a single command, which is
        /// what you want at the end of a participant slot rather than four paths to
        /// remember.
        /// </summary>
        public const string FolderName = "study";

        public static string Folder =>
            Path.Combine(Application.persistentDataPath, FolderName);

        /// <summary>
        /// Creates the study folder if it is not there. Safe to call repeatedly --
        /// Directory.CreateDirectory is a no-op on an existing path.
        /// </summary>
        public static void EnsureFolder() => Directory.CreateDirectory(Folder);

        public static string PathFor(string sessionId) =>
            Path.Combine(Folder, sessionId + ".json");

        /// <summary>
        /// Builds the session id, which is also every filename's stem.
        ///
        /// The UTC stamp alone guarantees uniqueness; participant, room and run are in there
        /// so the folder sorts sensibly and `ls P03_*` answers "what did this participant
        /// do". Colons are deliberately absent -- they are legal on the Quest's filesystem
        /// and illegal on the Windows machine the files get pulled to.
        /// </summary>
        public static string MakeSessionId(string participant, string room, int run, DateTime startUtc) =>
            $"{Sanitise(participant)}_{Sanitise(room)}_r{run:D2}_{startUtc:yyyyMMdd'T'HHmmss}Z";

        /// <summary>
        /// Which run this is for a participant, counted from what is already on disk.
        ///
        /// Derived rather than entered, because a run number is exactly the kind of field
        /// somebody forgets to advance on the second session of the day, and a duplicate
        /// P03_R2_r01 is a silently overwritten participant. Counting files cannot forget.
        /// </summary>
        public static int NextRun(string participant)
        {
            EnsureFolder();

            var prefix = Sanitise(participant) + "_";
            var run = 1;

            foreach (var path in Directory.GetFiles(Folder, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                // Scan copies share the stem and add their own suffix, so they would
                // otherwise inflate the count. Only the bare session file counts as a run.
                if (name.Contains(".scan.")) continue;

                run++;
            }

            return run;
        }

        public static void Save(StudySession session, string path = null)
        {
            EnsureFolder();

            path ??= PathFor(session.sessionId);
            session.capturedUtc = DateTime.UtcNow.ToString("o");

            var json = JsonUtility.ToJson(session, prettyPrint: true);
            File.WriteAllText(path, json);
        }

        public static StudySession Load(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[StudySessionIO] No session file at {path}");
                return null;
            }

            return JsonUtility.FromJson<StudySession>(File.ReadAllText(path));
        }

        /// <summary>
        /// Strips whatever a cycled field could produce that a filename cannot hold. The
        /// fields are picked from fixed lists so this should never fire, but a session id
        /// is the join key for every offline table and a stray character in it is a row
        /// nothing matches.
        /// </summary>
        private static string Sanitise(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";

            var clean = value.Trim();
            foreach (var bad in Path.GetInvalidFileNameChars()) clean = clean.Replace(bad, '-');

            return clean.Replace(' ', '-').Replace('_', '-').Replace('.', '-');
        }
    }
}
