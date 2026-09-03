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

            Assert("quantisation: within half a millimetre", worst <= 0.0005f, $"worst {worst:F6} m");
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
