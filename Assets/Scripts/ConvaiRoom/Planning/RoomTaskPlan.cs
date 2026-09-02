using System;
using System.Collections.Generic;
using System.Text;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Shared.Types;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// The plan the character is currently working through: an ordered, numbered list of steps,
    /// each optionally tied to a place in the room.
    ///
    /// This owns the plan rather than pushing it into Convai and forgetting it, and the reason
    /// is the same one that keeps <see cref="RoomScanContext"/> holding its described list.
    /// Dynamic context is a one-way channel -- you stage facts and they are sent -- so there is
    /// no index in it to advance, no list to re-read, and nothing the panel can draw. Three
    /// consumers need to agree about which step is current (the character, the panel, and the
    /// pointer's attention), and that agreement has to live somewhere in the scene.
    ///
    /// What Convai is told is a MIRROR of this, restated on every change:
    ///
    ///   - plan.task     what is being done
    ///   - plan.steps    the whole enumerated list, so she can answer "what's after this?"
    ///   - plan.current  where in it we are
    ///
    /// Advancing a step also sets the CURRENT ATTENTION OBJECT to that step's place, which is
    /// what makes "do I do that here?" and "take me there" work on the step you are on without
    /// anybody naming the furniture. It deliberately does not walk her anywhere: where she goes
    /// stays the conversation's business, exactly as it is for every other feature in this room.
    /// </summary>
    public class RoomTaskPlan : MonoBehaviour
    {
        private const string Tag = "[RoomPlan]";

        /// <summary>The SDK's ceiling on one dynamic-context value. Longer is rejected, not trimmed.</summary>
        private const int MaxStateLength = 1000;

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Supplies the character the plan is mirrored onto. Without one the plan still " +
                 "exists and still draws on the panel; she simply has not been told about it.")]
        public RoomCharacterVoice voice;

        [Header("Behaviour")]
        [Tooltip("Set the current attention object to the step's place as you advance.\n\n" +
                 "Leave this on. It is what makes 'that' mean the place the current step " +
                 "happens at, so you can ask about the step without naming the furniture.")]
        public bool attendToCurrentStep = true;

        [Tooltip("Most steps to keep. The planner is asked for no more than eight; this is the " +
                 "backstop for a reply that ignores it.")]
        public int maxSteps = 12;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>One step, as the room knows it.</summary>
        public readonly struct Step
        {
            /// <summary>Its place in the list, from 1. What the panel shows and she says.</summary>
            public readonly int Number;

            public readonly string Text;

            /// <summary>The place this happens at, or null when it happens nowhere in particular.</summary>
            public readonly string Where;

            public Step(int number, string text, string where)
            {
                Number = number;
                Text = text;
                Where = where;
            }

            public bool HasPlace => !string.IsNullOrEmpty(Where);

            /// <summary>The step as one line, the way it is said and drawn.</summary>
            public override string ToString() =>
                HasPlace ? $"{Number}. {Text} ({Where})" : $"{Number}. {Text}";
        }

        private readonly List<Step> _steps = new List<Step>();

        /// <summary>The steps in order. Empty when there is no plan.</summary>
        public IReadOnlyList<Step> Steps => _steps;

        /// <summary>What the plan is for, as the player asked for it.</summary>
        public string Task { get; private set; } = "";

        /// <summary>The planner's one-line summary, or empty.</summary>
        public string Summary { get; private set; } = "";

        /// <summary>Which step is current, from 0. -1 when there is no plan.</summary>
        public int CurrentIndex { get; private set; } = -1;

        public bool HasPlan => _steps.Count > 0;

        /// <summary>The current step, or default when there is no plan.</summary>
        public Step Current =>
            CurrentIndex >= 0 && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : default;

        /// <summary>
        /// Raised whenever the plan or the current step changes, so the panel can redraw without
        /// polling. Fired on clear as well as on set -- an emptied panel is a change too.
        /// </summary>
        public event Action OnChanged;

        private void Awake()
        {
            if (voice == null) voice = FindAnyObjectByType<RoomCharacterVoice>();
        }

        // -----------------------------------------------------------------
        // Setting
        // -----------------------------------------------------------------

        /// <summary>
        /// Replaces whatever plan was there with a new one, starting at step 1.
        /// </summary>
        public void SetPlan(string task, string summary, IReadOnlyList<RoomPlannerClient.PlannedStep> steps)
        {
            _steps.Clear();

            Task = string.IsNullOrWhiteSpace(task) ? "" : task.Trim();
            Summary = string.IsNullOrWhiteSpace(summary) ? "" : summary.Trim();

            if (steps != null)
            {
                var limit = maxSteps > 0 ? Mathf.Min(steps.Count, maxSteps) : steps.Count;

                for (var i = 0; i < limit; i++)
                {
                    var step = steps[i];
                    if (string.IsNullOrWhiteSpace(step.Text)) continue;

                    // Numbered here rather than taken from the planner. The number is this
                    // list's own index and has to match what the panel draws and what
                    // "step three" resolves to; a number the model chose could disagree with
                    // both the moment a step is dropped for being empty.
                    _steps.Add(new Step(_steps.Count + 1, step.Text.Trim(), step.Where));
                }

                if (steps.Count > limit)
                {
                    Debug.LogWarning($"{Tag} The planner returned {steps.Count} steps, over the " +
                                     $"{maxSteps} cap. Kept the first {limit}.");
                }
            }

            CurrentIndex = _steps.Count > 0 ? 0 : -1;

            if (verboseLogging && HasPlan)
            {
                Debug.Log($"{Tag} Plan for '{Task}': {_steps.Count} steps.\n" +
                          string.Join("\n", _steps));
            }

            Publish();
        }

        /// <summary>Throws the plan away. Safe to call when there is none.</summary>
        public void Clear()
        {
            if (!HasPlan && CurrentIndex < 0 && string.IsNullOrEmpty(Task)) return;

            _steps.Clear();
            Task = "";
            Summary = "";
            CurrentIndex = -1;

            if (verboseLogging) Debug.Log($"{Tag} Plan cleared.");

            Publish();
        }

        // -----------------------------------------------------------------
        // Moving through it
        // -----------------------------------------------------------------

        /// <summary>
        /// Moves <paramref name="delta"/> steps and returns the step landed on.
        ///
        /// Clamped rather than wrapped, and it reports whether it actually moved: "next" at the
        /// last step should say the plan is finished, not silently loop back to the beginning
        /// and start the task again.
        /// </summary>
        public bool TryMove(int delta, out Step step)
        {
            step = default;

            if (!HasPlan) return false;

            var target = Mathf.Clamp(CurrentIndex + delta, 0, _steps.Count - 1);

            if (target == CurrentIndex)
            {
                step = _steps[CurrentIndex];
                return false;
            }

            CurrentIndex = target;
            step = _steps[CurrentIndex];

            Publish();
            return true;
        }

        /// <summary>Jumps to a step by its printed number, from 1.</summary>
        public bool TryGoTo(int number, out Step step)
        {
            step = default;

            if (!HasPlan || number < 1 || number > _steps.Count) return false;

            CurrentIndex = number - 1;
            step = _steps[CurrentIndex];

            Publish();
            return true;
        }

        /// <summary>Whether the current step is the last one.</summary>
        public bool AtLastStep => HasPlan && CurrentIndex == _steps.Count - 1;

        // -----------------------------------------------------------------
        // Telling her
        // -----------------------------------------------------------------

        /// <summary>
        /// Mirrors the plan onto the character and points her attention at the current step.
        ///
        /// Everything here is Silent. A plan changing is not itself news -- she has either just
        /// read it out or just been asked to move through it, and either way the speaking is
        /// already handled by the action that caused this. A respond mode here would have her
        /// remark on her own bookkeeping.
        /// </summary>
        private void Publish()
        {
            OnChanged?.Invoke();

            var character = voice != null ? voice.Character : null;

            // The SDK drops staged context while the character is not in conversation, so this
            // is a real gate rather than a null check: pushing here would be pushing into a bin.
            if (character == null || !character.IsInConversation) return;

            var facts = new Dictionary<string, string>();

            if (!HasPlan)
            {
                // Emptied rather than removed, because a removed key leaves whatever she was
                // last told still sitting in her context -- she would answer "what's next?" from
                // a plan that has been thrown away.
                //
                // Worded as an EMPTY SLOT rather than as a condition, and the wording is the
                // whole of it. "Nothing is being planned right now" is a sentence about the
                // world, and a model handed that when the player asks for a plan reads it as
                // the answer: it says nothing is being planned right now, and means it. The
                // facts here describe what the field holds, and the first one says outright
                // that planning is still on the table -- an empty slot invites filling, a
                // standing condition does not.
                facts["plan.task"] = "none. No plan is being followed. A new one can be " +
                                     "planned whenever the player asks for one.";
                facts["plan.steps"] = "none";
                facts["plan.current"] = "none";
            }
            else
            {
                facts["plan.task"] = Clamp(Task, MaxStateLength);
                facts["plan.steps"] = Clamp(StepsAsText(), MaxStateLength);
                facts["plan.current"] = Clamp(CurrentAsText(), MaxStateLength);
            }

            character.DynamicContext.SetStates(facts, ConvaiRespondMode.Silent);

            AttendToCurrent(character);
        }

        /// <summary>
        /// Points "that" at wherever the current step happens.
        ///
        /// Never cleared when a step has no place, and that is deliberate for the same reason
        /// <see cref="RoomScanPointer"/> never clears on looking away: an unlocated step in the
        /// middle of a plan would otherwise wipe out the place you were just talking about, and
        /// the next "over there" would resolve to nothing.
        /// </summary>
        private void AttendToCurrent(ConvaiCharacter character)
        {
            if (!attendToCurrentStep || !HasPlan) return;

            var step = Current;
            if (!step.HasPlace) return;

            character.DynamicContext.SetCurrentAttentionObject(step.Where, ConvaiRespondMode.Silent);

            if (verboseLogging) Debug.Log($"{Tag} Attention -> '{step.Where}' for step {step.Number}.");
        }

        /// <summary>The whole plan as one block she can read back from.</summary>
        private string StepsAsText()
        {
            var builder = new StringBuilder(_steps.Count * 48);

            foreach (var step in _steps)
            {
                if (builder.Length > 0) builder.Append('\n');

                builder.Append(step.Number).Append(". ").Append(step.Text);

                if (step.HasPlace) builder.Append(" [at the ").Append(step.Where).Append(']');
            }

            return builder.ToString();
        }

        /// <summary>Where we are, said the way she would say it.</summary>
        private string CurrentAsText()
        {
            var step = Current;

            var where = step.HasPlace ? $", at the {step.Where}" : "";
            return $"step {step.Number} of {_steps.Count}{where}: {step.Text}";
        }

        private static string Clamp(string text, int limit) =>
            string.IsNullOrEmpty(text) || text.Length <= limit ? text : text.Substring(0, limit);
    }
}
