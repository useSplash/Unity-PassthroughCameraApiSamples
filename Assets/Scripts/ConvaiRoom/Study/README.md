# User-test instrumentation — scan accuracy and reference resolution

Records what happens during a user-test session, and measures what is really in the room to
compare it against.

This covers the **scan accuracy** family of the protocol (recall, precision, position error,
extent error, duplicate rate, time-to-detect), the **Convai request budget**, and the
**reference-resolution** block. Utterance timings, task markers, plan quality and the offline
plan harness are later phases; the session file is designed to gain their arrays without a
schema break.

Session files are `schemaVersion: 2`. Version 1 files predate the request counter — the
distinction matters because JsonUtility cannot tell an absent field from a zero one, so a v1
session and a v2 session where nobody spoke both read back as `convaiRequests: 0`.

---

## Scene setup (once)

Add five components to the room-manager GameObject (the one carrying `ObjectScanRecorder`):

| Component | Needed for | Inspector work |
|---|---|---|
| `StudySessionRecorder` | everything | none — it finds the rest itself |
| `RoomTruthMarker` | ground truth | **drag in `SentisYoloClasses.txt`** (see below) |
| `ScanObservationLog` | the extent ablation only | none |
| `ReferenceTrialRunner` | the reference block | none |
| `RoomAttentionExecutor` | the **naming** condition | bind to the `Look At` action (below) |

The Convai request counter and the utterance watch are **not** components — they have no
Inspector surface and no scene presence, so `StudySessionRecorder` owns and drives them rather
than growing this list for something you would never configure.

`RoomAttentionExecutor` also needs an action authored on the character's Convai Action Config,
or there is no naming condition and the block runs pointing-only (it says so, in the console and
in `referenceBlock.unavailable`):

```
Action name   Look At
Description   The player is indicating which object in the room they mean. They are NOT asking
              you to move, walk, fetch, or do anything with it — only to note which one they
              are referring to.
Parameter     object (string) — the object as the player named it
Target        None
Executor      the RoomAttentionExecutor component
Timeout       10
When finished Tell the player
```

**Pilot the routing before building a block on it.** The risk is not that the action fails —
it is that "the chair by the couch" routes to the existing **Move To** action instead and she
walks across the room mid-trial. Every clause about not moving is there to hold that boundary.

`RoomTruthMarker.labelsAsset` must point at
`Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/SentisYoloClasses.txt`.
It is the detector's own class list, and reading it rather than a hand-copied list is what
stops "in vocabulary" drifting from what the model can actually emit. **Marking refuses to
start without it** — guessing there would corrupt the recall figure rather than merely
inconvenience anyone.

Nothing else changes. The panel finds `StudySessionRecorder` in `Awake` and offers the study
button only when it is present, so a build without these components behaves exactly as it did
before any of this existed. No prefab re-bake is needed — the study borrows the action slot
that has always been empty on the panel's Home stage.

---

## Running a session

All of it is on the panel and the controller. There is no keyboard path, by design.

1. **Home → `STUDY SETUP`**
2. Dial in the fields. `FIELD:` cycles which one; `CHANGE:` advances it.
   - `PARTICIPANT` — P01…P16
   - `ROOM` — R1…R6. The ground-truth file is keyed by this, so keep the labels stable
     across the study; renaming R2 halfway through orphans everything measured in it.
   - `OBS LOG` — leave **OFF** for participant sessions (see the ablation section)
   - `REQ BUDGET` — Convai turns allowed, or `NONE`. See the request budget section.
   - `AT BUDGET` — `WARN ONLY` (default) or `HARD STOP`. Leave it on `WARN ONLY` unless a
     pilot has established the real ceiling.
   - cycling once more reaches `LEAVE SETUP`
3. **`START SESSION`.** The run number is counted from what is already on disk, so it cannot
   be forgotten or duplicated.
