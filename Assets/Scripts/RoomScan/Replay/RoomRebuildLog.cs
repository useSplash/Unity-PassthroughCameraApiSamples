using System;
using System.IO;
using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// Writes down what every <see cref="RoomScanRebuilder.OnRebuilt"/> decided: the wall
    /// alignment it solved, and where every object actually ended up in world space.
    ///
    /// WHO THIS IS FOR. The researcher scan harness, not the participant session -- the six
    /// rooms get re-scanned and re-set-up across visits, and this is what lets a researcher
    /// tell whether the wall-fit correction in RoomScanAligner is actually holding up across
    /// those re-anchoring events, rather than trusting the one-line console message from
    /// whichever rebuild happened to be the last one anybody watched. Nothing here is
    /// participant-facing and nothing in it is remotely sensitive -- geometry and an anchor
    /// UUID, never speech -- so it stays on for every rebuild rather than needing to be armed
    /// per session the way ScanObservationLog does.
    ///
    /// A HANDFUL OF ROWS, NOT A HOT PATH. RoomScanRebuilder.Rebuild runs a few times an hour at
    /// most -- once per scan load, not once per frame -- so unlike ScanObservationLog this
    /// re-serialises the WHOLE file on every rebuild rather than streaming appended CSV rows.
    /// That buys the same property StudySessionRecorder relies on: the file on disk is always
    /// complete and parseable, never a fragment that needs repairing after a crash.
    ///
    /// ONE FILE PER APP RUN, not one per rebuild and not one that grows forever across restarts.
    /// The stem is stamped once, the first time this activates, mirroring
    /// ObjectScanRecorder's own <c>scan_{timestamp}</c> convention for the no-session case.
    /// </summary>
    public class RoomRebuildLog : MonoBehaviour
    {
        private const string Tag = "[RebuildLog]";

        [Header("Wiring (left empty, this is found in the scene)")]
        public RoomScanRebuilder rebuilder;

        [Header("Debug")]
        public bool verboseLogging;

        private RoomRebuildLogFile _file;
        private string _path;

        /// <summary>Where this run's log is being written, once opened. Empty until then.</summary>
        public string Path => _path ?? "";

        /// <summary>Rebuilds recorded so far this run.</summary>
        public int Count => _file?.rebuilds.Count ?? 0;

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();

            if (rebuilder == null)
                Debug.LogError($"{Tag} No RoomScanRebuilder in the scene, so there is nothing " +
                               $"to log rebuilds from.", this);
        }

        private void OnEnable()
        {
            if (rebuilder != null) rebuilder.OnRebuilt += HandleRebuilt;
        }

        private void OnDisable()
        {
            if (rebuilder != null) rebuilder.OnRebuilt -= HandleRebuilt;
        }

        /// <summary>
        /// One rebuild, recorded.
        ///
        /// Skips a rebuild that spawned nothing -- Rebuild() still fires the event when there
        /// is no scan loaded at all (a Clear with nothing to replace it), and there is no
        /// alignment, no poses, and nothing a researcher could do with that row. Everything
        /// else -- including a rebuild that ran unanchored, in raw world space -- is recorded,
        /// because "this one cannot be trusted" is itself the fact <see cref="anchored"/>
        /// exists to carry forward.
        /// </summary>
        private void HandleRebuilt(RoomScanRebuilder source)
        {
            if (source?.Scan == null) return;

            try
            {
                EnsureOpen();

                var alignment = source.Alignment;
                var entry = new RoomRebuildEntry
                {
                    rebuiltUtc = DateTime.UtcNow.ToString("o"),
                    scanCapturedUtc = source.Scan.capturedUtc ?? "",
                    savedOriginAnchorUuid = source.Scan.originAnchorUuid ?? "",
                    currentAnchorUuid = source.Room != null ? source.Room.Anchor.Uuid.ToString() : "",
                    anchored = source.Room != null,
                    alignment = new RebuildAlignmentEntry
                    {
                        applied = alignment.Applied,
                        yawDegrees = alignment.YawDegrees,
                        translation = new Vec3(alignment.Translation),
                        error = alignment.Error,
                        margin = alignment.Margin,
                        ambiguous = alignment.Ambiguous,
                        summary = alignment.Summary ?? ""
                    }
                };

                foreach (var rebuilt in source.Rebuilt)
                {
                    if (rebuilt.Proxy == null) continue;

                    entry.poses.Add(new RebuiltPoseEntry
                    {
                        id = rebuilt.Data?.id ?? "",
                        label = rebuilt.Data?.label ?? "",
                        worldPosition = new Vec3(rebuilt.Proxy.transform.position),
                        worldRotation = new Quat(rebuilt.Proxy.transform.rotation)
                    });
                }

                _file.rebuilds.Add(entry);
                RoomRebuildLogIO.Save(_file, _path);

                if (verboseLogging)
                    Debug.Log($"{Tag} Recorded rebuild #{_file.rebuilds.Count}: " +
                              $"{entry.poses.Count} poses, anchored={entry.anchored}, " +
                              $"{(alignment.Applied ? $"aligned ({alignment.Summary})" : "unaligned")}.");
            }
            catch (Exception ex)
            {
                // Loud, but never thrown back into the event -- RoomScanRebuilder has other
                // listeners on the same event (RoomScanContext, the navmesh baker), and a
                // logging component failing must not stop the room from being usable.
                Debug.LogError($"{Tag} Could not record this rebuild: {ex}");
            }
        }

        private void EnsureOpen()
        {
            if (_file != null) return;

            _file = new RoomRebuildLogFile();
            _path = RoomRebuildLogIO.PathForStem($"rebuilds_{DateTime.UtcNow:yyyyMMdd'T'HHmmss}Z");

            Debug.Log($"{Tag} Logging rebuilds to {_path}");
        }
    }
}
