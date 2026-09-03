using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ConvaiRoom;
using RoomScan;
using UnityEditor;
using UnityEngine;

namespace ConvaiRoomEditor
{
    /// <summary>
    /// Checks the study harness without a headset, and without entering Play mode.
    ///
    /// The point of this is JsonUtility. It fails SILENTLY on the things that are easy to
    /// write by accident -- a Dictionary, a property where a field was meant, a nested
    /// generic -- and what you get back is an empty object and no error at all. On device
    /// that surfaces as a session file full of `[]` after the participant has gone home.
    /// A round trip is the only thing that catches it, and it costs a menu click.
    ///
    /// There is no test assembly in this project, so this follows the existing habit of
    /// putting checks behind an Editor menu item rather than introducing one.
    ///
    /// The pure logic here is deliberately static and side-effect free in the runtime classes
    /// (<see cref="RoomTruthMarker.ParseLabels"/>, <see cref="RoomTruthMarker.BoxFromCorners"/>,
    /// <see cref="StudySessionIO.MakeSessionId"/>) precisely so it can be exercised from here.
    /// </summary>
    public static class StudySelfCheck
    {
        private static int _passed;
        private static int _failed;

        [MenuItem("Tools/Convai Room/Study Self-check")]
        public static void Run()
        {
            _passed = 0;
            _failed = 0;

            CheckSessionRoundTrip();
            CheckTruthRoundTrip();
            CheckDiskRoundTrip();
            CheckSessionId();
            CheckNextRun();
            CheckBoxFromCorners();
            CheckLabelParsing();
            CheckQuantisation();
            CheckObservationReplay();
            CheckTrialSeed();
            CheckPlanScoringWords();
            CheckPlanScoringRoomMentions();
            CheckPlanScoringPercentile();
            CheckPlanScoringPlaceAgreement();
            CheckRebuildLogRoundTrip();
            CheckRebuildLogDiskRoundTrip();

            var message = $"[StudySelfCheck] {_passed} passed, {_failed} failed.";

            if (_failed == 0) Debug.Log(message);
            else Debug.LogError(message + " See the failures above.");
        }

        // -----------------------------------------------------------------
        // The one that matters: does JsonUtility keep everything?
        // -----------------------------------------------------------------

