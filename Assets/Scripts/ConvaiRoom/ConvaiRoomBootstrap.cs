using System;
using System.Collections;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Room;
using Convai.Shared.Actions;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;
using UnityEngine.AI;

namespace ConvaiRoom
{
    /// <summary>
    /// Orders startup for a Convai character living inside a replayed room scan.
    ///
    /// The ordering is the whole point. The object catalogue can only be sent to the
    /// backend as part of the connect handshake, so the scan has to be loaded and the
    /// navmesh baked BEFORE the room manager connects. That means
    /// <see cref="ConvaiRoomManager"/> must have Connect On Start switched off -- if it
    /// connects by itself the character comes up knowing nothing about the room, and
    /// there is no second chance without a reconnect.
    ///
    /// Each stage degrades on its own: no scan still connects (the character just cannot
    /// discuss the room), and a failed bake still connects (it can talk but not walk).
    /// </summary>
    public class ConvaiRoomBootstrap : MonoBehaviour
    {
        [Header("Room scan")]
        public RoomScanRebuilder rebuilder;
        public RoomScanNavMeshBuilder navMeshBuilder;

        [Header("Convai")]
        public ConvaiRoomManager roomManager;
        public RoomScanActionConfigBuilder actionConfigBuilder;

        [Header("Character placement")]
        [Tooltip("The character's NavMeshAgent. Kept disabled until the bake finishes so " +
                 "it does not complain about starting off-mesh.")]
        public NavMeshAgent agent;

        [Tooltip("How far in front of the player the character tries to appear.")]
        public float spawnDistanceFromPlayer = 1.5f;

        [Tooltip("How far to search outward for walkable floor if that spot is blocked.")]
        public float spawnSearchRadius = 3f;

        [Header("Startup")]
        [Tooltip("How long to wait for MRUK before replaying the scan anyway. Lets the " +
                 "scene run in the Editor with no headset attached.")]
        public float mrukTimeoutSeconds = 5f;

        [Header("Debug")]
        public bool verboseLogging = true;

        private bool _hasRun;

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (navMeshBuilder == null) navMeshBuilder = FindAnyObjectByType<RoomScanNavMeshBuilder>();
            if (roomManager == null) roomManager = FindAnyObjectByType<ConvaiRoomManager>();
            if (actionConfigBuilder == null)
                actionConfigBuilder = FindAnyObjectByType<RoomScanActionConfigBuilder>();

            // Enabling an agent before any navmesh exists logs a warning per agent per
            // frame. Cheaper to hold it off until there is a floor to stand on.
            if (agent != null) agent.enabled = false;
        }

        private void Start()
        {
            if (roomManager != null && roomManager.EffectiveConnectOnStart)
            {
                Debug.LogError("[ConvaiRoomBootstrap] ConvaiRoomManager has Connect On Start " +
                               "enabled. It will connect before the room scan is ready and the " +
                               "character will know nothing about the room. Switch it off on " +
                               "the room manager (or its profile asset).");
            }

            if (MRUK.Instance == null)
            {
                Debug.LogError("[ConvaiRoomBootstrap] No MRUK in the scene. The scan will replay " +
                               "in raw world space, so nothing will line up with the real room.");
                Run();
                return;
            }

            MRUK.Instance.RegisterSceneLoadedCallback(Run);
            StartCoroutine(RunIfMrukNeverReports());
        }

        /// <summary>
        /// MRUK only raises SceneLoadedEvent once it actually has room data. In the Editor
        /// with no headset attached -- or on a device where Space Setup was never run -- it
        /// stays silent forever, and every downstream stage would wait on it indefinitely.
        ///
        /// Proceeding anyway means the scan replays in raw world space rather than anchored
        /// to a real room, which is wrong on a headset but exactly right for desk testing.
        /// </summary>
        private IEnumerator RunIfMrukNeverReports()
        {
            yield return new WaitForSeconds(mrukTimeoutSeconds);
            if (_hasRun) yield break;

            Debug.LogWarning($"[ConvaiRoomBootstrap] MRUK reported no room within " +
                             $"{mrukTimeoutSeconds}s. Replaying the scan in raw world space " +
                             $"-- fine on a desk, but on a headset it means Space Setup has " +
                             $"not been run and nothing will line up with the real room.");
            Run();
        }

        /// <summary>Runs the startup sequence. Idempotent -- only the first call does work.</summary>
        public void Run()
        {
            if (_hasRun) return;
            _hasRun = true;

            if (rebuilder == null)
            {
                Debug.LogError("[ConvaiRoomBootstrap] No RoomScanRebuilder; nothing to replay.");
                Connect(null);
                return;
            }

            rebuilder.Rebuild();

            var baked = navMeshBuilder != null && navMeshBuilder.Build();
            if (baked) PlaceCharacter();
            else
                Debug.LogWarning("[ConvaiRoomBootstrap] No navmesh, so the character will not " +
                                 "be able to walk. It can still hold a conversation.");

            var config = actionConfigBuilder != null ? actionConfigBuilder.Build() : null;
            Connect(config);
        }

        /// <summary>
        /// Drops the character onto walkable floor in front of the player. Warp rather
        /// than assigning the transform: an agent placed by transform alone stays
        /// unregistered with the navmesh and refuses to path.
        /// </summary>
        private void PlaceCharacter()
        {
            if (agent == null) return;

            var desired = transform.position;
            var head = Camera.main;

            if (head != null)
            {
                var forward = head.transform.forward;
                forward.y = 0f;

                // Looking straight up or down leaves no usable heading.
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;

                desired = head.transform.position + forward.normalized * spawnDistanceFromPlayer;
            }

            if (!NavMesh.SamplePosition(desired, out var hit, spawnSearchRadius, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[ConvaiRoomBootstrap] No walkable floor within " +
                                 $"{spawnSearchRadius}m of the player. Leaving the character " +
                                 $"where it was placed in the scene.");
                return;
            }

            agent.enabled = true;
            agent.Warp(hit.position);

            if (verboseLogging)
                Debug.Log($"[ConvaiRoomBootstrap] Character placed at {hit.position}.");
        }

        private async void Connect(ConvaiActionConfig config)
        {
            if (roomManager == null)
            {
                Debug.LogError("[ConvaiRoomBootstrap] No ConvaiRoomManager to connect.");
                return;
            }

            try
            {
                IConvaiOperation<RoomSession> operation;

                if (config == null)
                {
                    Debug.LogWarning("[ConvaiRoomBootstrap] Connecting without a room catalogue; " +
                                     "the character will not know what is around it.");
                    operation = roomManager.ConnectAsync();
                }
                else
                {
                    // Only the config is overridden. The action templates stay on the
                    // character's ConvaiActionConfigSource, and the wire strings in this
                    // config were rendered from those same definitions -- so the backend's
                    // catalogue and the locally executable one cannot disagree.
                    operation = roomManager.ConnectAsync(new RoomSessionConnectOptions
                    {
                        ActionConfigOverride = config
                    });
                }

                await operation.AsTask();

                if (verboseLogging)
                    Debug.Log($"[ConvaiRoomBootstrap] Connected with " +
                              $"{config?.Objects?.Count ?? 0} room objects.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConvaiRoomBootstrap] Connect failed: {ex.Message}");
            }
        }
    }
}
