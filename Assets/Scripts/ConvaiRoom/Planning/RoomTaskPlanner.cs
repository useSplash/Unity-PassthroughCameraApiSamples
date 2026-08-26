using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// The executor behind the character's "Plan Task" action: turns "how do I do X?" into an
    /// enumerated, room-grounded plan she then reads out.
    ///
    /// There is no router in this project and there should not be one. The Convai backend
    /// already classifies intent on every single turn -- that is what choosing an action IS --
    /// so declaring this action with a description that says when to use it makes the backend
    /// the router. Ordinary conversation never reaches here; a procedural request does. The
    /// alternative, tapping OnTranscriptReceived and matching "how do I" in Unity, means
    /// classifying in parallel with a backend that is already composing a reply, and then
    /// fighting it for the turn.
    ///
    /// So the division of labour is unchanged from everything else in this room: the
    /// conversation decides that a plan is wanted and what it is for; this supplies the room
    /// the plan has to fit, and hands back the words. Where she walks afterwards is still
    /// nobody's business here.
    ///
    /// AUTHORING. This needs an action on the character's Convai Action Config:
    ///
    ///     Action name   Plan Task
    ///     Description   The player is asking how to do something, or to be shown how. Use this
    ///                   to work out the steps before answering.
    ///     Parameter     task (string) -- what they want to do, in their own words
    ///     Target        None
    ///     Executor      this component
    ///     Timeout       45   (the request itself is bounded separately, and shorter)
    ///     When finished Tell the player
    ///
    /// The last two are the ones that go wrong quietly. A timeout under the request's own
    /// budget has the action give up while the answer is still in the air; anything other than
    /// Tell The Player has her work out a plan and then decide not to mention it.
    /// </summary>
    public class RoomTaskPlanner : ConvaiActionExecutor<RoomTaskPlanner.PlanTaskParameters>
    {
        private const string Tag = "[RoomPlanner]";

        /// <summary>What the backend sends with the action.</summary>
        public sealed class PlanTaskParameters
        {
            /// <summary>The task, in the player's own words.</summary>
            [ConvaiActionParameter("task")]
            public string Task { get; set; }
        }

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Makes the request. Without one there is nothing to plan with and the action " +
                 "says so rather than failing silently.")]
        public RoomPlannerClient client;

        [Tooltip("Where the finished plan is kept, and what the panel draws.")]
        public RoomTaskPlan plan;

        [Tooltip("Supplies the room summary the planner is given as background. Optional -- " +
                 "without it the planner still gets the list of places, which is the part that " +
                 "does the real work.")]
        public RoomScanContext context;

        [Header("Grounding")]
        [Tooltip("Most places to offer the planner.\n\n" +
                 "This whole list goes into the request as a schema enum, so a cluttered scan " +
                 "is real cost on every plan. Over this, the nearest to you survive -- the end " +
                 "of the room you are standing in is the end a plan is usually about.")]
        public int maxPlaces = 30;

        [Header("Debug")]
        public bool verboseLogging = true;

        private void Awake()
        {
            if (client == null) client = FindAnyObjectByType<RoomPlannerClient>();
            if (plan == null) plan = FindAnyObjectByType<RoomTaskPlan>();
            if (context == null) context = FindAnyObjectByType<RoomScanContext>();
        }

        /// <inheritdoc />
        protected override async Task<ConvaiActionExecutionResult> ExecuteAsync(
            ConvaiActionInvocation invocation,
            PlanTaskParameters parameters,
            CancellationToken cancellationToken)
        {
            var task = parameters?.Task;

            if (string.IsNullOrWhiteSpace(task))
            {
                // Unhandled rather than Failed: nothing is broken, the action simply arrived
                // without the one thing it needs, and the message names both ways that happens.
                return ConvaiActionExecutionResult.Unhandled(
                    "This action needs a task to plan. Check the Plan Task action declares a " +
                    "'task' string parameter, and that the character is filling it in.");
            }

            if (client == null)
            {
                return ConvaiActionExecutionResult.Unhandled(
                    "There is no RoomPlannerClient in the scene, so nothing can work out a plan. " +
                    "Add one to the room manager.");
            }

            if (plan == null)
            {
                return ConvaiActionExecutionResult.Unhandled(
                    "There is no RoomTaskPlan in the scene, so a plan would have nowhere to live " +
                    "and nothing to draw it. Add one to the room manager.");
            }

            // Measured from the player rather than from the character. If the cap trims the
            // list, it should keep the places the person asking is standing among -- she may be
            // across the room, and a plan built around where she happens to be loitering is a
            // plan about the wrong half of the room.
            var player = ResolvePlayer();
            var origin = player != null ? player.position : CharacterTransform.position;

            var places = RoomTaskVocabulary.Collect(maxPlaces, origin);

            if (verboseLogging)
                Debug.Log($"{Tag} Planning '{task}' against {places.Count} groundable places.");

            var result = await client.PlanAsync(task, places, RoomSummary(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Ok)
            {
                // Failed, not Unhandled: the action was understood and genuinely attempted. The
                // answer is written as something she can say, because the player asked a
                // question out loud and silence is the worst possible reply to it.
                return ConvaiActionExecutionResult.Failed(
                    $"Could not plan '{task}': {result.Failure}",
                    ConvaiActionFailureReason.Custom);
            }

            plan.SetPlan(task, result.Summary, result.Steps);

            if (!plan.HasPlan)
            {
                return ConvaiActionExecutionResult.Failed(
                    $"The plan for '{task}' came back empty.",
                    ConvaiActionFailureReason.Custom);
            }

            return ConvaiActionExecutionResult.Answered(Speak(), $"Planned '{task}' in {plan.Steps.Count} steps.");
        }

        /// <summary>
        /// The plan as one paragraph she reads out.
        ///
        /// Every step is spoken here, rather than just the first, because the plan is asked for
        /// as a question -- "how do I do this?" -- and answering a question with "step one, and
        /// ask me for the rest" is not answering it. Stepping through afterwards is for doing
        /// the task; this is for knowing what it involves.
        ///
        /// Places are named inside the sentence rather than appended as a location field,
        /// because she is speaking: "clear a space on the dining table" is a sentence, and
        /// "clear a space. Location: dining table" is a form.
        /// </summary>
        private string Speak()
        {
            var steps = plan.Steps;
            var builder = new StringBuilder(steps.Count * 64);

            var summary = plan.Summary;
            builder.Append(string.IsNullOrEmpty(summary) ? $"Here is how to {plan.Task}." : summary);

            builder.Append(steps.Count == 1 ? " One step. " : $" {Spell(steps.Count)} steps. ");

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                builder.Append(Ordinal(i)).Append(", ").Append(Lower(step.Text));

                if (step.HasPlace) builder.Append(", at the ").Append(step.Where);

                builder.Append(". ");
            }

            builder.Append("Say next when you are ready to work through it.");

            return builder.ToString();
        }

        /// <summary>
        /// The room, as background for the planner.
        ///
        /// Read off <see cref="RoomScanContext"/> rather than rebuilt, so the planner is told
        /// the same thing about the room that the character was. Two descriptions of one room,
        /// built by two different pieces of code, is how a plan ends up sized for a room nobody
        /// is standing in.
        /// </summary>
        private string RoomSummary() => context != null ? context.RoomSummary : "";

        // -----------------------------------------------------------------
        // Words
        // -----------------------------------------------------------------

        private static readonly string[] Ordinals =
        {
            "First", "Second", "Third", "Fourth", "Fifth",
            "Sixth", "Seventh", "Eighth", "Ninth", "Tenth"
        };

        private static readonly string[] Numbers =
        {
            "Zero", "One", "Two", "Three", "Four", "Five",
            "Six", "Seven", "Eight", "Nine", "Ten"
        };

        /// <summary>"First", "Second"... falling back to "Step 11" past the words.</summary>
        private static string Ordinal(int index) =>
            index < Ordinals.Length ? Ordinals[index] : $"Step {index + 1}";

        /// <summary>Spelled out, because "4 steps" read aloud can come out as "four steps" or not.</summary>
        private static string Spell(int count) =>
            count >= 0 && count < Numbers.Length ? Numbers[count] : count.ToString();

        /// <summary>
        /// Drops the leading capital so a step reads as the back half of a sentence.
        ///
        /// Only the first letter, and only when the second is lowercase -- "TV" and "USB" start
        /// steps often enough that blanket lowercasing would mangle them.
        /// </summary>
        private static string Lower(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length > 1 && char.IsUpper(text[1])) return text;

            return char.ToLowerInvariant(text[0]) + text.Substring(1);
        }
    }
}
