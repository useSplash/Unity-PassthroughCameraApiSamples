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

        private MRUKRoom _room;
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

            var data = RoomScanIO.Load(string.IsNullOrEmpty(jsonPath) ? null : jsonPath);
            Scan = data;

            if (data == null)
            {
                // Still notify: listeners waiting to bake or connect must not hang
                // just because there is no scan on this device yet.
                OnRebuilt?.Invoke(this);
                return;
            }

            if (_room != null && data.originAnchorUuid != "none" &&
                data.originAnchorUuid != _room.Anchor.Uuid.ToString())
            {
                Debug.LogWarning($"[RoomScanRebuilder] Scan was captured in a different room " +
                                 $"({data.originAnchorUuid}). Boxes will be misplaced.");
            }

            foreach (var obj in data.objects)
                _spawned.Add(new RebuiltObject(obj, SpawnBox(obj)));

            Debug.Log($"[RoomScanRebuilder] Rebuilt {_spawned.Count} boxes.");
            OnRebuilt?.Invoke(this);
        }

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
            var label = ScanLabel.Attach(parent.transform, 0.03f);

            // A prefab box is scaled by the object's size, so the label has to divide
            // that back out or the text comes out stretched. A WireBox keeps its scale
            // at 1, in which case this is a no-op.
            var s = parent.transform.localScale;
            label.transform.localScale = new Vector3(1f / Mathf.Max(s.x, 1e-3f),
                                                     1f / Mathf.Max(s.y, 1e-3f),
                                                     1f / Mathf.Max(s.z, 1e-3f));
            label.transform.localPosition =
                new Vector3(0f, (size.y * 0.5f + labelHeightOffset) / Mathf.Max(s.y, 1e-3f), 0f);

            label.text = $"{obj.label}\n{obj.confidence:P0} · {obj.observations}x";
            label.color = new Color(boxColor.r, boxColor.g, boxColor.b, 1f);
        }

        public void Clear()
        {
            foreach (var entry in _spawned) if (entry.Proxy != null) Destroy(entry.Proxy);
            _spawned.Clear();
        }

        /// <summary>
        /// Room-local to world. Falls through unchanged when MRUK is absent, matching
        /// the recorder's own fallback so a scan captured without a room still replays
        /// self-consistently (just not anchored to anything real).
        /// </summary>
        public Vector3 RoomToWorld(Vector3 local)
            => _room == null ? local : _room.transform.TransformPoint(local);

        public Quaternion RoomToWorld(Quaternion local)
            => _room == null ? local : _room.transform.rotation * local;
    }

}
