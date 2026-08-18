using System;
using System.Collections;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Room;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Orders startup for a Convai character living inside a replayed room scan: wait for
    /// MRUK to report a room, replay the scan into it, then open the session.
    ///
    /// This owns the single connect call, so <see cref="ConvaiRoomManager"/> must have
    /// Connect On Start switched off. Two connects racing each other is the failure this
    /// guards against -- the second one wins and the first session is silently orphaned.
    ///
    /// Each stage degrades on its own: no scan still connects, the character simply has
    /// nothing replayed around it.
    /// </summary>
    public class ConvaiRoomBootstrap : MonoBehaviour
    {
        [Header("Room scan")]
        public RoomScanRebuilder rebuilder;

        [Header("Convai")]
        public ConvaiRoomManager roomManager;

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
            if (roomManager == null) roomManager = FindAnyObjectByType<ConvaiRoomManager>();
        }

        private void Start()
        {
            if (roomManager != null && roomManager.EffectiveConnectOnStart)
            {
                Debug.LogError("[ConvaiRoomBootstrap] ConvaiRoomManager has Connect On Start " +
                               "enabled, so it will open its own session alongside this one. " +
                               "Switch it off on the room manager (or its profile asset).");
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
                Debug.LogError("[ConvaiRoomBootstrap] No RoomScanRebuilder; nothing to replay.");
            else
                rebuilder.Rebuild();

            Connect();
        }

        private async void Connect()
        {
            if (roomManager == null)
            {
                Debug.LogError("[ConvaiRoomBootstrap] No ConvaiRoomManager to connect.");
                return;
            }

            try
            {
                IConvaiOperation<RoomSession> operation = roomManager.ConnectAsync();
                await operation.AsTask();

                if (verboseLogging)
                    Debug.Log($"[ConvaiRoomBootstrap] Connected with " +
                              $"{rebuilder?.Rebuilt?.Count ?? 0} boxes replayed.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConvaiRoomBootstrap] Connect failed: {ex.Message}");
            }
        }
    }
}