4. **The panel returns to its own flow** and the session records underneath it. Take the
   participant through the protocol normally — scan, proceed, bake, bring in the character.
   The study block appears on the details panel (INFO).
5. The study screen is reached again from the **third slot at Home and at Character**, which
   now reads `STUDY`. It has:
   - `MARK NOTE` — one press, stamps the instant. Always in the same place.
   - `NEXT:` — cycles `REF BLOCK` / `TASK` / `ASSIST` / `MARK TRUTH` / `END SESSION` /
     `LEAVE STUDY`
   - the action slot, which performs whichever the middle slot names
6. **`END SESSION`** when done (`NEXT: END SESSION` → `END IT`).

`LEAVE STUDY` puts the panel back to the app's own flow without ending anything — that is how
you get from the study screen back to `BRING IN CHARACTER`.

**Watch the details panel before the participant takes the headset off.** The `written:` line
is the one that matters — a session that has not been written for minutes is a session that is
not recording, and this is the only place that shows.

The session file is also written whenever the headset comes off (the proximity sensor pauses
the app), which is how a session usually really ends.

---

## Marking ground truth

Every accuracy number is a comparison against a list of what is actually in the room and
where. Marking is done in the headset because the controller is already tracked in the same
frame the scan is recorded in — the conversion goes through `ObjectScanRecorder.WorldToRoom`,
the same one the scanner uses, so there is no offline alignment step to get wrong.

From a running session: **`MARK TRUTH`**.

- `LABEL:` on the panel cycles the class. **The index does not reset after each object**, so a
  room with four chairs is one cycle and four commits, not four trips through eighty classes.
- **Left index trigger** — places a corner at the controller's tip. The accurate route: the
  tracked position *is* the measurement.
- **Left grip** — places a corner by raycasting forward, for a corner you cannot reach.
  Flagged as `viaRaycast` in the file, because it carries the depth pass's error rather than
  the tracker's, and the two must never be pooled into one accuracy claim.
- Two opposite corners of the object's bounding box make one object. `UNDO LAST` removes the
  previous object; while a first corner is down it reads `CANCEL CORNER` instead.
- Anything with no COCO class gets the **`OUT OF VOCAB`** label. This is not bookkeeping — it
  is what keeps the vocabulary's limits out of the recall figure. A bookshelf YOLO has no word
  for is a fact about COCO; a missed chair is a fact about the scanner.

Truth is stored **per room**, not per session (`truth_R2.json`), and re-entering the mode loads
and appends — so a room can be measured over several visits and reused across participants.

---

## The Convai request budget

Every participant conversation turn is counted and written to `convaiTurns` (instants and
backend message ids — **never text**), with the total in `summary.convaiRequests`.

**What costs a request.** A turn is one utterance the Convai backend processed. Pointing costs
**nothing** — it stages context silently and never opens a turn. The task planner costs nothing
either; it talks to Anthropic or Ollama directly and never touches Convai.

**There are two ceilings and only one of them is real.**

- The **backend quota** is real, and the SDK announces it (`UsageLimitReached`) and then
  terminates the pipeline — she simply stops answering. It never reports how much is left.
  This is now recorded (`summary.convaiQuotaExhausted`) and shown on the panel in red words,
  because nothing listened for it before and the failure was indistinguishable from a network
  problem.
- The **budget** on the setup screen is a planning figure, not a measurement.

That is why `AT BUDGET` defaults to `WARN ONLY`. Enforcing a guessed ceiling ends a
participant's session while quota remained, which costs a slot and buys nothing. On
`HARD STOP` the microphone is held shut once the budget is spent, and the panel says so —
turn it on only once a pilot has established what the real ceiling is.

The panel line reads `convai : 12 turns, 28 left of 40`. **`NOT COUNTING` in that line means
the counter never attached** — a count of zero because nobody spoke and a count of zero because
nothing was listening look identical otherwise.

---

## The task, and every plan attempt

