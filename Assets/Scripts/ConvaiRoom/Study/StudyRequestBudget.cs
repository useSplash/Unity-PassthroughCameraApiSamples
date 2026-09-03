using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Facades;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Counts the participant's conversation turns, holds them against a per-participant
    /// budget, and -- only when explicitly told to -- refuses further turns once it is spent.
    ///
    /// WHY THIS IS NOT A MONOBEHAVIOUR. Everything else the study adds is a component on the
    /// room manager, and the scene-setup instructions are a list of AddComponents someone
    /// follows on the machine the headset is plugged into. This is owned and ticked by
    /// <see cref="StudySessionRecorder"/> instead, so that list does not grow a seventh entry
    /// for something with no Inspector surface of its own. It is also the only file in the
    /// study that touches the Convai SDK, which keeps the recorder the pure listener its own
    /// doc comment claims it is.
    ///
    /// WHAT COUNTS AS A REQUEST. <c>OnFinalUserTranscriptionReceived</c>, which the SDK
    /// publishes once per participant utterance the backend has actually processed
    /// (PlayerConversationInput.HandleProcessedFinal). The alternatives were all wrong:
    /// <c>OnInteractionCreated</c> fires per interaction context, not per turn;
    /// <c>OnPlayerTranscriptReceived</c> fires on every interim ASR fragment; and
    /// <c>ConvaiCharacter.OnTranscriptReceived</c> is her reply, not the participant's speech.
    /// Pointing costs nothing at all -- RoomScanPointer stages context with
    /// ConvaiRespondMode.Silent -- and the planner never touches Convai, so neither is counted.
    ///
    /// The count is therefore of turns the BACKEND accepted, which is the same thing the quota
    /// bills for. It arrives after the request is already in flight, which is why enforcement
    /// below refuses the NEXT turn rather than the one that crossed the line. Nothing available
    /// in the SDK could refuse the one in flight; by the time anything local knows a turn
    /// happened, it has happened.
    ///
    /// WHY ENFORCEMENT IS OFF BY DEFAULT. There are two different ceilings here and only one of
    /// them is real. The backend enforces an actual quota and says so with
    /// <c>UsageLimitReached</c> -- after which, per the SDK's own remark on that event, "the
    /// pipeline is terminated immediately". Its exact value is not something the app can read;
    /// nothing in the SDK reports a remaining count. The 30-40 in the protocol is a planning
    /// figure written to size the trial block, not a number anybody measured. Enforcing a
    /// guessed ceiling would cut a participant off while quota remained, which costs a session
    /// and buys nothing. So the budget warns by default, and <see cref="Enforce"/> is the
    /// deliberate opt-in for a pilot that has established what the real ceiling is.
    /// </summary>
    public class StudyRequestBudget
    {
        private const string Tag = "[StudyBudget]";

        /// <summary>
        /// How many turns are allowed, or zero for "count but never refuse".
        ///
        /// Zero rather than a nullable or a separate switch, because this is set from a cycled
        /// panel field where "off" has to be one of the values in the ring.
        /// </summary>
        public int Budget;

        /// <summary>
        /// Whether to actually refuse turns once the budget is spent, as opposed to merely
        /// saying so on the panel.
        ///
        /// Off unless a pilot has established that the real backend ceiling is at or below
        /// <see cref="Budget"/>. See the class remark: enforcing a guessed ceiling ends a
        /// session that had quota left.
        /// </summary>
        public bool Enforce;

        public bool verboseLogging;

        /// <summary>Turns counted since the last <see cref="Reset"/>.</summary>
        public int Used { get; private set; }

        /// <summary>
        /// Turns left, or <see cref="int.MaxValue"/> when there is no budget. Never negative:
        /// the interesting fact past the line is "spent", and a negative remaining reads on the
        /// panel as though it were still counting down.
        /// </summary>
        public int Remaining => Budget <= 0 ? int.MaxValue : Mathf.Max(0, Budget - Used);

        /// <summary>Whether the budget is spent. Always false when there is no budget.</summary>
        public bool IsSpent => Budget > 0 && Used >= Budget;

        /// <summary>Whether the microphone is currently being held shut by this.</summary>
        public bool IsHolding { get; private set; }

        /// <summary>Whether the SDK is bound, which is what makes the count trustworthy.</summary>
        public bool IsBound => _binder.IsBound;

        /// <summary>
        /// Whether the backend said the real quota is gone. This is the unrecoverable one --
        /// the SDK terminates the pipeline straight after the message, so she stops answering
        /// and nothing local can bring her back.
        /// </summary>
        public bool QuotaExhausted { get; private set; }

        /// <summary>"daily", "monthly", "additional" -- whatever the backend called it.</summary>
        public string QuotaType { get; private set; } = "";

        public string QuotaMessage { get; private set; } = "";

        /// <summary>Raised per counted turn, with the running count and the backend message id.</summary>
        public event Action<int, string> OnTurn;

        /// <summary>Raised once, the first time the budget is spent.</summary>
        public event Action OnBudgetSpent;

        /// <summary>Raised when the backend reports the real quota gone: type, then message.</summary>
        public event Action<string, string> OnQuotaExhausted;

        // -----------------------------------------------------------------

        /// <summary>
        /// Keeps the subscription alive across the manager coming up and being replaced. Shared
        /// with <see cref="StudyTranscriptWatch"/>, which needs exactly the same awkward
        /// lifecycle -- see <see cref="ConvaiEventBinder"/> for why that is not inline here.
        /// </summary>
        private readonly ConvaiEventBinder _binder = new ConvaiEventBinder();

        private ConvaiRoomManager _room;
        private bool _announcedSpent;
        private bool _wired;

        /// <summary>
        /// Message ids already counted.
        ///
        /// The SDK resolves a message id for every processed final and there is no promise it
        /// publishes each exactly once -- a reconnect that replays the tail of a conversation
        /// would count those turns twice, and an over-count is the direction that ends a
        /// session early. Ids are a few dozen strings per participant, so the set costs
        /// nothing. Turns that arrive with no id at all are counted anyway rather than dropped;
        /// an under-count is the failure that spends real quota silently.
        /// </summary>
        private readonly HashSet<string> _counted = new HashSet<string>();

        // -----------------------------------------------------------------
        // Driving
        // -----------------------------------------------------------------

        /// <summary>
        /// Binds to the SDK if it is up, and re-asserts the hold if one is in force. Call every
        /// frame; it is cheap and does nothing on all but the frames that matter.
        /// </summary>
        public void Tick()
        {
            // Wired on the first tick rather than in a constructor, so verboseLogging set by
            // the recorder after construction is the value the binder actually uses.
            if (!_wired)
            {
                _wired = true;
                _binder.verboseLogging = verboseLogging;
                _binder.Bound += HandleBound;
                _binder.Unbinding += HandleUnbinding;
            }

            _binder.Tick();
            ApplyHold();
        }

        /// <summary>
        /// Starts the count again from zero, for a new participant.
        ///
        /// The budget is per participant, so this runs at session start. What it deliberately
        /// does NOT clear is <see cref="QuotaExhausted"/>: the backend quota is per account and
        /// per day, and a session started after one was hit is a session that will not work.
        /// Clearing it here would hide exactly that from the facilitator.
        /// </summary>
        public void Reset()
        {
            Used = 0;
            _announcedSpent = false;
            _counted.Clear();
        }

        /// <summary>
        /// Unsubscribes and gives the microphone back. Called from OnDisable and at session end
        /// so a hold cannot outlive the thing that took it -- an app left with a muted mic and
        /// nothing holding it is one that has to be restarted to be usable again.
        /// </summary>
        public void Detach()
        {
            _binder.Release();

            if (!IsHolding) return;

            IsHolding = false;
            var room = Room();
            if (room != null && room.IsMicMuted) room.SetMicMuted(false);
        }

        // -----------------------------------------------------------------
        // SDK binding
        // -----------------------------------------------------------------

        /// <summary>
        /// Takes the two events the budget is made of.
        ///
        /// Both come off <c>ConvaiEvents</c> rather than <c>ConvaiManager.Transcripts</c>, and
        /// that is what keeps the count independent of the <c>TranscriptSystemEnabled</c>
        /// setting: the transport publishes these unconditionally, and that flag gates only the
        /// presentation layer. A quota counter that stopped counting because somebody switched
        /// transcripts off would be the worst kind of silent failure.
        /// </summary>
        private void HandleBound(ConvaiEvents events)
        {
            events.OnFinalUserTranscriptionReceived += HandleFinalUserTranscription;
            events.OnUsageLimitReached += HandleUsageLimitReached;

            Debug.Log($"{Tag} Counting participant turns (budget {DescribeBudget()}, " +
                      $"{(Enforce ? "ENFORCED" : "warn only")}).");
        }

        private void HandleUnbinding(ConvaiEvents events)
        {
            events.OnFinalUserTranscriptionReceived -= HandleFinalUserTranscription;
            events.OnUsageLimitReached -= HandleUsageLimitReached;

            // Dropped with the manager it came from: the room manager is a component on that
            // GameObject, so a cached one outlives its manager only as a stale reference.
            _room = null;
        }

        /// <summary>
        /// One participant turn.
        ///
        /// The whole body is guarded. This runs inside the SDK's event hub, on the path that
        /// also delivers the transcript to everything else subscribed -- an exception thrown
        /// back into it from the study's counter could take the conversation down with it, and
        /// losing the session to the instrumentation is worse than losing the count.
        ///
        /// The text on this event is deliberately untouched. Participant speech does not go on
        /// disk anywhere in this study; the id and the instant are the whole record.
        /// </summary>
        private void HandleFinalUserTranscription(FinalUserTranscriptionReceived e)
        {
            try
            {
                var id = e.MessageId ?? "";

                if (!string.IsNullOrEmpty(id) && !_counted.Add(id)) return;

                Used++;
                OnTurn?.Invoke(Used, id);

                if (verboseLogging)
                    Debug.Log($"{Tag} Turn {Used} ({DescribeRemaining()}).");

                if (!IsSpent || _announcedSpent) return;

                _announcedSpent = true;
                OnBudgetSpent?.Invoke();

                Debug.LogWarning($"{Tag} The request budget of {Budget} is spent after " +
                                 $"{Used} turns. " +
                                 $"{(Enforce ? "The microphone is being held shut." : "Not enforcing -- turns continue.")}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} Counting a turn threw, and the count is now short: {ex}");
            }
        }

        /// <summary>
        /// The real ceiling, reported by the backend.
        ///
        /// Loud, and recorded, because this is the failure the whole item exists to make
        /// visible. Nothing in the app listened for it before: the pipeline would terminate,
        /// she would go quiet mid-sentence, and the facilitator would be left guessing whether
        /// it was the network, the headset, or the room. Everything recorded after this point
        /// is a session with no conversation in it.
        /// </summary>
        private void HandleUsageLimitReached(UsageLimitReached e)
        {
            try
            {
                QuotaExhausted = true;
                QuotaType = e.QuotaType ?? "";
                QuotaMessage = e.Message ?? "";

                Debug.LogError($"{Tag} CONVAI QUOTA EXHAUSTED ({QuotaType}): {QuotaMessage}. " +
                               $"The pipeline is terminated -- she will not answer again this " +
                               $"session. Counted {Used} turns before this.");

                OnQuotaExhausted?.Invoke(QuotaType, QuotaMessage);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} Handling the quota event threw: {ex}");
            }
        }

        // -----------------------------------------------------------------
        // Refusing turns
        // -----------------------------------------------------------------

        /// <summary>
        /// Holds the microphone shut for as long as the budget is spent and enforcement is on.
        ///
        /// Muting is the only lever there is. The room is hands-free, so a turn begins when the
        /// participant speaks and the audio track is already on its way; there is no "send"
        /// to intercept. Shutting the mic is what RoomCharacterVoice already does around her
        /// own turns, so this is the same mechanism rather than a second one.
        ///
        /// Re-asserted every tick rather than latched, for the reason RoomCharacterVoice.Hold
        /// gives for doing the same: the SDK reopens the mic on its own across a reconnect, and
        /// the voice gate's Release runs whenever she finishes speaking. A hold pressed once
        /// would be lifted by either within a frame or two. Reading the state back first keeps
        /// this a no-op on every tick but the one it matters on -- calling SetMicMuted
        /// unconditionally writes through to the audio track and logs an SDK line each time.
        /// </summary>
        private void ApplyHold()
        {
            var wanted = Enforce && IsSpent;

            if (wanted)
            {
                var room = Room();
                if (room == null) return;

                if (!room.IsMicMuted) room.SetMicMuted(true);

                if (IsHolding) return;

                IsHolding = true;
                Debug.LogWarning($"{Tag} Budget spent -- the microphone is held shut. " +
                                 $"Raise the budget or turn enforcement off to continue.");
                return;
            }

            if (!IsHolding) return;

            // Released unconditionally rather than only when she is quiet. If this lands
            // mid-sentence the voice gate re-shuts it on its very next Update, since that gate
            // re-decides from scratch every frame; the alternative is duplicating its
            // speaking-state logic here so the two can disagree about who is holding what.
            IsHolding = false;

            var current = Room();
            if (current != null && current.IsMicMuted) current.SetMicMuted(false);

            Debug.Log($"{Tag} The microphone is open again.");
        }

        /// <summary>
        /// The room manager, which is where the microphone lives.
        ///
        /// Reached through GetComponent for the same reason RoomCharacterVoice does it: the
        /// manager's own accessor for it is internal to the SDK. Cached, with Unity's null
        /// check, so a manager destroyed between scenes is looked up again rather than handed
        /// back as a live reference.
        /// </summary>
        private ConvaiRoomManager Room()
        {
            if (_room != null) return _room;

            var manager = ConvaiManager.ActiveManager;
            if (manager == null) return null;

            _room = manager.GetComponent<ConvaiRoomManager>();
            return _room;
        }

        // -----------------------------------------------------------------
        // Reporting
        // -----------------------------------------------------------------

        private string DescribeBudget() => Budget <= 0 ? "none" : Budget.ToString();

        private string DescribeRemaining() =>
            Budget <= 0 ? "no budget" : $"{Remaining} left of {Budget}";

        /// <summary>
        /// The panel line. Short: it shares a small readout with everything else the study
        /// shows, and the facilitator reads it at a glance mid-session.
        ///
        /// The unbound case is stated rather than hidden. A count of zero because nobody has
        /// spoken and a count of zero because the counter never attached look identical
        /// otherwise, and only one of them means the budget is unguarded.
        /// </summary>
        public string Describe()
        {
            if (QuotaExhausted)
                return $"convai    : QUOTA GONE ({QuotaType}) after {Used} turns -- she is offline";

            var line = $"convai    : {Used} turns, {DescribeRemaining()}";

            if (!IsBound) return line + " (NOT COUNTING - no session)";
            if (IsHolding) return line + " -- SPENT, mic held";
            if (IsSpent) return line + " -- SPENT (not enforced)";

            return line;
        }
    }
}
