using System.Collections.Generic;
using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// Draws a wireframe box in the room for every cluster ObjectScanRecorder is
    /// currently holding, and keeps them in sync as the scan grows.
    ///
    /// This is the live view of what ExportToJson() would write right now. Boxes come
    /// from the same room-local centre and size the exporter uses, so what you see is
    /// what lands in the file -- once a box turns from pendingColor to confirmedColor,
    /// meaning it has reached minObservations.
    ///
    /// Distinct from the SDK's ObjectDetectionVisualizer, which draws a flat quad per
    /// raw YOLO detection and throws it away next frame. These boxes are the persistent,
    /// deduplicated, multi-viewpoint result.
    ///
    /// Put this on the Room Scan object, next to ObjectScanRecorder.
    /// </summary>
    public class LiveScanVisualizer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The object carrying ObjectScanRecorder.")]
        public ObjectScanRecorder recorder;

        [Header("What to show")]
        [Tooltip("Also draw clusters that have not yet reached minObservations. " +
                 "Very useful for tuning -- it shows you what is being rejected.")]
        public bool showPending = true;

        [Tooltip("Text above each box: label, confidence, observation count.")]
        public bool showLabels = true;

        [Header("Appearance")]
        [Tooltip("Clusters still below minObservations.")]
        public Color pendingColor = new Color(1f, 0.75f, 0.2f, 0.45f);

        [Tooltip("Clusters that would be exported right now.")]
        public Color confirmedColor = new Color(0.3f, 1f, 0.6f, 0.95f);

        [Tooltip("Edge thickness, metres.")]
        public float lineWidth = 0.005f;

        [Tooltip("Gap between the top of a box and its label, metres.")]
        public float labelHeightOffset = 0.05f;

        [Tooltip("Optional override for the wireframe material. Leave empty for the built-in one.")]
        public Material lineMaterial;

        [Header("Performance")]
        [Tooltip("Seconds between refreshes. Detections land every frame; redrawing that " +
                 "often is wasted work. 0 refreshes every frame.")]
        public float refreshInterval = 0.15f;

        private class Entry
        {
            public WireBox Box;
            public TextMesh Label;
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly List<ObjectScanRecorder.ClusterView> _snapshot =
            new List<ObjectScanRecorder.ClusterView>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();

        private float _nextRefresh;

        private void Awake()
        {
            if (recorder == null)
                Debug.LogError("[LiveScanVisualizer] recorder is not assigned in the Inspector.");
        }

        private void OnEnable()
        {
            if (recorder != null) recorder.OnScanCleared += ClearBoxes;
            _nextRefresh = 0f;
        }

        private void OnDisable()
        {
            if (recorder != null) recorder.OnScanCleared -= ClearBoxes;
            ClearBoxes();
        }

        /// <summary>
        /// Polled rather than event-driven on purpose. Boxes are placed in world space
        /// from room-local data, so they have to be re-derived after the user recenters
        /// and the MRUK room anchor shifts -- an event on new detections alone would
        /// leave stale boxes hanging in the wrong place.
        /// </summary>
        private void LateUpdate()
        {
            if (recorder == null) return;
            if (Time.time < _nextRefresh) return;

            _nextRefresh = Time.time + Mathf.Max(0f, refreshInterval);
            Refresh();
        }

        public void Refresh()
        {
            if (recorder == null) return;

            recorder.SnapshotClusters(_snapshot);
            _seen.Clear();

            foreach (var view in _snapshot)
            {
                if (!view.Exportable && !showPending)
                {
                    Remove(view.Id);
                    continue;
                }

                if (!_entries.TryGetValue(view.Id, out var entry))
                {
                    entry = CreateEntry(view);
                    _entries[view.Id] = entry;
                }

                Apply(entry, view);
                _seen.Add(view.Id);
            }

            // Anything we hold that the recorder no longer reports has gone away.
            _stale.Clear();
            foreach (var id in _entries.Keys)
                if (!_seen.Contains(id)) _stale.Add(id);

            foreach (var id in _stale) Remove(id);
        }

        private Entry CreateEntry(ObjectScanRecorder.ClusterView view)
        {
            var box = WireBox.Create($"cluster_{view.Id:D3}_{view.Label}", transform,
                                     lineMaterial, lineWidth);

            var entry = new Entry { Box = box };
            if (showLabels) entry.Label = ScanLabel.Attach(box.transform);
            return entry;
        }

        private void Apply(Entry entry, ObjectScanRecorder.ClusterView view)
        {
            var tint = view.Exportable ? confirmedColor : pendingColor;

            entry.Box.transform.SetPositionAndRotation(
                recorder.RoomToWorld(view.RoomCenter),
                recorder.RoomToWorld(view.RoomRotation));

            entry.Box.SetSize(view.Size);
            entry.Box.SetWidth(lineWidth);
            entry.Box.SetColor(tint);

            if (entry.Label == null) return;

            // The box transform is never scaled, so this needs no counter-scaling.
            entry.Label.transform.localPosition =
                new Vector3(0f, view.Size.y * 0.5f + labelHeightOffset, 0f);

            entry.Label.text = $"{view.Label}\n{view.Confidence:P0} · {view.Observations}x";

            // Keep text opaque; a faint pending box is helpful, faint text is not.
            entry.Label.color = new Color(tint.r, tint.g, tint.b, 1f);
        }

        private void Remove(int id)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;

            if (entry.Box != null) Destroy(entry.Box.gameObject);
            _entries.Remove(id);
        }

        private void ClearBoxes()
        {
            foreach (var entry in _entries.Values)
                if (entry.Box != null) Destroy(entry.Box.gameObject);

            _entries.Clear();
        }
    }
}