From the study screen: `NEXT: TASK` → `START TASK` / `END TASK`, and `NEXT: ASSIST` →
`MARK ASSIST` while one is open. The action slot's label always says which way the toggle will
go, so you are never guessing whether a press starts or stops the clock.

Task boundaries are set by hand because **nothing in the app means "the task began"** — the
participant says a sentence, and a plan request is neither the start nor the end of the task.
What the panel supplies is the two instants on the same clock as the plans and the utterances.

**A task left open is called out on the details panel** (`task: OPEN 214s`). A forgotten
`END TASK` is otherwise invisible and silently swallows the gap before whatever comes next.
Ending the session with one open stamps its end time but does **not** mark it completed.

`plans[]` gets a row for **every** attempt, including failures and cancellations. A latency
distribution built from successes alone describes a faster planner than the one anybody used:
the slow attempts are exactly the ones that time out or get abandoned. Cancellations are
counted apart from failures — the caller gave up, which is not the planner failing.

Three things this records that nothing did before:

- **Latency.** `PlanAsync` was never timed; only the request timeout bounded it. This is the
  number a person waiting in a headset cares about most.
- **`droppedLocations`.** The "dropped the location" warning was logged and never counted, and
  `step.HasPlace == false` cannot tell *"the planner said nowhere"* from *"the planner named a
  place the room no longer has"* — a modelling result and a stale scan respectively.
- **The condition, not just the outcome.** `placesOffered` **and** `hadRoomSummary` together
  are what mark an ungrounded attempt. Emptying the place list is not enough on its own: a
  summary naming the furniture reimports the vocabulary the ablation removes.

**The task text is not stored** — only `taskCharacters`. In a participant session the task is
the player's own words arriving through the Plan Task action's parameter, which makes it
participant speech. The offline plan harness subscribes to the same event and keeps its tasks
in full, because those are researcher-authored prompts.

---

## Utterance counts and timings

Every boundary in the conversation goes to `speech[]`: who spoke, whether they started,
stopped, were understood, finished a turn, were cut off, or got no answer at all.

**It is an event log, not a set of durations.** Nothing subtracts one timestamp from another on
device. How long she took to start answering, how long an utterance ran, whether she was talked
over — all of it is a subtraction between two rows that are both in the file. Computing
intervals on device would mean choosing now which ones matter, before anyone has seen a
session, and there is one shot per participant.

Two distinctions the log preserves that are easy to lose:

- **`stopped` and `final` are different events.** The first is when they finished speaking, the
  second is when the backend had finished understanding them. Measuring from only one folds the
  recognition delay into either the participant's thinking time or her response time.
- **`no-response` is not a failure.** The backend decided not to answer. Without the row it
  looks exactly like a reply that never arrived.

**No text, and nowhere to put it.** `characters` is a length, not a recording — it cannot be
read back into words, and it answers whether people give longer referring expressions when
naming is hard. Her text is not measured at all; her speech is timed acoustically, which
avoids a per-chunk row for every streamed reply and touches nothing she said.

`speech[]` joins to `convaiTurns[]` on `messageId`. `summary.participantUtterances` and
`summary.convaiRequests` count the same event and should agree — they are kept separately on
purpose, one being the bill and one the interaction, and a disagreement means one of the two
subscriptions missed events.

**This does not need the transcript system.** Everything comes off `ConvaiEvents`, which the
transport publishes unconditionally; `TranscriptSystemEnabled` gates only the presentation layer
(`ConvaiTranscripts`, the inspector relay, the transcript UIs). The obvious route through
`Transcripts.TurnCommitted` would have made every speech measurement depend on a setting anyone
can switch off from a settings panel. It is currently on (`_transcriptSystemEnabled: 1`); none
of this relies on that.

---

## The reference-resolution block

The study's primary outcome: cue an object, and see whether the app can resolve the
participant's reference to it.

From the study screen: **`NEXT: REF BLOCK` → `OPEN TRIALS`**, then `START BLOCK`. The block is
generated on first entry, from the objects in the **replayed scan** — so replay a scan first.

