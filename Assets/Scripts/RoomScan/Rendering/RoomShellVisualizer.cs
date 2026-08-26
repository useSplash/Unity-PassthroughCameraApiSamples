using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace RoomScan
{
    /// <summary>
    /// Outlines the room MRUK captured during Space Setup: the floor and ceiling polygons
    /// and every wall.
    ///
    /// Each outline is parented to its own MRUK anchor, so it follows that anchor for free
    /// when the user recenters -- there is nothing to reposition per frame.
    ///
    /// Worth leaving on while tuning a scan. Object positions are stored relative to the
    /// room anchor, so if this shell does not line up with the real room, nothing built on
    /// that frame will either -- and you would otherwise only find out after a recenter.
    /// </summary>
    public class RoomShellVisualizer : MonoBehaviour
    {
        [Header("What to show")]
        public bool showFloor = true;
        public bool showWalls = true;

        [Tooltip("Off by default -- a ceiling outline sits above your view and mostly adds clutter.")]
        public bool showCeiling;

        [Tooltip("Draw as soon as MRUK reports a room. Turn off to drive it from the controller only.")]
        public bool drawOnSceneLoaded = true;

        [Header("Appearance")]
        public Color floorColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        public Color wallColor = new Color(0.4f, 0.6f, 1f, 0.5f);
        public Color ceilingColor = new Color(0.6f, 0.6f, 0.7f, 0.35f);

        [Tooltip("Edge thickness, metres. Walls are large, so these read better a little " +
                 "thicker than the object boxes.")]
        public float lineWidth = 0.01f;

        [Tooltip("Lift each outline off its surface along the plane normal so it does not " +
                 "z-fight with real geometry. MRUK's own debugger uses 1cm.")]
        public float surfaceOffset = 0.01f;

        [Tooltip("Optional override for the wireframe material. Leave empty for the built-in one.")]
        public Material lineMaterial;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<Vector3> _pointScratch = new List<Vector3>();
        private MRUKRoom _room;

        /// <summary>True while outlines are on screen.</summary>
        public bool IsShowing => _spawned.Count > 0;

        private void Start()
        {
            if (MRUK.Instance == null)
            {
                Debug.LogError("[RoomShellVisualizer] No MRUK in the scene, so there is no room to outline.");
                return;
            }

            MRUK.Instance.RegisterSceneLoadedCallback(() =>
            {
                _room = MRUK.Instance.GetCurrentRoom();

                if (_room == null)
                {
                    Debug.LogError("[RoomShellVisualizer] MRUK loaded but there is no current room. " +
                                   "Run Space Setup on the headset (Settings > Physical Space).");
                    return;
                }

                if (drawOnSceneLoaded) Draw();
            });
        }

        private void OnDisable() => Clear();

        public void Draw()
        {
            Clear();
            if (_room == null) return;

            if (showFloor)
                foreach (var floor in _room.FloorAnchors) AddOutline(floor, floorColor);

            if (showCeiling)
                foreach (var ceiling in _room.CeilingAnchors) AddOutline(ceiling, ceilingColor);

            if (showWalls)
                foreach (var wall in _room.WallAnchors) AddOutline(wall, wallColor);

            Debug.Log($"[RoomShellVisualizer] Outlined {_spawned.Count} surfaces.");
        }

        public void Toggle()
        {
            if (IsShowing) Clear();
            else Draw();
        }

        public void Clear()
        {
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _spawned.Clear();
        }

        private void AddOutline(MRUKAnchor anchor, Color color)
        {
            if (anchor == null) return;
            if (!TryBuildOutline(anchor)) return;

            // Parented to the anchor, so the anchor's own transform does the placing.
            var loop = WireLoop.Create($"outline_{anchor.Label}", anchor.transform, lineMaterial, lineWidth);
            loop.SetPoints(_pointScratch);
            loop.SetColor(color);

            _spawned.Add(loop.gameObject);
        }

        /// <summary>
        /// Fills the scratch list with the anchor's outline in ITS OWN local space -- plane-local
        /// XY with +Z along the normal, which is why the loop can simply be parented to the anchor.
        ///
        /// Prefers the boundary polygon over PlaneRect: a room with ten walls does not have a
        /// rectangular floor, and the rect would cut corners off it.
        /// </summary>
        private bool TryBuildOutline(MRUKAnchor anchor)
        {
            _pointScratch.Clear();

            var boundary = anchor.PlaneBoundary2D;
            if (boundary != null && boundary.Count >= 3)
            {
                foreach (var p in boundary)
                    _pointScratch.Add(new Vector3(p.x, p.y, surfaceOffset));

                return true;
            }

            if (!anchor.PlaneRect.HasValue) return false;

            var r = anchor.PlaneRect.Value;
            _pointScratch.Add(new Vector3(r.xMin, r.yMin, surfaceOffset));
            _pointScratch.Add(new Vector3(r.xMax, r.yMin, surfaceOffset));
            _pointScratch.Add(new Vector3(r.xMax, r.yMax, surfaceOffset));
            _pointScratch.Add(new Vector3(r.xMin, r.yMax, surfaceOffset));
            return true;
        }
    }
}
