using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RoomScan
{
    // ---------------------------------------------------------------------
    // JSON schema. All poses are ROOM-LOCAL (relative to the MRUK room
    // anchor or your fallback spatial anchor), never raw world space.
    // Unity's JsonUtility handles these: plain [Serializable] classes,
    // public fields only, no dictionaries, no properties.
    // ---------------------------------------------------------------------

    [Serializable]
    public class Vec3
    {
        public float x, y, z;
        public Vec3() { }
        public Vec3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public class Quat
    {
        public float x, y, z, w = 1f;
        public Quat() { }
        public Quat(Quaternion q) { x = q.x; y = q.y; z = q.z; w = q.w; }
        public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
    }

    [Serializable]
    public class ScannedObject
    {
        public string id;
        public string label;             // COCO class name from YOLO
        public float confidence;         // best confidence observed
        public int observations;         // how many frames agreed
        public Vec3 position;            // room-local centre of the box
        public Quat rotation;            // room-local orientation
        public Vec3 size;                // full extents, metres
        public string firstSeenUtc;
        public string lastSeenUtc;
    }

    [Serializable]
    public class WallRecord
    {
        public Vec3 center;
        public Quat rotation;
        public Vec3 size;
    }

    [Serializable]
    public class RoomShell
    {
        public float floorY;
        public float ceilingY;
        public List<WallRecord> walls = new List<WallRecord>();
    }

    [Serializable]
    public class RoomScanFile
    {
        public int schemaVersion = 1;
        public string capturedUtc;
        public string originAnchorUuid;   // MRUK room UUID or your spatial anchor UUID
        public RoomShell room = new RoomShell();
        public List<ScannedObject> objects = new List<ScannedObject>();
    }

    // ---------------------------------------------------------------------
    // Disk IO. Application.persistentDataPath on Quest resolves to
    // /sdcard/Android/data/<package>/files -- pullable over adb.
    // ---------------------------------------------------------------------

    public static class RoomScanIO
    {
        public const string DefaultFileName = "room_scan.json";

        public static string DefaultPath =>
            Path.Combine(Application.persistentDataPath, DefaultFileName);

        /// <summary>
        /// Editor-side fallback. persistentDataPath is empty on a desktop that has never
        /// run the scanner, so drop an adb-pulled room_scan.json in StreamingAssets to
        /// iterate on replay scenes without a headset.
        ///
        /// On Android this resolves to a jar: URL inside the APK, which File.Exists
        /// reports as missing -- so the fallback silently does nothing on device, which
        /// is exactly what we want. Never rely on it at runtime.
        /// </summary>
        public static string StreamingAssetsPath =>
            Path.Combine(Application.streamingAssetsPath, DefaultFileName);

        public static void Save(RoomScanFile data, string path = null)
        {
            path ??= DefaultPath;
            data.capturedUtc = DateTime.UtcNow.ToString("o");

            var json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(path, json);

            Debug.Log($"[RoomScanIO] Wrote {data.objects.Count} objects -> {path}");
        }

        public static RoomScanFile Load(string path = null)
        {
            var explicitPath = !string.IsNullOrEmpty(path);
            path ??= DefaultPath;

            if (!File.Exists(path))
            {
                // Only fall back when the caller did not name a specific file. An explicit
                // path that is missing is a mistake worth surfacing, not one to paper over.
                if (!explicitPath && File.Exists(StreamingAssetsPath))
                {
                    path = StreamingAssetsPath;
                    Debug.Log($"[RoomScanIO] No scan in persistentDataPath; using the " +
                              $"StreamingAssets copy at {path}");
                }
                else
                {
                    Debug.LogWarning($"[RoomScanIO] No scan file at {path}");
                    return null;
                }
            }

            var data = JsonUtility.FromJson<RoomScanFile>(File.ReadAllText(path));
            Debug.Log($"[RoomScanIO] Loaded {data.objects.Count} objects from {path}");
            return data;
        }
    }
}
