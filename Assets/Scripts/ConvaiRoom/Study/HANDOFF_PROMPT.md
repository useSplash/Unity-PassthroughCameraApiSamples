# Handoff — study instrumentation, phase 3

Read this first, then start at **Build order, item 3**. Items 1 and 2 are done.

## Where things stand

Branch **`user-test-metrics`** (off `refinement`). Phases 1 and 2 are done, compile under both
`dotnet` and the Unity editor, and are uncommitted in the working tree.

**Decisions taken (2026-09-03), do not reopen:**

- **24 trials** (3 reps), quota ceiling **40**. Worst case is 38 requests.
- **The analysis is within-participant, reported per participant.** The mixed-effects model is
  gone — 4 clusters cannot estimate a random-intercept variance. Protocol **revision 4** in the
  artifact already says so, including that four-of-four gives p = 0.0625 one-sided under a sign
  test, which is the smallest p-value n = 4 can produce and does not reach .05.
- **The hard stop is opt-in, not the default.** See the request-budget facts below.

The app is a Quest passthrough MR assistant: scan a room with YOLO → save `room_scan.json`
→ replay and align → bake a NavMesh → spawn a Convai character who knows the room, can be
pointed at objects, and can plan household tasks. Source is `Assets/Scripts/ConvaiRoom/`
and `Assets/Scripts/RoomScan/`.

Phase 1 added a user-study recorder. See `README.md` in this folder for how it is operated.

**Protocol (revision 3):** https://claude.ai/code/artifact/d1adeec4-f5fd-4662-a94d-56076c8e1a83

## The two hard constraints

Everything below is shaped by these. Do not design past them.

1. **≤ 30–40 Convai requests per participant.** A conversation turn is a request.
2. **15–20 minutes in the headset**, plus a 20–30 minute interview afterwards.

**n = 4 participants.** This is fixed — time and resources.

## What is already built

| File | Role |
|---|---|
| `Study/StudySessionData.cs` | Session DTOs + `StudySessionIO` |
| `Study/StudySessionRecorder.cs` | Session lifecycle, panel sub-mode, scan capture |
| `Study/RoomTruthData.cs` | Ground-truth DTOs + `RoomTruthIO` |
| `Study/RoomTruthMarker.cs` | In-headset ground-truth marking |
| `RoomScan/Capture/ScanObservationLog.cs` | Raw detection log (off by default) |
| `Study/Editor/StudySelfCheck.cs` | `Tools > Convai Room > Study Self-check` |
| `Study/Editor/ExtentAblationTool.cs` | Offline union-vs-percentile replay |
| `Study/StudyRequestBudget.cs` | Convai turn counter, budget, opt-in hard stop |
| `Study/ReferenceTrialRunner.cs` | The reference-resolution block |
| `Grounding/RoomAttentionExecutor.cs` | The "Look At" action — naming sets attention |

Edits to existing files: `ConvaiRoomModePanel` gained `OnStageChanged`, `OnReported`, a
public `Stage` enum, a study sub-mode branch in `LayOutActions`, and the study entry slot at
**`Stage.Character` as well as `Stage.Home`**. `ObjectScanRecorder` gained four log hooks,
`recordObservations`, public `WorldToRoom`, public `MinBoxExtent`. `RoomScanPointer` gained
`OnAttentionChanged`. `RoomScanContext` gained `TryResolve(nameOrAlias, out GameObject)`.

**A phase-1 bug was fixed doing this.** `StartSession` set `_mode = Mode.Session`, so the study
owned all three action slots for the entire session and `BRING IN CHARACTER` was unreachable
while recording — which made the character phase, and therefore every reference trial,
impossible. It now returns the panel to its own flow; the study screen is re-entered from
slot 2 at Home and at Character. The README's claim that "the panel is back to its normal flow
underneath" was describing the intent, not the code.

## Build order

### 1. Convai request counter, budget, hard stop — **DONE**

### 2. Reference-resolution trial runner — **DONE**