        private static void CheckSessionRoundTrip()
        {
            var session = new StudySession
            {
                sessionId = "P03_R2_r01_20260903T141205Z",
                participantId = "P03",
                roomLabel = "R2",
                run = 1,
                startedUtc = DateTime.UtcNow.ToString("o"),
                appVersion = "0.1",
                deviceModel = "Quest 3",
                build = new StudyBuild
                {
                    minObservations = 5,
                    extentPercentile = 0.8f,
                    extentSampleCount = 64,
                    mergeRadius = 0.5f,
                    observationLogArmed = true,
                    ignoredLabels = new List<string> { "person" }
                }
            };

            session.scanRuns.Add(new ScanRunEntry { tStart = 1.5f, tStop = 61.25f, exportedAtStop = 7 });
            session.saves.Add(new ScanSaveEntry
            {
                t = 61f, objects = 7, bytes = 4096,
                copiedTo = "P03_R2_r01_20260903T141205Z.scan.1.json", viaPanel = false
            });
            session.milestones.Add(new MilestoneEntry
            {
                t = 12.5f, clusterId = 3, observations = 5, label = "chair",
                roomPosition = new Vec3(new Vector3(1.25f, 0.5f, -2f))
            });
            session.reports.Add(new ReportEntry { t = 61f, stage = "Scanning", text = "saved 7 objects" });
            session.notes.Add(new NoteEntry { t = 0f, kind = "session-start", text = "P03 R2 run 1" });
            session.convaiTurns.Add(new ConvaiTurnEntry { t = 180.5f, index = 1, messageId = "msg-7" });

            // A turn the SDK gave no message id for. The empty string is the case that matters
            // here: JsonUtility writes "" and reads it back as "", but a null written on one
            // side and read as "" on the other would make an offline join silently drop rows.
            session.convaiTurns.Add(new ConvaiTurnEntry { t = 195f, index = 2, messageId = "" });

            session.referenceBlock = new ReferenceBlock
            {
                ran = true, seed = 12345, seedSource = "P03", reps = 3,
                cueSeconds = 2f, timeoutSeconds = 60f, namingAttemptCap = 2,
                scanId = "2026-09-03T14:12:05Z", planned = 24, completed = 24,
                unavailable = new List<string> { "naming/4", "pointing/4" }
            };

            session.referenceTrials.Add(new ReferenceTrialEntry
            {
                index = 1, modality = "naming", distractors = 3, actualDistractors = 3, rep = 1,
                targetId = "obj-7", targetLabel = "chair", targetName = "chair by the couch",
                tCueStart = 200f, tCueEnd = 202f, latency = 4.25f, attempts = 1,
                correct = true, outcome = "correct"
            });

            // The scored-incorrect case, which carries the value that is easiest to lose: a
            // latency of -1 meaning "there never was a correct answer". A zero here would read
            // offline as an instantaneous correct response.
            session.referenceTrials.Add(new ReferenceTrialEntry
            {
                index = 2, modality = "pointing", distractors = 1, actualDistractors = 2, rep = 1,
                targetId = "obj-2", targetLabel = "bottle", targetName = "bottle",
                tCueStart = 220f, tCueEnd = 222f, latency = -1f, attempts = 2,
                correct = false, outcome = "timeout"
            });

            session.referenceAttempts.Add(new ReferenceAttemptEntry
            {
                trialIndex = 2, attempt = 1, t = 225f, latency = 3f,
                modality = "pointing", indicatedId = "obj-3", correct = false
            });

            // A name that resolved to nothing. Empty is the value, not a missing one.
            session.referenceAttempts.Add(new ReferenceAttemptEntry
            {
                trialIndex = 2, attempt = 2, t = 231f, latency = 9f,
                modality = "pointing", indicatedId = "", correct = false
            });

            session.speech.Add(new SpeechEventEntry
            {
                t = 180.1f, speaker = "participant", kind = "started", messageId = "", characters = 0
            });
            session.speech.Add(new SpeechEventEntry
            {
                t = 182.4f, speaker = "participant", kind = "final",
                messageId = "msg-7", characters = 23
            });
            session.speech.Add(new SpeechEventEntry
            {
                t = 183.9f, speaker = "character", kind = "started", messageId = "utt-3", characters = 0
            });

            session.tasks.Add(new TaskEntry
            {
                tStart = 400f, tEnd = 640f, assists = 2, completed = true
            });

            // A task the session ended on top of: stamped, but never called finished.
            session.tasks.Add(new TaskEntry
            {
                tStart = 700f, tEnd = 760f, assists = 0, completed = false
            });

            session.plans.Add(new PlanAttemptEntry
            {
                t = 410f, ok = true, cancelled = false, failure = "",
                backend = "anthropic", model = "claude-haiku-4-5",
                latency = 4.75f, placesOffered = 22, hadRoomSummary = true,
                steps = 6, groundedSteps = 4, droppedLocations = 1, taskCharacters = 48
            });

            // The ungrounded arm: no places AND no summary. Both fields are needed to describe
            // it, which is why the round trip checks them together.
            session.plans.Add(new PlanAttemptEntry
            {
                t = 500f, ok = false, cancelled = true, failure = "",
                backend = "ollama", model = "qwen2.5:7b-instruct",
                latency = 31.5f, placesOffered = 0, hadRoomSummary = false,
                steps = 0, groundedSteps = 0, droppedLocations = 0, taskCharacters = 48
            });

            session.summary.participantUtterances = 1;
            session.summary.characterUtterances = 1;
            session.summary.characterInterruptions = 0;
            session.summary.llmNoResponses = 0;

            session.summary.convaiRequests = 2;
            session.summary.convaiBudget = 40;
            session.summary.convaiBudgetEnforced = false;
            session.summary.convaiQuotaExhausted = true;
            session.summary.convaiQuotaType = "daily";

            var json = JsonUtility.ToJson(session, true);
            var back = JsonUtility.FromJson<StudySession>(json);

            Assert("session: survives a round trip", back != null);
            if (back == null) return;

            Assert("session: id kept", back.sessionId == session.sessionId);
            Assert("session: build kept", back.build != null && back.build.minObservations == 5);
            Assert("session: ignoredLabels kept",
                   back.build.ignoredLabels != null && back.build.ignoredLabels.Count == 1
                   && back.build.ignoredLabels[0] == "person");

            // Every array, individually. A list that comes back empty is the exact silent
            // failure this whole check exists for, and "one of them is empty" is not a
            // useful thing to be told.
            Assert("session: scanRuns kept", back.scanRuns.Count == 1);
            Assert("session: saves kept", back.saves.Count == 1);
            Assert("session: milestones kept", back.milestones.Count == 1);
            Assert("session: reports kept", back.reports.Count == 1);
            Assert("session: notes kept", back.notes.Count == 1);
            Assert("session: convaiTurns kept", back.convaiTurns.Count == 2);

            Assert("session: bool false survives", back.saves.Count == 1 && !back.saves[0].viaPanel);
            Assert("session: long survives", back.saves.Count == 1 && back.saves[0].bytes == 4096);

            // The turn count is the number the request budget is spent against, and the whole
            // point of the file for anyone reading it later. A summary that round-trips to
            // zero is a participant who looks like they never spoke.
            Assert("session: convaiRequests kept", back.summary.convaiRequests == 2);
            Assert("session: convaiBudget kept", back.summary.convaiBudget == 40);
            Assert("session: enforced-false survives", !back.summary.convaiBudgetEnforced);
            Assert("session: quotaExhausted kept", back.summary.convaiQuotaExhausted);
            Assert("session: quotaType kept", back.summary.convaiQuotaType == "daily");

            Assert("session: turn id kept",
                   back.convaiTurns.Count == 2 && back.convaiTurns[0].messageId == "msg-7");

            // Empty, not null. A null here reads as "no id" everywhere it is joined on, which
            // is the same thing an empty string means -- but only one of the two can be
            // compared without a null check in every consumer.
            Assert("session: empty turn id round-trips as empty",
                   back.convaiTurns.Count == 2 && back.convaiTurns[1].messageId == "");

            Assert("session: schemaVersion is 2 since the turn counter",
                   back.schemaVersion == 2, $"got {back.schemaVersion}");

            // The reference block is the study's primary outcome, so every part of it is
            // checked individually rather than by counting rows.
            Assert("session: referenceTrials kept", back.referenceTrials.Count == 2);
            Assert("session: referenceAttempts kept", back.referenceAttempts.Count == 2);
            Assert("session: referenceBlock kept",
                   back.referenceBlock != null && back.referenceBlock.seed == 12345);
            Assert("session: block seedSource kept",
                   back.referenceBlock != null && back.referenceBlock.seedSource == "P03");
            Assert("session: block unavailable list kept",
                   back.referenceBlock != null && back.referenceBlock.unavailable.Count == 2 &&
                   back.referenceBlock.unavailable[0] == "naming/4");

            Assert("session: trial scored on scan id",
                   back.referenceTrials.Count == 2 && back.referenceTrials[0].targetId == "obj-7");

            // -1 rather than 0, and the one number in this file whose loss would be invisible:
            // a zeroed latency reads as a correct answer given instantly.
            Assert("session: absent latency stays -1",
                   back.referenceTrials.Count == 2 &&
                   Mathf.Abs(back.referenceTrials[1].latency + 1f) < 1e-4f,
                   back.referenceTrials.Count == 2
                       ? $"got {back.referenceTrials[1].latency}"
                       : "no trial");

            Assert("session: actualDistractors kept apart from the condition",
                   back.referenceTrials.Count == 2 &&
                   back.referenceTrials[1].distractors == 1 &&
                   back.referenceTrials[1].actualDistractors == 2);

            Assert("session: unresolved attempt round-trips as empty",
                   back.referenceAttempts.Count == 2 && back.referenceAttempts[1].indicatedId == "");

            Assert("session: speech events kept", back.speech.Count == 3);

            // The join between utterance timings and the request count. If this id is lost the
            // two tables cannot be lined up at all, and neither carries the other's information.
            Assert("session: speech joins to a turn on messageId",
                   back.speech.Count == 3 && back.speech[1].messageId == "msg-7" &&
                   back.convaiTurns.Count == 2 && back.convaiTurns[0].messageId == "msg-7");

            Assert("session: utterance length kept, and it is only a length",
                   back.speech.Count == 3 && back.speech[1].characters == 23);

            // The event log's whole point: durations and latencies are subtractions between
            // rows, so the rows have to keep their order and their instants.
            Assert("session: speech instants survive in order",
                   back.speech.Count == 3 &&
                   back.speech[0].t < back.speech[1].t && back.speech[1].t < back.speech[2].t);

            Assert("session: speech counts kept",
                   back.summary.participantUtterances == 1 && back.summary.characterUtterances == 1);

            Assert("session: tasks kept", back.tasks.Count == 2);
            Assert("session: plans kept", back.plans.Count == 2);

            // completed=false is the default a bool round-trips to, so a lost field would look
            // exactly like an abandoned task. Both values are checked for that reason.
            Assert("session: task completed-true survives",
                   back.tasks.Count == 2 && back.tasks[0].completed);
            Assert("session: task completed-false survives",
                   back.tasks.Count == 2 && !back.tasks[1].completed);
            Assert("session: assists kept", back.tasks.Count == 2 && back.tasks[0].assists == 2);

            // Cancelled and failed are different outcomes and must not collapse into "not ok".
            Assert("session: plan cancelled kept apart from failed",
                   back.plans.Count == 2 && !back.plans[1].ok && back.plans[1].cancelled &&
                   !back.plans[0].cancelled);

            Assert("session: plan latency kept",
                   back.plans.Count == 2 && Mathf.Abs(back.plans[0].latency - 4.75f) < 1e-4f);

            // The dropped-location count is the whole reason this event exists: before it, a
            // step with no place could not be told from one whose place was thrown away.
            Assert("session: droppedLocations kept apart from groundedSteps",
                   back.plans.Count == 2 && back.plans[0].droppedLocations == 1 &&
                   back.plans[0].groundedSteps == 4 && back.plans[0].steps == 6);

            // The ungrounded arm needs both fields; either one alone describes a different run.
            Assert("session: ungrounded condition round-trips as both",
                   back.plans.Count == 2 && back.plans[1].placesOffered == 0 &&
                   !back.plans[1].hadRoomSummary);

            Assert("session: task text is a length, not text",
                   back.plans.Count == 2 && back.plans[0].taskCharacters == 48);

            // Nested [Serializable] inside a list element -- the shape most likely to come
            // back null without anyone noticing.
            var nested = back.milestones.Count == 1 ? back.milestones[0].roomPosition : null;
            Assert("session: nested Vec3 kept", nested != null);
            Assert("session: nested Vec3 value kept",
                   nested != null && Mathf.Abs(nested.x - 1.25f) < 1e-4f
                   && Mathf.Abs(nested.z + 2f) < 1e-4f);
        }

