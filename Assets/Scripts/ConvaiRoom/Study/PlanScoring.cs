using System;
using System.Collections.Generic;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// The arithmetic the plan corpus is scored with.
    ///
    /// Separated from the harness that drives it, and from the Editor entirely, for one reason:
    /// every function here is pure, so <c>StudySelfCheck</c> can assert them against answers
    /// worked out on paper. The harness is a loop and a network call and cannot be checked that
    /// way; this is where the claims actually live, so this is the part that has to be right.
    ///
    /// Nothing here is computed on device or written into a session file. The corpus keeps the
    /// plans and these run over it afterwards, which means a measure nobody thought of yet can
    /// still be added later against the same data.
    /// </summary>
    public static class PlanScoring
    {
        /// <summary>
        /// Steps whose text names something the room has.
        ///
        /// Whole-word matching, case-insensitive. Substring matching would count "table" inside
        /// "comfortable" and "tv" inside "tvs", and a specificity measure that fires on
        /// "comfortable" is worse than none. Multi-word names ("chair by the couch") are matched
        /// on the whole phrase, and also on their bare label, because a planner told about a
        /// "chair by the couch" routinely writes "the chair" -- which is still naming a thing
        /// this room has.
        ///
        /// A step counts once however many places it names. The question is whether the step is
        /// about this room, not how densely it is furnished.
        /// </summary>
        public static int RoomMentions(IReadOnlyList<PlanStepRecord> steps,
                                       IReadOnlyList<string> placeNames)
        {
            if (steps == null || placeNames == null) return 0;

            var needles = new List<string>();

            foreach (var name in placeNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                needles.Add(name.Trim());

                // "chair by the couch" -> also "chair". Taken from the front because the label
                // leads every name this project generates; see RoomScanContext.NameThem.
                var space = name.IndexOf(' ');
                if (space > 0) needles.Add(name.Substring(0, space).Trim());
            }

            var count = 0;

            foreach (var step in steps)
            {
                if (step == null || string.IsNullOrWhiteSpace(step.text)) continue;

                foreach (var needle in needles)
                {
                    if (!ContainsWord(step.text, needle)) continue;

                    count++;
                    break;
                }
            }

            return count;
        }

        /// <summary>
        /// Whether <paramref name="needle"/> appears in <paramref name="haystack"/> as whole
        /// words rather than as a fragment of a longer one.
        ///
        /// Boundaries are "not a letter or digit", which handles the punctuation a plan step
        /// actually contains -- commas, full stops, brackets -- without a regex whose behaviour
        /// on a name containing a bracket would have to be reasoned about.
        /// </summary>
        public static bool ContainsWord(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;

            var at = 0;

            while (true)
            {
                var found = haystack.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return false;

                var before = found == 0 || !IsWordChar(haystack[found - 1]);
                var afterAt = found + needle.Length;
                var after = afterAt >= haystack.Length || !IsWordChar(haystack[afterAt]);

                if (before && after) return true;

                at = found + 1;
                if (at >= haystack.Length) return false;
            }
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

        /// <summary>
        /// Words per step, averaged over the steps that have any.
        ///
        /// A crude specificity proxy, and it is labelled crude wherever it appears: "tidy up"
        /// and "put the two mugs on the tray by the sink" are different kinds of instruction and
        /// this is the cheapest number that tells them apart at all. The blind rating sheet is
        /// what actually answers specificity; this is for spotting a backend that has collapsed
        /// into one-word steps without reading four hundred plans to notice.
        /// </summary>
        public static float WordsPerStep(IReadOnlyList<PlanStepRecord> steps)
        {
            if (steps == null || steps.Count == 0) return 0f;

            var words = 0;
            var counted = 0;

            foreach (var step in steps)
            {
                if (step == null || string.IsNullOrWhiteSpace(step.text)) continue;

                words += step.text.Split(new[] { ' ', '\t', '\n', '\r' },
                                         StringSplitOptions.RemoveEmptyEntries).Length;
                counted++;
            }

            return counted == 0 ? 0f : (float)words / counted;
        }

        /// <summary>
        /// The value at <paramref name="percentile"/> (0..1) of <paramref name="values"/>.
        ///
        /// Nearest-rank on a copy, so the caller's list is not reordered underneath it -- the
        /// harness holds its records in run order and a sort in place here would scramble the
        /// correspondence between a latency and the plan it came from.
        ///
        /// Latency is reported by percentile rather than as a mean because the distribution is
        /// the point: a planner that answers in three seconds nineteen times out of twenty and
        /// in ninety seconds once is not the same product as one that always takes eight, and
        /// they have the same mean.
        /// </summary>
        public static float Percentile(IReadOnlyList<float> values, float percentile)
        {
            if (values == null || values.Count == 0) return 0f;

            var sorted = new List<float>(values);
            sorted.Sort();

            var clamped = Mathf.Clamp01(percentile);
            var rank = Mathf.CeilToInt(clamped * sorted.Count) - 1;

            return sorted[Mathf.Clamp(rank, 0, sorted.Count - 1)];
        }

        /// <summary>
        /// How much two plans agree about WHERE, as a Jaccard index over their place sets.
        ///
        /// Places rather than step text, deliberately. Asked the same question twice a model
        /// rewords freely -- "clear the table" and "clear off the dining table" are the same
        /// instruction -- so text agreement would measure phrasing and report it as
        /// inconsistency. The grounded claim is about the room: does she keep choosing the same
        /// places for the same task? That is what this answers.
        ///
        /// Two plans that ground nothing agree completely, and that is correct rather than a
        /// degenerate case: they made the same choice, which was to name no place at all. The
        /// caller reports it alongside the grounded-step counts, where an agreement of 1 over
        /// empty sets is obvious rather than flattering.
        /// </summary>
        public static float PlaceAgreement(IReadOnlyList<PlanStepRecord> a,
                                           IReadOnlyList<PlanStepRecord> b)
        {
            var left = PlaceSet(a);
            var right = PlaceSet(b);

            if (left.Count == 0 && right.Count == 0) return 1f;

            var shared = 0;
            foreach (var place in left)
                if (right.Contains(place)) shared++;

            var union = left.Count + right.Count - shared;

            return union == 0 ? 1f : (float)shared / union;
        }

        /// <summary>
        /// Mean pairwise agreement across a set of repeats of one (room, task, condition).
        ///
        /// Every pair rather than each against the first: comparing to the first repeat would
        /// make the answer depend on which run happened to go first, and with ten repeats that
        /// is a real difference rather than a quibble.
        /// </summary>
        public static float MeanPlaceAgreement(IReadOnlyList<PlanRecord> repeats)
        {
            if (repeats == null || repeats.Count < 2) return 1f;

            var total = 0f;
            var pairs = 0;

            for (var i = 0; i < repeats.Count; i++)
            {
                for (var j = i + 1; j < repeats.Count; j++)
                {
                    total += PlaceAgreement(repeats[i].steps, repeats[j].steps);
                    pairs++;
                }
            }

            return pairs == 0 ? 1f : total / pairs;
        }

        private static HashSet<string> PlaceSet(IReadOnlyList<PlanStepRecord> steps)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (steps == null) return set;

            foreach (var step in steps)
                if (step != null && !string.IsNullOrWhiteSpace(step.where)) set.Add(step.where.Trim());

            return set;
        }
    }
}
