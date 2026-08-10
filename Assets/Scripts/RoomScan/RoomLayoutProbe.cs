using System.Text;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Drop this on any GameObject in your scene (alongside the MRUK object).
/// Verifies MRUK loaded, dumps the room layout, and shows how to convert
/// world-space points into ROOM-RELATIVE coordinates -- which is what you
/// must store in JSON so detections survive a recenter / app restart.
/// </summary>
public class RoomLayoutProbe : MonoBehaviour
{
    [Tooltip("Log every anchor found in the room on load.")]
    public bool verboseDump = true;

    private MRUKRoom _room;

    private void Start()
    {
        // Fires once scene data is available (device scan or JSON).
        MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
    }

    private void OnSceneLoaded()
    {
        _room = MRUK.Instance.GetCurrentRoom();

        if (_room == null)
        {
            Debug.LogError("[RoomLayoutProbe] No room. Run Space Setup on the headset " +
                           "(Settings > Physical Space > Space Setup) and relaunch.");
            return;
        }

        Debug.Log($"[RoomLayoutProbe] Room loaded. Anchors: {_room.Anchors.Count}");

        if (!verboseDump) return;

        var sb = new StringBuilder();

        // Structural anchors -- your "room layout".
        if (_room.FloorAnchor != null)
            sb.AppendLine($"FLOOR   pos={_room.FloorAnchor.transform.position}");
        if (_room.CeilingAnchor != null)
            sb.AppendLine($"CEILING pos={_room.CeilingAnchor.transform.position}");

        foreach (var wall in _room.WallAnchors)
            sb.AppendLine($"WALL    pos={wall.transform.position} size={wall.PlaneRect?.size}");

        // Everything Scene API captured: couch, table, screen, storage, etc.
        foreach (var a in _room.Anchors)
        {
            var localPos = WorldToRoom(a.transform.position);
            sb.AppendLine($"ANCHOR  label={a.Label} world={a.transform.position} room-local={localPos}");
        }

        Debug.Log(sb.ToString());
    }

    // ---- The two conversions you need for persistence -------------------

    /// <summary>World space -> room-anchor space. Store THIS in your JSON.</summary>
    public Vector3 WorldToRoom(Vector3 worldPoint)
        => _room == null ? worldPoint : _room.transform.InverseTransformPoint(worldPoint);

    /// <summary>Room-anchor space -> world space. Use when rebuilding from JSON.</summary>
    public Vector3 RoomToWorld(Vector3 roomPoint)
        => _room == null ? roomPoint : _room.transform.TransformPoint(roomPoint);

    /// <summary>Rotation equivalents, for oriented bounding boxes.</summary>
    public Quaternion WorldToRoom(Quaternion worldRot)
        => _room == null ? worldRot : Quaternion.Inverse(_room.transform.rotation) * worldRot;

    public Quaternion RoomToWorld(Quaternion roomRot)
        => _room == null ? roomRot : _room.transform.rotation * roomRot;

    // ---- Handy for snapping detections that miss the depth raycast ------

    /// <summary>Nearest point on the room surfaces to a given world point.</summary>
    public bool TrySnapToSurface(Vector3 worldPoint, out Vector3 surfacePoint)
    {
        surfacePoint = worldPoint;
        if (_room == null) return false;

        // The optional last argument is a LabelFilter, not a SurfaceType; the default
        // includes every label. Returns Mathf.Infinity -- not NaN -- when nothing is found.
        var dist = _room.TryGetClosestSurfacePosition(worldPoint, out var pos, out _);
        if (float.IsInfinity(dist)) return false;

        surfacePoint = pos;
        return true;
    }
}