- 2 modalities (naming, pointing) × 4 distractor counts × `reps` (default 3) = **24 trials**.
- The order is shuffled from a seed derived from the participant id, and **the seed is
  recorded**. Same participant, same block, forever — so a re-run is comparable rather than new.
- The target is highlighted for ~2 s. **t0 is when the cue ends**, which is machine-precise and
  does not depend on a facilitator judging when the participant started.
- **Naming trials are capped at 2 attempts**, then scored incorrect. This is the single thing
  that makes the request budget bounded rather than hopeful — every naming attempt is a
  request. Pointing is uncapped because it costs nothing.
- A trial ends on a correct indication, the cap, a 60 s timeout, or `GIVE UP`.
- `END BLOCK` stops early and **keeps every trial already run**.

**Trials are scored on the scan file's id, never on the display name.** Display names are
invented at rebuild time by a greedy landmark pass and are not stable across rebuilds, so
"chair by the couch" can be a different chair after the next replay.

**How a distractor count is realised.** Not by hiding furniture — the room is the room. A trial
with N distractors is one whose *target* has N competitors sharing its label; four chairs make a
three-distractor target. Where the room cannot supply a condition exactly, the least crowded
available target is used and `actualDistractors` records what it really was — **analyse on that
field, not on `distractors`**. Where it cannot supply one at all, the condition is listed in
`referenceBlock.unavailable` and its cells are empty by construction, not by attrition.

**No referring expression is stored.** A naming attempt that resolved to nothing is recorded as
having happened and failed; what was said to cause that cannot be recovered. That is the
no-transcript-text decision, and it has a real cost worth stating in the write-up.

---

## The offline plan harness

**No headset needed, and no participant either.** At n = 4 the participant session supports a
within-participant probe and a qualitative account of one task each — four observations, not a
sample. This corpus is where the study's quantitative claims about plan quality actually come
from, and nothing about it depends on how many participants there were: a room does not need
someone standing in it to be planned about.

**`Tools > Convai Room > Plan Corpus Harness`.** Add saved `room_scan.json` files, one task per
line, tick the conditions (grounded / ungrounded) and backends (Anthropic / Ollama) to run, set
repeats, and press Start. It runs entirely in the Editor — no scene, no Play Mode.

- **Repeats = 1** is a full-factorial sweep: every scan × every task × every ticked condition ×
  every ticked backend, once each.
- **Repeats ≥ 2** is the consistency mode — the same cell run repeatedly, scored on how much the
  plan agrees with itself. 10 is the study's own target; narrow the scans, tasks or backends
  first, or the request count multiplies fast.

**It does not need a scene**, and that is the load-bearing fact that makes it possible at all.
`RoomTaskVocabulary.Collect` — what the live app calls for its place list — searches for
`ConvaiActionTarget` components in a *loaded scene*, and returns nothing here by construction.
`RoomScanContext.PlacesFor` and `.SummaryFor` are the offline replacements: the same naming and
description code the headset runs (`Choose`, `NameThem`, `BuildDescription`), reading only the
scan file. A harness that re-derived names its own way would silently plan against a differently
named room while looking exactly like the one participants saw.

**The player-distance trim has no player, so the room's centre stands in.** The live path caps
the offered places to `maxPlaces` nearest *the player*; offline there is no player, so
`PlacesFor`'s second parameter caps nearest *the room centre* instead — the same anchor every
object's own description already reports its distance from. This is a documented divergence,
not a hidden one: skipping the cap entirely would hand the planner a bigger vocabulary than any
participant's session ever did.

**Every attempt reuses `RoomPlannerClient.OnPlanAttempt`** — the event item 4 built specifically
so the participant recorder and this harness measure "what is a plan attempt" identically rather
than through two paths that could disagree.

