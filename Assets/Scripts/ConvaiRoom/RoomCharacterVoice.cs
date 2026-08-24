using System;
using System.Collections;
using System.Threading.Tasks;
using Convai.Runtime.Components;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ConvaiRoom
{
    /// <summary>
    /// Opens the Convai session for whichever character the spawner has put in the room, and
    /// closes it again when she is taken away.
    ///
    /// This exists because the room flow spawns its character rather than authoring it into the
    /// scene, and everything the SDK's own samples rely on assumes the opposite. In the LipSync
    /// sample the character sits in the hierarchy from the first frame: ConvaiManager finds it,
    /// ConvaiRoomManager composes a room around it, Connect On Start opens the session, and the
    /// character's own Auto Connect starts the conversation. Here there is no character at all
    /// until phase 2, so the room manager comes up with nothing to compose -- it says as much,
    /// "waiting for runtime character registration" -- and every one of those automatic steps
    /// is a step that never happens.
    ///
    /// So they are taken deliberately instead, in the one order that works:
    ///
    ///   1. Ask for the microphone. Hands-free is the whole interaction on a headset, and on
    ///      Android the permission has to be granted before the mic can be opened.
    ///   2. Name the character to ConvaiManager, which composes the room around her.
    ///   3. Start the conversation, which connects and then waits for the character to be ready.
    ///
    /// Nothing here decides what she says. Once the session is up the SDK owns the conversation
    /// end to end -- the mic goes to the server, the reply comes back as audio on her own
    /// AudioSource, and ConvaiLipSyncComponent drives her face from it. This just gets the
    /// session open and says, out loud and on the panel, which step failed when one does.
    /// </summary>
    public class RoomCharacterVoice : MonoBehaviour
    {
        private const string Tag = "[RoomVoice]";

        /// <summary>How far along the session is, in the terms the panel reports.</summary>
        public enum VoiceState
        {
            /// <summary>No character in the room, so there is nothing to connect.</summary>
            Idle,

            /// <summary>Waiting on the Android microphone permission dialog.</summary>
            WaitingForMicrophone,

            /// <summary>Connecting, or connected and waiting for the character to be ready.</summary>
            Connecting,

            /// <summary>Connected and ready: she is listening and can answer.</summary>
            Ready,

            /// <summary>A step failed. <see cref="LastFailure"/> says which.</summary>
            Failed
        }

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Where the character comes from. This follows its spawn and despawn events " +
                 "rather than being driven by the panel, so a character that appears for any " +
                 "reason gets a session and one that goes away takes its session with it.")]
        public RoomCharacterSpawner spawner;

        [Header("Microphone")]
        [Tooltip("Ask for the microphone permission before connecting.\n\n" +
                 "Leave this on. The room is in hands-free mode, so the mic is the only way " +
                 "into the conversation -- without it she connects, stands there and never " +
                 "hears a word. The SDK requests this permission from its settings panel, " +
                 "which this app does not use.")]
        public bool requestMicrophonePermission = true;

        [Tooltip("How long to wait for the permission dialog before connecting anyway. The " +
                 "dialog pauses the app, so this is wall-clock time with room for someone to " +
                 "read it.")]
        public float microphoneTimeoutSeconds = 30f;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>Where the session has got to. Read by the panel.</summary>
        public VoiceState State { get; private set; } = VoiceState.Idle;

        /// <summary>
        /// Why the session failed, short enough for the panel. Empty unless
        /// <see cref="State"/> is <see cref="VoiceState.Failed"/>.
        /// </summary>
        public string LastFailure { get; private set; } = "";

        /// <summary>The character this session belongs to, or null when there is none.</summary>
        public ConvaiCharacter Character { get; private set; }

        /// <summary>True while she is actually talking, which is the liveliest thing to report.</summary>
        public bool IsSpeaking => Character != null && Character.IsSpeaking;

        /// <summary>
        /// Whether the microphone was granted. False also covers "never asked", which is why
        /// the panel reports it separately from the connection rather than as a failure: a
        /// connected character with no mic is a real state, not a broken one.
        /// </summary>
        public bool HasMicrophone { get; private set; }

        private void Awake()
        {
            if (spawner == null) spawner = FindAnyObjectByType<RoomCharacterSpawner>();

            if (spawner == null)
            {
                Debug.LogError($"{Tag} No RoomCharacterSpawner in the scene, so no character " +
                               $"will ever arrive here and no session will ever open.", this);
            }
        }

        private void OnEnable()
        {
            if (spawner == null) return;

            spawner.OnSpawned += HandleSpawned;
            spawner.OnDespawning += HandleDespawning;

            // The spawner may already be holding a character -- this component can be enabled
            // after one is standing in the room, and then the spawn event has already been and
            // gone. Picking it up here means the session opens either way.
            if (spawner.IsSpawned) HandleSpawned(spawner.Character);
        }

        private void OnDisable()
        {
            if (spawner != null)
            {
                spawner.OnSpawned -= HandleSpawned;
                spawner.OnDespawning -= HandleDespawning;
            }

            Close();
        }

        // -----------------------------------------------------------------
        // Opening
        // -----------------------------------------------------------------

        private void HandleSpawned(GameObject character)
        {
            Close();

            if (character == null) return;

            Character = character.GetComponentInChildren<ConvaiCharacter>();

            if (Character == null)
            {
                Fail("character prefab has no ConvaiCharacter",
                    $"{Tag} '{character.name}' has no ConvaiCharacter component, so there is " +
                    $"nothing to open a session for. Add one to the prefab and set the " +
                    $"Character ID from your Convai dashboard.");
                return;
            }

            StartCoroutine(Open());
        }

        /// <summary>
        /// Walks the three steps in order. A coroutine rather than a plain method because the
        /// first step waits on a system dialog, and the two after it must not run until it is
        /// answered -- connecting first would open the session with the mic still shut.
        /// </summary>
        private IEnumerator Open()
        {
            State = VoiceState.WaitingForMicrophone;
            LastFailure = "";

            yield return EnsureMicrophone();

            // Deliberately not fatal. She can still speak without a microphone -- the audio
            // comes down from the server either way -- so a denied permission costs the ability
            // to talk BACK, not the session. Saying so is the point; failing here would hide a
            // working half of the feature behind a permission prompt someone dismissed.
            if (!HasMicrophone)
            {
                Debug.LogWarning($"{Tag} No microphone permission, so she will connect but " +
                                 $"cannot hear you. Grant it in the headset's app permissions, " +
                                 $"or press RESPAWN to be asked again.");
            }

            if (Character == null)
            {
                State = VoiceState.Idle;
                yield break;
            }

            if (!Register()) yield break;

            State = VoiceState.Connecting;
            Connect(Character);
        }

        /// <summary>
        /// Tells ConvaiManager which character to build the room around.
        ///
        /// One call does both halves of that: naming an explicit conversation target also makes
        /// her the owned character, because the manager resolves ownership from the target when
        /// nothing else has claimed it. Setting it re-runs ownership, which injects her
        /// dependencies and hands the room manager a room it can actually compose -- the step
        /// that was missing at startup, when there was no character to compose around.
        /// </summary>
        private bool Register()
        {
            var manager = ConvaiManager.ActiveManager;

            if (manager == null)
            {
                return Fail("no ConvaiManager in the scene",
                    $"{Tag} There is no active ConvaiManager, so nothing can compose a room " +
                    $"around the character. Add one to the scene -- GameObject > Convai > " +
                    $"Setup Required Components.");
            }

            manager.SetExplicitConversationTarget(Character);

            // Injection is what SetExplicitConversationTarget does on our behalf, and only if
            // Auto Inject is on. Without it StartConversationAsync throws a message about
            // dependencies that reads like an SDK bug rather than a tickbox on the manager.
            if (!Character.IsInjected)
            {
                return Fail("Convai manager did not inject the character",
                    $"{Tag} The character was registered but never injected, so the session " +
                    $"cannot start. Switch Auto Inject on in the ConvaiManager inspector.");
            }

            if (verboseLogging)
                Debug.Log($"{Tag} Registered '{Character.CharacterName}' " +
                          $"(id={Character.CharacterId}) as the conversation target.");

            return true;
        }

        /// <summary>
        /// Starts the conversation and waits for the character to report ready.
        ///
        /// Ready is a later signal than connected and the one worth waiting for: the room can be
        /// up while the character behind it is still being brought online, and a panel that says
        /// she is listening a couple of seconds before she is leaves you talking to nobody.
        /// </summary>
        private async void Connect(ConvaiCharacter character)
        {
            try
            {
                await character.StartConversationAsync().AsTask();

                if (IsStale(character)) return;

                State = VoiceState.Ready;

                Debug.Log($"{Tag} '{character.CharacterName}' is connected and ready " +
                          $"(microphone={HasMicrophone}). Talk to her.");
            }
            catch (Exception ex)
            {
                if (IsStale(character)) return;

                Fail(Summarise(ex),
                    $"{Tag} Could not start the conversation with " +
                    $"'{character.CharacterName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Whether a connect that has just finished belongs to a character we have since let go
        /// of -- despawned, or the whole phase left behind -- in which case its result must not
        /// be written to the state the panel reads.
        ///
        /// ReferenceEquals rather than ==, and that is the whole point of the method. Unity's
        /// equality treats a destroyed object as equal to null, so the exact case this is
        /// guarding -- our field cleared to null while the captured reference points at a
        /// character that has been destroyed -- compares as a MATCH under ==, and the stale
        /// result gets published as though it were current.
        /// </summary>
        private bool IsStale(ConvaiCharacter character) => !ReferenceEquals(Character, character);

        /// <summary>
        /// Trims an SDK exception down to something that fits the panel.
        ///
        /// Only the first sentence survives, which is where the SDK puts the part worth reading
        /// -- a missing API key says so up front and then spends three lines on where to set it.
        /// The one exception is cancellation, which arrives with no message at all and would
        /// otherwise reach the panel as a bare type name.
        /// </summary>
        private static string Summarise(Exception ex)
        {
            if (ex is OperationCanceledException)
                return "connected, but she never reported ready";

            var message = ex.Message;
            if (string.IsNullOrEmpty(message)) return ex.GetType().Name;

            var stop = message.IndexOf(". ", StringComparison.Ordinal);
            if (stop > 0) message = message.Substring(0, stop);

            return message.Length <= 80 ? message : message.Substring(0, 77) + "...";
        }

        // -----------------------------------------------------------------
        // Microphone
        // -----------------------------------------------------------------

        /// <summary>
        /// Gets the Android microphone permission, asking for it if this is the first run.
        ///
        /// The SDK never asks. It has a permission service, but the only thing that calls it is
        /// the settings panel's microphone test, which this app does not use -- so on a fresh
        /// install the mic is simply never granted and the character hears silence forever. The
        /// manifest entry is necessary too and equally invisible: without RECORD_AUDIO in
        /// Assets/Plugins/Android/AndroidManifest.xml this request is refused instantly, with
        /// no dialog and nothing in the log to say why.
        /// </summary>
        private IEnumerator EnsureMicrophone()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                HasMicrophone = true;
                yield break;
            }

            if (!requestMicrophonePermission)
            {
                HasMicrophone = false;
                yield break;
            }

            var answered = false;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => answered = true;
            callbacks.PermissionDenied += _ => answered = true;

            Permission.RequestUserPermission(Permission.Microphone, callbacks);

            if (verboseLogging) Debug.Log($"{Tag} Asked for the microphone permission.");

            // Polled as well as waited on. The dialog takes focus away from the app, and the
            // callbacks do not always survive that -- the granted flag is the reliable answer
            // and the callback only makes the common case immediate.
            var deadline = Time.realtimeSinceStartup + microphoneTimeoutSeconds;
            while (!answered && Time.realtimeSinceStartup < deadline)
            {
                if (Permission.HasUserAuthorizedPermission(Permission.Microphone)) break;
                yield return null;
            }

            HasMicrophone = Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
            // Nothing to grant off-device: the Editor uses the desktop microphone directly.
            HasMicrophone = true;
            yield break;
#endif
        }

        // -----------------------------------------------------------------
        // Closing
        // -----------------------------------------------------------------

        private void HandleDespawning() => Close();

        /// <summary>
        /// Ends the session and forgets the character.
        ///
        /// Called before the character is destroyed rather than after, which is the only order
        /// that lets the session be closed politely -- once the GameObject is gone there is
        /// nothing left to call StopConversationAsync on and the room is left holding an agent
        /// that has vanished.
        ///
        /// The stop is fired and not awaited on purpose. This runs on the way out of the
        /// character phase and the caller is about to destroy the object regardless; waiting on
        /// a network round trip would stall the transition for no gain.
        /// </summary>
        public void Close()
        {
            // Open is the only coroutine this component runs, and stopping it mid-dialog is
            // exactly what leaving the character phase should do -- otherwise a permission
            // answered after the character has gone goes on to connect a session for her.
            StopAllCoroutines();

            var character = Character;
            Character = null;
            State = VoiceState.Idle;
            LastFailure = "";

            if (character == null) return;

            // A connect still in flight is deliberately left alone. It is cancelled by the
            // character's own destroy token a moment from now, and there is nothing to close
            // politely until the session is actually up.
            if (character.IsInConversation)
            {
                if (verboseLogging)
                    Debug.Log($"{Tag} Closing the session with '{character.CharacterName}'.");

                character.StopConversationAsync().AsTask().ContinueWith(
                    task => Debug.LogWarning(
                        $"{Tag} The session did not close cleanly: " +
                        $"{task.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            // Cleared last. Changing ownership makes the manager re-compose the room, and doing
            // that before the stop above would have it rebuild around a session that is halfway
            // through being closed.
            if (ConvaiManager.ActiveManager != null)
                ConvaiManager.ActiveManager.SetExplicitConversationTarget(null);
        }

        private bool Fail(string summary, string logLine)
        {
            State = VoiceState.Failed;
            LastFailure = summary;
            Debug.LogWarning(logLine);
            return false;
        }
    }
}
