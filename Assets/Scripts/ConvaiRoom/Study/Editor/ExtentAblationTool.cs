using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ConvaiRoom;
using RoomScan;
using UnityEditor;
using UnityEngine;

namespace ConvaiRoomEditor
{
    /// <summary>
    /// Re-runs the extent estimator offline, two ways, over one recorded scan.
    ///
    /// The shipped scanner measures an object's size by taking per-axis percentiles across a
    /// rolling window of observation boxes. It replaced a monotonic union -- an Encapsulate
    /// that only ever grew, so one bad depth hit was baked into the box forever. The study
    /// claims the percentile band is the better estimator, and a claim like that needs the
    /// other one run on THE SAME observations, not on a different scan of the same room.
    ///
    /// That is only possible because <see cref="RoomScan.ScanObservationLog"/> wrote the
    /// observations down as they arrived. This replays that file.
    ///
    /// FIDELITY. Three things make a naive replay come out wrong, and all three are modelled
    /// here:
    ///
    ///  1. Absorb does not copy a merged cluster's boxes -- it takes each box's 8 CORNERS into
    ///     the survivor's frame and re-fits an axis-aligned box round them. An oriented box's
    ///     AABB in a rotated frame is strictly larger, so merges INFLATE. A replay that simply
    ///     unioned room-local corners would come out smaller than the old code ever did, and
    ///     would flatter the percentile method it is supposed to be a baseline for.
    ///  2. The runtime percentile sees only the last `samples` observations; the union sees
    ///     all of them. That asymmetry is the intervention, not a confound -- but it means the
    ///     ring buffer has to be simulated exactly, overwrite order included.
    ///  3. Merges re-enter the survivor's ring through the same AddObservation, so an absorb
    ///     pushes the survivor's own older samples out.
    ///
    /// So the baseline reported by this tool is precisely: monotonic Encapsulate over every
    /// observation of the surviving cluster, re-framed on merge the way Absorb re-frames.
    /// Say that in the write-up; "union" on its own is ambiguous and the ambiguity is worth
    /// about 30% of the result.
    /// </summary>
    public static class ExtentAblationTool
    {
        private const string Tag = "[ExtentAblation]";

        /// <summary>
        /// One cluster, replayed.
        ///
        /// Two histories, deliberately. <see cref="RingMins"/> is a faithful simulation of the
        /// runtime's bounded window, cursor and all, and produces the percentile estimate.
        /// <see cref="AllMins"/> keeps everything and produces the union baseline. Neither can
        /// be derived from the other.
        /// </summary>
        private class Replay
        {
            public int Id;
            public string Label;
            public Vector3 Origin;
            public Quaternion Rotation;

            public readonly List<Vector3> RingMins = new List<Vector3>();
            public readonly List<Vector3> RingMaxs = new List<Vector3>();
            public int Cursor;

            public readonly List<Vector3> AllMins = new List<Vector3>();
            public readonly List<Vector3> AllMaxs = new List<Vector3>();

            public int Observations;

            public Vector3 ToLocal(Vector3 roomPoint) => Quaternion.Inverse(Rotation) * (roomPoint - Origin);
            public Vector3 ToRoom(Vector3 localPoint) => Origin + Rotation * localPoint;

            /// <summary>
            /// Byte-for-byte the runtime's ObjectScanRecorder.Cluster.AddObservation, including
            /// the cursor arithmetic. Any divergence here silently changes which samples the
            /// percentile is taken over.
            /// </summary>
            public void AddToRing(Vector3 min, Vector3 max, int capacity)
            {
                capacity = Mathf.Max(1, capacity);

                while (RingMins.Count > capacity)
                {
                    RingMins.RemoveAt(RingMins.Count - 1);
                    RingMaxs.RemoveAt(RingMaxs.Count - 1);
                }

                if (RingMins.Count < capacity)
                {
                    RingMins.Add(min);
                    RingMaxs.Add(max);
                    Cursor = RingMins.Count % capacity;
                }
                else
                {
                    RingMins[Cursor] = min;
                    RingMaxs[Cursor] = max;
                    Cursor = (Cursor + 1) % capacity;
                }
            }
        }

        /// <summary>
        /// One exported object, with the size the app shipped and the two sizes this tool
        /// recomputed. Public so <see cref="StudySelfCheck"/> can assert on a replay of a
        /// synthetic log -- the merge re-framing below is subtle enough that a silent
        /// regression in it would change a published number.
        /// </summary>
        public struct Export
        {
            public int ClusterId;
            public int Index;
            public string Label;

