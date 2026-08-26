using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Runtime.SceneMetadata;
using Convai.Shared.Types;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Registers the room's FIXED features -- doors, windows, tables, storage -- as things the
    /// character knows about and can be sent to, alongside the objects the scan found.
    ///
    /// This exists because the scan and Space Setup know about disjoint halves of a room, and
    /// the half the scan misses is the half a task plan most often needs. The detector behind
    /// the scan has a fixed class list: it can find a chair, a laptop and a potted plant, and it
    /// has no class at all for a door, a window, a worktop or a cupboard. Those are exactly the
    /// fixed features you plan around -- "start at the door", "put it in the cupboard" -- and
    /// MRUK has been holding them the whole time, because the player drew them during Space
    /// Setup.
    ///
    /// The components go straight onto the anchor's own GameObject rather than onto a proxy, for
    /// the same reason <see cref="RoomScanContext"/> puts them on the scan boxes: the target
    /// then belongs to the anchor, dies with it when the room is reloaded, and needs no
    /// bookkeeping to stay in step with the room.
    ///
    /// Surfaces and structure are deliberately left out. A floor, a ceiling and a wall face are
    /// not places you send someone -- "walk to the floor" resolves to the middle of the room --
    /// and registering them would crowd the planner's vocabulary with three entries it can never
    /// sensibly use.
    /// </summary>
    public class RoomAnchorContext : MonoBehaviour
    {
        private const string Tag = "[RoomAnchors]";

        /// <summary>The SDK's own limits, matching <see cref="RoomScanContext"/>.</summary>
        private const int MaxNameLength = 50;
        private const int MaxDescriptionLength = 200;

        [Header("What to register")]
        [Tooltip("Describe the room's fixed features as Convai world objects she can name.")]
        public bool describeAnchors = true;

        [Tooltip("Register them as action targets too, so she can be asked to walk to them and " +
                 "so a plan step can be grounded at one.")]
        public bool makeAnchorsWalkable = true;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>How many anchors were registered on the last pass. Read by the panel.</summary>
        public int RegisteredCount { get; private set; }

        /// <summary>
        /// The labels worth registering, and what to call each one out loud.
        ///
        /// Spelled out rather than derived from the enum name because the enum names are wire
        /// identifiers -- DOOR_FRAME, WALL_ART -- and a character saying "walk to the door
        /// frame" sounds like she is reading a schema. The word here is the word she says and
        /// the word the planner grounds against, so it is the word a person would use.
        /// </summary>
        private static readonly (MRUKAnchor.SceneLabels Label, string Name)[] Placeable =
        {
            (MRUKAnchor.SceneLabels.DOOR_FRAME, "door"),
            (MRUKAnchor.SceneLabels.WINDOW_FRAME, "window"),
            (MRUKAnchor.SceneLabels.TABLE, "table"),
            (MRUKAnchor.SceneLabels.STORAGE, "storage unit"),
            (MRUKAnchor.SceneLabels.COUCH, "couch"),
            (MRUKAnchor.SceneLabels.BED, "bed"),
            (MRUKAnchor.SceneLabels.SCREEN, "screen"),
            (MRUKAnchor.SceneLabels.LAMP, "lamp"),
            (MRUKAnchor.SceneLabels.PLANT, "plant"),
            (MRUKAnchor.SceneLabels.WALL_ART, "picture"),
        };

        private void Start()
        {
            if (MRUK.Instance == null)
            {
                Debug.LogWarning($"{Tag} No MRUK in the scene, so the room's doors, windows and " +
                                 $"surfaces will not be registered. She will still know about " +
                                 $"everything the scan found.");
                return;
            }

            // Registering off the scene-loaded callback rather than in Start, because the room
            // does not exist yet on the first frame and iterating nothing would leave the
            // vocabulary permanently short of every fixed feature in the room.
            MRUK.Instance.RegisterSceneLoadedCallback(Register);
        }

        /// <summary>
        /// Registers every placeable anchor in the current room. Safe to call again; a second
        /// pass reconfigures the same components rather than adding more.
        /// </summary>
        public void Register()
        {
            RegisteredCount = 0;

            if (!describeAnchors) return;

            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;

            if (room == null)
            {
                Debug.LogWarning($"{Tag} MRUK reported no current room, so no fixed features " +
                                 $"were registered.");
                return;
            }

            var chosen = new List<(MRUKAnchor Anchor, string Word)>();

            foreach (var anchor in room.Anchors)
            {
                if (anchor == null) continue;
                if (!TryWord(anchor, out var word)) continue;

                chosen.Add((anchor, word));
            }

            // Counted up front so a lone table is "the table" rather than "table 1", the same
            // rule the scan's own naming follows. A number on a unique thing reads as a promise
            // that there is a second one somewhere.
            var totals = new Dictionary<string, int>();
            foreach (var entry in chosen)
            {
                totals.TryGetValue(entry.Word, out var count);
                totals[entry.Word] = count + 1;
            }

            var claimed = ExistingTargetNames();
            var seen = new Dictionary<string, int>();
            var registered = new List<string>();

            foreach (var entry in chosen)
            {
                seen.TryGetValue(entry.Word, out var index);
                index++;
                seen[entry.Word] = index;

                var name = totals[entry.Word] == 1 ? entry.Word : $"{entry.Word} {index}";

                // A name the scan has already taken would make both unresolvable -- the backend
                // matches on the name, and two targets answering to one string is worse than
                // either of them missing. The anchor yields, because the scan's name is the one
                // she has already been describing.
                if (!claimed.Add(name))
                {
                    if (verboseLogging)
                        Debug.Log($"{Tag} Skipped a {entry.Word}: the name '{name}' is already " +
                                  $"taken by something the scan found.");

                    continue;
                }

                Describe(entry.Anchor, name);
                registered.Add(name);
                RegisteredCount++;
            }

            if (verboseLogging)
            {
                Debug.Log(RegisteredCount > 0
                    ? $"{Tag} Registered {RegisteredCount} fixed features: {string.Join(", ", registered)}"
                    : $"{Tag} The room has no doors, windows or surfaces from Space Setup to " +
                      $"register. Only scanned objects will be groundable.");
            }
        }

        /// <summary>
        /// Names already spoken for by a live action target -- almost always the scan's objects.
        ///
        /// Read from the scene rather than from <see cref="RoomScanContext"/> so this holds
        /// whatever actually exists, including targets some later feature adds. The SDK's own
        /// registry would be the better source and is internal to its assembly.
        /// </summary>
        private static HashSet<string> ExistingTargetNames()
        {
            var names = new HashSet<string>();

            var targets = FindObjectsByType<ConvaiActionTarget>(FindObjectsInactive.Exclude);

            foreach (var target in targets)
            {
                // Its own previous pass does not count as a clash, or a second Register() would
                // skip every anchor it registered the first time.
                if (target == null || !target.enabled) continue;
                if (target.GetComponent<MRUKAnchor>() != null) continue;

                if (!string.IsNullOrWhiteSpace(target.TargetName)) names.Add(target.TargetName);
            }

            return names;
        }

        /// <summary>What to call this anchor, or false when it is not somewhere you go.</summary>
        private static bool TryWord(MRUKAnchor anchor, out string word)
        {
            foreach (var candidate in Placeable)
            {
                if ((anchor.Label & candidate.Label) != 0)
                {
                    word = candidate.Name;
                    return true;
                }
            }

            word = null;
            return false;
        }

        private void Describe(MRUKAnchor anchor, string name)
        {
            var description = BuildDescription(anchor, name);
            var go = anchor.gameObject;

            if (!go.TryGetComponent<ConvaiObjectMetadata>(out var metadata))
                metadata = go.AddComponent<ConvaiObjectMetadata>();

            // Through the properties rather than at AddComponent time: the component registers
            // itself in OnEnable, which AddComponent runs before there is a name to register.
            // The setters are what mark the registry dirty and get the finished entry re-sent.
            metadata.ObjectName = name;
            metadata.ObjectDescription = description;
            metadata.IncludeInMetadata = true;

            if (!go.TryGetComponent<ConvaiActionTarget>(out var target))
            {
                if (!makeAnchorsWalkable) return;
                target = go.AddComponent<ConvaiActionTarget>();
            }

            target.TargetName = name;
            target.Description = description;
            target.Kind = ConvaiActionTargetKind.Object;
            target.enabled = makeAnchorsWalkable;
        }

        /// <summary>
        /// One sentence about a fixed feature.
        ///
        /// Kept shorter than the scan's own descriptions on purpose. A door is a door: its size
        /// tells you nothing you want, and the useful fact -- that it is a way out of the room --
        /// is carried by the word itself. Where a size does mean something, a table you could
        /// work at versus one you could not, it is included.
        /// </summary>
        private static string BuildDescription(MRUKAnchor anchor, string name)
        {
            var text = $"The {name} in this room, from the headset's room setup.";

            if (anchor.VolumeBounds.HasValue)
            {
                var size = anchor.VolumeBounds.Value.size;
                text += $" Roughly {Metres(size.x)} by {Metres(size.z)}, {Metres(size.y)} tall.";
            }
            else if (anchor.PlaneRect.HasValue)
            {
                var rect = anchor.PlaneRect.Value;
                text += $" Roughly {Metres(rect.width)} by {Metres(rect.height)}.";
            }

            return Clamp(text, MaxDescriptionLength);
        }

        private static string Metres(float value) => $"{Mathf.Abs(value):0.##} m";

        private static string Clamp(string text, int limit) =>
            string.IsNullOrEmpty(text) || text.Length <= limit ? text : text.Substring(0, limit);
    }
}
