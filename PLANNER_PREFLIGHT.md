# Planner Preflight

What has to be true before you launch, so Camila can work out a plan instead of
improvising one in conversation.

The planner runs on a model on your own machine through Ollama. It is free --- no
account, no quota, nothing to run out of. `Max Tokens` in the inspector is a
generation-length cap, not a balance.

---

## Every launch

Three things, in this order, on the PC the headset is tethered to. This is the part
that breaks between sessions.

### 1. Confirm Ollama is up and the model is there

```bash
ollama list
```

You want `qwen2.5:7b` in the list. A connection error means the server is not
running.

An empty `ollama ps` is **not** a fault. It only shows models currently held in
memory, and Ollama unloads them after a few minutes idle. It is only meaningful
straight after a request.

### 2. Open the tunnel

```bash
adb reverse tcp:11434 tcp:11434
```

This puts the PC's own loopback in front of the headset's, which is why the app is
pointed at `127.0.0.1` rather than a LAN address. Ollama on its default loopback
binding is correct here --- you do **not** need `OLLAMA_HOST=0.0.0.0`.

```bash
adb reverse --list
```

Expect `tcp:11434 tcp:11434`.

> **The one that catches you out.** `adb reverse` does not survive unplugging the
> headset, a device reboot, or an adb server restart. It is the usual reason
> planning worked yesterday and does not today, and it fails as *"I couldn't reach
> my planner just then."*

### 3. Launch the app fresh

A clean launch, not a reconnect. The `Plan Task` and `Step Through Plan`
descriptions are sent to the Convai backend when a session opens, so a reconnect
keeps whatever the last session was told.

---

## Once per clone

Independent of each other, no particular order.

### Convai API key --- required

Unity menu -> **Convai -> Settings**, paste the key. It lands in
`Assets/Resources/ConvaiSettings.asset`. Without it she does not speak at all, so
this fails long before planning does.

**Never commit that file.** The key is obfuscated, not encrypted, and `origin` is a
public fork. It is held out of the tree with `git update-index --skip-worktree`;
undo with `--no-skip-worktree` if it ever needs a real edit.

### Pull the planner model

```bash
ollama pull qwen2.5:7b
```

Ollama does not fetch on demand --- it answers with an error naming the model,
which surfaces in the headset as *"I couldn't work out how to..."*

### Check the planner's scene settings

On the `Room Scan` object, the `RoomPlannerClient` component:

| Field | Value |
| --- | --- |
| Backend | `Ollama` |
| Ollama Url | `http://127.0.0.1:11434` |
| Ollama Model | `qwen2.5:7b` |
| Timeout Seconds | `40` --- stays under Plan Task's own 45s |

### Anthropic planner key --- only if you switch backend

Not needed on Ollama; the code does not even look for it. If you switch `Backend`
to Anthropic, the key is read from two places, in this order:

```
/sdcard/Android/data/com.samples.passthroughcamera/files/planner_key.txt
Assets/Resources/planner_key.txt
```

The device path is the useful one --- push it with `adb push` and it is picked up
within about five seconds, no rebuild and no restart. The bundled copy is
gitignored and only makes a build self-contained.

---

## Confirm it is live

The health probe reports the planner on the panel in-headset, so you can check
without pulling a log:

```
planner  via=ollama  at=http://127.0.0.1:11434/api/chat
         model=qwen2.5:7b  places=14  no plan
```

The line reads ok once an address and a model are set. It deliberately does **not**
ping the server: reachability costs a round trip, and being unreachable reports
itself the moment somebody asks for a plan.

`places=` is the number worth watching. A configured planner with zero places means
the scan found nothing to ground steps against, and every step comes back unplaced.

---

## If she cannot plan

Every failure says which one it is out loud. Match her sentence to the row, then
read the matching `[RoomPlanner]` line:

```bash
adb logcat -c; adb logcat -s Unity:V
```

| What she says | What is wrong |
| --- | --- |
| *"I couldn't reach my planner just then."* | The request went out and died. Tunnel down, Ollama asleep, or cleartext blocked by a stale build. |
| *"I couldn't work out how to X, sorry."* | Ollama answered but the plan was unusable --- model not pulled, or the reply ignored the schema. The raw reply is in the log. |
| *"My planner isn't set up on this headset..."* | A component is missing in the scene. The log line names which one. |
| *"I didn't catch what you wanted me to plan."* | The action fired without its `task` parameter. May be a one-off turn; if it repeats, check the parameter on the Plan Task action. |
| None of the above --- she just improvises a list | The action never reached the executor. That is Convai action config, not the planner. Expect no `[RoomPlanner]` lines at all. |

---

## Background

Things that are settled, but expensive to re-derive when something goes wrong.

### Cleartext http

The app talks to Ollama over plain http, which Android has refused by default since
API 28. `android:usesCleartextTraffic="true"` in `Assets/Plugins/Android/AndroidManifest.xml`
is what permits it, with `tools:replace="android:usesCleartextTraffic"` to win the
merge against `com.unity.dt.app-ui`, which sets the same attribute to false.

The refusal happens *before* the socket is opened, so it arrives as a connection
error --- the same shape a sleeping server has, which sends you off to check the one
thing that is working.

Both attributes only exist in an installed APK. A manifest change means a rebuild,
not a redeploy.

### Why a silent action is the worst failure

An executor that returns `ConvaiActionExecutionResult.Unhandled` is **force-silenced
by the SDK**. `ConvaiActionFeedbackComposer` composes a batch whose steps are all
Unhandled with `forceSilent: true`, which overrides every feedback mode on the
character --- no setting on `ConvaiActionFeedbackRelay` can voice one.

When that happens the action completes, says nothing, and the Convai backend fills
the silence by improvising: *unable to make a formal plan*, followed by a plan in
prose that never reached `RoomTaskPlan` and so was never on the panel. Several
rounds of diagnosis were spent reading those improvisations as though they came from
this project's code.

So the guards in `RoomTaskPlanner` and `RoomTaskStepExecutor` return `Answered`
instead. Only `Answer` reaches the character; `Message` is documented as text she
never hears. If you add a guard to either executor, do not reach for `Unhandled`
however honest it looks.

### A latent one: `num_ctx` is never set

`BuildOllamaRequest` sets `num_predict` but not `num_ctx`, and Ollama defaults that
to 4096. With `maxPlaces: 30` and their descriptions in the system prompt, a busy
scan could overrun the context window. It has not bitten yet. If plans start coming
back thin or ignoring places that are plainly in the room, this is the first thing
to look at.
