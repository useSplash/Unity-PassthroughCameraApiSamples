using System;
using System.Collections.Generic;
using System.IO;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// One real object in the room, measured by hand.
    ///
    /// Room-local, in the same frame as <see cref="ScannedObject"/>, which is the entire
    /// reason ground truth is marked in the headset rather than measured with a tape and
    /// typed up afterwards: a tape gives you distances in the room's own terms and then
    /// somebody has to get those into the scan's coordinate frame, which is the hard part
    /// and the part that quietly ruins a position-error figure.
    /// </summary>
    [Serializable]
    public class TruthObject
    {
        public string id;

        /// <summary>
        /// The COCO class name, taken from the detector's own label list, or free text when
        /// the object has no COCO class at all.
        /// </summary>
        public string label;

        /// <summary>
        /// Whether <see cref="label"/> is a class the model could possibly emit.
        ///
        /// This is not bookkeeping, it is a measure. Recall is scored over objects whose
        /// class exists in COCO, and room coverage is reported separately, so that the
        /// vocabulary's limits are not counted as a failure of the pipeline. A bookshelf the
        /// model has no word for is a fact about COCO; missing a chair is a fact about the
        /// scanner. Without this flag the two are the same number.
        /// </summary>
        public bool inVocabulary;

        /// <summary>Room-local centre, midway between the two marked corners.</summary>
        public Vec3 position;

        /// <summary>Full extents in metres, the absolute difference of the two corners.</summary>
        public Vec3 size;

        /// <summary>
        /// Room-local orientation. Identity for corner marking, which produces a box aligned
        /// to the room axes. Carried anyway so an oriented capture can be added later
        /// without a schema break, and so the offline comparison never has to assume.
        /// </summary>
        public Quat rotation = new Quat();

        /// <summary>The two corners as marked, kept so a suspect box can be audited.</summary>
        public Vec3 cornerA;
        public Vec3 cornerB;

        /// <summary>
        /// True when a corner was placed by raycast rather than by touching it.
        ///
        /// The two are not the same measurement. Touching puts the controller's tracked
        /// position on the corner and is good to well under a centimetre; a raycast lands on
        /// whatever surface the depth pass reports and carries that pass's own error -- which
        /// is a meaningful fraction of the 0.10 m the position figure is being tested
        /// against. Flagged so the two are never pooled into one accuracy claim.
        /// </summary>
        public bool viaRaycast;

        public string markedUtc;
    }

    /// <summary>
    /// What is really in one room.
    ///
    /// Keyed by room rather than by session, and reused across participants, because the
    /// room does not change between them. Re-entering the marking mode loads this and
    /// appends, so a room can be measured over several visits.
    /// </summary>
    [Serializable]
    public class RoomTruthFile
    {
        public int schemaVersion = 1;
        public string capturedUtc;
        public string roomLabel;

        /// <summary>
        /// The MRUK room this was measured in. Worth recording: truth marked in one Space
        /// Setup and compared against a scan replayed under a different one is comparing
        /// two different frames, and the anchor UUID is the only thing that says so.
        /// </summary>
        public string anchorUuid;

        public List<TruthObject> objects = new List<TruthObject>();
    }

    public static class RoomTruthIO
    {
        public static string PathFor(string roomLabel) =>
            Path.Combine(StudySessionIO.Folder, $"truth_{Clean(roomLabel)}.json");

        public static void Save(RoomTruthFile data, string path = null)
        {
            StudySessionIO.EnsureFolder();

            path ??= PathFor(data.roomLabel);
            data.capturedUtc = DateTime.UtcNow.ToString("o");

            var json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(path, json);

            Debug.Log($"[RoomTruthIO] Wrote {data.objects.Count} truth objects -> {path}");
        }

        /// <summary>
        /// Loads the room's truth, or hands back an empty file for that room.
        ///
        /// Never null, unlike RoomScanIO.Load. A missing truth file is the normal state of a
        /// room nobody has measured yet, and the marking mode's job is to start one -- so
        /// returning null here would only mean every caller writes the same two lines.
        /// </summary>
        public static RoomTruthFile LoadOrCreate(string roomLabel)
        {
            var path = PathFor(roomLabel);

            if (!File.Exists(path))
                return new RoomTruthFile { roomLabel = roomLabel };

            var data = JsonUtility.FromJson<RoomTruthFile>(File.ReadAllText(path));

            if (data == null) return new RoomTruthFile { roomLabel = roomLabel };

            data.objects ??= new List<TruthObject>();
            Debug.Log($"[RoomTruthIO] Loaded {data.objects.Count} truth objects from {path}");

            return data;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";

            var clean = value.Trim();
            foreach (var bad in Path.GetInvalidFileNameChars()) clean = clean.Replace(bad, '-');

            return clean.Replace(' ', '-');
        }
    }
}
