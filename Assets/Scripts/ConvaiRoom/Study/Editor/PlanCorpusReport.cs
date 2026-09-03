using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ConvaiRoom;

namespace ConvaiRoomEditor
{
    /// <summary>
    /// Turns a finished PlanCorpus into the things a person actually reads: an aggregate
    /// summary, a de-identified sheet for blind rating, and -- when the run repeated cells --
    /// a consistency report.
    ///
    /// Kept separate from PlanCorpusHarness because none of this needs the harness's state, an
    /// EditorWindow, or even the Editor at all -- it is a pure function of a PlanCorpus, callable
    /// on any saved corpus file whether or not the run that produced it is still open. Re-running
    /// the report after editing a corpus by hand, or after a run was interrupted and resumed by
    /// concatenating two files, needs nothing more than this class and the file.
    ///
    /// FAILURE REASONS ARE GROUPED VERBATIM RATHER THAN CLASSIFIED. RoomPlannerClient's failure
    /// strings ("could not reach the planner", "the plan came back malformed", a raw Ollama or
    /// Anthropic error message) already distinguish connectivity from a schema problem from a
    /// refusal; inventing a "schema violation" bucket on top would mean guessing which free-text
    /// string belongs in it, and a wrong guess would misreport the very number the harness exists
    /// to produce honestly. Tallying the exact strings is less tidy and cannot be wrong.
    /// </summary>
    public static class PlanCorpusReport
    {
        /// <summary>Writes every report file next to the corpus. Returns the summary's path.</summary>
        public static string Write(PlanCorpus corpus, string folder)
        {
            Directory.CreateDirectory(folder);

            var stem = Path.Combine(folder, corpus.runId);
            var summaryPath = stem + ".summary.txt";

            WriteSummary(corpus, summaryPath);
            WriteBlindSheet(corpus, stem + ".blind.csv", stem + ".blind_key.csv");

            if (corpus.repeats > 1) WriteConsistency(corpus, stem + ".consistency.csv");

            return summaryPath;
        }

        // -----------------------------------------------------------------
        // Aggregate summary
        // -----------------------------------------------------------------

        /// <summary>One (condition, backend) cell's worth of numbers.</summary>
        private class Group
        {
            public int Total;
            public int Ok;
            public int Cancelled;
            public int TotalSteps;
            public int TotalGrounded;
            public int TotalDropped;
            public int TotalMentions;
            public float SumWordsPerStep;
            public readonly List<float> Latencies = new List<float>();
            public readonly Dictionary<string, int> FailureCounts = new Dictionary<string, int>();
        }

        private static void WriteSummary(PlanCorpus corpus, string path)
        {
            var groups = new Dictionary<string, Group>();
            var order = new List<string>();

            foreach (var plan in corpus.plans)
            {
                var key = $"{plan.condition}/{plan.backend}";

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new Group();
                    groups[key] = group;
                    order.Add(key);
                }

                Accumulate(group, plan);
            }

            var sb = new StringBuilder();

            sb.Append("Plan corpus  ").Append(corpus.runId).AppendLine();
            sb.Append(corpus.completed).Append('/').Append(corpus.planned).Append(" plans, ")
              .Append(corpus.rooms.Count).Append(" rooms, ").Append(corpus.tasks.Count)
              .Append(" tasks, repeats=").Append(corpus.repeats).AppendLine();
            sb.Append(corpus.startedUtc).Append(" -> ").Append(corpus.endedUtc).AppendLine();
            sb.AppendLine();