        /// <summary>
        /// The trial seed: same participant, same block, forever.
        ///
        /// Properties rather than a hard-coded expected number, deliberately. A constant worked
        /// out by hand here would be a second implementation of FNV-1a, and a check that agrees
        /// with a mistake is worse than no check. What the study actually depends on is that the
        /// seed is a pure function of the participant id, that different participants get
        /// different blocks, and that it is a legal System.Random seed -- which is exactly what
        /// string.GetHashCode would fail, since .NET randomises string hashing per process and
        /// a seed taken from it differs between two runs of the same build.
        /// </summary>
        private static void CheckTrialSeed()
        {
            var a = ReferenceTrialRunner.SeedFor("P03");
            var b = ReferenceTrialRunner.SeedFor("P03");
            var c = ReferenceTrialRunner.SeedFor("P04");

            Assert("seed: same participant, same seed", a == b, $"{a} vs {b}");
            Assert("seed: different participants differ", a != c, $"both {a}");
            Assert("seed: non-negative", a >= 0 && c >= 0, $"{a}, {c}");

            // System.Random rejects int.MinValue outright and this must never hand it one.
            Assert("seed: usable by System.Random", NewRandomSucceeds(a) && NewRandomSucceeds(c));

            // An empty id is what an unconfigured participant field would produce. It must
            // still yield a workable block rather than throwing on the way into the headset.
            Assert("seed: empty id still seeds", ReferenceTrialRunner.SeedFor("") >= 0);
            Assert("seed: null id still seeds", ReferenceTrialRunner.SeedFor(null) >= 0);
        }

