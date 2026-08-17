using System.Collections.Generic;
using System.Text;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Turns a replayed room scan into the object catalogue the Convai backend reasons
    /// over, so the character can both talk about what is in the room and be told to
    /// walk to it.
    ///
    /// Every object here costs prompt tokens on every single turn, not just the turn it
    /// gets mentioned -- hence the confidence floor and the hard cap. A scan with two
    /// hundred low-confidence detections would otherwise quietly eat the context window.
    /// </summary>
    public class RoomScanActionConfigBuilder : MonoBehaviour
    {
        [Header("Sources")]
        public RoomScanRebuilder rebuilder;

        [Tooltip("Supplies the action templates (Move To, Look At). The wire strings are " +
                 "rendered from these, so the backend catalogue cannot drift from what " +
                 "this client can actually execute.")]
        public ConvaiActionConfigSource configSource;

        [Header("Filtering")]
        [Tooltip("YOLO reports plenty it is not sure about. Below this, the object is not " +
                 "worth telling the character about.")]
        [Range(0f, 1f)]
        public float minConfidence = 0.4f;

        [Tooltip("Keeps the highest-confidence objects when the scan finds more than this.")]
        public int maxObjects = 40;

        [Tooltip("Labels never sent. 'person' is the player and anyone with them -- the " +
                 "character should not treat them as scenery.")]
        public string[] ignoredLabels = { "person" };

        [Tooltip("Matches the navmesh builder's threshold so 'standing on the floor' in a " +
                 "description means the same thing the character has to walk around.")]
        public float groundThreshold = 0.15f;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>Object names sent on the last build, in the order they were sent.</summary>
        public IReadOnlyList<string> LastObjectNames => _lastNames;

        /// <summary>
        /// The full definitions sent on the last build, so a caller can get from a
        /// catalogue name back to the proxy it refers to. The candidate list is filtered
        /// and re-sorted by confidence, so it does not line up index-for-index with
        /// <see cref="RoomScanRebuilder.Rebuilt"/> -- pairing those two by position picks
        /// the wrong object.
        /// </summary>
        public IReadOnlyList<ConvaiActionObjectDefinition> LastObjects => _lastObjects;

        private readonly List<string> _lastNames = new List<string>();
        private readonly List<ConvaiActionObjectDefinition> _lastObjects =
            new List<ConvaiActionObjectDefinition>();

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
        }

        /// <summary>
        /// Builds the config to hand to ConnectAsync. Returns null when there is nothing
        /// worth sending, in which case the caller should connect without an override
        /// rather than sending an empty catalogue.
        /// </summary>
        public ConvaiActionConfig Build()
        {
            _lastNames.Clear();
            _lastObjects.Clear();

            if (rebuilder == null)
            {
                Debug.LogError("[RoomScanActionConfigBuilder] No RoomScanRebuilder assigned.");
                return null;
            }

            if (configSource == null)
            {
                Debug.LogError("[RoomScanActionConfigBuilder] No ConvaiActionConfigSource " +
                               "assigned, so there are no action templates to offer.");
                return null;
            }

            var actions = RenderActionStrings();
            if (actions.Count == 0)
            {
                Debug.LogError("[RoomScanActionConfigBuilder] No action definitions with a " +
                               "bound executor. Convai drops the whole action_config when " +
                               "nothing is executable, so the objects would be ignored.");
                return null;
            }

            var candidates = SelectObjects();
            if (candidates.Count == 0)
            {
                Debug.LogWarning("[RoomScanActionConfigBuilder] Scan produced no objects " +
                                 "above the confidence floor.");
                return null;
            }

            var floorY = rebuilder.Scan?.room?.floorY ?? 0f;
            var objects = new List<ConvaiActionObjectDefinition>(candidates.Count);
            var usedNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var entry in candidates)
            {
                var name = UniqueName(entry.Data.label, usedNames);

                objects.Add(new ConvaiActionObjectDefinition
                {
                    Name = name,
                    Description = Describe(entry.Data, candidates, floorY),
                    GameObjectReference = entry.Proxy
                });

                _lastNames.Add(name);
            }

            _lastObjects.AddRange(objects);

            if (verboseLogging)
            {
                Debug.Log($"[RoomScanActionConfigBuilder] Offering {objects.Count} objects and " +
                          $"{actions.Count} actions: {string.Join(", ", _lastNames)}");
            }

            return new ConvaiActionConfig
            {
                Actions = actions,
                Objects = objects,
                Characters = new List<ConvaiActionCharacterDefinition>()
            };
        }

        /// <summary>
        /// Renders each authored definition to its backend template string. Definitions
        /// without an executor are skipped -- offering the backend an action this client
        /// cannot run just produces commands that get rejected on arrival.
        /// </summary>
        private List<string> RenderActionStrings()
        {
            var actions = new List<string>();

            foreach (var definition in configSource.Definitions)
            {
                if (definition == null || string.IsNullOrWhiteSpace(definition.ActionName))
                    continue;

                if (definition.Executor == null)
                {
                    Debug.LogWarning($"[RoomScanActionConfigBuilder] Action " +
                                     $"'{definition.ActionName}' has no executor bound; skipping.");
                    continue;
                }

                actions.Add(definition.ToActionConfigString());
            }

            return actions;
        }

        private List<RoomScanRebuilder.RebuiltObject> SelectObjects()
        {
            var kept = new List<RoomScanRebuilder.RebuiltObject>();

            foreach (var entry in rebuilder.Rebuilt)
            {
                var obj = entry.Data;
                if (obj == null || entry.Proxy == null) continue;
                if (string.IsNullOrWhiteSpace(obj.label)) continue;
                if (obj.confidence < minConfidence) continue;
                if (IsIgnored(obj.label)) continue;

                kept.Add(entry);
            }

            // Highest confidence first, so trimming to the cap drops the least certain.
            kept.Sort((a, b) => b.Data.confidence.CompareTo(a.Data.confidence));

            if (maxObjects > 0 && kept.Count > maxObjects)
            {
                if (verboseLogging)
                {
                    Debug.Log($"[RoomScanActionConfigBuilder] Scan had {kept.Count} objects; " +
                              $"keeping the {maxObjects} most confident.");
                }

                kept.RemoveRange(maxObjects, kept.Count - maxObjects);
            }

            return kept;
        }

        private bool IsIgnored(string label)
        {
            if (ignoredLabels == null) return false;

            foreach (var ignored in ignoredLabels)
                if (!string.IsNullOrEmpty(ignored) &&
                    string.Equals(ignored, label, System.StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        /// Convai resolves targets by exact name and rejects the entire config on a
        /// duplicate, so three chairs have to become "chair", "chair 2", "chair 3".
        /// </summary>
        private static string UniqueName(string label, HashSet<string> used)
        {
            var baseName = label.Trim();
            if (used.Add(baseName)) return baseName;

            for (var i = 2; ; i++)
            {
                var candidate = $"{baseName} {i}";
                if (used.Add(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Plain-language description. This is the only thing the model sees about the
        /// object beyond its name, so it carries size, whether it is underfoot, and what
        /// it sits near -- enough to answer "what is in this room?" usefully.
        /// </summary>
        private string Describe(ScannedObject obj,
                                List<RoomScanRebuilder.RebuiltObject> all,
                                float floorY)
        {
            var size = obj.size.ToVector3();
            var builder = new StringBuilder();

            builder.Append("A ").Append(obj.label).Append(", about ");
            builder.Append(size.x.ToString("0.0")).Append(" x ").Append(size.z.ToString("0.0"));
            builder.Append(" m and ").Append(size.y.ToString("0.0")).Append(" m tall");

            builder.Append(RoomScanNavMeshBuilder.IsOnFloor(obj, floorY, groundThreshold)
                ? ", standing on the floor"
                : ", raised off the floor");

            var neighbour = NearestLabel(obj, all);
            if (!string.IsNullOrEmpty(neighbour)) builder.Append(", next to a ").Append(neighbour);

            builder.Append('.');
            return builder.ToString();
        }

        private static string NearestLabel(ScannedObject obj,
                                           List<RoomScanRebuilder.RebuiltObject> all)
        {
            var origin = obj.position.ToVector3();
            var bestDistance = float.MaxValue;
            string bestLabel = null;

            foreach (var other in all)
            {
                if (ReferenceEquals(other.Data, obj)) continue;

                var distance = Vector3.Distance(origin, other.Data.position.ToVector3());
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                bestLabel = other.Data.label;
            }

            // Beyond a couple of metres "next to" stops being true.
            return bestDistance <= 2f ? bestLabel : null;
        }
    }
}
