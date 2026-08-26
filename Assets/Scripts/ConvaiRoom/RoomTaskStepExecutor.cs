using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Walks the player through a plan that already exists: next, back, repeat, first, last.
    ///
    /// Moving through a plan does NOT move the character. Advancing sets the current attention
    /// object to the step's place -- so "is this the right spot?" and "take me there" both
    /// resolve to where the step happens -- and stops there. Sending her automatically would
    /// turn a five-step plan into five traversals of the room, and would take the one decision
    /// this project has consistently left to the conversation and hard-code it into a button.
    /// If you want her at the step, ask her; the walk target is already the step's place.
    ///
    /// AUTHORING. One action on the character's Convai Action Config:
    ///
    ///     Action name   Step Through Plan
    ///     Description   Move through the steps of the plan already being followed. Use this
    ///                   for next, go back, repeat that, start over, or last step.
    ///     Parameter     direction (choice) -- next, back, repeat, first, last
    ///     Target        None
    ///     Executor      this component
    ///     When finished Tell the player
    ///
    /// Separately authored "Next Step" and "Previous Step" actions also work: when no direction
    /// parameter arrives, the action's own name is read instead. That fallback exists because
    /// one-action-with-a-parameter and three-plainly-named-actions are both reasonable ways to
    /// author this, and which routes better is a question only the backend can answer.
    /// </summary>
    public class RoomTaskStepExecutor : ConvaiActionExecutor<RoomTaskStepExecutor.StepParameters>
    {
        private const string Tag = "[RoomPlanStep]";

        /// <summary>Which way to move.</summary>
        public sealed class StepParameters
        {
            /// <summary>next, back, repeat, first or last. Empty falls back to the action name.</summary>
            [ConvaiActionParameter("direction")]
            public string Direction { get; set; }

            /// <summary>
            /// An explicit step number, when the player named one ("go to step three").
            /// Zero means none -- the parameter is optional and absence is the normal case.
            /// </summary>
            [ConvaiActionParameter("step")]
            public int Step { get; set; }
        }

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("The plan being stepped through.")]
        public RoomTaskPlan plan;

        [Header("Debug")]
        public bool verboseLogging = true;

        private void Awake()
        {
            if (plan == null) plan = FindAnyObjectByType<RoomTaskPlan>();
        }

        /// <inheritdoc />
        protected override Task<ConvaiActionExecutionResult> ExecuteAsync(
            ConvaiActionInvocation invocation,
            StepParameters parameters,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Execute(invocation, parameters));
        }

        private ConvaiActionExecutionResult Execute(
            ConvaiActionInvocation invocation, StepParameters parameters)
        {
            if (plan == null)
            {
                return ConvaiActionExecutionResult.Unhandled(
                    "There is no RoomTaskPlan in the scene, so there is nothing to step through. " +
                    "Add one to the room manager.");
            }

            // Answered rather than Failed. The player said "next" out loud; the useful reply is
            // the reason there is nothing to advance, said back to them, not a console line.
            if (!plan.HasPlan)
            {
                return ConvaiActionExecutionResult.Answered(
                    "We are not working through anything yet. Ask me how to do something and I " +
                    "will lay the steps out.");
            }

            // An explicit number wins over a direction. "Go back to step two" carries both, and
            // the number is the specific instruction of the pair.
            if (parameters != null && parameters.Step > 0)
                return GoTo(parameters.Step);

            var direction = Resolve(parameters?.Direction, invocation);

            if (verboseLogging) Debug.Log($"{Tag} {direction} from step {plan.Current.Number}.");

            switch (direction)
            {
                case Move.Repeat: return Say(plan.Current, "Again. ");
                case Move.First: return GoTo(1);
                case Move.Last: return GoTo(plan.Steps.Count);
                case Move.Back: return Shift(-1);
                default: return Shift(1);
            }
        }

        // -----------------------------------------------------------------
        // Moving
        // -----------------------------------------------------------------

        private ConvaiActionExecutionResult Shift(int delta)
        {
            if (plan.TryMove(delta, out var step)) return Say(step, "");

            // Clamped rather than moved, which is a real answer rather than a failure: the
            // player is at one end of the plan and should be told so, not silently re-read the
            // step they are already on.
            return delta > 0
                ? ConvaiActionExecutionResult.Answered(
                    $"That was the last step, so that is the whole of {Describe()}.")
                : ConvaiActionExecutionResult.Answered(
                    "We are already at the first step.");
        }

        private ConvaiActionExecutionResult GoTo(int number)
        {
            if (plan.TryGoTo(number, out var step)) return Say(step, "");

            return ConvaiActionExecutionResult.Answered(
                $"There is no step {number}. The plan has {plan.Steps.Count}.");
        }

        /// <summary>
        /// The step, said out loud, with its place folded into the sentence.
        ///
        /// The "of five" tail is there on every step deliberately. In a headset there is no
        /// scrollback for speech, and knowing how much is left is most of what makes a spoken
        /// list followable.
        /// </summary>
        private ConvaiActionExecutionResult Say(RoomTaskPlan.Step step, string prefix)
        {
            var where = step.HasPlace ? $", at the {step.Where}" : "";

            var answer = $"{prefix}Step {step.Number} of {plan.Steps.Count}{where}. {step.Text}";

            if (plan.AtLastStep && string.IsNullOrEmpty(prefix))
                answer += " That is the last one.";

            return ConvaiActionExecutionResult.Answered(
                answer, $"Step {step.Number}/{plan.Steps.Count}");
        }

        // -----------------------------------------------------------------
        // Direction
        // -----------------------------------------------------------------

        private enum Move { Next, Back, Repeat, First, Last }

        /// <summary>
        /// Which way to move, from the parameter when there is one and from the action's own
        /// name when there is not.
        ///
        /// Matched on substrings rather than exact values because both sources are loose: the
        /// backend fills a choice parameter with whatever word the player used, and an authored
        /// action name is whatever somebody typed into the inspector. "Previous Step",
        /// "previous", "go back" and "back" all have to mean the same thing.
        /// </summary>
        private static Move Resolve(string direction, ConvaiActionInvocation invocation)
        {
            var text = direction;

            if (string.IsNullOrWhiteSpace(text))
                text = invocation?.Definition?.ActionName;

            if (string.IsNullOrWhiteSpace(text)) return Move.Next;

            text = text.ToLowerInvariant();

            // Order matters: "start over" contains neither "back" nor "next" but does mean
            // first, and it is checked before the general cases so it cannot fall through.
            if (Has(text, "first") || Has(text, "start over") || Has(text, "restart")) return Move.First;
            if (Has(text, "last") || Has(text, "final") || Has(text, "end")) return Move.Last;
            if (Has(text, "repeat") || Has(text, "again") || Has(text, "current")) return Move.Repeat;
            if (Has(text, "back") || Has(text, "previous") || Has(text, "prior")) return Move.Back;

            return Move.Next;
        }

        private static bool Has(string text, string token) =>
            text.IndexOf(token, StringComparison.Ordinal) >= 0;

        /// <summary>The plan, named the way she would refer to it.</summary>
        private string Describe() =>
            string.IsNullOrEmpty(plan.Task) ? "the plan" : plan.Task;
    }
}
