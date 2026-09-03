# Handoff — study instrumentation, phase 6

Read this first, then start at **Build order, item 6** — the last one on the original list.
Items 1–5 are done, and item 5 (the one that carries the study's actual statistical weight) is
verified against a live Unity Editor, not just a `dotnet` compile check.

## Where things stand

Branch **`user-test-metrics`** (off `refinement`). Items 1–5 are done and compile clean.
Items 1–2 are committed (`f118cc1`), item 3 (`8e35b17`), item 4 (`4d1f981`); item 5 is
uncommitted in the tree.

**One background task is pending**, spawned this session: `task_9895b05d`, two small
pre-existing bugs found while verifying item 5 live (a `RoomTruthMarker.ParseLabels` empty-input
edge case, and a floating-point boundary in a self-check's own tolerance). Neither blocks
anything here; see the task or the "Panel"/self-check facts below for exact file:line detail.

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

**Protocol (revision 4, updated 2026-09-03):** https://claude.ai/code/artifact/d1adeec4-f5fd-4662-a94d-56076c8e1a83

## The two hard constraints

Everything below is shaped by these. Do not design past them.

1. **≤ 40 Convai requests per participant** (settled — see decisions above). A conversation
   turn is a request; the hard stop that would enforce this is opt-in and off by default,
   because the SDK never reports how much of the real quota is actually left.
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
| `Study/ConvaiEventBinder.cs` | Keeps an SDK subscription alive across manager swaps |
| `Study/StudyTranscriptWatch.cs` | Utterance counts and timings, never text |
| `Study/PlanCorpusData.cs` | Offline plan corpus DTOs + `PlanCorpusIO` |
| `Study/PlanScoring.cs` | Pure, self-checked corpus arithmetic |
| `Study/Editor/PlanCorpusHarness.cs` | `Tools > Convai Room > Plan Corpus Harness` |
| `Study/Editor/PlanCorpusReport.cs` | Summary / blind sheet / consistency export |

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

### 3. Utterance counts and timings — **DONE**

`Study/StudyTranscriptWatch.cs`, plus `Study/ConvaiEventBinder.cs` extracted from the request
counter so the two SDK listeners share one bind/rebind path instead of two copies of it.

Counts, turn ids and instants only. `characters` is an utterance LENGTH, not text — it cannot
be read back into words and it answers whether referring expressions get longer when naming is
hard. Her text is not measured at all; her speech is timed acoustically, which avoids a row per
streamed chunk.

**It does not need the transcript system** — see the corrected facts below.

### 4. Task markers and the planner event — **DONE**

`RoomPlannerClient.OnPlanAttempt` is raised from a `finally`, so the guard clauses that answer
without a request are timed alongside the ones that wait for a model. Cancellations are flagged
before the rethrow and counted apart from failures. `Parse` gained an `out int dropped` — an
out parameter rather than a field, because more than one plan can be in flight and a field
would hold whichever finished last. Listener exceptions are swallowed with a LogError so
instrumentation cannot destroy the plan it is measuring.

Panel: the session screen's cycle gained `TASK` (a toggle whose label says which way it will
go) and `ASSIST` (greyed with "no task open" rather than refused after the press).

**Still not recorded: steps advanced.** It is in the protocol's `plans[]` yields and it is
blocked by the `RoomTaskPlan` bug below, which is a behaviour change to shipped code and was
left out of item 4's stated scope deliberately. Do it as part of item 5 or as its own thing.

### 5. Offline plan harness — **DONE**

`Study/Editor/PlanCorpusHarness.cs` (EditorWindow, `Tools > Convai Room > Plan Corpus Harness`),
`Study/Editor/PlanCorpusReport.cs` (summary/blind-sheet/consistency export), `Study/PlanScoring.cs`
(pure, self-checked arithmetic), `Study/PlanCorpusData.cs` (DTOs).

Two facts worth knowing before touching this again:

- **There is no canonical list of the "six household tasks"** anywhere in this repo or the
  protocol artifact — the dropped-features table just says "six household tasks" and the
  participant-session block names one example ("constrained adapt"/"help me set this space up")
  as one of "the other four types". Nobody wrote the other five/six down where code could find
  them. The harness therefore takes tasks as **input** (one per line in the window), not as a
  hardcoded list — do not invent task text and put it in code.
- **`RoomTaskVocabulary.Collect` cannot be reused offline** — it searches the *live scene* for
  `ConvaiActionTarget` components and returns nothing without one. `RoomScanContext` gained two
  new static methods that read only the scan file: `PlacesFor(scan, maxObjects, maxPlaces=0)`
  and `SummaryFor(scan)`. They run the SAME naming/description code the headset does
  (`Choose`/`NameThem`/`BuildDescription`, `Choose` was made static to allow this), which is
  what makes the corpus comparable to what participants actually saw rather than a parallel
  guess at it. `Contents` was split into an instance version (reads `_described`, for the live
  character) and a static `ContentsOf` (reads the scan directly, for both `SummaryFor` and the
  live path when nothing has been described yet) — that split already existed as a branch inside
  `Contents`; it is now two methods instead of one with a condition in it.
- **`maxPlaces` has no player to measure from offline.** The live path (`RoomTaskPlanner` →
  `RoomTaskVocabulary.Collect(maxPlaces, playerPosition)`) sorts nearest-to-player and trims.
  `PlacesFor`'s second cap does the same sort but from the **room centre** — the only anchor
  that exists without a person standing somewhere. This is a real, documented divergence, not
  an oversight; skipping the cap entirely would have been the wrong kind of easy.
- **`OnPlanAttempt` (item 4) is reused, not reimplemented.** Subscribe around each
  `PlanAsync` call, capture the one event it raises, and read latency/groundedness/dropped-count
  straight off it. This is why item 4 built that event the way it did.
- **Verified live, not just `dotnet build`.** UnityMCP is connected in this project now (it
  was not in the previous session — see the correction below). Ran the actual Editor
  self-check: 117/119 passed; the 2 failures are pre-existing, in code nobody touched this
  session (`RoomTruthMarker.ParseLabels` empty-input edge case, and a floating-point boundary
  in `CheckQuantisation`'s own tolerance). Flagged as a separate task
  (`task_9895b05d`), not fixed — out of this item's scope. Also opened the harness window
  itself via `execute_menu_item`: zero errors/warnings on a real `OnGUI` pass. Did **not**
  press Start — that fires real network requests and costs real API/Ollama time, not something
  to trigger unattended.
- **Not implemented**: schema-violation as a distinct category. Failures are tallied by their
  exact message instead (`.summary.txt` lists every distinct string with a count) — a guessed
  taxonomy could misclassify an ambiguous failure, and the exact strings already exist for free.

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

**Transcripts** — this section was wrong in one important way; corrected 2026-09-03.

- `TranscriptSystemEnabled` **is on** (`_transcriptSystemEnabled: 1` in `ConvaiSettings.asset`).
  That was the last open external to-do and it is now closed.
- **But nothing in the study depends on it.** The flag gates only the PRESENTATION layer —
  `ConvaiTranscripts`, `ConvaiTranscriptEventRelay` and the transcript UIs, all of which check
  `IsPresentationEnabled`. The transport (`PlayerConversationInput`, `RTVIHandler`) publishes
  the domain events **unconditionally**, so everything on `ConvaiEvents` keeps arriving with
  transcripts switched off. Both the request counter and the speech watch take that route
  deliberately; going through `Transcripts.TurnCommitted` would have put every conversation
  measurement in the study behind a toggle in a settings panel.
- `ConvaiCharacter.OnTranscriptReceived` is **her TTS text**, not the participant's. Wrong event.
- Useful events beyond the participant final: `OnPlayerSpeakingStateChanged`,
  `OnCharacterSpeechStateChanged` (carries `UtteranceId`), `OnCharacterTurnCompleted` (carries
  `WasInterrupted`), `OnLlmNoResponseReceived` (she chose not to answer — otherwise invisible,
  and indistinguishable from a reply that never came).

**Panel**
- `SlotCount = 3`. `LayOutActions` leaves **slot 2 empty at `Stage.Home` and `Stage.Character`**,
  which is why the study needs **no prefab re-bake** — new `SlotAction` entries, not new buttons.
  Keep it that way; a re-bake risks prefab GUIDs.
- Study mode is a sub-mode flag, **not new `Stage` members** — adding stages would invalidate
  any claim about the shipped six-stage flow.

**Planner** — the first two were fixed by item 4; kept here because they explain the shape of
`OnPlanAttempt` and the offline harness inherits both.
- ~~`PlanAsync` is never timed~~ — it is now, from a `finally`, including the guard clauses
  that answer in a millisecond without a request. Those belong in the distribution: a latency
  figure built only from real round trips describes a planner nobody used.
- ~~The dropped-location warning is never counted~~ — `droppedLocations` now carries it, and it
  is the only thing separating "the planner said nowhere" from "the planner named a place the
  room no longer has". Keep them apart in the analysis.
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

**Unity MCP** — corrected 2026-09-03: it **is connected now** (`Unity 6000.4.3f1`, scene
`Room Flow.unity`, project `Unity-PassthroughCameraApiSamples`), where an earlier session found
`ConnectionRefused`. Don't assume either state — check `mcpforunity://editor/state` before
relying on it. When it is up, prefer it over a `dotnet` compile-check for anything with runtime
behavior: item 5's self-check and Editor-window verification were both confirmed by actually
running them, which is strictly stronger than syntax-checking the C# alone.

## Git hygiene

Stage by explicit path; **never `git add -A`**. These are permanently dirty and must never be
staged: `Packages/manifest.json`, `.vscode/*`, `Unity-PassthroughCameraApiSamples.slnx`,
`ProjectSettings/Packages/com.unity.testtools.codecoverage/Settings.json`,
`Assets/Resources/ConvaiSettings.asset` (holds a live API key; `origin` is a public fork).
Two remotes: `origin` (useSplash fork) and `upstream` (oculus-samples) — always pass
`--repo useSplash/...` to `gh`.

Items 1–4 are committed (`f118cc1`, `8e35b17`, `4d1f981`; see the top of this file). Item 5 is
uncommitted — commit it before starting item 6, same reasoning as every item before it.

## External work — not code, start it in parallel

1. **Author the "Look At" action** in the Convai dashboard. Word it narrowly: *the player is
   indicating which object they mean; they are not asking you to move or do anything.* Without
   it there is no naming condition and no study.
2. **Routing pilot.** Risk: "the chair by the couch" routes to the existing Move To action and
   walks her across the room. Test before building the block around it.
3. ~~Verify `TranscriptSystemEnabled`~~ — **done, it is on**, and nothing depends on it anyway.
4. **Five `AddComponent`s** on the room-manager GameObject (the one with `ObjectScanRecorder`):
   `StudySessionRecorder`, `RoomTruthMarker` (+ drag in `SentisYoloClasses.txt`),
   `ScanObservationLog`, `ReferenceTrialRunner`, `RoomAttentionExecutor`. All five exist.
   The turn counter and the speech watch are deliberately **not** components — no Inspector
   surface, no scene presence — so `StudySessionRecorder` owns and ticks them and this list
   stayed at five.

   `RoomAttentionExecutor` must also be set as the executor on the `Look At` action in the
   Convai dashboard, or naming resolves to nothing and the block runs pointing-only (it says
   so, in the console and in `referenceBlock.unavailable`).

5. **A new `.cs` file has no `.meta` until Unity focuses/refreshes.** True for the instant right
   after `Write`, not a standing problem — confirmed this session that UnityMCP's
   `refresh_unity` (or just proceeding to the next tool call, which seems to trigger it
   incidentally) generates them within the same turn. Still worth checking before a commit if
   Unity was never focused in between.
6. **Six household task prompts, for item 5's harness.** Nobody has written these down where
   code could find them — see item 5's notes above. Whoever runs the corpus sweep needs to
   supply real task text; this is a content decision, not a code one.

## Open decisions

None outstanding for the code. Both of revision 3's are settled at the top of this file, and
protocol **revision 4** in the artifact reflects them.

Two things need a person, not a build session, before the corpus or the participant sessions
can actually run: the **routing pilot** (external work item 2) and the **task prompts**
(external work item 6, just above). Item 6 in the build order — rebuild/alignment/per-object
poses — has no open questions and can be built without either.
