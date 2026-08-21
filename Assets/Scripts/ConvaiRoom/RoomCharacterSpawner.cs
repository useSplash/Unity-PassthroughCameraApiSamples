using UnityEngine;
using UnityEngine.AI;
using Convai.Modules.BodyAnimation.Components;

namespace ConvaiRoom
{
    /// <summary>
    /// Puts the phase 2 character into the scanned room: finds a spot on the baked NavMesh,
    /// instantiates the character prefab there, and turns it to face the player.
    ///
    /// The character is spawned rather than authored into the scene on purpose. Phase 1 is a
    /// scan, and a Convai character sitting in the hierarchy through it is a rig being
    /// animated, a NavMeshAgent looking for a surface that does not exist yet, and -- once the
    /// session is wired -- a connection opened before anyone asked for one. Nothing about the
    /// character is useful until there is a floor to stand on, so nothing about it exists
    /// until then.
    ///
    /// This owns placement only, and placement is the last thing here that is ours. Where the
    /// character then walks and what she says both belong to the Convai session: she moves
    /// because the conversation moved her, using the SDK's own locomotion over the navmesh
    /// phase 1 baked. Nothing in this project sends her anywhere.
    /// </summary>
    public class RoomCharacterSpawner : MonoBehaviour
    {
        private const string Tag = "[RoomCharacter]";