            /// <summary>What the export record says the cluster had seen.</summary>
            public int Observations;

            /// <summary>
            /// What the replay actually counted, merges included.
            ///
            /// Kept separately from <see cref="Observations"/> as an integrity check: the two
            /// disagreeing means the log lost records, or the replay applied a merge the
            /// runtime did not. Either way the extents from that object are not trustworthy,
            /// and a silent discrepancy is exactly what would not get noticed.
            /// </summary>
            public int ReplayedObservations;

            public Vector3 RoomPosition;

            /// <summary>What the app actually wrote into room_scan.json.</summary>
            public Vector3 ShippedSize;

            /// <summary>Recomputed from the log. Should match <see cref="ShippedSize"/>.</summary>
            public Vector3 PercentileSize;

            /// <summary>The baseline: monotonic union over every observation.</summary>
            public Vector3 UnionSize;
        }

        // -----------------------------------------------------------------

        [MenuItem("Tools/Convai Room/Extent Ablation (pick an .obs.csv)")]
        public static void Run()
        {
            var start = Directory.Exists(StudySessionIO.Folder)
                ? StudySessionIO.Folder
                : Application.persistentDataPath;

            var path = EditorUtility.OpenFilePanel("Pick an observation log", start, "csv");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                Process(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} Failed on {path}: {ex}");
            }
        }

        private static void Process(string path)
        {
            var exports = ReplayLog(path, out var observations, out var merges,
                                    out var clusters, out var truncated);

            if (truncated)
                Debug.LogWarning($"{Tag} {Path.GetFileName(path)} has no end marker -- it was " +
                                 $"cut off mid-session. Later clusters may have no export " +
                                 $"record, and those objects cannot be scored.");

            if (exports.Count == 0)
            {
                Debug.LogWarning($"{Tag} No export records in {Path.GetFileName(path)}. Nothing " +
                                 $"was ever saved during this scan, so there is nothing to " +
                                 $"compare. {clusters} clusters, {observations} observations.");
                return;
            }

            var truth = TryLoadTruth(path);
            var outPath = System.IO.Path.ChangeExtension(path, null) + ".extents.csv";

            Write(outPath, exports, truth, LastCapacity, LastPercentile);
            Report(exports, observations, merges, clusters, outPath);

            EditorUtility.RevealInFinder(outPath);
        }

        /// <summary>Settings read from the last log's header, for the output's own header.</summary>
        private static int LastCapacity = 64;
        private static float LastPercentile = 0.8f;

        /// <summary>
        /// Parses one observation log and replays it, returning what was exported.
        ///
        /// Separated from <see cref="Process"/> so it can be exercised on a synthetic log
        /// without a file dialog or an output file. This is where every fidelity decision in
        /// the class comment actually lives.
        /// </summary>
        public static List<Export> ReplayLog(string path, out int observations, out int merges,
                                             out int clusterCount, out bool truncated)
        {
            var capacity = 64;
            var percentile = 0.8f;

            truncated = true;
            observations = 0;
            merges = 0;

            var clusters = new Dictionary<int, Replay>();
            var exports = new List<Export>();

            foreach (var raw in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (raw[0] == '#')
                {
                    ReadHeader(raw, ref capacity, ref percentile);

                    // The writer's last act is this line. Without it the log was cut off, and
                    // its final clusters may never have reached an export record.
                    if (raw.StartsWith("# end", StringComparison.Ordinal))
                    {
                        truncated = false;
                        WarnIfDropped(raw);
                    }

                    continue;
                }

                var f = raw.Split(',');
                if (f.Length < 3) continue;

                switch (f[0])
                {
                    case "c": ReadCluster(f, clusters); break;
                    case "o": ReadObservation(f, clusters, capacity, ref observations); break;
                    case "m": ReadMerge(f, clusters, capacity, ref merges); break;
                    case "x": ReadExport(f, clusters, exports, percentile, capacity); break;
                }
            }

            LastCapacity = capacity;
            LastPercentile = percentile;
            clusterCount = clusters.Count;

            return exports;
        }

        // -----------------------------------------------------------------
        // Replay
        // -----------------------------------------------------------------

        private static void ReadHeader(string line, ref int capacity, ref float percentile)
        {
            if (!line.StartsWith("# params", StringComparison.Ordinal)) return;

            foreach (var token in line.Split(' '))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0) continue;