Each row also gets, beyond what the live session records:
- **`roomMentions`** — steps whose *text* names something the room has, whether or not the step
  was grounded to it. On the grounded arm it is a specificity signal; on the ungrounded arm it
  is a **leakage check** — an ungrounded planner was told nothing about the room, so any mention
  of its furniture got there by guessing a common word, and a high count means the arm is less
  ungrounded than the condition claims.
- **`wordsPerStep`** — a crude, explicitly-labelled specificity proxy. The blind sheet is what
  actually answers specificity; this just flags a backend that has collapsed into one-word steps
  without reading four hundred plans to notice.

**Domain reload will eat a run in progress.** A script recompile while the harness is going
destroys its window state along with whatever request was in flight — nothing can prevent that.
The corpus is flushed to disk after *every* completed plan for exactly this reason: the worst a
reload costs is the one job that was running, not the run. Don't edit scripts mid-run.

**Output**, next to the corpus JSON in `<persistentDataPath>/plans/`:
- `plans_<timestamp>.json` — every plan, full text, full condition. Not de-identified; this is
  the researcher's own file.
- `.summary.txt` — per (condition, backend): success rate, latency p50/p90/p99, groundedness,
  dropped locations, words/step, room mentions, and every distinct failure reason **tallied
  verbatim** rather than sorted into an invented "schema violation" bucket that could misclassify
  an ambiguous case.
- `.blind.csv` + `.blind_key.csv` — the rating sheet and its key, **kept as separate files on
  purpose**. The sheet hides backend, model and condition — exactly what could bias a
  correctness/safety/appropriateness rating — while room and task stay visible since the rater
  needs them. The shuffle is seeded from the run id (not `string.GetHashCode()`, which is
  unstable across processes) so regenerating the report later reproduces the same blind ids
  rather than invalidating ratings already in progress.
- `.consistency.csv` — only written when `repeats > 1`: mean place-set agreement per (room,
  task, condition, backend) cell, over every pair of its repeats.

**`PlanScoring`** holds every piece of arithmetic above as pure, static functions — separated
from the harness specifically so `StudySelfCheck` can assert them against answers worked out on
paper. A network loop cannot be checked that way; this is where the claims actually live, so
this is the part that has to be provably right.

---

## The extent ablation

The scanner sizes an object by taking per-axis percentiles over a rolling window of
observation boxes. That replaced a monotonic union which only ever grew, so one bad depth hit
was baked in forever. Showing the percentile band is better needs the union re-run **on the
same observations** — and the scanner keeps none of them, so `ScanObservationLog` writes them
down as they arrive.

**Do not arm this during a participant session.** The comparison needs *scans*, not
participants: three researcher-driven instrumented scans answer it just as well, with none of
the risk of a disk write landing inside the phase whose accuracy is being reported on. Set
`ObjectScanRecorder.recordObservations` in the Inspector and the log opens itself — no session
required.

Then, in the Editor: **`Tools > Convai Room > Extent Ablation`**, pick the `.obs.csv`. It
writes a `.extents.csv` beside it with, per exported object, the shipped size, the recomputed
percentile size, the union size, and — where a truth file for that room exists — the per-axis
error of each against the true extent.

Three things that would silently give a wrong answer, all handled, all worth stating in the
write-up:

- `Absorb` does not copy a merged cluster's boxes — it moves their **8 corners** into the
  survivor's frame and re-fits. That inflates. A naive union over room-local corners comes out
  *smaller* than the old code ever did and flatters the intervention.
- The runtime percentile sees only the last `extentSampleCount` observations; the union sees
  all of them. That asymmetry **is** the intervention, not a confound — but it means the
  parameters have to travel with the data, which is why they are in the log header.
- The replay reports `replayedObservations` beside the logged count. If they disagree, the log
  lost records and that object's extents are not trustworthy.

So the baseline is precisely: *monotonic `Encapsulate` over every observation of the surviving
cluster, re-framed on merge the way `Absorb` re-frames*. "Union" on its own is ambiguous, and
the ambiguity is worth about 30% of the result.