        [Header("Character")]
        [Tooltip("The Convai character to drop into the room. Needs an Animator; a " +
                 "NavMeshAgent and ConvaiNavMeshLocomotion are added here if it has none.")]
        public GameObject characterPrefab;

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Says whether anything is baked, and supplies the agent dimensions the bake " +
                 "was made for. Without it the character can still spawn, but its agent will " +
                 "not be matched to the floor it has to walk on.")]
        public RoomScanNavMeshBuilder navMeshBuilder;

        [Header("Placement")]
        [Tooltip("How far in front of the player to try first. The ladder widens out from " +
                 "here when the preferred spot is off the navmesh or inside furniture.")]
        public float preferredDistance = 2f;

        [Tooltip("Never spawn closer than this. A character that materialises inside your " +
                 "personal space is startling in a headset, and on top of you it is invisible.")]
        public float minPlayerDistance = 1f;

        [Tooltip("How far from a candidate point to go looking for walkable floor. Kept " +
                 "small: a large radius silently drags the spawn somewhere you did not aim.")]
        public float sampleRadius = 0.6f;

        [Tooltip("How far from the player to go looking for the baked floor itself. Covers " +
                 "standing head height with slack -- candidates are dropped onto whatever " +
                 "this finds rather than being sampled at your eye height.")]
        public float maxFloorDrop = 3f;

        [Header("Agent")]
        [Tooltip("Push the bake's agent dimensions onto the character's NavMeshAgent.\n\n" +
                 "Leave this on. The navmesh is baked for a 0.25 m half-width so the " +
                 "character fits between real furniture, and Unity's stock agent is 0.5 m. " +
                 "Mismatched, the agent refuses gaps the floor says are walkable and MoveTo " +
                 "fails on paths that plainly exist.")]
        public bool matchAgentToBake = true;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>The live character, or null when none is spawned.</summary>
        public GameObject Character { get; private set; }

        /// <summary>The character's locomotion, or null when none is spawned.</summary>
        public ConvaiNavMeshLocomotion Locomotion { get; private set; }

        public bool IsSpawned => Character != null;

        /// <summary>Where the last successful spawn placed the character.</summary>
        public Vector3 LastSpawnPoint { get; private set; }

        /// <summary>
        /// Why the last <see cref="Spawn"/> failed, ready to put on the panel. Empty after a
        /// successful spawn.
        /// </summary>
        public string LastFailure { get; private set; } = "";

        /// <summary>
        /// Candidate distances as multiples of <see cref="preferredDistance"/>, in the order
        /// they are tried. The preferred distance first, then nearer and further, because in a
        /// small room the far end is often the only clear floor and the near end is often
        /// where you are standing.
        /// </summary>
        private static readonly float[] DistanceLadder = { 1f, 0.8f, 1.3f, 1.6f, 0.6f };

        /// <summary>
        /// Candidate bearings off the player's forward, in degrees, paired outward so the
        /// character appears as close to straight ahead as the floor allows. The last entries
        /// put it behind you, which is worse than in front but far better than not at all.
        /// </summary>
        private static readonly float[] AngleLadder =
            { 0f, -25f, 25f, -50f, 50f, -80f, 80f, -115f, 115f, -150f, 150f, 180f };

        private void Awake()
        {
            if (navMeshBuilder == null) navMeshBuilder = FindAnyObjectByType<RoomScanNavMeshBuilder>();
        }

        /// <summary>
        /// Drops the character into the room. Replaces any character already spawned, so this
        /// doubles as the re-spawn when one ends up somewhere useless.
        ///
        /// Returns false and sets <see cref="LastFailure"/> rather than throwing: every failure
        /// here is a scene-setup problem the person in the headset can act on, and the panel
        /// is where they will read it.
        /// </summary>
        public bool Spawn()
        {
            LastFailure = "";

            if (characterPrefab == null)
            {
                return Fail("no character prefab assigned",
                    $"{Tag} No character prefab assigned on the spawner, so there is nothing " +
                    $"to put in the room. Assign one in the Inspector.");
            }

            if (navMeshBuilder != null && !navMeshBuilder.HasNavMesh)
            {
                return Fail("bake a navmesh first",
                    $"{Tag} Spawn refused: nothing is baked, so the character would have no " +
                    $"floor to stand on and could not walk anywhere. Press BAKE NAVMESH first.");
            }

            if (!TryFindSpawnPoint(out var point, out var facing))
            {
                return Fail("no walkable floor to stand on",
                    $"{Tag} Spawn refused: no point on the baked navmesh was reachable around " +
                    $"the player. The bake may have produced floor only where furniture is, or " +
                    $"the player may be standing outside the scanned room.");
            }

            Despawn();

            Character = InstantiateConfigured(point, facing);
            Locomotion = Character.GetComponentInChildren<ConvaiNavMeshLocomotion>();
            LastSpawnPoint = point;

            if (Locomotion == null)
            {
                Debug.LogWarning($"{Tag} The character has no ConvaiNavMeshLocomotion, so " +
                                 $"nothing can drive it around the room. One is normally added " +
                                 $"here -- check the prefab does not have it on a disabled child.");
            }

            WarnIfItWillSlide();

            if (verboseLogging)
            {
                Debug.Log($"{Tag} Spawned '{characterPrefab.name}' at {point:F2} " +
                          $"(locomotion={(Locomotion != null ? "yes" : "MISSING")}, " +
                          $"agent={DescribeAgent()}).");
            }

            return true;
        }

        /// <summary>
        /// Says so when the character will move without animating.
        ///
        /// This is worth a line of its own because of how the failure presents: the NavMeshAgent
        /// is the position authority, so a character with no body animation still travels the
        /// path perfectly -- it just arrives having glided the whole way with its feet still.
        /// That looks like a broken rig or a bad model rather than a component nobody added,
        /// and it is the single easiest thing to get wrong when building the prefab.
        ///
        /// Not fatal, and deliberately not: a sliding character still proves the navmesh, the
        /// spawn point and the pathing, which is most of what phase 2 is.
        /// </summary>
        private void WarnIfItWillSlide()
        {
            if (Character == null) return;
            if (Character.GetComponentInChildren<ConvaiBodyAnimationController>() != null) return;

            Debug.LogWarning($"{Tag} '{Character.name}' has no ConvaiBodyAnimationController, so " +
                             $"it will slide to its destination rather than walk. Add one to the " +
                             $"prefab with a body animation set and config assigned -- the SDK " +
                             $"ships a female set under Packages/com.convai.convai-sdk-for-unity/" +
                             $"SamplesShared/Profiles/Embodiment/BodyAnimation.");
        }

        /// <summary>Removes the character. Safe to call when none is spawned.</summary>
        public void Despawn()
        {
            if (Character == null) return;

            if (verboseLogging) Debug.Log($"{Tag} Despawned '{Character.name}'.");

            Destroy(Character);
            Character = null;
            Locomotion = null;
        }

        private void OnDisable() => Despawn();

        // -----------------------------------------------------------------
        // Instantiation
        // -----------------------------------------------------------------

        /// <summary>
        /// Instantiates the prefab with its NavMeshAgent already configured.
        ///
        /// The instance is built underneath a deactivated holder so no Awake runs until the
        /// agent is in place and sized. That ordering is the whole reason this is not a plain
        /// Instantiate: ConvaiNavMeshLocomotion.Awake adds a NavMeshAgent when it finds none
        /// and then counts itself the owner, and an owned agent gets its dimensions re-derived
        /// from the rig by the body animation controller -- overwriting the bake's dimensions
        /// with Unity's idea of what fits this model. Adding the agent first means the
        /// locomotion adopts ours and leaves its size alone.
        /// </summary>
        private GameObject InstantiateConfigured(Vector3 point, Quaternion facing)
        {
            var holder = new GameObject($"{characterPrefab.name} (spawning)");
            holder.SetActive(false);

            var instance = Instantiate(characterPrefab, holder.transform);
            instance.name = characterPrefab.name;

            ConfigureAgent(instance);

            // Placed BEFORE it is activated, which matters now the agent is switched on above.
            // A NavMeshAgent takes ownership of the transform the moment it wakes: waking at
            // the holder's origin, it snaps to whatever navmesh is nearest THERE and then
            // ignores a plain transform write, dragging the character back off the spot we
            // chose. Positioning it first means it wakes already standing there.
            instance.transform.SetPositionAndRotation(point, facing);

            // Reparented out of the holder, which is what actually activates it and lets
            // every Awake run. Kept under this spawner's transform rather than at the root so
            // the character is findable next to the rest of the room flow, and the world pose
            // is kept so the placement above survives the reparent.
            instance.transform.SetParent(transform, true);

            Destroy(holder);
            return instance;
        }

        /// <summary>
        /// Makes sure the character has an agent sized for the floor it is about to stand on.
        ///
        /// Height and climb are matched as well as radius: a taller agent than the bake is
        /// clipped by the build volume's headroom, and a larger climb lets paths walk up onto
        /// the low furniture the bake deliberately punched out.
        /// </summary>
        private void ConfigureAgent(GameObject instance)
        {
            if (!instance.TryGetComponent<NavMeshAgent>(out var agent))
                agent = instance.AddComponent<NavMeshAgent>();

            // Switched on explicitly, because a prefab can perfectly well carry a disabled one
            // -- the SDK's own sample character does, and this character was built from it. A
            // disabled agent is not on the navmesh, so MoveTo refuses every destination while
            // the character stands there idling as though nothing had been asked of it. That
            // reads as broken pathing rather than as a tickbox.
            agent.enabled = true;

            if (instance.GetComponentInChildren<ConvaiNavMeshLocomotion>() == null)
                instance.AddComponent<ConvaiNavMeshLocomotion>();

            if (!matchAgentToBake || navMeshBuilder == null) return;

            agent.radius = navMeshBuilder.agentRadius;
            agent.height = navMeshBuilder.agentHeight;

            // Base offset stays at 0: the bake places walkable surface at the real floor
            // height, so a rig whose root is at its feet needs no lift.
            agent.baseOffset = 0f;
        }

        private string DescribeAgent()
        {
            if (Character == null || !Character.TryGetComponent<NavMeshAgent>(out var agent))
                return "none";

            return $"r={agent.radius:F2} h={agent.height:F2} onMesh={agent.isOnNavMesh}";
        }

        // -----------------------------------------------------------------
        // Placement
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds somewhere on the baked navmesh to put the character, preferring in front of
        /// the player and working outward.
        ///
        /// Candidates are sampled onto the navmesh rather than tested against it, so a point
        /// that lands just inside a table gets nudged out to real floor instead of being
        /// thrown away. <see cref="sampleRadius"/> is what stops that nudge from becoming a
        /// teleport across the room.
        /// </summary>
        public bool TryFindSpawnPoint(out Vector3 point, out Quaternion facing)
        {
            point = Vector3.zero;
            facing = Quaternion.identity;

            var head = Camera.main;
            if (head == null)
            {
                Debug.LogWarning($"{Tag} No main camera, so there is no player to place the " +
                                 $"character relative to.");
                return false;
            }

            var origin = head.transform.position;

            // Resolved once, before any candidate is built, and the whole reason this is not a
            // plain loop of samples. The candidates below fan out from the player HORIZONTALLY,
            // which leaves every one of them at eye height -- and the bake is on the floor, a
            // metre and a half underneath. Sampling a 0.6m radius from up there reaches nothing
            // at all, so every candidate misses and the spawn reports no walkable floor while
            // the navmesh is drawn plainly in front of you. Candidates are dropped onto this
            // height before they are sampled.
            if (!NavMesh.SamplePosition(origin, out var underfoot, maxFloorDrop, NavMesh.AllAreas))
            {
                Debug.LogWarning($"{Tag} No baked floor within {maxFloorDrop:F1}m of the player, " +
                                 $"so there is nowhere to stand the character. The likeliest " +
                                 $"cause is standing outside the room that was scanned.");
                return false;
            }

            var floorY = underfoot.position.y;

            var forward = head.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();

            foreach (var distance in DistanceLadder)
            {
                foreach (var angle in AngleLadder)
                {
                    var direction = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                    var candidate = origin + direction * (preferredDistance * distance);

                    // Onto the floor before sampling. Without this the candidate is at head
                    // height and sampleRadius can never span the drop -- see floorY above.
                    candidate.y = floorY;

                    if (!NavMesh.SamplePosition(candidate, out var hit, sampleRadius, NavMesh.AllAreas))
                        continue;

                    // Re-checked after sampling, not before. The sample is what decides where
                    // the character actually ends up, and it can pull a comfortable candidate
                    // back to the edge of the floor right next to the player.
                    var toPlayer = hit.position - origin;
                    toPlayer.y = 0f;
                    if (toPlayer.sqrMagnitude < minPlayerDistance * minPlayerDistance) continue;

                    point = hit.position;

                    // Turned to face the player. A character that arrives with its back to you
                    // reads as one that has not noticed you rather than one that is waiting.
                    var back = -toPlayer.normalized;
                    facing = Quaternion.LookRotation(back, Vector3.up);
                    return true;
                }
            }

            return false;
        }

        private bool Fail(string summary, string logLine)
        {
            LastFailure = summary;
            Debug.LogWarning(logLine);
            return false;
        }
    }
}
