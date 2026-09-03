using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime;
using Convai.Runtime.Actions;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// The executor behind the character's "Look At" action: turns "the chair by the couch" into
    /// the same attention object that pointing at it would have set.
    ///
    /// WHY THIS EXISTS. Pointing could already say which object you meant; naming could not.
    /// <c>SetCurrentAttentionObject</c> had exactly two callers before this one --
    /// RoomScanPointer and RoomTaskPlan -- and neither of them is reachable by speech, so there
    /// was no path at all from a spoken name to "that". The reference-resolution study needs the
    /// two modalities to end in the same place or it is comparing a measurement with nothing;
    /// outside the study it is the missing half of a feature that only ever had one hand.
    ///
    /// It routes the way everything else here routes: the backend already classifies intent on
    /// every turn, so an action whose description says when to use it makes the backend the
    /// router. See RoomTaskPlanner for the full argument.
    ///
    /// AUTHORING. This needs an action on the character's Convai Action Config:
    ///
    ///     Action name   Look At
    ///     Description   The player is indicating which object in the room they mean. They are
    ///                   NOT asking you to move, walk, fetch, or do anything with it -- only to
    ///                   note which one they are referring to.
    ///     Parameter     object (string) -- the object as the player named it
    ///     Target        None
    ///     Executor      this component
    ///     Timeout       10
    ///     When finished Tell the player
    ///
    /// The description is worded that hard on purpose. The risk this action carries is not that
    /// it fails -- it is that "the chair by the couch" routes to the existing Move To action
    /// instead and she walks across the room mid-trial. Every clause about NOT moving is there
    /// to hold the boundary against an action that has a much more natural claim on a sentence
    /// with a furniture name in it. Pilot the routing before building a study block on it.
    /// </summary>
    public class RoomAttentionExecutor : ConvaiActionExecutor<RoomAttentionExecutor.LookAtParameters>
    {
        private const string Tag = "[RoomAttention]";

        /// <summary>What the backend sends with the action.</summary>
        public sealed class LookAtParameters
        {
            /// <summary>The object, as the player named it. May be an alias.</summary>
            [ConvaiActionParameter("object")]
            public string Object { get; set; }
        }

        [Header("Wiring (left empty, these are found in the scene)")]
        [Tooltip("Resolves the spoken name -- including aliases like 'chair 2' -- to a box.")]
        public RoomScanContext context;

        [Tooltip("Supplies the character whose attention this sets.")]
        public RoomCharacterVoice voice;

        [Header("Feedback")]
        [Tooltip("What she says once she has it. Kept to a few words: this action fires in the " +
                 "middle of a sentence about the object, and a full reply here talks over the " +
                 "thing the player was actually asking.")]
        public string acknowledgement = "Got it.";

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>
        /// Raised when a spoken name resolved to a box, with the name and the box.
        ///
        /// Deliberately the same shape as <see cref="RoomScanPointer.OnAttentionChanged"/>, so a
        /// listener that scores "which object did they indicate" can take both modalities
        /// through one handler and not grow a branch that could treat them differently.
        ///
        /// Raised only on a resolve. A name that matched nothing never became an indication of
        /// anything, and reporting it here would make "they meant nothing" indistinguishable
        /// from "they meant the wrong thing" -- which for the study are different outcomes.
        /// </summary>
        public event Action<string, GameObject> OnAttentionChanged;

        /// <summary>
        /// Raised when a name arrived and matched nothing. Carries what was said.
        ///
        /// Separate from the resolve event because it is a different fact about the app: the
        /// participant produced a referring expression and this app could not turn it into an
        /// object. For a reference-resolution measure that is an outcome, not an error.
        /// </summary>
        public event Action<string> OnAttentionUnresolved;

        private void Awake()
        {
            if (context == null) context = FindAnyObjectByType<RoomScanContext>();
            if (voice == null) voice = FindAnyObjectByType<RoomCharacterVoice>();
        }

        /// <inheritdoc />
        protected override Task<ConvaiActionExecutionResult> ExecuteAsync(
            ConvaiActionInvocation invocation,
            LookAtParameters parameters,
            CancellationToken cancellationToken)
        {
            var spoken = parameters?.Object;

            // Worth asking again about rather than declaring broken: the backend may simply have
            // invoked the action without filling the parameter in, and the next turn may carry
            // it. Same reading as RoomTaskPlanner's empty-task branch.
            if (string.IsNullOrWhiteSpace(spoken))
            {
                Debug.LogWarning($"{Tag} The Look At action arrived with no 'object' parameter. " +
                                 $"Check the action declares an 'object' string parameter and " +
                                 $"that the character is filling it in.");

                return Done("Which one do you mean?", "Look At arrived with no object parameter.");
            }

            if (context == null)
            {
                return Done("I'm not sure which one you mean.",
                            "There is no RoomScanContext in the scene, so no name can be " +
                            "resolved to a box. Add one to the room manager.");
            }

            if (!context.TryResolve(spoken, out var proxy) || proxy == null)
            {
                // Not a failure of the action -- it ran, and the answer is that this app does
                // not know that name. Said out loud, because the alternative is a silence the
                // player reads as her ignoring them.
                if (verboseLogging)
                    Debug.Log($"{Tag} '{spoken}' matched no object in the room.");

                OnAttentionUnresolved?.Invoke(spoken);

                return Done($"I don't know which one you mean by {spoken}.",
                            $"No scan object is named or aliased '{spoken}'.");
            }

            var name = TargetName(proxy) ?? spoken;

            // Before the character check, and for the reason RoomScanPointer.OnAttentionChanged
            // is raised before its own gate: what follows is a network round trip, and this
            // app's answer to "which object did they mean" is already final here.
            OnAttentionChanged?.Invoke(name, proxy);

            var character = voice != null ? voice.Character : null;

            if (character != null && character.IsInConversation)
            {
                // Silent, exactly as pointing is. Naming a thing is not a remark about it, and
                // the turn that carried the name is already being answered.
                character.DynamicContext.SetCurrentAttentionObject(name, ConvaiRespondMode.Silent);

                if (verboseLogging) Debug.Log($"{Tag} Attention -> '{name}' (said '{spoken}')");
            }
            else
            {
                // Reachable in the editor and in a scene with no character. The resolve above
                // still happened and still fired, which is what makes this testable at all.
                if (verboseLogging)
                    Debug.Log($"{Tag} Resolved '{spoken}' -> '{name}' with nobody connected.");
            }

            return Done(acknowledgement, $"Attention set to '{name}'.");
        }

        /// <summary>
        /// Answered, always, and never Failed or Unhandled.
        ///
        /// Unhandled is silent no matter what the feedback relay is set to -- the SDK composes
        /// an all-Unhandled batch with forceSilent -- and Failed carries no answer at all
        /// because the constructor that would build one is private. Both leave her standing
        /// there having been asked a question. RoomTaskPlanner reasons this out at length; the
        /// same reading applies here, and the developer's version of events still goes to the
        /// console through the message.
        /// </summary>
        private static Task<ConvaiActionExecutionResult> Done(string spoken, string message) =>
            Task.FromResult(ConvaiActionExecutionResult.Answered(spoken, message));

        /// <summary>
        /// What the backend knows this box as. Read off the box for the same reason
        /// RoomScanPointer reads it there: the name sent as attention is then literally the
        /// name registered as a walk target, because it is the same field on the same component.
        /// </summary>
        private static string TargetName(GameObject proxy) =>
            proxy != null && proxy.TryGetComponent<ConvaiActionTarget>(out var target)
                ? target.TargetName
                : null;
    }
}