A log that dropped records says so in its trailer and the tool refuses to treat it as valid.

---

## Files, and getting them off the headset

Everything lands in one folder so it is a single pull:

```
<persistentDataPath>/study/
  P03_R2_r01_20260903T141205Z.json          the session
  P03_R2_r01_20260903T141205Z.scan.1.json   a copy of room_scan.json, one per save
  P03_R2_r01_20260903T141205Z.obs.csv       only when the log was armed
  truth_R2.json                             ground truth, per room
```

On a Quest that is `/sdcard/Android/data/<package>/files/study`.

```bash
adb pull /sdcard/Android/data/<your.package>/files/study ./study-data
```

**The scan copies are the load-bearing artifact.** `room_scan.json` lives at one fixed path and
the next participant overwrites it, so without the per-session copy the subject of every
accuracy measurement is gone by the time anyone sits down to measure it. The recorder also
notices a save made with the **A button** — which bypasses the panel's guards entirely and
announces nothing — copies it anyway, and records it as a protocol deviation (`viaPanel:
false`).

---

## Checking it without a headset

**`Tools > Convai Room > Study Self-check`** — asserts and logs. Covers:

- **JsonUtility round trips**, in memory and through the real disk path. This is the one that
  matters: JsonUtility fails *silently* on a dictionary, a property where a field was meant, or
  a nested generic, and hands back an empty object with no error. On device that surfaces as a
  session file full of `[]` after the participant has gone home.
- session-id shape, filename legality, run-number derivation (including that scan copies are
  not miscounted as runs)
- corner-to-box maths, including that corner order does not matter
- label parsing, including the CRLF trim — a class of `"chair\r"` matches no scanned label and
  would read as a recall failure rather than the string bug it is
- **the reference-block DTOs**, including that an absent latency stays `-1` (a zeroed one reads
  offline as a correct answer given instantly) and that an unresolved naming attempt round-trips
  as an empty id rather than a null
- **the trial seed** — that it is a pure function of the participant id and a legal
  `System.Random` seed. Asserted as properties rather than against a hard-coded number: a
  constant worked out by hand would be a second implementation of FNV-1a, and a check that
  agrees with a mistake is worse than no check. `string.GetHashCode` is what this rules out —
  .NET randomises string hashing per process, so a seed from it differs between two runs of the
  same build and the recorded number would reproduce nothing
- **`PlanScoring`**, against numbers worked out by hand: whole-word matching against the exact
  counterexample in its own remark (`"table"` really is a substring of `"comfortable"`),
  words-per-step ignoring blank steps, nearest-rank percentiles including that the caller's list
  is not reordered, and place agreement's partial-overlap and mean-of-every-pair cases
- **the ablation replay**, against two synthetic logs with answers worked out on paper: one
  where the percentile must trim an outlier the union keeps (0.5 m vs 2.0 m), and one where a
  merge across a 90° frame must land at 1.5 m. Both were confirmed independently before being
  written down.

Compile-checking without opening Unity: copy `Assembly-CSharp.csproj` to `_compilecheck.csproj`
in the project root (the `.csproj` extension is mandatory) and build with the output
redirected. **`EnableDefaultItems` is `false` and the file list is explicit**, so new files must
be added as `<Compile Include>` lines or the build goes green having compiled none of them.

---

## What this phase does not do

- **Recall, precision, position error and duplicate rate are offline computations.** The app
  captures the inputs — the scan copy and the truth file, in the same coordinate frame — and
  does not score them.
- **The extent ablation only works on scans captured with the log armed.** Retrospective
  analysis of a participant scan is impossible by construction; the observations are gone.
- **Controller-tip marking needs reachable corners.** The raycast route is the escape hatch and
  is flagged separately, never pooled.
- Orientation-failure verdicts ("did the room come back rotated?") are a human judgement.
  `MARK NOTE` stamps the instant on the same clock as everything else so the call can be made
  from the recording rather than from memory.
