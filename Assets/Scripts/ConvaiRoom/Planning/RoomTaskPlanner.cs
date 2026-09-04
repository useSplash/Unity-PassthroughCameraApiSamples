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
    ///     Description   Work out the steps for something the player wants to get done, then
    ///                   answer with them. Run this whenever they ask how to do something, to
    ///                   be shown or walked through it, for a plan, or for help with a task or
    ///                   chore, however they word it. Tidying, cleaning, setting up,
    ///                   organising, moving things, fixing and cooking all count, including
    ///                   when the task is about this room. Prefer running it over answering
    ///                   from your own knowledge. Skip it only for questions that just ask what
    ///                   something is or what the room contains. To continue a plan already
    ///                   underway, use Step Through Plan.
    ///     Parameter     task (string) -- what they want to get done, in their own words
    ///     Target        None
    ///     Executor      this component
    ///     Timeout       45   (the request itself is bounded separately, and shorter)
    ///     When finished Tell the player
    ///
    /// The last two are the ones that go wrong quietly. A timeout under the request's own
    /// budget has the action give up while the answer is still in the air; anything other than
    /// Tell The Player has her work out a plan and then decide not to mention it.
    ///
    /// THE DESCRIPTION IS THE ROUTER, and it has already been wrong once in a way that cost a
    /// session. It used to read "Do not use it for questions about the room or about you",
    /// which excluded the exact requests this exists for -- every task in a room app is about
    /// the room, and "how do I clean up the room" was steered away by the one clause meant to
    /// keep ordinary chat out. The replacement excludes what is genuinely not procedural
    /// (naming and describing) rather than anything that mentions the room.
    ///
    /// It also used to end with three sentences of rationale aimed at whoever was authoring it
    /// -- that steps said without the action never reach the panel. She reads that as an
    /// instruction never to answer without the tool, so on the turn the action did not fire she
    /// refused and invented a reason: "the planning tool is not responding", a sentence nothing
    /// in this file can produce. Rationale belongs here, in the comment. The description gets
    /// trigger conditions only.
    /// </summary>
    public class RoomTaskPlanner : ConvaiActionExecutor<RoomTaskPlanner.PlanTaskParameters>
    {
        private const string Tag = "[RoomPlanner]";

        /// <summary>
        /// What she says when the planner cannot run at all.
        ///
        /// Shared by the setup guards and by <see cref="Excuse"/>'s missing-key branch, because
        /// from where the player is standing they are one situation: something on this headset
        /// is not set up, and asking again will not change it.
        /// </summary>
        private const string NotSetUp =
            "I can't work out steps for that yet. My planner isn't set up on this headset, so " +
            "I can talk about the room but not plan anything in it.";

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

            // A task that arrived empty is the one guard here that is worth asking again about:
            // the backend may simply have invoked the action without filling the parameter in,
            // and the next turn may well carry it. So she asks for it rather than declaring the
            // headset broken, and the console gets the reading that is actually actionable.
            if (string.IsNullOrWhiteSpace(task))
            {
                return CannotRun(
                    "I didn't catch what you wanted me to plan. Can you say that again?",
                    "This action needs a task to plan. Check the Plan Task action declares a " +
                    "'task' string parameter, and that the character is filling it in.");
            }

            if (client == null)
            {
                return CannotRun(NotSetUp,
                    "There is no RoomPlannerClient in the scene, so nothing can work out a plan. " +
                    "Add one to the room manager.");
            }

            if (plan == null)
            {
                return CannotRun(NotSetUp,
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
                return Excuse(task, result.Failure);

            plan.SetPlan(task, result.Summary, result.Steps);

            if (!plan.HasPlan)
                return Excuse(task, "the plan came back empty");

            return ConvaiActionExecutionResult.Answered(Speak(), $"Planned '{task}' in {plan.Steps.Count} steps.");
        }

        /// <summary>
        /// Says out loud that the action could not run at all, and puts the developer's reason
        /// in the console.
        ///
        /// These three guards used to return <c>Unhandled</c>, which is the status the SDK
        /// documents for "this component cannot handle this step" and reads like the honest
        /// answer. It is silent, and NOT by the feedback relay's choice:
        /// <c>ConvaiActionFeedbackComposer</c> composes a batch whose steps are all Unhandled
        /// with <c>forceSilent: true</c>, which overrides every feedback mode on the character.
        /// No relay setting can voice one.
        ///
        /// So the action completed, said nothing, and the backend filled the silence by
        /// improvising -- unable to make a formal plan, followed by a plan in prose that never
        /// reached RoomTaskPlan and so was never on the panel. That is precisely the failure
        /// this executor exists to prevent, arriving through the one path that could not report
        /// itself. Adding the relay did not help and could not have.
        ///
        /// Answered for the same reason <see cref="Excuse"/> is: only <c>Answer</c> reaches the
        /// character. The console line is logged here rather than left to <c>message</c>,
        /// because a misconfiguration is worth a warning in its own right and nothing in the SDK
        /// promises to surface that field.
        /// </summary>
        private static ConvaiActionExecutionResult CannotRun(string spoken, string message)
        {
            Debug.LogWarning($"{Tag} {message}");

            return ConvaiActionExecutionResult.Answered(spoken, message);
        }

        /// <summary>
        /// Says out loud that the plan did not happen, and why.
        ///
        /// Answered rather than Failed, and it is worth being clear that this is a deliberate
        /// reading of the SDK rather than a shrug at it. Only <c>Answer</c> reaches the
        /// character; <c>Message</c> is documented as text she never hears, and there is no
        /// factory for a failed result that carries an answer -- the constructor that would
        /// build one is private. So a Failed result here is a silent one, and silence is the
        /// worst possible reply to a question somebody asked out loud: she simply stops, and
        /// the feature reads as broken rather than as unconfigured.
        ///
        /// Answered is defensible on its own terms. This action's job is to find something out,
        /// and it did: it found out that it cannot plan this, and it is telling you. The exact
        /// reason still goes to the console through <c>message</c>, where it belongs.
        ///
        /// The excuses are grouped rather than mapped one for one. A player hears three useful
        /// distinctions -- it is not set up, it could not be reached, or something else went
        /// wrong -- and mapping every internal string to its own sentence would be a table that
        /// silently stops matching the moment one of them is reworded.
        /// </summary>
        private ConvaiActionExecutionResult Excuse(string task, string failure)
        {
            var reason = failure ?? "";

            string spoken;

            if (reason.IndexOf("key", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                spoken = NotSetUp;
            }
            else if (reason.IndexOf("reach", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                spoken = "I couldn't reach my planner just then. Ask me again in a moment.";
            }
            else
            {
                spoken = $"I couldn't work out how to {Lower(task)}, sorry.";
            }

            return ConvaiActionExecutionResult.Answered(
                spoken, $"Could not plan '{task}': {reason}");
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