        private static bool NewRandomSucceeds(int seed)
        {
            try
            {
                var _ = new System.Random(seed).Next();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // -----------------------------------------------------------------
        // The offline plan harness's arithmetic. Every one of these is pure -- see the
        // remark on PlanScoring for why that is the point -- so each is checked against an
        // answer worked out on paper rather than against another run of the same code.
        // -----------------------------------------------------------------

        private static void CheckPlanScoringWords()
        {
            Assert("words: matches whole words, not fragments",
                   PlanScoring.ContainsWord("this chair looks comfortable", "table") == false);

            // The exact counterexample PlanScoring's own remark gives: "table" is a literal
            // substring of "comfortable" (com-for-TABLE), which is precisely what whole-word
            // matching exists to reject.
            Assert("words: 'table' really is inside 'comfortable'",
                   "comfortable".IndexOf("table", StringComparison.OrdinalIgnoreCase) >= 0);

            Assert("words: matches at the end of a sentence",
                   PlanScoring.ContainsWord("put it on the table", "table"));

            Assert("words: does not match 'tv' inside 'tvs'",
                   PlanScoring.ContainsWord("there are two tvs here", "tv") == false);

            Assert("words: does match a bare 'tv'",
                   PlanScoring.ContainsWord("turn on the tv please", "tv"));

            Assert("words: case-insensitive",
                   PlanScoring.ContainsWord("Clear the TABLE please", "table"));

            var steps = new List<PlanStepRecord>
            {
                new PlanStepRecord { text = "Clear the table." },
                new PlanStepRecord { text = "Put the mugs on the tray." },
                new PlanStepRecord { text = "" }
            };

            // (3 words + 6 words) / 2 counted steps. The blank step must not count toward the
            // divisor, or a plan padded with empty steps would read as more concise than it is.
            var wps = PlanScoring.WordsPerStep(steps);
            Assert("words: words-per-step averages only non-empty steps",
                   Mathf.Abs(wps - 4.5f) < 1e-4f, $"got {wps}");

            Assert("words: words-per-step is zero for an empty plan",
                   PlanScoring.WordsPerStep(new List<PlanStepRecord>()) == 0f);
        }

        private static void CheckPlanScoringRoomMentions()
        {
            var vocab = new List<string> { "chair by the couch", "kitchen counter" };

            var steps = new List<PlanStepRecord>
            {
                // Matches the full multi-word name.
                new PlanStepRecord { text = "Put the mugs on the kitchen counter." },

                // Matches the LEADING WORD of a multi-word name -- "chair by the couch" is
                // spoken about as "the chair" far more often than by its whole invented name,
                // and that is specifically what the leading-word needle is for.
                new PlanStepRecord { text = "Sit in the chair for a while." },

                // Names nothing in the room at all.
                new PlanStepRecord { text = "Go get comfortable." },

                new PlanStepRecord { text = "Clear the table." }
            };

            Assert("mentions: counts the full-phrase and leading-word matches, not the misses",
                   PlanScoring.RoomMentions(steps, vocab) == 2,
                   $"got {PlanScoring.RoomMentions(steps, vocab)}");

            Assert("mentions: zero against an empty vocabulary",
                   PlanScoring.RoomMentions(steps, new List<string>()) == 0);
        }

        private static void CheckPlanScoringPercentile()
        {
            var values = new List<float> { 30f, 10f, 50f, 20f, 40f };

            // Nearest-rank over {10,20,30,40,50}: p50 is the middle value, p0 and p100 are the
            // ends. Worked out by hand rather than against a stats library, since the whole
            // point of nearest-rank here is that it needs no interpolation to reason about.
            Assert("percentile: p50 is the median",
                   Mathf.Approximately(PlanScoring.Percentile(values, 0.5f), 30f));
            Assert("percentile: p0 is the minimum",
                   Mathf.Approximately(PlanScoring.Percentile(values, 0f), 10f));
            Assert("percentile: p100 is the maximum",
                   Mathf.Approximately(PlanScoring.Percentile(values, 1f), 50f));
            Assert("percentile: p90 of five values",
                   Mathf.Approximately(PlanScoring.Percentile(values, 0.9f), 50f));

            Assert("percentile: does not reorder the caller's list",
                   values[0] == 30f && values[1] == 10f && values[2] == 50f,
                   string.Join(",", values));

            Assert("percentile: empty list is zero", PlanScoring.Percentile(new List<float>(), 0.5f) == 0f);
        }

        private static void CheckPlanScoringPlaceAgreement()
        {
            List<PlanStepRecord> Steps(params string[] wheres)
            {
                var list = new List<PlanStepRecord>();
                foreach (var w in wheres) list.Add(new PlanStepRecord { where = w });
                return list;
            }

            Assert("agreement: two empty ground sets agree completely",
                   PlanScoring.PlaceAgreement(Steps(""), Steps("")) == 1f);

            Assert("agreement: identical sets agree completely, order and duplicates ignored",
                   PlanScoring.PlaceAgreement(Steps("table", "sink"), Steps("sink", "table", "sink")) == 1f);

            Assert("agreement: case-insensitive",
                   PlanScoring.PlaceAgreement(Steps("Table"), Steps("table")) == 1f);

            Assert("agreement: disjoint sets agree not at all",
                   PlanScoring.PlaceAgreement(Steps("table"), Steps("counter")) == 0f);

            // {table,sink} vs {table,counter}: shared=1, union=3 -> 1/3. The one case here that
            // is not 0 or 1, and the one most likely to have an off-by-one in the union term.
            var partial = PlanScoring.PlaceAgreement(Steps("table", "sink"), Steps("table", "counter"));
            Assert("agreement: partial overlap is shared/union",
                   Mathf.Abs(partial - 1f / 3f) < 1e-4f, $"got {partial}");

            var repeats = new List<PlanRecord>
            {
                new PlanRecord { steps = Steps("table") },
                new PlanRecord { steps = Steps("table") },
                new PlanRecord { steps = Steps("counter") }
            };

            // Pairs: (1,2)=1, (1,3)=0, (2,3)=0 -> mean 1/3. Every pair rather than "compare to
            // the first" is what this is checking: comparing only to repeats[0] would also give
            // 1/3 here by coincidence, so the real test is that THREE pairs were considered.
            var mean = PlanScoring.MeanPlaceAgreement(repeats);
            Assert("agreement: mean is over every pair, not just the first",
                   Mathf.Abs(mean - 1f / 3f) < 1e-4f, $"got {mean}");

            Assert("agreement: a single repeat trivially agrees with itself",
                   PlanScoring.MeanPlaceAgreement(new List<PlanRecord> { new PlanRecord() }) == 1f);
            Assert("agreement: null repeats is the same trivial case", PlanScoring.MeanPlaceAgreement(null) == 1f);
        }

        /// <summary>
        /// The rebuild log's own JsonUtility round trip -- the check that matters most for it,
        /// same reasoning as everywhere else in this file. Two entries deliberately at opposite
        /// extremes: one aligned and anchored, one neither, so a bug that only shows up on the
        /// "everything true" or "everything false" path cannot hide in the other entry.
        /// </summary>
        private static void CheckRebuildLogRoundTrip()
        {
            var file = new RoomRebuildLogFile();

            var aligned = new RoomRebuildEntry
            {
                rebuiltUtc = DateTime.UtcNow.ToString("o"),
                scanCapturedUtc = "2026-09-01T10:00:00Z",
                savedOriginAnchorUuid = "anchor-old",
                currentAnchorUuid = "anchor-new",
                anchored = true,
                alignment = new RebuildAlignmentEntry
                {
                    applied = true,
                    yawDegrees = 87.5f,
                    translation = new Vec3(new Vector3(0.4f, 0f, -1.1f)),
                    error = 0.08f,
                    margin = 0.02f,
                    ambiguous = true,
                    summary = "yaw 87.5deg, offset 1.17 m, error 0.08 m over 4 walls"
                }
            };
            aligned.poses.Add(new RebuiltPoseEntry
            {
                id = "obj_004", label = "chair",
                worldPosition = new Vec3(new Vector3(1f, 0.5f, 2f)),
                worldRotation = new Quat(Quaternion.Euler(0f, 45f, 0f))
            });
            file.rebuilds.Add(aligned);

            // The opposite extreme: unanchored, unaligned, no poses at all. Every bool here
            // defaults to false, which is exactly the value JsonUtility would ALSO produce for
            // a field that silently failed to serialise -- so this entry is the one that would
            // stay quiet about a bug the first entry could not catch.
            file.rebuilds.Add(new RoomRebuildEntry
            {
                rebuiltUtc = DateTime.UtcNow.ToString("o"),
                scanCapturedUtc = "2026-09-01T11:00:00Z",
                anchored = false,
                alignment = new RebuildAlignmentEntry { applied = false, summary = "no MRUK room" }
            });

            var json = JsonUtility.ToJson(file, true);
            var back = JsonUtility.FromJson<RoomRebuildLogFile>(json);

            Assert("rebuild: survives a round trip", back != null);
            if (back == null) return;

            Assert("rebuild: both entries kept", back.rebuilds.Count == 2);
            if (back.rebuilds.Count < 2) return;

            Assert("rebuild: scan id kept", back.rebuilds[0].scanCapturedUtc == "2026-09-01T10:00:00Z");
            Assert("rebuild: anchor uuids kept apart",
                   back.rebuilds[0].savedOriginAnchorUuid == "anchor-old" &&
                   back.rebuilds[0].currentAnchorUuid == "anchor-new");

            Assert("rebuild: anchored-true survives", back.rebuilds[0].anchored);
            Assert("rebuild: anchored-false survives", !back.rebuilds[1].anchored);

            Assert("rebuild: alignment applied-true survives", back.rebuilds[0].alignment.applied);
            Assert("rebuild: alignment applied-false survives", !back.rebuilds[1].alignment.applied);
            Assert("rebuild: ambiguous-true survives", back.rebuilds[0].alignment.ambiguous);

            Assert("rebuild: yaw and translation kept",
                   Mathf.Approximately(back.rebuilds[0].alignment.yawDegrees, 87.5f) &&
                   Mathf.Abs(back.rebuilds[0].alignment.translation.ToVector3().z + 1.1f) < 1e-4f);

            Assert("rebuild: poses kept for the entry that has them",
                   back.rebuilds[0].poses.Count == 1 && back.rebuilds[0].poses[0].id == "obj_004");
            Assert("rebuild: pose world rotation kept",
                   Mathf.Abs(back.rebuilds[0].poses[0].worldRotation.ToQuaternion().eulerAngles.y - 45f) < 1e-2f);

            Assert("rebuild: empty pose list survives as empty, not null",
                   back.rebuilds[1].poses != null && back.rebuilds[1].poses.Count == 0);
        }

        private static void CheckRebuildLogDiskRoundTrip()
        {
            var file = new RoomRebuildLogFile();
            file.rebuilds.Add(new RoomRebuildEntry
            {
                rebuiltUtc = DateTime.UtcNow.ToString("o"),
                scanCapturedUtc = "self-check",
                anchored = true,
                alignment = new RebuildAlignmentEntry { applied = true, summary = "self-check" }
            });

            var path = RoomRebuildLogIO.PathForStem("selfcheck_rebuilds");

            try
            {
                RoomRebuildLogIO.Save(file, path);
                Assert("rebuild disk: file written", File.Exists(path), path);

                var back = RoomRebuildLogIO.Load(path);

                Assert("rebuild disk: reads back", back != null);
                Assert("rebuild disk: entry survives",
                       back != null && back.rebuilds.Count == 1 &&
                       back.rebuilds[0].scanCapturedUtc == "self-check");
                Assert("rebuild disk: capturedUtc stamped at write",
                       back != null && !string.IsNullOrEmpty(back.capturedUtc));
            }
            catch (Exception ex)
            {
                Assert("rebuild disk: round trip without throwing", false, ex.Message);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* scratch */ }
            }
        }

        private static void CheckTruthRoundTrip()
        {
            var file = new RoomTruthFile { roomLabel = "R2", anchorUuid = "abc-123" };

            file.objects.Add(new TruthObject
            {
                id = "truth_000",
                label = "chair",
                inVocabulary = true,
                position = new Vec3(new Vector3(1f, 0.45f, 2f)),
                size = new Vec3(new Vector3(0.5f, 0.9f, 0.5f)),
                cornerA = new Vec3(new Vector3(0.75f, 0f, 1.75f)),
                cornerB = new Vec3(new Vector3(1.25f, 0.9f, 2.25f)),
                viaRaycast = false,
                markedUtc = DateTime.UtcNow.ToString("o")
            });

            file.objects.Add(new TruthObject
            {
                id = "truth_001",
                label = RoomTruthMarker.OutOfVocabulary,
                inVocabulary = false,
                position = new Vec3(Vector3.zero),
                size = new Vec3(Vector3.one),
                viaRaycast = true
            });

            var back = JsonUtility.FromJson<RoomTruthFile>(JsonUtility.ToJson(file, true));

            Assert("truth: survives a round trip", back != null);
            if (back == null) return;

            Assert("truth: both objects kept", back.objects.Count == 2);
            Assert("truth: inVocabulary true kept", back.objects[0].inVocabulary);

            // The distinction the whole recall figure rests on. If this ever collapses,
            // objects COCO cannot name get scored as pipeline misses.
            Assert("truth: inVocabulary false kept", back.objects.Count > 1 && !back.objects[1].inVocabulary);
            Assert("truth: viaRaycast kept", back.objects.Count > 1 && back.objects[1].viaRaycast);
            Assert("truth: corners kept",
                   back.objects[0].cornerA != null
                   && Mathf.Abs(back.objects[0].cornerA.x - 0.75f) < 1e-4f);

            // rotation has an initialiser; make sure it does not come back null and
            // NullReference the offline comparison.
            Assert("truth: rotation not null", back.objects[0].rotation != null);
        }

        /// <summary>
        /// Writes a session through the real IO path and reads it back off disk.
        ///
        /// Separate from the in-memory round trip above, and worth its own check: that one
        /// proves the DTOs are shaped right, this one proves the folder gets created, the
        /// filename is legal, and the bytes come back. Those are different failures and only
        /// one of them can be found without touching a filesystem.
        /// </summary>
        private static void CheckDiskRoundTrip()
        {
            var session = new StudySession
            {
                sessionId = StudySessionIO.MakeSessionId("PZZ", "R1", 99, DateTime.UtcNow),
                participantId = "PZZ",
                roomLabel = "R1"
            };

            session.notes.Add(new NoteEntry { t = 0f, kind = "self-check", text = "disk round trip" });

            var path = StudySessionIO.PathFor(session.sessionId);

            try
            {
                StudySessionIO.Save(session);
                Assert("disk: file written", File.Exists(path), path);

                var back = StudySessionIO.Load(path);

                Assert("disk: reads back", back != null);
                Assert("disk: id survives", back != null && back.sessionId == session.sessionId);
                Assert("disk: notes survive", back != null && back.notes.Count == 1);
                Assert("disk: capturedUtc stamped at write",
                       back != null && !string.IsNullOrEmpty(back.capturedUtc));
            }
            catch (Exception ex)
            {
                Assert("disk: round trip without throwing", false, ex.Message);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* scratch */ }
            }
        }