            foreach (var key in order)
            {
                var g = groups[key];
                sb.Append("== ").Append(key).Append(" ==").AppendLine();
                sb.Append("  n=").Append(g.Total)
                  .Append("  ok=").Append(g.Ok)
                  .Append(" (").Append(Pct(g.Ok, g.Total)).Append(")")
                  .Append("  cancelled=").Append(g.Cancelled).AppendLine();

                sb.Append("  latency p50=").Append(Fmt(PlanScoring.Percentile(g.Latencies, 0.5f)))
                  .Append("s  p90=").Append(Fmt(PlanScoring.Percentile(g.Latencies, 0.9f)))
                  .Append("s  p99=").Append(Fmt(PlanScoring.Percentile(g.Latencies, 0.99f)))
                  .Append("s").AppendLine();

                // Groundedness is reported over the OK plans' steps, since a plan that never
                // produced a step cannot be graded on how well it grounded them -- pooling a
                // zero in would understate groundedness for reasons that have nothing to do
                // with grounding.
                sb.Append("  groundedness=").Append(Pct(g.TotalGrounded, g.TotalSteps))
                  .Append(" (").Append(g.TotalGrounded).Append('/').Append(g.TotalSteps)
                  .Append(" steps)  dropped locations=").Append(g.TotalDropped).AppendLine();

                sb.Append("  words/step=").Append(Fmt(g.Ok > 0 ? g.SumWordsPerStep / g.Ok : 0f))
                  .Append("  room mentions/plan=")
                  .Append(Fmt(g.Ok > 0 ? (float)g.TotalMentions / g.Ok : 0f)).AppendLine();

                if (g.FailureCounts.Count > 0)
                {
                    sb.Append("  failures:").AppendLine();
                    foreach (var pair in g.FailureCounts)
                        sb.Append("    ").Append(pair.Value).Append("x  ").Append(pair.Key).AppendLine();
                }

                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static void Accumulate(Group g, PlanRecord plan)
        {
            g.Total++;

            if (plan.cancelled)
            {
                g.Cancelled++;
                return;
            }

            if (!plan.ok)
            {
                var reason = string.IsNullOrEmpty(plan.failure) ? "(no reason given)" : plan.failure;
                g.FailureCounts.TryGetValue(reason, out var count);
                g.FailureCounts[reason] = count + 1;
                return;
            }

            g.Ok++;
            g.Latencies.Add(plan.latency);
            g.TotalSteps += plan.steps.Count;
            g.TotalGrounded += plan.groundedSteps;
            g.TotalDropped += plan.droppedLocations;
            g.TotalMentions += plan.roomMentions;
            g.SumWordsPerStep += plan.wordsPerStep;
        }

        private static string Pct(int part, int whole) =>
            whole <= 0 ? "n/a" : (100f * part / whole).ToString("F0", CultureInfo.InvariantCulture) + "%";

        private static string Fmt(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

        // -----------------------------------------------------------------
        // Blind rating sheet
        // -----------------------------------------------------------------

        /// <summary>
        /// One CSV a rater opens, and one they do not until afterwards.
        ///
        /// De-identified means blind to backend, model and condition -- exactly the three facts
        /// that could bias a correctness, safety or appropriateness judgement if the rater
        /// happened to know them going in. Room and task stay visible because the rater needs
        /// them to judge whether the steps make sense at all. Row order is shuffled so a
        /// pattern across consecutive rows (every third one being Ollama, say) cannot leak the
        /// hidden variables by position.
        ///
        /// The key file is the only way back from a blind id to what produced it. Keeping the
        /// two apart, rather than one CSV with extra columns to ignore, is what makes "opened
        /// the wrong file too early" a real failure mode worth designing against -- a habit is
        /// easy to break, a separate file is not.
        /// </summary>
        private static void WriteBlindSheet(PlanCorpus corpus, string sheetPath, string keyPath)
        {
            var order = new List<int>(corpus.plans.Count);
            for (var i = 0; i < corpus.plans.Count; i++) order.Add(i);

            // A fixed seed derived from the run id, and specifically NOT corpus.runId.GetHashCode()
            // -- .NET randomises string hashing per process, so that seed would differ between
            // two runs of the SAME build, same trap ReferenceTrialRunner.SeedFor avoids for the
            // same reason. The point of a fixed seed is that re-running this report generator
            // against an already-saved corpus (after fixing a bug in this class, say) reproduces
            // the same shuffle rather than handing a rater's in-progress ratings a new blind id
            // for every plan.
            var random = new Random(StableSeed(corpus.runId));
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            var sheet = new StringBuilder();
            var key = new StringBuilder();

            sheet.AppendLine("blindId,room,task,summary,steps,correctness,safety,appropriateness");
            key.AppendLine("blindId,originalIndex,condition,backend,model,repeat,ok,failure");

            for (var blindId = 0; blindId < order.Count; blindId++)
            {
                var plan = corpus.plans[order[blindId]];

                var stepsJoined = new StringBuilder();
                for (var i = 0; i < plan.steps.Count; i++)
                {
                    if (i > 0) stepsJoined.Append(" || ");
                    stepsJoined.Append(plan.steps[i].text);
                    if (!string.IsNullOrEmpty(plan.steps[i].where))
                        stepsJoined.Append(" [").Append(plan.steps[i].where).Append(']');
                }

                sheet.Append(blindId).Append(',')
                     .Append(Csv(plan.room)).Append(',')
                     .Append(Csv(plan.task)).Append(',')
                     .Append(Csv(plan.summary)).Append(',')
                     .Append(Csv(stepsJoined.ToString()))
                     // Trailing empty columns for a rater to fill in by hand.
                     .Append(",,,")
                     .AppendLine();

                key.Append(blindId).Append(',').Append(plan.index).Append(',')
                   .Append(Csv(plan.condition)).Append(',').Append(Csv(plan.backend)).Append(',')
                   .Append(Csv(plan.model)).Append(',').Append(plan.repeat).Append(',')
                   .Append(plan.ok ? 1 : 0).Append(',').Append(Csv(plan.failure))
                   .AppendLine();
            }

            File.WriteAllText(sheetPath, sheet.ToString());
            File.WriteAllText(keyPath, key.ToString());
        }

        // -----------------------------------------------------------------
        // Consistency mode
        // -----------------------------------------------------------------

        /// <summary>
        /// Mean place-set agreement across the repeats of each (room, task, condition, backend)
        /// cell. Only meaningful once there IS more than one repeat, which is why the harness
        /// only calls this when <c>repeats > 1</c>.
        /// </summary>
        private static void WriteConsistency(PlanCorpus corpus, string path)
        {
            var cells = new Dictionary<string, List<PlanRecord>>();
            var order = new List<string>();

            foreach (var plan in corpus.plans)
            {
                var key = $"{plan.room}|{plan.task}|{plan.condition}|{plan.backend}";

                if (!cells.TryGetValue(key, out var list))
                {
                    list = new List<PlanRecord>();
                    cells[key] = list;
                    order.Add(key);
                }

                list.Add(plan);
            }

            var sb = new StringBuilder();
            sb.AppendLine("room,task,condition,backend,repeats,meanPlaceAgreement");

            foreach (var key in order)
            {
                var repeats = cells[key];
                var first = repeats[0];
                var agreement = PlanScoring.MeanPlaceAgreement(repeats);

                sb.Append(Csv(first.room)).Append(',').Append(Csv(first.task)).Append(',')
                  .Append(Csv(first.condition)).Append(',').Append(Csv(first.backend)).Append(',')
                  .Append(repeats.Count).Append(',')
                  .Append(agreement.ToString("F3", CultureInfo.InvariantCulture))
                  .AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        // -----------------------------------------------------------------

        /// <summary>
        /// FNV-1a. See the remark on its call site for why this exists instead of
        /// <c>string.GetHashCode()</c>; identical in spirit to
        /// <see cref="ReferenceTrialRunner.SeedFor"/>, kept as its own small copy here rather
        /// than shared, since the two features are otherwise unconnected and an 8-line hash is
        /// not worth a cross-cutting dependency between them.
        /// </summary>
        private static int StableSeed(string text)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;

                var hash = offset;
                foreach (var c in text ?? "")
                {
                    hash ^= c;
                    hash *= prime;
                }

                return (int)(hash & 0x7FFFFFFF);
            }
        }

        /// <summary>
        /// RFC 4180 field quoting. Task and step text are researcher- and model-written prose,
        /// not identifiers, so commas, quotes and even embedded newlines are ordinary content
        /// here rather than something to reject or strip.
        /// </summary>
        private static string Csv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";

            var needsQuoting = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuoting) return field;

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
    }
}