Both are built, verified by a forced clean `dotnet` rebuild of `Assembly-CSharp` and
`Assembly-CSharp-Editor` (0 errors, only the 7 pre-existing CS0618 warnings), and covered by
new `StudySelfCheck` assertions. See `README.md` for how they are operated, and the two
sections below for what they turned up.

**Still not done for item 2, and it is external:** the routing pilot for the `Look At` action.
The risk is unchanged — "the chair by the couch" routing to `Move To` and walking her across
the room mid-trial. Nothing in the code can test that.

### 3. Utterance counts and timings

New: `Study/StudyTranscriptWatch.cs`. **Counts, turn ids and timestamps only — never store
transcript text.** This was an explicit decision; participant speech does not go on disk.

### 4. Task markers and the planner event

Panel controls for task start/end and assists. One `OnPlanAttempt` event on
`RoomPlannerClient`, raised in a `finally` so success, failure and `OperationCanceledException`
are all timed, carrying backend, model, places offered, latency, steps, grounded steps and
the dropped-location count. Shared with item 5 — build once, use twice.

### 5. Offline plan harness — no headset needed

Drive the planner unattended across all six saved scans and both backends; target ~400
plans. Emits groundedness, schema violations, specificity, latency percentiles, and a
de-identified plan sheet for blind rating (correctness, safety, appropriateness). Also a
consistency mode: 10 repeats per (room, task), reporting step-set agreement.

At n = 4 this corpus carries most of the study's quantitative weight — it is not affected
by the participant count. Weight effort accordingly.

### 6. Rebuild / alignment / per-object poses

Subscribe to `RoomScanRebuilder.OnRebuilt`; record `Alignment` (`Error`, `Margin`,
`Ambiguous`, `YawDegrees`) and per-object world poses keyed by scan id. Serves the
researcher scan harness, not the participant session.

**Dropped:** wrong-button-presses-per-stage. The flow is barely exercised in a 16-minute session.

## Facts already established — do not re-derive

These cost real time to work out. They are verified against the current code.

**Request budget**
- Pointing costs **zero** Convai requests. `RoomScanPointer` calls
  `SetCurrentAttentionObject(name, ConvaiRespondMode.Silent)` — context staging, not a turn.
- The planner is **off-quota**. `RoomPlannerClient` calls `https://api.anthropic.com/v1/messages`
  or a local Ollama URL directly via `UnityWebRequest`. It never touches Convai.
- **A turn is `ConvaiEvents.OnFinalUserTranscriptionReceived`**, published once per processed
  final in `PlayerConversationInput.HandleProcessedFinal`. The alternatives are all wrong:
  `OnInteractionCreated` fires per interaction context, `OnPlayerTranscriptReceived` per interim
  ASR fragment, and `ConvaiCharacter.OnTranscriptReceived` is her reply.
- **The backend quota is real and the SDK announces it.** `ConvaiEvents.OnUsageLimitReached`
  carries a quota type ("daily"/"monthly"/"additional") and, per the SDK's own remark, "the
  pipeline is terminated immediately after this message". Nothing in the app listened for it
  before — an exhausted quota looked exactly like a network fault. It is now recorded.
- **The SDK never reports how much quota is left.** That is why the 30–40 is a planning figure
  and why enforcement defaults to warn-only: refusing turns at a guessed ceiling ends a session
  that had quota left. Enforcement, when on, holds the microphone shut — the only lever there
  is, since hands-free has no "send" to intercept, and it must be re-asserted every tick or
  `RoomCharacterVoice`'s own gate lifts it when she stops speaking.
- `ConvaiManager.Events` **throws** while initialisation is incomplete. `IsInitialized` is the
  gate; there is no ready event to hang a subscription on, so it is polled.

**Attention**
- **There is no attention-by-voice path.** `SetCurrentAttentionObject` has exactly two
  callers: `RoomScanPointer.cs:291` and `RoomTaskPlan.cs:302`. Item 2 adds the third.
- The only two `ConvaiActionExecutor<>` subclasses are `RoomTaskPlanner` ("Plan Task") and
  `RoomTaskStepExecutor` ("Step Through Plan"). Follow their authoring pattern.
