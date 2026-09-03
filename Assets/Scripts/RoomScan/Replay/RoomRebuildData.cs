using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RoomScan
{
    // ---------------------------------------------------------------------
    // What RoomRebuildLog leaves behind. Same rules as RoomScanData: plain
    // [Serializable] classes, public fields only, no dictionaries, no
    // properties -- JsonUtility fails SILENTLY on any of those.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The wall fit applied at one rebuild, copied field-for-field from
    /// <see cref="RoomAlignment"/>.
    ///
    /// All of it, not just the four the study asked for. It is one small struct already
    /// computed at the point this is written, and a partial copy would be an arbitrary line to
    /// draw -- <see cref="Applied"/> and <see cref="Translation"/> are exactly as load-bearing
    /// for deciding whether a rebuild's poses can be trusted as <c>Error</c> and <c>Margin</c>
    /// are.
    /// </summary>
    [Serializable]
    public class RebuildAlignmentEntry
    {
        public bool applied;
        public float yawDegrees;
        public Vec3 translation = new Vec3();
        public float error;
        public float margin;
        public bool ambiguous;
        public string summary = "";
    }

    /// <summary>
    /// Where one scanned object actually ended up, in world space, at one rebuild.
    ///
    /// Read off the spawned proxy's own transform rather than recomputed from the scan file and
    /// the alignment -- <see cref="RoomScanRebuilder.SpawnBox"/> is the only place that decides
    /// where a box really goes, and asking the proxy is asking the thing that happened rather
    /// than re-deriving it a second way that could quietly drift from the first if that method
    /// ever changes.
    /// </summary>
    [Serializable]
    public class RebuiltPoseEntry
    {
        public string id = "";
        public string label = "";
        public Vec3 worldPosition = new Vec3();
        public Quat worldRotation = new Quat();
    }

    /// <summary>
    /// One call to <see cref="RoomScanRebuilder.Rebuild"/>, with what it decided and where
    /// everything landed.
    ///
    /// <see cref="scanCapturedUtc"/> is "the scan id" the study handoff asks for. There is no
    /// dedicated id field on a scan file -- <see cref="RoomScanFile.capturedUtc"/> is the
    /// closest thing to one, and <c>ReferenceTrialRunner.ScanId</c> already uses it as exactly
    /// that. Reusing it here rather than inventing a second identity scheme for "the same scan"
    /// is the whole point: two definitions of scan identity in one codebase is how a join
    /// silently matches the wrong rows.
    /// </summary>
    [Serializable]
    public class RoomRebuildEntry
    {
        /// <summary>When this rebuild happened. Its own clock -- see the class remark on why.</summary>
        public string rebuiltUtc = "";

        public string scanCapturedUtc = "";

        /// <summary>The MRUK anchor the scan was captured against, from the file.</summary>
        public string savedOriginAnchorUuid = "";

        /// <summary>
        /// The MRUK anchor the boxes were actually placed relative to this time, or empty when
        /// there was none. Comparing this to <see cref="savedOriginAnchorUuid"/> is how a
        /// re-anchoring event (Space Setup re-run) is told apart from an ordinary replay.
        /// </summary>
        public string currentAnchorUuid = "";

        /// <summary>
        /// False means the room anchor was missing entirely and every pose below is in RAW
        /// WORLD SPACE -- positioned relative to wherever the app happened to start, not to the
        /// real room. RoomScanRebuilder itself only says this to the console; without it here,
        /// reading the log later gives no way to tell a trustworthy rebuild from one that
        /// should be thrown out.
        /// </summary>
        public bool anchored;

        public RebuildAlignmentEntry alignment = new RebuildAlignmentEntry();

        public List<RebuiltPoseEntry> poses = new List<RebuiltPoseEntry>();
    }

    /// <summary>One app run's worth of rebuilds, across however many scans it replayed.</summary>
    [Serializable]
    public class RoomRebuildLogFile
    {
        public int schemaVersion = 1;
        public string capturedUtc = "";

        public List<RoomRebuildEntry> rebuilds = new List<RoomRebuildEntry>();
    }

    /// <summary>
    /// Disk IO. Mirrors StudySessionIO deliberately, including its refusal to catch: the write
    /// is unguarded here and the CALLER wraps it, so the caller can put the failure somewhere a
    /// researcher will actually see it.
    /// </summary>
    public static class RoomRebuildLogIO
    {
        /// <summary>
        /// The same folder name StudySessionIO and ScanObservationLog use, repeated as a
        /// literal rather than referenced. RoomScan is the lower layer; nothing in it should
        /// have to know ConvaiRoom exists, which is the same reasoning ScanObservationLog gives
        /// for its own copy of this string.
        /// </summary>
        public const string FolderName = "study";

        public static string Folder => Path.Combine(Application.persistentDataPath, FolderName);

        public static void EnsureFolder() => Directory.CreateDirectory(Folder);

        public static string PathForStem(string stem) =>
            Path.Combine(Folder, stem + ".rebuilds.json");

        public static void Save(RoomRebuildLogFile file, string path)
        {
            EnsureFolder();

            file.capturedUtc = DateTime.UtcNow.ToString("o");
            File.WriteAllText(path, JsonUtility.ToJson(file, prettyPrint: true));
        }

        public static RoomRebuildLogFile Load(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[RoomRebuildLogIO] No rebuild log at {path}");
                return null;
            }

            return JsonUtility.FromJson<RoomRebuildLogFile>(File.ReadAllText(path));
        }
    }
}
