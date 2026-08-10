# Handoff Prompt — Quest 3 Room Object Scanner

> Paste everything below the line into your code session. Copy the three `.cs`
> files into the repo first (suggested: `Assets/Scripts/RoomScan/`) so the agent
> can read them.

---

## Project goal

Unity app for Meta Quest 3 that:

1. Scans a room at **runtime** using YOLO object detection over the Passthrough
   Camera API, plus the Depth API for 3D positioning.
2. Maps each detected object into the room layout with a 3D bounding box.
3. Serialises the result to JSON.
4. Reloads that JSON in a later session and recreates the bounding boxes in the
   correct physical locations.

## Current state

Working Unity scene containing these Meta XR Building Blocks:

- Camera Rig
- Passthrough
- Passthrough Camera Access
- Object Detection (YOLO via Unity Inference Engine / Sentis) — **confirmed working**

Not yet added: MR Utility Kit, Environment Raycast Manager, and the scan pipeline.

## Environment — do not change these

| Setting | Value | Notes |
|---|---|---|
| Device | Meta Quest 3 | Horizon OS v74+ required for PCA |
| XR Plug-in Provider | **Oculus** | OpenXR is deprecating Oculus eventually, but the project works on Oculus. **Do not migrate.** |
| Meta XR SDK | Core SDK + Building Blocks | MRUK version must match Core SDK version exactly |

**Known issue, already diagnosed:** switching to the OpenXR provider caused
virtual objects to become invisible while passthrough still rendered. Root cause
is `OVRPassthroughLayer.Placement` resetting to `Overlay` (must be `Underlay`)
and/or the center-eye camera losing `Clear Flags = Solid Color` with background
alpha `0`. Not a scene-loading bug. Do not attempt the migration as part of this
work.

## Architecture decisions already made — preserve these

1. **MRUK is the coordinate frame, not the object source.** Objects come from
   YOLO at runtime. MRUK supplies the walls/floor shell and, critically, a
   persistent room anchor. The Depth API does not require Space Setup, but the
   room anchor does.

2. **All persisted coordinates are room-local.** Everything goes through
   `room.transform.InverseTransformPoint()` before serialisation and
   `TransformPoint()` on load. Raw world coordinates are meaningless after a
   recenter or restart. The room UUID is stored in the JSON so a mismatched
   room can be detected on load.

3. **Rays are built from the frame's capture pose, not the live pose.**
   `PassthroughCameraUtils.ScreenPointToRayInWorld()` uses the current head pose,
   but frames are 40–60ms old. The code uses `ScreenPointToRayInCamera()` and
   transforms by the pose recorded alongside the `WebCamTexture` frame. This
   requires capturing `GetCameraPoseInWorld(eye)` at frame-grab time and
   carrying it through inference.

4. **Depth extent is recovered from multiple viewpoints.** A single 2D box gives
   width and height at the hit depth only. Each cluster holds a `Bounds` grown
   via `Encapsulate()` across observations, which converges on true volume as
   the user walks around the object.

5. **Detections are clustered, not stored per-frame.** Same label within
   `mergeRadius` merges; a cluster must reach `minObservations` before export.

## Files provided

- `RoomScanData.cs` — JSON schema (`RoomScanFile`, `ScannedObject`, `RoomShell`)
  and disk IO via `JsonUtility` to `Application.persistentDataPath`.
- `ObjectScanRecorder.cs` — the accumulator. Entry point is
  `ProcessDetection(label, confidence, boxPixels, capturePose)`.
- `RoomScanRebuilder.cs` — reads the JSON back and spawns wireframe boxes.

These are written but **untested on device**. Treat them as a starting point,
not verified code.

## Tasks, in order — verify each before moving on

### 1. Add MR Utility Kit
- Install `com.meta.xr.mrutilitykit` at the version matching the installed Core SDK.
- On `OVRCameraRig` → `OVRManager`: Quest Features → Scene Support = **Required**;
  Permission Requests On Startup → check **Scene**.