- `RoomScanPointer.AttentionName` is `ConvaiActionTarget.TargetName` — a **display name
  invented at rebuild time** ("chair by the couch", "chair 3"), not the scan file's id.
  **Score trials by scan id**: resolve the proxy through `rebuilder.Rebuilt`, whose proxies
  are named `$"{obj.id}_{obj.label}"` (`RoomScanRebuilder.cs:229`).
- Display names are **not stable across rebuilds** — landmark assignment is greedy-by-distance
  and depends on the `maxObjects = 40` cap. Another reason to key on id.
- `RoomTaskVocabulary.Contains` compares `Place.Name` only; **aliases are invisible to it**.
  A participant saying "chair 2" resolves for the Convai backend but not in Unity.
  **`RoomScanContext.TryResolve(nameOrAlias, out GameObject)` now exists** and closes the hole
  by reading the `ConvaiActionTarget` components directly — `RoomTaskVocabulary` still has the
  old behaviour and could be pointed at it.
- **A distractor count is realised as a property of the target**, not by hiding furniture: a
  trial with N distractors is one whose target has N same-label competitors. Rooms cannot always
  supply the 4-distractor cell; `referenceBlock.unavailable` records that, and
  `actualDistractors` records what a trial really got. **Analyse on `actualDistractors`.**
- Four identical chairs get **unique** names (`RoomScanContext.NameThem`), so a distractor
  set is nameable; with no unique landmarks in the room they fall back to "chair 1..4".

**Transcripts**
- Available but never enabled in this project: `ConvaiManager.Transcripts` →
  `TurnCommitted` / `GetTurns`, or the inspector relay `ConvaiTranscriptEventRelay`
  (`OnFinalPlayerTranscriptReceived`, payload carries `_turnId`, `_text`, `_isFinal`).
- Gated behind a `TranscriptSystemEnabled` runtime setting — **verify it is on**.
- `ConvaiCharacter.OnTranscriptReceived` is **her TTS text**, not the participant's. Wrong event.

**Panel**
- `SlotCount = 3`. `LayOutActions` leaves **slot 2 empty at `Stage.Home` and `Stage.Character`**,
  which is why the study needs **no prefab re-bake** — new `SlotAction` entries, not new buttons.
  Keep it that way; a re-bake risks prefab GUIDs.
- Study mode is a sub-mode flag, **not new `Stage` members** — adding stages would invalidate
  any claim about the shipped six-stage flow.

**Planner**
- `PlanAsync` (`RoomPlannerClient.cs:228`) is **never timed**; only `request.timeout` bounds it.
- The `"Dropped the location '{x}' from a step"` warning (`:799`) is **never counted**, and
  `step.HasPlace == false` cannot distinguish "planner said nowhere" from "location dropped".
- An ungrounded condition is ~3 lines: pass an empty place list **and withhold `RoomSummary`**
  (a summary naming the furniture reimports the vocabulary the ablation removes).
  `AppendSchema` and `BuildSystemPrompt` already handle the zero-place case deliberately.

**Plan stepping**
- `RoomTaskPlan.TryMove` / `TryGoTo` returning **false does not call `Publish()`**, so
  `OnChanged` never fires — "already at the last step" is invisible to every listener.

## House conventions

- **Namespaces are flat**: `ConvaiRoom`, `RoomScan`, `ConvaiRoomEditor`. Sub-folders get no
  sub-namespace. No `.asmdef` under `Assets/Scripts/` — everything is Assembly-CSharp.
- **Persistence**: copy `RoomScan/RoomScanData.cs` exactly — `[Serializable]` public-field-only
  DTOs (JsonUtility: no dictionaries, no properties, no nested generics), leading
  `schemaVersion`, `capturedUtc` stamped at write, unguarded `File.WriteAllText`, try/catch
  at the **caller**. Reuse `RoomScan.Vec3` / `Quat`.
- Every MonoBehaviour: `private const string Tag = "[ShortName]";` used as `$"{Tag} ..."`.
- Optional wiring: public fields with `[Header]`/`[Tooltip]`, self-resolved in `Awake` via
  `if (x == null) x = FindAnyObjectByType<X>();`. Keep doing this — it is why scene wiring is
  six `AddComponent`s and no reference dragging.
