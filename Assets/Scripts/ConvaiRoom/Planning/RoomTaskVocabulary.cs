using System.Collections.Generic;
using Convai.Runtime.Actions;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// The places in this room a plan step is allowed to name.
    ///
    /// This is the single most load-bearing idea in the planner, so it is worth saying plainly:
    /// the planner does not invent locations. It is handed this list and constrained to pick
    /// from it, which is what makes "where in the room can this be done" a solved problem
    /// rather than a second matching pass. A step that says "at the sink" means the sink the
    /// character can already walk to, describe, and have pointed at, because it is the same
    /// string on the same component.
    ///
    /// The set is taken from the live <see cref="ConvaiActionTarget"/> components rather than
    /// from the scan or from MRUK directly, and that choice is the whole reason this composes:
    /// an action target is BY DEFINITION something the backend can resolve and the walk
    /// executor can be sent to. Any other source -- the scan file, the described list, a
    /// hand-kept registry -- would let the planner name a place that reads fine in the plan and
    /// then fails the moment anyone says "take me there".
    ///
    /// A scene search rather than the SDK's own registry because
    /// <c>ConvaiActionTarget.ActiveTargets</c> is internal to the Convai assembly. The cost is
    /// irrelevant here: this runs once per planning request, not per frame.
    /// </summary>
    public static class RoomTaskVocabulary
    {
        /// <summary>One place a plan step may be grounded to.</summary>
        public readonly struct Place
        {
            /// <summary>The exact name the backend, the walk executor and the pointer all use.</summary>
            public readonly string Name;

            /// <summary>What it is, so the planner can tell a table from a television.</summary>
            public readonly string Description;

            /// <summary>The thing itself, for distance work and for setting attention.</summary>
            public readonly GameObject Target;

            public Place(string name, string description, GameObject target)
            {
                Name = name;
                Description = description;
                Target = target;
            }
        }

        /// <summary>
        /// Every place currently groundable, in no particular order.
        ///
        /// Disabled targets are skipped rather than listed and filtered later: a target is
        /// withdrawn from the backend the moment it is disabled, so offering one to the planner
        /// would be offering a place that has already stopped existing.
        /// </summary>
        /// <param name="max">
        /// Most places to return, or zero for all of them. The cap exists because this list is
        /// sent to the planner as an enum on every request, and a cluttered scan can run to
        /// hundreds of boxes. Nearest-first when it bites -- see the sort below.
        /// </param>
        /// <param name="near">
        /// Where to measure from when the cap trims the list. Usually the player: the places you
        /// are standing among are the ones a plan about this room most likely concerns.
        /// </param>
        public static List<Place> Collect(int max = 0, Vector3? near = null)
        {
            var targets = Object.FindObjectsByType<ConvaiActionTarget>(FindObjectsInactive.Exclude);

            var places = new List<Place>(targets.Length);
            var claimed = new HashSet<string>();

            foreach (var target in targets)
            {
                if (target == null || !target.enabled) continue;

                var name = target.TargetName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Two targets under one name would give the planner a choice it cannot express
                // and the backend a name it cannot resolve. First one wins, quietly -- this is a
                // symptom of a naming bug elsewhere, and the planner is the wrong place to
                // report it.
                if (!claimed.Add(name)) continue;

                places.Add(new Place(name, target.Description, target.gameObject));
            }

            if (max <= 0 || places.Count <= max) return places;

            // Nearest first, so the cap keeps the end of the room you are actually in. Sorting
            // only when the cap bites keeps the common case free.
            var origin = near ?? Vector3.zero;
            places.Sort((a, b) =>
            {
                var da = a.Target != null ? (a.Target.transform.position - origin).sqrMagnitude : float.MaxValue;
                var db = b.Target != null ? (b.Target.transform.position - origin).sqrMagnitude : float.MaxValue;
                return da.CompareTo(db);
            });

            places.RemoveRange(max, places.Count - max);
            return places;
        }

        /// <summary>
        /// Whether a name the planner returned is one that was actually offered.
        ///
        /// Belt and braces on top of the schema enum. The model is constrained to the list, but
        /// a step grounded to a place that has since been destroyed -- a scan reloaded while the
        /// request was in flight -- would send the character somewhere that no longer exists.
        /// </summary>
        public static bool Contains(IReadOnlyList<Place> places, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            foreach (var place in places)
                if (string.Equals(place.Name, name, System.StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>The place under a given name, or a default <see cref="Place"/> when none.</summary>
        public static Place Find(IReadOnlyList<Place> places, string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (var place in places)
                    if (string.Equals(place.Name, name, System.StringComparison.OrdinalIgnoreCase))
                        return place;
            }

            return default;
        }
    }
}
