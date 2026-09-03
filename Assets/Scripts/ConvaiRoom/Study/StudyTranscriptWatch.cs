using System;
using Convai.Domain.DomainEvents.Participant;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Runtime.Facades;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Writes down when each side of the conversation spoke, and for how long. Never what was
    /// said.
    ///
    /// WHAT THIS IS FOR. The request counter answers "how much quota did this participant
    /// spend"; this answers "what did the conversation look like". Those are different
    /// questions with different units -- one counts turns the backend billed for, the other
    /// times an exchange -- and the two deliberately overlap on one event rather than sharing a
    /// counter, because a shared counter is a single number that has to mean both things and
    /// ends up meaning neither. They join offline on <c>messageId</c>.
    ///
    /// NO TEXT, ANYWHERE. Participant speech does not go on disk in this study, and this is the
    /// component that would have been the easy place to break that. What is kept is the length
    /// of an utterance in characters, which is a measure and not a recording: it cannot be read
    /// back into words, and it answers a real question -- whether people give longer referring
    /// expressions when naming is hard. Her text is not measured at all; see
    /// <see cref="HandleCharacterSpeech"/> for why.
    ///
    /// AN EVENT LOG, NOT DURATIONS. Nothing here subtracts one timestamp from another. Every
    /// duration and every latency the analysis could want -- how long she took to start
    /// answering, how long a participant's utterance ran, whether she was talked over -- is a
    /// subtraction between two rows that are both in the file. Computing them on device would
    /// mean choosing now which intervals matter, before anyone has seen a session, and there is
    /// exactly one shot per participant. This is the same reasoning that made
    /// StudySessionRecorder an event log rather than a set of running totals.
    ///
    /// IT DOES NOT NEED THE TRANSCRIPT SYSTEM. Everything here comes off <c>ConvaiEvents</c>,
    /// which the transport publishes unconditionally. <c>TranscriptSystemEnabled</c> gates only
    /// the presentation layer -- <c>ConvaiTranscripts</c>, the inspector relay and the
    /// transcript UIs -- so the obvious route through <c>Transcripts.TurnCommitted</c> would
    /// have made every speech measurement in the study depend on a project setting somebody can
    /// switch off from a settings panel. It is currently on; this does not rely on that.
    /// </summary>
    public class StudyTranscriptWatch
    {
        private const string Tag = "[StudySpeech]";

        public bool verboseLogging;

        /// <summary>Participant utterances the backend processed.</summary>
        public int ParticipantUtterances { get; private set; }

        /// <summary>Character turns that finished.</summary>
        public int CharacterUtterances { get; private set; }

        /// <summary>Her turns that were cut off, which is the participant talking over her.</summary>
        public int Interruptions { get; private set; }

        /// <summary>Turns where the backend decided not to answer at all.</summary>
        public int NoResponses { get; private set; }

        public bool IsBound => _binder.IsBound;

        /// <summary>Raised per recorded event, for the recorder to write down.</summary>
        public event Action<SpeechEventEntry> OnSpeechEvent;

        // -----------------------------------------------------------------

        private readonly ConvaiEventBinder _binder = new ConvaiEventBinder();
        private bool _wired;

        /// <summary>Where session-relative time comes from. Injected, so the file has one clock.</summary>
        public Func<float> TimeSource;

        public void Tick()
        {
            if (!_wired)
            {
                _wired = true;
                _binder.verboseLogging = verboseLogging;
                _binder.Bound += HandleBound;
                _binder.Unbinding += HandleUnbinding;
            }

            _binder.Tick();
        }

        /// <summary>Starts the counts again for a new participant.</summary>
        public void Reset()
        {
            ParticipantUtterances = 0;
            CharacterUtterances = 0;
            Interruptions = 0;
            NoResponses = 0;
        }

        public void Detach() => _binder.Release();

        // -----------------------------------------------------------------
        // Subscriptions
        // -----------------------------------------------------------------

        private void HandleBound(ConvaiEvents events)
        {
            events.OnPlayerSpeakingStateChanged += HandlePlayerSpeaking;
            events.OnFinalUserTranscriptionReceived += HandlePlayerFinal;
            events.OnCharacterSpeechStateChanged += HandleCharacterSpeech;
            events.OnCharacterTurnCompleted += HandleCharacterTurnCompleted;
            events.OnLlmNoResponseReceived += HandleNoResponse;

            Debug.Log($"{Tag} Watching utterance timings (counts and instants only, no text).");
        }

        private void HandleUnbinding(ConvaiEvents events)
        {
            events.OnPlayerSpeakingStateChanged -= HandlePlayerSpeaking;
            events.OnFinalUserTranscriptionReceived -= HandlePlayerFinal;
            events.OnCharacterSpeechStateChanged -= HandleCharacterSpeech;
            events.OnCharacterTurnCompleted -= HandleCharacterTurnCompleted;
            events.OnLlmNoResponseReceived -= HandleNoResponse;
        }

        // -----------------------------------------------------------------
        // The participant
        // -----------------------------------------------------------------

        /// <summary>
        /// The acoustic boundaries of a participant utterance.
        ///
        /// Kept separately from the processed final below because they are not the same instant
        /// and the gap between them is worth having: "stopped" is when they finished speaking,
        /// "final" is when the backend had finished understanding it. Anything computed from
        /// only one of the two would fold the recognition delay into either the participant's
        /// thinking time or her response time, depending which end you measured from.
        /// </summary>
        private void HandlePlayerSpeaking(PlayerSpeakingStateChanged e) =>
            Record("participant", e.IsSpeaking ? "started" : "stopped", "", 0);

        /// <summary>
        /// One participant utterance, as the backend understood it.
        ///
        /// The same event the request counter uses, subscribed independently. The length is
        /// taken here and the text is not: <c>e.Text</c> is in scope on this line and goes no
        /// further, which is the whole discipline in one place.
        /// </summary>
        private void HandlePlayerFinal(FinalUserTranscriptionReceived e)
        {
            ParticipantUtterances++;
            Record("participant", "final", e.MessageId, (e.Text ?? "").Length);
        }

        // -----------------------------------------------------------------
        // The character
        // -----------------------------------------------------------------

        /// <summary>
        /// When she started and stopped making sound.
        ///
        /// Her speech is measured acoustically rather than through
        /// <c>OnCharacterTranscriptReceived</c>, which arrives in chunks as the reply streams
        /// and would put a variable number of rows in the file per turn for no gain. What the
        /// study wants from her side is timing -- how long the participant waited, how long she
        /// talked -- and the audio boundaries are that, exactly, without touching her text at
        /// all.
        /// </summary>
        private void HandleCharacterSpeech(CharacterSpeechStateChanged e) =>
            Record("character", e.IsSpeaking ? "started" : "stopped", e.UtteranceId, 0);

        /// <summary>
        /// Her turn, finished or cut off.
        ///
        /// <c>WasInterrupted</c> is worth its own kind rather than a flag on the row: being
        /// talked over is a fact about the interaction, and in a headset it is usually the
        /// microphone gate letting her own voice back in rather than the participant
        /// deliberately interrupting. Either way it is something to be able to count.
        /// </summary>
        private void HandleCharacterTurnCompleted(CharacterTurnCompleted e)
        {
            CharacterUtterances++;

            if (e.WasInterrupted) Interruptions++;

            Record("character", e.WasInterrupted ? "interrupted" : "turn-done", "", 0);
        }

        /// <summary>
        /// The backend decided not to answer.
        ///
        /// Recorded because it is otherwise invisible and looks exactly like a failure: the
        /// participant spoke, the turn was spent, and nothing came back. Distinguishing "she
        /// chose not to reply" from "the reply never arrived" is not something the timings
        /// alone can do.
        /// </summary>
        private void HandleNoResponse(LlmNoResponseReceived e)
        {
            NoResponses++;
            Record("character", "no-response", "", 0);
        }

        // -----------------------------------------------------------------

        /// <summary>
        /// Adds one row.
        ///
        /// Guarded whole. This runs inside the SDK's event hub, on the path that also delivers
        /// these events to the conversation itself -- an exception thrown back into it from the
        /// study's bookkeeping could take the session down, and losing a participant to the
        /// instrumentation is worse than losing the measurement.
        /// </summary>
        private void Record(string speaker, string kind, string messageId, int characters)
        {
            try
            {
                var entry = new SpeechEventEntry
                {
                    t = TimeSource != null ? TimeSource() : Time.realtimeSinceStartup,
                    speaker = speaker,
                    kind = kind,
                    messageId = messageId ?? "",
                    characters = characters
                };

                OnSpeechEvent?.Invoke(entry);

                if (verboseLogging) Debug.Log($"{Tag} {speaker} {kind} at {entry.t:F2}s");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} Recording a speech event threw: {ex}");
            }
        }

        /// <summary>The panel line. One row, because it shares a small readout.</summary>
        public string Describe()
        {
            if (!IsBound) return "speech    : not watching - no session";

            var line = $"speech    : {ParticipantUtterances} said, {CharacterUtterances} replied";

            if (NoResponses > 0) line += $", {NoResponses} unanswered";
            if (Interruptions > 0) line += $", {Interruptions} cut off";

            return line;
        }
    }
}