        // -----------------------------------------------------------------
        // Identity
        // -----------------------------------------------------------------

        private static void CheckSessionId()
        {
            var when = new DateTime(2026, 9, 3, 14, 12, 5, DateTimeKind.Utc);
            var id = StudySessionIO.MakeSessionId("P03", "R2", 1, when);

            Assert("sessionId: shape", id == "P03_R2_r01_20260903T141205Z", id);

            // A colon is legal on the Quest's filesystem and illegal on the Windows machine
            // these get pulled to, so a session id that contains one is a file that cannot
            // be copied off the headset.
            var invalid = Path.GetInvalidFileNameChars();
            var clean = id.IndexOfAny(invalid) < 0;
            Assert("sessionId: usable as a filename", clean, id);

            var messy = StudySessionIO.MakeSessionId("P 3/x", "R:2", 12, when);
            Assert("sessionId: sanitises awkward input", messy.IndexOfAny(invalid) < 0, messy);
            Assert("sessionId: run is zero-padded", messy.Contains("_r12_"), messy);
        }

        /// <summary>
        /// Exercises run derivation against the real folder, then cleans up.
        ///
        /// Against the real path on purpose: NextRun reads StudySessionIO.Folder, and a check
        /// that pointed somewhere else would be checking a different function than the one
        /// that runs on device. A participant id no study would use keeps it out of the way.
        /// </summary>
        private static void CheckNextRun()
        {
            const string participant = "PZZ";

            StudySessionIO.EnsureFolder();
            var made = new List<string>();

            try
            {
                var first = StudySessionIO.NextRun(participant);
                Assert("nextRun: starts at 1 for an unseen participant", first == 1, $"got {first}");

                for (var i = 0; i < 2; i++)
                {
                    var path = StudySessionIO.PathFor(
                        StudySessionIO.MakeSessionId(participant, "R1", i + 1,
                                                     DateTime.UtcNow.AddSeconds(i)));
                    File.WriteAllText(path, "{}");
                    made.Add(path);
                }

                var third = StudySessionIO.NextRun(participant);
                Assert("nextRun: counts existing sessions", third == 3, $"got {third}");

                // Scan copies share the stem. Counting them as runs would advance the number
                // by however many times somebody saved, which is not what a run is.
                var decoy = Path.Combine(StudySessionIO.Folder,
                                         $"{participant}_R1_r01_20260101T000000Z.scan.1.json");
                File.WriteAllText(decoy, "{}");
                made.Add(decoy);

                var afterDecoy = StudySessionIO.NextRun(participant);
                Assert("nextRun: ignores scan copies", afterDecoy == 3, $"got {afterDecoy}");
            }
            catch (Exception ex)
            {
                Assert("nextRun: ran without throwing", false, ex.Message);
            }
            finally
            {
                foreach (var path in made)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch { /* a leftover in a scratch folder is not worth failing over */ }
                }
            }
        }

