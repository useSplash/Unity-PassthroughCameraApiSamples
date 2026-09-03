using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ConvaiRoom
{
    // ---------------------------------------------------------------------
    // What the offline plan harness leaves behind.
    //
    // Same rules as the rest of the study's persistence: plain [Serializable]
    // classes, public fields only, no dictionaries, no properties. JsonUtility
    // fails SILENTLY on any of those.
    //
    // Unlike a session file, this one holds TASK TEXT and PLAN TEXT in full.
    // That is not an inconsistency with the no-transcript rule: nothing here
    // is anybody's speech. The tasks are researcher-authored prompts and the
    // steps are the model's output, and the whole point of the corpus is that
    // a human can read the plans back and rate them.
    // ---------------------------------------------------------------------

    /// <summary>One step as the planner returned it, after the location was checked.</summary>
    [Serializable]
    public class PlanStepRecord
    {
        public string text = "";

        /// <summary>The place, or empty when the planner grounded it nowhere.</summary>
        public string where = "";
    }

    /// <summary>
    /// One plan, with everything needed to score it and nothing needed to identify who ran it.
    ///
    /// <see cref="condition"/> is "grounded" or "ungrounded". The ungrounded arm is an empty
    /// place list AND a withheld room summary -- withholding the summary matters as much as
    /// emptying the list, because a sentence naming the furniture reimports the vocabulary the
    /// ablation is removing. Both are recorded rather than inferred so a run can be checked
    /// afterwards rather than trusted.
    /// </summary>
    [Serializable]
    public class PlanRecord
    {
        public int index;

        public string room = "";
        public string scanFile = "";
        public string task = "";
        public string condition = "";

        public string backend = "";
        public string model = "";

        /// <summary>Which repeat this is, for the consistency mode. 1 in a single pass.</summary>
        public int repeat = 1;

        public bool ok;
        public bool cancelled;
        public string failure = "";

        public float latency;

        public int placesOffered;
        public bool hadRoomSummary;

        public string summary = "";
        public List<PlanStepRecord> steps = new List<PlanStepRecord>();

        /// <summary>Steps whose place the room actually has.</summary>
        public int groundedSteps;

        /// <summary>
        /// Locations the planner named that the room does not have, and which were therefore
        /// thrown away. On the ungrounded arm this is expected to be zero for a different reason
        /// than on the grounded arm -- there were no places to name -- so read it against
        /// <see cref="placesOffered"/>.
        /// </summary>
        public int droppedLocations;

        /// <summary>
        /// Steps whose TEXT names something this room actually has, whether or not the step was
        /// grounded to it.
        ///
        /// It reads as two different measures on the two arms, and both are wanted. On the
        /// grounded arm it is specificity: did the planner use the vocabulary it was given in
        /// the prose, or only in the location field, where "tidy the area" and "clear the
        /// dining table" score the same. On the ungrounded arm it is a LEAKAGE CHECK: an
        /// ungrounded planner was told nothing about this room, so any step naming its furniture
        /// got there by guessing a common word, and a high count means the arm is less
        /// ungrounded than the condition claims.
        ///
        /// Counted against the room's full place list, not the offered one, which is what lets
        /// it mean anything at all on the arm where nothing was offered.
        /// </summary>
        public int roomMentions;

        /// <summary>
        /// Words per step, averaged. A crude specificity proxy and labelled as one: "tidy up"
        /// and "put the two mugs on the tray by the sink" are different kinds of instruction and
        /// this is the cheapest number that separates them at all. The blind rating sheet is
        /// what actually answers specificity.
        /// </summary>
        public float wordsPerStep;
    }

    /// <summary>
    /// One harness run: every plan, and the conditions they were produced under.
    ///
    /// Written whole rather than appended for the reason the session file is: this is one
    /// JsonUtility object and there is no append that keeps it valid. It is flushed as it goes
    /// so a run interrupted after two hundred plans is two hundred usable plans rather than a
    /// file that needs repairing.
    /// </summary>
    [Serializable]
    public class PlanCorpus
    {
        public int schemaVersion = 1;
        public string capturedUtc = "";

        public string runId = "";
        public string startedUtc = "";
        public string endedUtc = "";

        /// <summary>What was asked for, so a short run is not mistaken for a complete one.</summary>
        public int planned;
        public int completed;

        /// <summary>Repeats per (room, task, condition, backend). 1 is the sweep, >1 consistency.</summary>
        public int repeats = 1;

        public List<string> rooms = new List<string>();
        public List<string> tasks = new List<string>();
        public List<string> backends = new List<string>();

        public List<PlanRecord> plans = new List<PlanRecord>();
    }

    /// <summary>
    /// Disk IO for the corpus. Mirrors StudySessionIO, including its refusal to catch: the
    /// write is unguarded and the CALLER wraps it, because the caller is an Editor tool that
    /// can put the failure in a dialog where somebody will see it.
    /// </summary>
    public static class PlanCorpusIO
    {
        public const string FolderName = "plans";

        public static string Folder => Path.Combine(Application.persistentDataPath, FolderName);

        public static void EnsureFolder() => Directory.CreateDirectory(Folder);

        public static string PathFor(string runId) => Path.Combine(Folder, runId + ".json");

        /// <summary>
        /// The run id, which is also the filename stem. Colons are deliberately absent -- legal
        /// on one filesystem and illegal on the Windows machine these get read on.
        /// </summary>
        public static string MakeRunId(DateTime startUtc) =>
            $"plans_{startUtc:yyyyMMdd'T'HHmmss}Z";

        public static void Save(PlanCorpus corpus, string path = null)
        {
            EnsureFolder();

            path ??= PathFor(corpus.runId);
            corpus.capturedUtc = DateTime.UtcNow.ToString("o");

            File.WriteAllText(path, JsonUtility.ToJson(corpus, prettyPrint: true));
        }

        public static PlanCorpus Load(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[PlanCorpusIO] No corpus at {path}");
                return null;
            }

            return JsonUtility.FromJson<PlanCorpus>(File.ReadAllText(path));
        }
    }
}