- **Comments explain WHY** — trade-offs, failure modes, what was tried and rejected. Match the
  surrounding density; it is much higher than typical.
- **Panel or controller only.** Every control must be reachable from the in-world panel or a
  Touch binding. No mouse or keyboard paths, not even for Editor testing. Aim-dependent
  actions (marking a corner, pointing at an object) are the legitimate controller exception.
- C# 9 / netstandard2.1: no `record`, no file-scoped namespaces.

## Verification

**Compile without the editor.** Copy `Assembly-CSharp.csproj` → `_compilecheck.csproj` in the
project root (the `.csproj` extension is mandatory) and `dotnet build` with
`BaseIntermediateOutputPath` / `OutputPath` redirected to a scratch dir; delete the copy after.
Same for `Assembly-CSharp-Editor.csproj`.

**`EnableDefaultItems` is `false` and the file list is explicit.** New `.cs` files are not
compiled by the copy until Unity regenerates the csproj — a green build that silently skipped
every new file is the failure mode. Inject `<Compile Include>` lines, or let Unity regenerate
first. Expect 7 pre-existing CS0618 warnings from `Assets/PassthroughCameraApiSamples/`.

**Logic checks** go in `Study/Editor/StudySelfCheck.cs` — there is no test assembly, and the
repo's habit is Editor menu items that assert and log. Extend it for anything new; the
JsonUtility round-trip check is the one that matters, because JsonUtility fails silently.

**Unity MCP** was not connected in the previous session (`ConnectionRefused`). Check whether
it is available before assuming scene edits are possible. The editor itself was running and
idle. Nothing built so far needs MCP.

## Git hygiene

Stage by explicit path; **never `git add -A`**. These are permanently dirty and must never be
staged: `Packages/manifest.json`, `.vscode/*`, `Unity-PassthroughCameraApiSamples.slnx`,
`ProjectSettings/Packages/com.unity.testtools.codecoverage/Settings.json`,
`Assets/Resources/ConvaiSettings.asset` (holds a live API key; `origin` is a public fork).
Two remotes: `origin` (useSplash fork) and `upstream` (oculus-samples) — always pass
`--repo useSplash/...` to `gh`.

Phase 1 is uncommitted. Committing it before starting phase 2 is reasonable.

## External work — not code, start it in parallel

1. **Author the "Look At" action** in the Convai dashboard. Word it narrowly: *the player is
   indicating which object they mean; they are not asking you to move or do anything.* Without
   it there is no naming condition and no study.
2. **Routing pilot.** Risk: "the chair by the couch" routes to the existing Move To action and
   walks her across the room. Test before building the block around it.
3. **Verify `TranscriptSystemEnabled`** is on.
4. **Six `AddComponent`s** on the room-manager GameObject (the one with `ObjectScanRecorder`):
   `StudySessionRecorder`, `RoomTruthMarker` (+ drag in `SentisYoloClasses.txt`),
   `ScanObservationLog`, `ReferenceTrialRunner`, `RoomAttentionExecutor` — all five now exist —
   then `StudyTranscriptWatch` when it is built. The turn counter is deliberately **not** a
   component; it is owned and ticked by `StudySessionRecorder` so this list did not grow a
   seventh entry for something with no Inspector surface.

   `RoomAttentionExecutor` must also be set as the executor on the `Look At` action in the
   Convai dashboard, or naming resolves to nothing and the block runs pointing-only (it says
   so, in the console and in `referenceBlock.unavailable`).

5. **New `.cs` files have no `.meta` yet** — Unity generates them on next focus. Let it, rather
   than hand-writing GUIDs.

## Open decisions

None outstanding. Both of revision 3's are settled at the top of this file, and protocol
**revision 4** in the artifact reflects them.

The next judgement call belongs to item 5: the offline plan harness now carries most of the
study's quantitative weight, and nothing about it depends on the participant count. Weight
effort there over anything that only improves the four participant sessions.
