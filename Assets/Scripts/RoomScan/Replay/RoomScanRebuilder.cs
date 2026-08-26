using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace RoomScan
{
    /// <summary>
    /// Reads room_scan.json back and rebuilds wireframe bounding boxes in the
    /// room. Waits for MRUK so the room anchor exists before converting
    /// room-local coordinates back to world space.
    ///
    /// Downstream systems (navmesh baking, Convai action targets) consume
    /// <see cref="Rebuilt"/> rather than re-reading the file, so world placement
    /// is computed exactly once.
    /// </summary>
    public class RoomScanRebuilder : MonoBehaviour
    {
        /// <summary>One scanned object paired with the world-space proxy spawned for it.</summary>
        public readonly struct RebuiltObject
        {
            public readonly ScannedObject Data;
            public readonly GameObject Proxy;

            public RebuiltObject(ScannedObject data, GameObject proxy)
            {
                Data = data;
                Proxy = proxy;
            }
        }

        [Header("Visuals")]
        [Tooltip("Optional. If empty, a runtime WireBox is used -- same look as the live scan.")]
        public GameObject boxPrefab;

        [Tooltip("Optional override for the wireframe material. Leave empty for the built-in one.")]
        public Material lineMaterial;

        [Tooltip("Edge thickness, metres. Ignored when boxPrefab is set.")]
        public float lineWidth = 0.005f;

        [Tooltip("Colour of rebuilt boxes. Ignored when boxPrefab is set.")]
        public Color boxColor = new Color(0.3f, 0.7f, 1f, 0.95f);

        public bool showLabels = true;
        public float labelHeightOffset = 0.1f;

        [Header("Source")]
        public string jsonPath;          // blank = Application.persistentDataPath/room_scan.json
        public bool rebuildOnStart = true;

        [Header("Wall alignment")]
        [Tooltip("Fit the scan's saved walls onto the walls MRUK reports now, and correct every " +
                 "object pose by the difference.\n\nOnly does anything when the room anchor has " +
                 "moved since capture -- re-running Space Setup in the same room is the case " +
                 "this exists for. Same room, same setup, the correction solves to nothing.")]
        public bool alignToSavedWalls = true;

        [Tooltip("Mean wall error, in metres, above which the fit is thrown away and the file's " +
                 "own coordinates are used unchanged. A bad fit is worse than no fit: it moves " +
                 "every object confidently to the wrong place.")]
        public float maxWallError = 0.35f;

        [Tooltip("How much better the winning orientation must score than the runner-up before " +
                 "it is trusted. Under this, the room's walls fit more than one way round and " +
                 "the log says so.")]
        public float ambiguityMargin = 0.1f;

        private MRUKRoom _room;
        private RoomAlignment _alignment = RoomAlignment.None("not solved yet");
        private readonly List<RebuiltObject> _spawned = new List<RebuiltObject>();

        /// <summary>Raised after every <see cref="Rebuild"/>, including one that spawned nothing.</summary>
        public event Action<RoomScanRebuilder> OnRebuilt;

        /// <summary>The scan behind the current proxies. Null until a successful Rebuild.</summary>
        public RoomScanFile Scan { get; private set; }

        /// <summary>Proxies from the last Rebuild, in file order.</summary>
        public IReadOnlyList<RebuiltObject> Rebuilt => _spawned;

        /// <summary>
        /// The MRUK room the scan was replayed into, or null when MRUK is absent --
        /// in which case room-local coordinates were treated as world space.
        /// </summary>
        public MRUKRoom Room => _room;

        /// <summary>
        /// The wall fit applied to the last Rebuild. Never applied means the poses went in
        /// exactly as the file stored them; check <see cref="RoomAlignment.Summary"/> for why.
        /// </summary>
        public RoomAlignment Alignment => _alignment;

        private void Start()
        {
            if (MRUK.Instance != null)
            {
                MRUK.Instance.RegisterSceneLoadedCallback(() =>
                {
                    _room = MRUK.Instance.GetCurrentRoom();
                    if (rebuildOnStart) Rebuild();
                });
            }
            else
            {
                Debug.LogError("[RoomScanRebuilder] No MRUK in the scene. Room-local coordinates will " +
                               "be treated as world space, so boxes will land in the wrong place.");
                if (rebuildOnStart) Rebuild();
            }
        }

        public void Rebuild()
        {
            Clear();

            // Resolve the room here rather than trusting the scene-loaded callback to have
            // fired first. Two things race for it: this component's own callback, and
            // ConvaiRoomBootstrap, which registers its own and additionally gives up after
            // a timeout. Whichever registers first wins, and component Start() order within
            // a GameObject is undefined -- so Rebuild could run with _room still null even
            // though MRUK had the room ready. That silently falls through to raw world
            // space, which anchors every box to wherever the app happened to start instead
            // of to the physical room.
            if (_room == null && MRUK.Instance != null)
                _room = MRUK.Instance.GetCurrentRoom();

            var data = RoomScanIO.Load(string.IsNullOrEmpty(jsonPath) ? null : jsonPath);
            Scan = data;

            if (data == null)
            {
                // Cleared rather than left alone: Alignment is public, and a stale fit from a
                // previous rebuild would describe boxes that no longer exist.
                _alignment = RoomAlignment.None("no scan loaded");

                // Still notify: listeners waiting to bake or connect must not hang
                // just because there is no scan on this device yet.
                OnRebuilt?.Invoke(this);
                return;
            }

            var reanchored = _room != null && data.originAnchorUuid != "none" &&
                             data.originAnchorUuid != _room.Anchor.Uuid.ToString();

            // Solved before anything spawns -- RoomToWorld reads it for every box.
            _alignment = alignToSavedWalls && _room != null
                ? RoomScanAligner.Solve(data, _room, maxWallError, ambiguityMargin)
                : RoomAlignment.None(alignToSavedWalls ? "no MRUK room" : "alignment turned off");

            ReportAlignment(reanchored, data.originAnchorUuid);

            foreach (var obj in data.objects)
                _spawned.Add(new RebuiltObject(obj, SpawnBox(obj)));

            // Say which frame the boxes landed in. Anchored vs world-space is the difference
            // between boxes that sit on the real furniture and boxes that drift with wherever
            // the app was launched, and the two look identical in the log otherwise.
            if (_room != null)
                Debug.Log($"[RoomScanRebuilder] Rebuilt {_spawned.Count} boxes, anchored to " +
                          $"MRUK room {_room.Anchor.Uuid}.");
            else
                Debug.LogWarning($"[RoomScanRebuilder] Rebuilt {_spawned.Count} boxes in RAW " +
                                 $"WORLD SPACE -- no MRUK room was available, so they are " +
                                 $"positioned relative to wherever the app started, not to the " +
                                 $"real room. On a headset this means Space Setup has not run, " +
                                 $"or the scan replayed before MRUK finished loading.");
            OnRebuilt?.Invoke(this);
        }

        /// <summary>
        /// Says what the wall fit did, and how much to trust it.
        ///
        /// The anchor UUID changing is the whole reason this feature exists -- re-running Space
        /// Setup in the same room produces a new one -- so on its own it is news, not a fault.
        /// It only becomes a warning when the fit also failed, because that is the combination
        /// that puts boxes somewhere wrong.
        /// </summary>
        private void ReportAlignment(bool reanchored, string originUuid)
        {
            if (_alignment.Applied)
            {
                if (_alignment.Ambiguous)
                    Debug.LogWarning($"[RoomScanRebuilder] Aligned to the current walls, but the " +
                                     $"room fits more than one way round -- the runner-up was only " +
                                     $"{_alignment.Margin:F2} m worse. A rectangular room maps onto " +
                                     $"itself at 180 degrees and a square one every 90, so the walls " +
                                     $"cannot settle this. Took {_alignment.Summary}. If the room " +
                                     $"came back rotated, this is why.");
                else
                    Debug.Log($"[RoomScanRebuilder] Aligned to the current walls: " +
                              $"{_alignment.Summary} (runner-up {_alignment.Margin:F2} m worse).");

                return;
            }

            if (reanchored)
                Debug.LogWarning($"[RoomScanRebuilder] The scan was captured against a different " +
                                 $"room anchor ({Shorten(originUuid)}) and could not be realigned: " +
                                 $"{_alignment.Summary}. Boxes will be misplaced.");
            else
                Debug.Log($"[RoomScanRebuilder] Using the scan's own coordinates unchanged " +
                          $"({_alignment.Summary}).");
        }

        private static string Shorten(string uuid)
            => string.IsNullOrEmpty(uuid) || uuid.Length <= 8 ? uuid : uuid.Substring(0, 8) + "...";

        private GameObject SpawnBox(ScannedObject obj)
        {
            var worldPos = RoomToWorld(obj.position.ToVector3());
            var worldRot = RoomToWorld(obj.rotation.ToQuaternion());
            var size = obj.size.ToVector3();

            GameObject go;
            if (boxPrefab != null)
            {
                go = Instantiate(boxPrefab, worldPos, worldRot, transform);
                go.transform.localScale = size;
            }
            else
            {
                var box = WireBox.Create("box", transform, lineMaterial, lineWidth);
                box.transform.SetPositionAndRotation(worldPos, worldRot);
                box.SetSize(size);
                box.SetColor(boxColor);
                go = box.gameObject;
            }

            go.name = $"{obj.id}_{obj.label}";

            if (showLabels) AttachLabel(go, obj, size);

            return go;
        }

        private void AttachLabel(GameObject parent, ScannedObject obj, Vector3 size)
        {
            // No counter-scaling here any more, and no character size either. A prefab box is
            // scaled by the object's size and would stretch the text, but undoing that is the
            // caption's own business now -- and so is how big it is, so a replayed box and the
            // live box it came from are captioned identically.
            var label = ScanLabel.Attach(parent.transform);

            label.Place(size.y, labelHeightOffset);
            label.Set(obj.label, obj.confidence, obj.observations, boxColor);
        }

        public void Clear()
        {
            foreach (var entry in _spawned) if (entry.Proxy != null) Destroy(entry.Proxy);
            _spawned.Clear();
        }

        /// <summary>
        /// Room-local to world, by way of the wall alignment.
        ///
        /// Two corrections, in order: the file's frame to today's room frame (identity unless
        /// the room anchor moved since capture), then room-local to world. Falls through
        /// unchanged when MRUK is absent, matching the recorder's own fallback so a scan
        /// captured without a room still replays self-consistently -- just not anchored to
        /// anything real.
        /// </summary>
        public Vector3 RoomToWorld(Vector3 local)
            => _room == null ? local : _room.transform.TransformPoint(_alignment.Apply(local));

        public Quaternion RoomToWorld(Quaternion local)
            => _room == null ? local : _room.transform.rotation * _alignment.Apply(local);
    }

}