- Run `Meta → Tools → Update AndroidManifest.xml`, then
  `Meta → Tools → Project Setup Tool` and clear all warnings.
- Add MRUK to the scene via Building Blocks (Scene Debugger is useful here).
  Note: the **Room Model** block is deprecated — do not use it.
- **Verify:** `MRUK.Instance.GetCurrentRoom()` returns non-null and anchors log
  with sane world positions. Requires Space Setup completed on the headset.

### 2. Add Environment Raycast (Depth API)
- Add `EnvironmentRaycastManager` (the Instant Content Placement block includes it).
- Confirm `EnvironmentRaycastManager.IsSupported == true`.
- **Verify:** a raycast straight forward from the head returns
  `EnvironmentRaycastHitStatus.Hit` with a plausible distance.

### 3. Capture and propagate the frame pose
- Find where the Object Detection block grabs the `WebCamTexture` frame.
- Record `PassthroughCameraUtils.GetCameraPoseInWorld(eye)` at that exact moment.
- Thread that `Pose` through to wherever detections are emitted.
- **Verify:** log the pose alongside each detection; it should lag the live head
  pose slightly during head movement.

### 4. Wire the recorder
- In the Object Detection block's `DetectionManager` (or equivalent), find the
  loop that iterates YOLO outputs to draw 2D UI boxes.
- Call `ProcessDetection(className, confidence, boxRect, capturePose)` per detection.
- **Important:** confirm the coordinate convention of `boxRect`. The recorder
  assumes camera-image pixels with a top-left origin. If the block emits
  normalised (0–1) coords or a bottom-left origin, convert before calling, or the
  raycast will hit the wrong part of the room.
- **Verify:** `PendingClusterCount` grows as you look around; clusters do not
  multiply for a single stationary object.

### 5. Export and inspect
- Trigger `ExportToJson()` from a controller button.
- Pull with `adb pull /sdcard/Android/data/<package>/files/room_scan.json`
- **Verify:** object count and positions are plausible; sizes are not absurd.

### 6. Rebuild
- Add `RoomScanRebuilder`, relaunch, confirm boxes land on the real objects.
- **Verify:** quit, recenter the headset, relaunch — boxes must still be correct.
  If they drift, the room-local transform is being applied wrongly somewhere.

## API names that drift between SDK versions

Check the installed signature if these fail to compile:

- `MRUKAnchor.Label` — older versions use `AnchorLabels` (a `List<string>`)
- `MRUKAnchor.PlaneRect` — nullable `Rect?`
- `EnvironmentRaycastHit.status` / `.point` / `.normal` — casing has varied
- `MRUKRoom.TryGetClosestSurfacePosition` — parameter order has changed
- `PassthroughCameraUtils.ScreenPointToRayInCamera` — namespace
  `PassthroughCameraSamples` comes from the sample assets, not the core package

## Constraints

- Do not switch the XR provider to OpenXR.
- Do not add `OVRSceneManager` — it conflicts with MRUK.
- Keep a single `OVRCameraRig` in the scene.
- Stock YOLO uses 80 COCO classes. Furniture coverage is decent (chair, couch,
  bed, dining table, tv) but there is no "bookshelf" or "desk". If the room
  vocabulary matters, plan a model swap as separate work.

## Tuning defaults to revisit on device

| Field | Default | Adjust when |
|---|---|---|
| `minObservations` | 5 | Lower if real objects are missing; raise if phantoms appear |
| `mergeRadius` | 0.5 m | Lower to ~0.3 for dense rooms (two chairs side by side merge) |
| `minConfidence` | 0.5 | Raise if noisy |
| `maxRaycastDistance` | 6 m | Room-size dependent |

## First response

Read the three scripts and the existing Object Detection block, then tell me
what you find for step 4 — specifically the exact class and method emitting YOLO
detections, and the coordinate convention of its bounding box. Do not start
editing until we agree on that.