        // -----------------------------------------------------------------
        // Marking maths
        // -----------------------------------------------------------------

        private static void CheckBoxFromCorners()
        {
            var a = new Vector3(1f, 0f, 2f);
            var b = new Vector3(0.5f, 0.9f, 1.5f);

            RoomTruthMarker.BoxFromCorners(a, b, out var center, out var size);

            Assert("box: centre", (center - new Vector3(0.75f, 0.45f, 1.75f)).magnitude < 1e-4f,
                   center.ToString("F3"));
            Assert("box: size", (size - new Vector3(0.5f, 0.9f, 0.5f)).magnitude < 1e-4f,
                   size.ToString("F3"));

            // Marking bottom-left-then-top-right and the reverse are the same measurement.
            RoomTruthMarker.BoxFromCorners(b, a, out var center2, out var size2);
            Assert("box: corner order does not matter",
                   (center - center2).magnitude < 1e-6f && (size - size2).magnitude < 1e-6f);

            Assert("box: size is never negative", size2.x >= 0f && size2.y >= 0f && size2.z >= 0f);
        }

        private static void CheckLabelParsing()
        {
            // Windows line endings, a blank line and trailing space -- all three are in the
            // shipped asset or one edit away from being.
            var labels = RoomTruthMarker.ParseLabels("person\r\nbicycle\r\n\r\n  chair  \r\n");

            Assert("labels: count", labels.Count == 4, string.Join("|", labels));
            Assert("labels: carriage returns stripped", labels.Contains("chair"),
                   string.Join("|", labels));
            Assert("labels: blank lines dropped", !labels.Contains(""));
            Assert("labels: out-of-vocab is last",
                   labels[labels.Count - 1] == RoomTruthMarker.OutOfVocabulary);

            Assert("labels: empty input still offers out-of-vocab",
                   RoomTruthMarker.ParseLabels("").Count == 1);
        }

