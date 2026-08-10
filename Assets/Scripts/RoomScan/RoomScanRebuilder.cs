using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace RoomScan
{
    /// <summary>
    /// Reads room_scan.json back and rebuilds wireframe bounding boxes in the
    /// room. Waits for MRUK so the room anchor exists before converting
    /// room-local coordinates back to world space.
    /// </summary>
    public class RoomScanRebuilder : MonoBehaviour
    {
        [Header("Visuals")]
        [Tooltip("Optional. If empty, a unit cube with the wireframe material is used.")]
        public GameObject boxPrefab;
        public Material wireframeMaterial;
        public bool showLabels = true;
        public float labelHeightOffset = 0.1f;

        [Header("Source")]
        public string jsonPath;          // blank = Application.persistentDataPath/room_scan.json
        public bool rebuildOnStart = true;

        private MRUKRoom _room;
        private readonly List<GameObject> _spawned = new List<GameObject>();

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
            if (data == null) return;

            if (_room != null && data.originAnchorUuid != "none" &&
                data.originAnchorUuid != _room.Anchor.Uuid.ToString())
            {
                Debug.LogWarning($"[RoomScanRebuilder] Scan was captured in a different room " +
                                 $"({data.originAnchorUuid}). Boxes will be misplaced.");
            }

            foreach (var obj in data.objects)
                _spawned.Add(SpawnBox(obj));

            Debug.Log($"[RoomScanRebuilder] Rebuilt {_spawned.Count} boxes.");
        }

        private GameObject SpawnBox(ScannedObject obj)
        {
            var worldPos = RoomToWorld(obj.position.ToVector3());
            var worldRot = RoomToWorld(obj.rotation.ToQuaternion());

            GameObject go;
            if (boxPrefab != null)
            {
                go = Instantiate(boxPrefab, worldPos, worldRot, transform);
                go.transform.localScale = obj.size.ToVector3();
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.SetParent(transform, false);
                go.transform.SetPositionAndRotation(worldPos, worldRot);
                go.transform.localScale = obj.size.ToVector3();

                // Collider off so boxes don't fight your interactions.
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);

                if (wireframeMaterial != null)
                    go.GetComponent<Renderer>().material = wireframeMaterial;
            }

            go.name = $"{obj.id}_{obj.label}";

            if (showLabels) AttachLabel(go, obj);

            return go;
        }

        private void AttachLabel(GameObject parent, ScannedObject obj)
        {
            var labelGo = new GameObject("label");
            labelGo.transform.SetParent(parent.transform, false);

            // Sit above the box, and undo the parent's non-uniform scale so
            // the text isn't stretched.
            var s = parent.transform.localScale;
            labelGo.transform.localPosition = new Vector3(0f, 0.5f + labelHeightOffset / Mathf.Max(s.y, 1e-3f), 0f);
            labelGo.transform.localScale = new Vector3(1f / Mathf.Max(s.x, 1e-3f),
                                                       1f / Mathf.Max(s.y, 1e-3f),
                                                       1f / Mathf.Max(s.z, 1e-3f));

            var tm = labelGo.AddComponent<TextMesh>();
            tm.text = $"{obj.label}\n{obj.confidence:P0} · {obj.observations}x";
            tm.characterSize = 0.03f;
            tm.fontSize = 96;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;

            labelGo.AddComponent<FaceCamera>();
        }

        public void Clear()
        {
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _spawned.Clear();
        }

        private Vector3 RoomToWorld(Vector3 local)
            => _room == null ? local : _room.transform.TransformPoint(local);

        private Quaternion RoomToWorld(Quaternion local)
            => _room == null ? local : _room.transform.rotation * local;
    }

}
