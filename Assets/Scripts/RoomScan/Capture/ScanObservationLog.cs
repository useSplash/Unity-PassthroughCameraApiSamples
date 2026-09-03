using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// Writes down every detection as it arrives, so the extent estimator can be re-run
    /// offline against a different one.
    ///
    /// The scanner keeps no evidence. <see cref="ObjectScanRecorder"/> folds each detection
    /// into a centroid sum, a confidence maximum and a rolling 64-entry ring of cluster-local
    /// boxes, and everything else about that detection is gone the moment the method returns.
    /// That is the right call for a scanner -- it is what stops one bad depth hit being baked
    /// into a box forever -- but it means the percentile band cannot be compared against the
    /// monotonic union it replaced, because by the time you want to compare them the
    /// observations no longer exist. This log is the only way that comparison can be made.
    ///
    /// Four kinds of record, in one strictly time-ordered file:
    ///
    ///   c  a cluster was created, with the frame its extents are measured in
    ///   o  an observation was folded into a cluster
    ///   m  two clusters were merged
    ///   x  a cluster was exported, under the id the scan file gave it
    ///
    /// The x record is not optional. BuildScanFile numbers its output obj_000, obj_001 and
    /// discards the cluster id, so without x there is no way back from an exported row to
    /// the observations that produced it, and the whole log is unjoinable.
    ///
    /// OFF unless deliberately armed. See <see cref="ObjectScanRecorder.recordObservations"/>
    /// -- and note the advice there: do not arm it during a participant session. The ablation
    /// needs scans, not participants.
    /// </summary>
    public class ScanObservationLog : MonoBehaviour
    {
        private const string Tag = "[ObsLog]";

        /// <summary>Bumped when the column layout below changes. Written into the header.</summary>
        public const int SchemaVersion = 1;

        [Header("Buffering")]
        [Tooltip("Records held between drains. Detections arrive faster than the disk wants " +
                 "to be written to, so they queue here and are formatted in LateUpdate.\n\n" +
                 "At ~150 detections a second and 72 fps this only ever holds a handful; the " +
                 "size is headroom for a burst, not a working set.")]
        public int capacity = 4096;

        [Tooltip("Stop after this many records. A six-minute scan writes roughly 60,000, so " +
                 "the default is several scans of headroom -- it exists so a switch left on " +
                 "overnight cannot fill the headset, not to bound a session.")]
        public int maxRecords = 400000;

        /// <summary>
        /// One queued record.
        ///
        /// A single struct with a kind byte and ten integer slots rather than four record
        /// types, so all four kinds share one queue and therefore stay in STRICT TIME ORDER.
        /// The offline replay depends on that: a merge has to be applied to exactly the
        /// observations that preceded it, and two queues would have to be re-interleaved on
        /// a timestamp that is only millisecond-resolution.
        ///
        /// Every field is an integer because formatting a float allocates under Mono, and
        /// this queue is filled from inside the detection callback. The string is a reference
        /// to a label that already exists, so it allocates nothing either.
        /// </summary>
        private struct Record
        {
            public byte Kind;
            public int TimeMs;
            public int A, B;
            public int I0, I1, I2, I3, I4, I5, I6, I7, I8, I9;
            public string Text;
        }

        private Record[] _ring;
        private int _head;
        private int _tail;
        private int _count;

        private StreamWriter _writer;
        private readonly StringBuilder _line = new StringBuilder(160);
        private float _openedAt;

        /// <summary>Whether a file is open and records are being kept.</summary>
        public bool IsOpen => _writer != null;

        /// <summary>Records written to disk so far.</summary>
        public int Written { get; private set; }

        /// <summary>
        /// Records thrown away because the queue was full when they arrived.
        ///
        /// Non-zero disqualifies this scan's extent ablation, and that is why it is counted
        /// rather than merely avoided: the alternative to dropping is blocking the detection
        /// path on a disk write, which would corrupt the very timings the log exists to
        /// support. Better to lose a record and say so.
        /// </summary>
        public int Dropped { get; private set; }

        public string Path { get; private set; }

        /// <summary>
        /// Where a log with this name goes: alongside the study's other output.
        ///
        /// The folder name is repeated from StudySessionIO rather than referenced, and that
        /// is deliberate -- RoomScan is the lower layer and nothing in it should have to know
        /// that ConvaiRoom exists. One duplicated string literal is the cheaper of the two
        /// prices.
        /// </summary>
        public static string PathForStem(string stem) =>
            System.IO.Path.Combine(Application.persistentDataPath, "study", stem + ".obs.csv");

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        /// <summary>
        /// Opens a log and writes its header. Closes any log already open.
        ///
        /// <paramref name="parameters"/> is the recorder's settings as one line. The
        /// comparison is not reproducible without them -- the runtime percentile sees only
        /// the last extentSampleCount observations while an offline union sees all of them,
        /// and that asymmetry IS the intervention, so the numbers have to travel with the
        /// data.
        /// </summary>
        public void Open(string path, string parameters)
        {
            Close();

            try
            {
                var folder = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                // A large buffer with AutoFlush off, so the actual write() syscall happens
                // every few seconds rather than every line.
                _writer = new StreamWriter(path, append: false, Encoding.ASCII, 1 << 16)
                {
                    AutoFlush = false
                };
            }
            catch (Exception ex)
            {
                _writer = null;
                Debug.LogError($"{Tag} Could not open {path}: {ex.Message}");
                return;
            }

            Path = path;
            _openedAt = Time.realtimeSinceStartup;
            Written = 0;
            Dropped = 0;

            _ring = new Record[Mathf.Max(64, capacity)];
            _head = _tail = _count = 0;

            _writer.WriteLine($"# obs schema {SchemaVersion} openedUtc={DateTime.UtcNow:o}");
            _writer.WriteLine($"# params {parameters}");
            _writer.WriteLine("# units: positions mm, rotations x10000, confidence percent, time ms");
            _writer.WriteLine("# c,tMs,clusterId,originX,originY,originZ,rotX,rotY,rotZ,rotW,label");
            _writer.WriteLine("# o,tMs,clusterId,confPct,cX,cY,cZ,minX,minY,minZ,maxX,maxY,maxZ");
            _writer.WriteLine("# m,tMs,keepId,dropId");
            _writer.WriteLine("# x,tMs,clusterId,exportIndex,observations,pX,pY,pZ,sX,sY,sZ,label");

            Debug.Log($"{Tag} Recording observations -> {path}");
        }

        /// <summary>Drains what is queued, writes the trailer and closes. Safe when not open.</summary>
        public void Close()
        {
            if (_writer == null) return;

            Drain();

            try
            {
                // The trailer is what makes a truncated file detectable. A log that ends
                // without it was cut off mid-session, and its last cluster may be missing
                // the export record that joins it to the scan.
                _writer.WriteLine($"# end written={Written} dropped={Dropped} " +
                                  $"closedUtc={DateTime.UtcNow:o}");
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Trouble closing {Path}: {ex.Message}");
            }

            Debug.Log($"{Tag} Closed {Path} -- {Written} records, {Dropped} dropped.");
            _writer = null;
        }

        private void OnDisable() => Close();

        /// <summary>
        /// Flushes when the headset comes off.
        ///
        /// Taking a Quest off trips the proximity sensor and pauses the app, and that -- not
        /// a clean quit -- is how a session usually ends. Without this the last buffered
        /// records never reach the file.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (!paused || _writer == null) return;

            Drain();
            try { _writer.Flush(); }
            catch (Exception ex) { Debug.LogWarning($"{Tag} Flush on pause failed: {ex.Message}"); }
        }

        // -----------------------------------------------------------------
        // Recording. All of these run inside the detection callback.
        // -----------------------------------------------------------------

        public void RecordCluster(int clusterId, string label, Vector3 origin, Quaternion rotation)
        {
            if (!TryReserve(out var i)) return;

            ref var r = ref _ring[i];
            r.Kind = (byte)'c';
            r.TimeMs = NowMs();
            r.A = clusterId;
            r.I0 = Mm(origin.x); r.I1 = Mm(origin.y); r.I2 = Mm(origin.z);
            r.I3 = Q(rotation.x); r.I4 = Q(rotation.y); r.I5 = Q(rotation.z); r.I6 = Q(rotation.w);
            r.Text = label;
        }

        public void RecordObservation(int clusterId, float confidence, Vector3 roomCenter,
                                      Vector3 localMin, Vector3 localMax)
        {
            if (!TryReserve(out var i)) return;

            ref var r = ref _ring[i];
            r.Kind = (byte)'o';
            r.TimeMs = NowMs();
            r.A = clusterId;
            r.I0 = Mathf.Clamp(Mathf.RoundToInt(confidence * 100f), 0, 100);
            r.I1 = Mm(roomCenter.x); r.I2 = Mm(roomCenter.y); r.I3 = Mm(roomCenter.z);
            r.I4 = Mm(localMin.x); r.I5 = Mm(localMin.y); r.I6 = Mm(localMin.z);
            r.I7 = Mm(localMax.x); r.I8 = Mm(localMax.y); r.I9 = Mm(localMax.z);
            r.Text = null;
        }

        public void RecordMerge(int keepId, int dropId)
        {
            if (!TryReserve(out var i)) return;

            ref var r = ref _ring[i];
            r.Kind = (byte)'m';
            r.TimeMs = NowMs();
            r.A = keepId;
            r.B = dropId;
            r.Text = null;
        }

        public void RecordExport(int clusterId, int exportIndex, string label, int observations,
                                 Vector3 roomPosition, Vector3 size)
        {
            if (!TryReserve(out var i)) return;

            ref var r = ref _ring[i];
            r.Kind = (byte)'x';
            r.TimeMs = NowMs();
            r.A = clusterId;
            r.B = exportIndex;
            r.I0 = observations;
            r.I1 = Mm(roomPosition.x); r.I2 = Mm(roomPosition.y); r.I3 = Mm(roomPosition.z);
            r.I4 = Mm(size.x); r.I5 = Mm(size.y); r.I6 = Mm(size.z);
            r.Text = label;
        }

        /// <summary>
        /// Claims the next queue slot, or reports that there is none.
        ///
        /// The only thing that happens on the hot path: one bounds check and two integer
        /// increments. No formatting, no allocation, no IO -- all of that waits for
        /// <see cref="Drain"/>, because a disk stall inside the detection callback would
        /// drop detections and corrupt the accuracy measurement this log exists to serve.
        /// </summary>
        private bool TryReserve(out int index)
        {
            index = -1;

            if (_writer == null) return false;

            if (Written + _count >= maxRecords)
            {
                // Said once. A cap hit mid-scan is worth knowing about, but a warning per
                // detection would be a hundred lines a second.
                if (Dropped == 0)
                    Debug.LogWarning($"{Tag} Hit the {maxRecords}-record cap; no longer recording.");

                Dropped++;
                return false;
            }

            if (_count >= _ring.Length)
            {
                Dropped++;
                return false;
            }

            index = _head;
            _head = (_head + 1) % _ring.Length;
            _count++;
            return true;
        }

        // -----------------------------------------------------------------
        // Draining
        // -----------------------------------------------------------------

        private void LateUpdate()
        {
            if (_writer != null && _count > 0) Drain();
        }

        /// <summary>Formats everything queued and hands it to the writer's buffer.</summary>
        private void Drain()
        {
            if (_writer == null) return;

            try
            {
                while (_count > 0)
                {
                    ref var r = ref _ring[_tail];
                    _tail = (_tail + 1) % _ring.Length;
                    _count--;

                    _line.Clear();
                    Format(ref r, _line);
                    _writer.WriteLine(_line.ToString());

                    Written++;
                }
            }
            catch (Exception ex)
            {
                // Stop rather than complain every frame. A log that failed to write is a log
                // whose ablation is void, and the trailer's absence will say so.
                Debug.LogError($"{Tag} Write failed, closing the log: {ex.Message}");

                try { _writer.Dispose(); } catch { /* already broken */ }
                _writer = null;
            }
        }

        private static void Format(ref Record r, StringBuilder line)
        {
            line.Append((char)r.Kind).Append(',').Append(r.TimeMs).Append(',').Append(r.A);

            switch (r.Kind)
            {
                case (byte)'c':
                    line.Append(',').Append(r.I0).Append(',').Append(r.I1).Append(',').Append(r.I2)
                        .Append(',').Append(r.I3).Append(',').Append(r.I4).Append(',').Append(r.I5)
                        .Append(',').Append(r.I6).Append(',').Append(Safe(r.Text));
                    break;

                case (byte)'o':
                    line.Append(',').Append(r.I0)
                        .Append(',').Append(r.I1).Append(',').Append(r.I2).Append(',').Append(r.I3)
                        .Append(',').Append(r.I4).Append(',').Append(r.I5).Append(',').Append(r.I6)
                        .Append(',').Append(r.I7).Append(',').Append(r.I8).Append(',').Append(r.I9);
                    break;

                case (byte)'m':
                    line.Append(',').Append(r.B);
                    break;

                case (byte)'x':
                    line.Append(',').Append(r.B).Append(',').Append(r.I0)
                        .Append(',').Append(r.I1).Append(',').Append(r.I2).Append(',').Append(r.I3)
                        .Append(',').Append(r.I4).Append(',').Append(r.I5).Append(',').Append(r.I6)
                        .Append(',').Append(Safe(r.Text));
                    break;
            }
        }

        /// <summary>
        /// COCO labels contain spaces but never commas, so this is belt and braces -- a
        /// comma in a label would silently shift every column after it.
        /// </summary>
        private static string Safe(string text) =>
            string.IsNullOrEmpty(text) ? "" : text.Replace(',', ' ');

        private int NowMs() => Mathf.RoundToInt((Time.realtimeSinceStartup - _openedAt) * 1000f);

        /// <summary>
        /// Metres to millimetres. 1 mm sits an order of magnitude below the depth sensor's
        /// own noise and two below the 0.10 m the position figure is tested against, so the
        /// quantisation is free -- and integers keep the formatter allocation-free.
        /// </summary>
        private static int Mm(float metres) => Mathf.RoundToInt(metres * 1000f);

        private static int Q(float unit) => Mathf.RoundToInt(unit * 10000f);
    }
}