        /// <summary>
        /// The millimetre quantisation the observation log writes with.
        ///
        /// Checked as a property rather than by calling into the log, which is a MonoBehaviour
        /// and would need a scene. What matters is the guarantee the offline tool relies on:
        /// a metre value survives the trip to integer millimetres and back to within half a
        /// millimetre, two orders below the 0.10 m the position figure is tested against.
        /// </summary>
        private static void CheckQuantisation()
        {
            var worst = 0f;

            foreach (var metres in new[] { 0f, 0.0004f, 1.2345f, -2.7182f, 6f, -6f })
            {
                var back = Mathf.RoundToInt(metres * 1000f) / 1000f;
                worst = Mathf.Max(worst, Mathf.Abs(back - metres));
            }

            // The exact bound is 0.5 mm (half of a 1 mm step), but the multiply/round/divide/
            // subtract chain above is float32 throughout, so the computed worst case can land a
            // few float32 ULPs past the literal 0.0005f (observed: ~8e-8 m, on 1.2345f) even
            // though quantisation itself is correct. FloatSlop absorbs that accumulation without
            // loosening the guarantee being tested -- it is three orders below the bound, not a
            // wider bound.
            const float FloatSlop = 1e-6f;
            Assert("quantisation: within half a millimetre", worst <= 0.0005f + FloatSlop,
                   $"worst {worst:F8} m");
        }

        // -----------------------------------------------------------------
        // The ablation replay
        // -----------------------------------------------------------------

        /// <summary>
        /// Replays two synthetic logs with hand-worked answers.
        ///
        /// This is the check worth having. The replay in ExtentAblationTool reproduces the
        /// scanner's ring buffer, its percentile indexing and Absorb's 8-corner re-framing,
        /// and every one of those can be subtly wrong in a way that still produces plausible
        /// numbers -- which then go into a paper. Both scenarios below have answers that can
        /// be worked out on paper, so "plausible" is not the standard being applied.
        /// </summary>
        private static void CheckObservationReplay()
        {
            CheckReplayTrimsOutliers();
            CheckReplayReframesOnMerge();
            CheckReplayAppliesTheExtentFloor();
        }

        /// <summary>
        /// A laptop-lid-thin object, where ObjectScanRecorder's per-axis floor bites.
        ///
        /// Describe clamps every axis to MinBoxExtent AFTER the estimate, so a replay that
        /// skipped it would report 0.01 m where the app shipped 0.05 m -- and would compare a
        /// floored union against an unfloored percentile, which measures the floor rather than
        /// the estimator. Only thin objects are affected, which is exactly why this needs a
        /// check rather than a reading of the code.
        /// </summary>
        private static void CheckReplayAppliesTheExtentFloor()
        {
            var log = new StringBuilder();
            log.AppendLine("# obs schema 1 openedUtc=2026-09-03T00:00:00Z");
            log.AppendLine("# params percentile=1.00 samples=64 minObs=1");
            log.AppendLine("c,0,1,0,0,0,0,0,0,10000,laptop");

            // 10 mm thick on y -- under the 50 mm floor.
            for (var i = 0; i < 5; i++)
                log.AppendLine($"o,{i * 10},1,90,0,0,0,-250,-5,-250,250,5,250");

            log.AppendLine("x,100,1,0,5,0,0,0,500,50,500,laptop");
            log.AppendLine("# end written=7 dropped=0 closedUtc=2026-09-03T00:01:00Z");

            var exports = Replay(log.ToString(), "floor");
            if (exports == null || exports.Count != 1)
            {
                Assert("replay: thin object exported", false, $"got {exports?.Count ?? -1}");
                return;
            }

            var e = exports[0];

            Assert("replay: extent floor applied to the percentile",
                   Mathf.Abs(e.PercentileSize.y - 0.05f) < 1e-3f, $"y = {e.PercentileSize.y:F4} m");

            Assert("replay: extent floor applied to the union too",
                   Mathf.Abs(e.UnionSize.y - 0.05f) < 1e-3f, $"y = {e.UnionSize.y:F4} m");

            Assert("replay: floored size matches what the app shipped",
                   (e.PercentileSize - e.ShippedSize).magnitude < 1e-3f,
                   $"recomputed {e.PercentileSize:F3} vs shipped {e.ShippedSize:F3}");
        }