                var key = token.Substring(0, eq);
                var value = token.Substring(eq + 1);

                if (key == "samples" && int.TryParse(value, out var s)) capacity = s;
                else if (key == "percentile" &&
                         float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    percentile = p;
            }
        }

        private static void WarnIfDropped(string endLine)
        {
            foreach (var token in endLine.Split(' '))
            {
                if (!token.StartsWith("dropped=", StringComparison.Ordinal)) continue;
                if (!int.TryParse(token.Substring(8), out var dropped) || dropped <= 0) return;

                Debug.LogError($"{Tag} This log dropped {dropped} records because the drain " +
                               $"could not keep up. The observation stream has holes in it, so " +
                               $"the extent ablation from this scan is NOT valid. Re-run it.");
            }
        }

        private static void ReadCluster(string[] f, Dictionary<int, Replay> clusters)
        {
            if (f.Length < 11) return;

            var id = I(f[2]);

            clusters[id] = new Replay
            {
                Id = id,
                Origin = new Vector3(M(f[3]), M(f[4]), M(f[5])),
                Rotation = new Quaternion(Q(f[6]), Q(f[7]), Q(f[8]), Q(f[9])),
                Label = f[10]
            };
        }

        private static void ReadObservation(string[] f, Dictionary<int, Replay> clusters,
                                            int capacity, ref int count)
        {
            if (f.Length < 13) return;
            if (!clusters.TryGetValue(I(f[2]), out var c)) return;

            var min = new Vector3(M(f[7]), M(f[8]), M(f[9]));
            var max = new Vector3(M(f[10]), M(f[11]), M(f[12]));

            c.AddToRing(min, max, capacity);

            c.AllMins.Add(min);
            c.AllMaxs.Add(max);

            c.Observations++;
            count++;
        }

        /// <summary>
        /// Applies a merge the way Absorb does.
        ///
        /// The 8-corner re-framing is the whole point. Absorb re-expresses each of the dropped
        /// cluster's boxes in the survivor's frame by transforming its corners and re-fitting
        /// an axis-aligned box, which is strictly larger than the original whenever the two
        /// frames differ. Skipping that step is the single easiest way to produce a union
        /// baseline that is smaller than the code it is standing in for.
        /// </summary>
        private static void ReadMerge(string[] f, Dictionary<int, Replay> clusters,
                                      int capacity, ref int merges)
        {
            if (f.Length < 4) return;

            var keepId = I(f[2]);
            var dropId = I(f[3]);

            if (!clusters.TryGetValue(keepId, out var keep)) return;
            if (!clusters.TryGetValue(dropId, out var drop)) return;

            // The ring, exactly as the runtime does it: only the boxes still in the dropped
            // cluster's window, re-entered through AddObservation so they push the survivor's
            // own older samples out.
            for (var i = 0; i < drop.RingMins.Count; i++)
            {
                Reframe(drop, keep, drop.RingMins[i], drop.RingMaxs[i], out var min, out var max);
                keep.AddToRing(min, max, capacity);
            }

            // The union baseline: every observation, not just the windowed ones, re-framed the
            // same way.
            for (var i = 0; i < drop.AllMins.Count; i++)
            {
                Reframe(drop, keep, drop.AllMins[i], drop.AllMaxs[i], out var min, out var max);
                keep.AllMins.Add(min);
                keep.AllMaxs.Add(max);
            }

            keep.Observations += drop.Observations;

            clusters.Remove(dropId);
            merges++;
        }

        private static void Reframe(Replay from, Replay to, Vector3 localMin, Vector3 localMax,
                                    out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (var corner = 0; corner < 8; corner++)
            {
                var local = new Vector3(
                    (corner & 1) == 0 ? localMin.x : localMax.x,
                    (corner & 2) == 0 ? localMin.y : localMax.y,
                    (corner & 4) == 0 ? localMin.z : localMax.z);

                var moved = to.ToLocal(from.ToRoom(local));

                min = Vector3.Min(min, moved);
                max = Vector3.Max(max, moved);
            }
        }

        private static void ReadExport(string[] f, Dictionary<int, Replay> clusters,
                                       List<Export> exports, float percentile, int capacity)
        {
            if (f.Length < 12) return;
            if (!clusters.TryGetValue(I(f[2]), out var c)) return;

            exports.Add(new Export
            {
                ClusterId = c.Id,
                Index = I(f[3]),
                Label = f[11],
                Observations = I(f[4]),
                ReplayedObservations = c.Observations,
                RoomPosition = new Vector3(M(f[5]), M(f[6]), M(f[7])),
                ShippedSize = new Vector3(M(f[8]), M(f[9]), M(f[10])),
                PercentileSize = Percentile(c, percentile),
                UnionSize = Union(c)
            });
        }

        // -----------------------------------------------------------------
        // The two estimators
        // -----------------------------------------------------------------

        /// <summary>Reproduces ObjectScanRecorder.LocalBoundsOf over the simulated ring.</summary>
        private static Vector3 Percentile(Replay c, float percentile)
        {
            if (c.RingMins.Count == 0) return Vector3.zero;

            var size = Vector3.zero;
            var trim = Mathf.Clamp01(1f - Mathf.Clamp(percentile, 0.2f, 1f)) * 0.5f;

            for (var axis = 0; axis < 3; axis++)
            {
                var lower = PercentileOf(c.RingMins, axis, trim);
                var upper = PercentileOf(c.RingMaxs, axis, 1f - trim);

                size[axis] = Mathf.Max(0f, upper - lower);
            }

            return Floor(size);
        }

        /// <summary>
        /// Applies the same per-axis floor Describe applies after the estimate.
        ///
        /// Both estimators get it, and they have to. The shipped size is floored, so a
        /// percentile recomputed without it would not match what the app wrote -- and a union
        /// compared against a floored percentile would be measuring the floor rather than the
        /// estimator. It only bites on thin objects, which is precisely where the discrepancy
        /// would be least likely to be noticed and most likely to matter.
        /// </summary>
        private static Vector3 Floor(Vector3 size) => new Vector3(
            Mathf.Max(size.x, ObjectScanRecorder.MinBoxExtent),
            Mathf.Max(size.y, ObjectScanRecorder.MinBoxExtent),
            Mathf.Max(size.z, ObjectScanRecorder.MinBoxExtent));

        private static float PercentileOf(List<Vector3> samples, int axis, float t)
        {
            if (samples.Count == 0) return 0f;

            var values = new List<float>(samples.Count);
            foreach (var s in samples) values.Add(s[axis]);
            values.Sort();

            var index = Mathf.Clamp(Mathf.RoundToInt(t * (values.Count - 1)), 0, values.Count - 1);
            return values[index];
        }

        /// <summary>The monotonic Encapsulate, over everything.</summary>
        private static Vector3 Union(Replay c)
        {
            if (c.AllMins.Count == 0) return Vector3.zero;

            var min = c.AllMins[0];
            var max = c.AllMaxs[0];

            for (var i = 1; i < c.AllMins.Count; i++)
            {
                min = Vector3.Min(min, c.AllMins[i]);
                max = Vector3.Max(max, c.AllMaxs[i]);
            }

            return Floor(max - min);
        }

        // -----------------------------------------------------------------
        // Truth join
        // -----------------------------------------------------------------

        /// <summary>
        /// Loads the room's ground truth, if a session file beside this log names the room.
        ///
        /// Best effort. The comparison of the two estimators against each other stands on its
        /// own; the truth join is what turns it into an error rather than a difference, and a
        /// scan taken before the room was ever measured simply does not have one yet.
        /// </summary>
        private static RoomTruthFile TryLoadTruth(string logPath)
        {
            var stem = Path.GetFileName(logPath);
            var dot = stem.IndexOf(".obs.csv", StringComparison.Ordinal);
            if (dot <= 0) return null;

            var sessionPath = Path.Combine(Path.GetDirectoryName(logPath) ?? "",
                                           stem.Substring(0, dot) + ".json");

            var session = File.Exists(sessionPath) ? StudySessionIO.Load(sessionPath) : null;
            if (session == null || string.IsNullOrEmpty(session.roomLabel)) return null;

            var truth = RoomTruthIO.LoadOrCreate(session.roomLabel);
            return truth.objects.Count > 0 ? truth : null;
        }

        /// <summary>
        /// The nearest truth object of the same label, or null.
        ///
        /// Label-matched first and distance-limited second, because the alternative -- nearest
        /// of anything -- pairs a missed chair with the couch beside it and reports a plausible
        /// small error for an object that was never found. An unmatched export is left blank
        /// rather than guessed at.
        /// </summary>
        private static TruthObject NearestTruth(RoomTruthFile truth, string label, Vector3 position)
        {
            if (truth == null) return null;

            TruthObject best = null;
            var bestDistance = 1f;   // metres; beyond this it is a different object

            foreach (var candidate in truth.objects)
            {
                if (!string.Equals(candidate.label, label, StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate.position == null) continue;

                var distance = Vector3.Distance(candidate.position.ToVector3(), position);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = candidate;
            }

            return best;
        }

        // -----------------------------------------------------------------
        // Output
        // -----------------------------------------------------------------

        private static void Write(string path, List<Export> exports, RoomTruthFile truth,
                                  int capacity, float percentile)
        {
            var sb = new StringBuilder();

            sb.Append("# extent ablation  samples=").Append(capacity)
              .Append(" percentile=").Append(percentile.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" truth=").Append(truth != null ? truth.roomLabel : "none").AppendLine();

            sb.AppendLine("objId,clusterId,label,observations,replayedObservations," +
                          "shippedX,shippedY,shippedZ," +
                          "percentileX,percentileY,percentileZ," +
                          "unionX,unionY,unionZ," +
                          "truthId,truthX,truthY,truthZ," +
                          "percentileErrPctX,percentileErrPctY,percentileErrPctZ," +
                          "unionErrPctX,unionErrPctY,unionErrPctZ");

            foreach (var e in exports)
            {
                var match = NearestTruth(truth, e.Label, e.RoomPosition);

                sb.Append($"obj_{e.Index:D3},").Append(e.ClusterId).Append(',')
                  .Append(e.Label).Append(',').Append(e.Observations).Append(',')
                  .Append(e.ReplayedObservations).Append(',')
                  .Append(V(e.ShippedSize)).Append(',')
                  .Append(V(e.PercentileSize)).Append(',')
                  .Append(V(e.UnionSize)).Append(',');

                if (match == null || match.size == null)
                {
                    sb.AppendLine(",,,,,,,,,");
                    continue;
                }

                var trueSize = match.size.ToVector3();

                sb.Append(match.id).Append(',').Append(V(trueSize)).Append(',')
                  .Append(ErrorPct(e.PercentileSize, trueSize)).Append(',')
                  .Append(ErrorPct(e.UnionSize, trueSize))
                  .AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>Per-axis signed error as a percentage of the true extent.</summary>
        private static string ErrorPct(Vector3 estimate, Vector3 truth)
        {
            var sb = new StringBuilder();

            for (var axis = 0; axis < 3; axis++)
            {
                if (axis > 0) sb.Append(',');

                // A true extent of zero cannot be a denominator. It should not happen -- the
                // marker refuses a box with no size -- but a blank is honest and a division
                // by zero would poison the column.
                if (Mathf.Abs(truth[axis]) < 1e-4f) continue;

                var pct = (estimate[axis] - truth[axis]) / truth[axis] * 100f;
                sb.Append(pct.ToString("F2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static void Report(List<Export> exports, int observations, int merges,
                                   int clusters, string outPath)
        {
            // The headline, as a median rather than a mean: one cluster that never settled can
            // drag a mean anywhere, and the protocol reports medians for the position figure
            // for the same reason.
            var ratios = new List<float>();

            foreach (var e in exports)
                for (var axis = 0; axis < 3; axis++)
                    if (e.UnionSize[axis] > 1e-4f)
                        ratios.Add(e.PercentileSize[axis] / e.UnionSize[axis]);

            ratios.Sort();

            var median = ratios.Count > 0 ? ratios[ratios.Count / 2] : 0f;

            Debug.Log($"{Tag} {exports.Count} exported objects from {observations} observations " +
                      $"({clusters} clusters live at the end, {merges} merges).\n" +
                      $"Median percentile extent is {median:P0} of the union's on the same axis " +
                      $"-- i.e. the union runs {(median > 0f ? 1f / median : 0f):F2}x larger.\n" +
                      $"Per-object rows, and the error against ground truth where it exists: {outPath}");
        }

        // -----------------------------------------------------------------

        private static string V(Vector3 v) =>
            $"{v.x.ToString("F4", CultureInfo.InvariantCulture)}," +
            $"{v.y.ToString("F4", CultureInfo.InvariantCulture)}," +
            $"{v.z.ToString("F4", CultureInfo.InvariantCulture)}";

        private static int I(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        /// <summary>Millimetres back to metres.</summary>
        private static float M(string s) => I(s) / 1000f;

        /// <summary>Quaternion components back from their x10000 integers.</summary>
        private static float Q(string s) => I(s) / 10000f;
    }
}