        /// <summary>
        /// Nineteen tight observations and one wild one, on the x axis only.
        ///
        /// The percentile band should discard the outlier -- that is the entire reason it
        /// replaced the union -- and the union should keep it. With 20 samples and a 0.8 band
        /// the trim index lands at 2 from the bottom and 17 from the top, so the outlier at
        /// either end falls outside: percentile x is 0.5 m and union x is 2.0 m, exactly.
        /// The untouched y and z axes must come out identical under both, which is what says
        /// the two estimators differ only where they should.
        /// </summary>
        private static void CheckReplayTrimsOutliers()
        {
            var log = new StringBuilder();
            log.AppendLine("# obs schema 1 openedUtc=2026-09-03T00:00:00Z");
            log.AppendLine("# params percentile=0.80 samples=64 minObs=5");
            log.AppendLine("c,0,1,0,0,0,0,0,0,10000,chair");

            for (var i = 0; i < 19; i++)
                log.AppendLine($"o,{i * 10},1,90,0,0,0,-250,-250,-250,250,250,250");

            // The bad depth hit, wide on x only.
            log.AppendLine("o,200,1,90,0,0,0,-1000,-250,-250,1000,250,250");

            log.AppendLine("x,300,1,0,20,0,0,0,500,500,500,chair");
            log.AppendLine("# end written=22 dropped=0 closedUtc=2026-09-03T00:01:00Z");

            var exports = Replay(log.ToString(), "outliers");
            if (exports == null || exports.Count != 1)
            {
                Assert("replay: one exported object", false, $"got {exports?.Count ?? -1}");
                return;
            }

            var e = exports[0];

            Assert("replay: percentile trims the outlier",
                   Mathf.Abs(e.PercentileSize.x - 0.5f) < 1e-3f, $"x = {e.PercentileSize.x:F4} m");

            Assert("replay: union keeps the outlier",
                   Mathf.Abs(e.UnionSize.x - 2.0f) < 1e-3f, $"x = {e.UnionSize.x:F4} m");

            Assert("replay: untouched axes agree",
                   Mathf.Abs(e.PercentileSize.y - e.UnionSize.y) < 1e-3f
                   && Mathf.Abs(e.PercentileSize.z - e.UnionSize.z) < 1e-3f,
                   $"pct {e.PercentileSize:F3} union {e.UnionSize:F3}");

            // The percentile path is the shipped one, so the recomputed value has to agree
            // with what the app wrote. A mismatch means the replay has drifted from
            // ObjectScanRecorder and every number it produces is about a different estimator.
            Assert("replay: matches the size the app shipped",
                   (e.PercentileSize - e.ShippedSize).magnitude < 1e-3f,
                   $"recomputed {e.PercentileSize:F3} vs shipped {e.ShippedSize:F3}");
        }

        /// <summary>
        /// Two clusters a metre apart, the second turned 90 degrees, merged.
        ///
        /// Absorb re-expresses the dropped cluster's boxes in the survivor's frame by moving
        /// their corners, so the survivor's union has to reach from its own -0.25 out to the
        /// far side of the other box at +1.25 -- 1.5 m on x. A replay that forgot the
        /// re-framing, or applied the offset in the wrong direction, lands on 0.5 or 2.5 and
        /// nothing downstream would ever question it.
        ///
        /// A quarter-turn about Y maps axes onto axes, so the expected answer is exact rather
        /// than approximately inflated -- which is what makes it a usable assertion.
        /// </summary>
        private static void CheckReplayReframesOnMerge()
        {
            var log = new StringBuilder();
            log.AppendLine("# obs schema 1 openedUtc=2026-09-03T00:00:00Z");
            log.AppendLine("# params percentile=1.00 samples=64 minObs=1");

            // Cluster 1 at the origin, unrotated.
            log.AppendLine("c,0,1,0,0,0,0,0,0,10000,chair");
            log.AppendLine("o,10,1,90,0,0,0,-250,-250,-250,250,250,250");

            // Cluster 2 a metre along x, turned 90 degrees about Y: (0, sin45, 0, cos45).
            log.AppendLine("c,20,2,1000,0,0,0,7071,0,7071,chair");
            log.AppendLine("o,30,2,90,1000,0,0,-250,-250,-250,250,250,250");

            log.AppendLine("m,40,1,2");
            log.AppendLine("x,50,1,0,2,0,0,0,1500,500,500,chair");
            log.AppendLine("# end written=6 dropped=0 closedUtc=2026-09-03T00:01:00Z");

            var exports = Replay(log.ToString(), "merge");
            if (exports == null || exports.Count != 1)
            {
                Assert("replay: merge produced one object", false, $"got {exports?.Count ?? -1}");
                return;
            }

            var e = exports[0];

            Assert("replay: merge re-frames onto the survivor",
                   Mathf.Abs(e.UnionSize.x - 1.5f) < 1e-2f, $"x = {e.UnionSize.x:F4} m, expected 1.5");

            Assert("replay: merge leaves the other axes alone",
                   Mathf.Abs(e.UnionSize.y - 0.5f) < 1e-2f && Mathf.Abs(e.UnionSize.z - 0.5f) < 1e-2f,
                   $"{e.UnionSize:F3}");

            // The replay counted these itself, adding the dropped cluster's tally to the
            // survivor's. Agreeing with what the export record independently claims is what
            // says the merge was applied to the right cluster.
            Assert("replay: observations are summed across the merge",
                   e.ReplayedObservations == 2, $"replay counted {e.ReplayedObservations}");

            Assert("replay: replayed count agrees with the logged one",
                   e.ReplayedObservations == e.Observations,
                   $"replay {e.ReplayedObservations} vs log {e.Observations}");
        }

        /// <summary>Writes a synthetic log to a scratch file, replays it, and cleans up.</summary>
        private static List<ExtentAblationTool.Export> Replay(string contents, string name)
        {
            StudySessionIO.EnsureFolder();

            var path = Path.Combine(StudySessionIO.Folder, $"PZZ_selfcheck_{name}.obs.csv");

            try
            {
                File.WriteAllText(path, contents);

                var exports = ExtentAblationTool.ReplayLog(path, out _, out _, out _,
                                                           out var truncated);

                Assert($"replay ({name}): end marker seen", !truncated);
                return exports;
            }
            catch (Exception ex)
            {
                Assert($"replay ({name}): ran without throwing", false, ex.Message);
                return null;
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* scratch */ }
            }
        }

        // -----------------------------------------------------------------

        private static void Assert(string what, bool ok, string detail = null)
        {
            if (ok)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[StudySelfCheck] FAILED: {what}" +
                           (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
        }
    }
}
