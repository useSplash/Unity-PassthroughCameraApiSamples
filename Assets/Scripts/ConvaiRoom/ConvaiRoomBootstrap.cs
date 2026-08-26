using System.Collections;
using Convai.Runtime.Adapters.Networking;
using Meta.XR.MRUtilityKit;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Orders startup for the room: wait for MRUK to report a room, then replay the scan into
    /// it.
    ///
    /// This used to open the Convai session here as well, and could not: the room the SDK
    /// connects is composed around a character, and in this flow there is no character until
    /// phase 2 spawns one. Connecting at startup therefore failed every time on
    /// "Failed to resolve any owned characters", which reads like a broken connection rather
    /// than a connection made too early. The session now belongs to
    /// <see cref="RoomCharacterVoice"/>, which opens it when a character actually exists.
    ///
    /// What is left here is still worth its own component: the scan has to be replayed before
    /// anything can be baked or stood on, and it has to wait for MRUK or it lands in raw world
    /// space. Each stage degrades on its own -- no MRUK still replays, just unanchored.
    /// </summary>
    public class ConvaiRoomBootstrap : MonoBehaviour
    {
        private const string Tag = "[ConvaiRoomBootstrap]";

        [Header("Room scan")]
        public RoomScanRebuilder rebuilder;

        [Header("Convai")]
        [Tooltip("Read only, and only to check its Connect On Start is off. Nothing here " +
                 "connects -- the character's session is opened by RoomCharacterVoice once " +
                 "there is a character to open it for.")]
        public ConvaiRoomManager roomManager;

        [Header("Startup")]
        [Tooltip("How long to wait for MRUK before replaying the scan anyway. Lets the " +
                 "scene run in the Editor with no headset attached.")]
        public float mrukTimeoutSeconds = 5f;

        [Tooltip("Replay whatever scan is on disk as soon as MRUK reports a room.\n\n" +
                 "Off, and it should stay off. The panel decides when a scan is replayed now: " +
                 "the app opens on a choice between scanning and loading, and a room that has " +
                 "already filled itself with the last scan's boxes has answered that question " +
                 "for you. Turn it on only to look at a saved scan with the panel out of the " +
                 "way.")]
        public bool replayScanOnStart;

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
            WarnIfConnectingOnItsOwn();

            if (MRUK.Instance == null)
            {
                Debug.LogError($"{Tag} No MRUK in the scene. The scan will replay in raw world " +
                               $"space, so nothing will line up with the real room.");
                Run();
                return;
            }

            MRUK.Instance.RegisterSceneLoadedCallback(Run);
            StartCoroutine(RunIfMrukNeverReports());
        }

        /// <summary>
        /// Says so when the room manager will try to connect by itself.
        ///
        /// It cannot succeed at startup -- there is no character yet -- so the only thing an
        /// early connect produces is a failure in the log that looks exactly like the real
        /// thing going wrong later. Worth a line of its own because the setting is on the room
        /// manager, several components away from where the confusion appears.
        /// </summary>
        private void WarnIfConnectingOnItsOwn()
        {
            if (roomManager == null || !roomManager.EffectiveConnectOnStart) return;

            Debug.LogError($"{Tag} ConvaiRoomManager has Connect On Start enabled, so it will " +
                           $"try to open a session before the room has a character in it. That " +
                           $"connect can only fail, and its error buries the real one. Switch " +
                           $"Connect On Start off on the room manager (or its profile asset).");
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

            Debug.LogWarning($"{Tag} MRUK reported no room within {mrukTimeoutSeconds}s. " +
                             $"Replaying the scan in raw world space -- fine on a desk, but on " +
                             $"a headset it means Space Setup has not been run and nothing " +
                             $"will line up with the real room.");
            Run();
        }

        /// <summary>Runs the startup sequence. Idempotent -- only the first call does work.</summary>
        public void Run()
        {
            if (_hasRun) return;
            _hasRun = true;

            if (rebuilder == null)
            {
                Debug.LogError($"{Tag} No RoomScanRebuilder; nothing to replay.");
                return;
            }

            // The wait above is still worth doing with the replay switched off. It is what
            // decides whether the room came up anchored at all, and that answer is wanted
            // before anyone presses anything -- the panel reads the same MRUK room and would
            // otherwise be the first thing to notice it was missing, several button presses in.
            if (!replayScanOnStart)
            {
                if (verboseLogging)
                    Debug.Log($"{Tag} Room is up; leaving the scan alone. The panel replays it " +
                              $"when someone asks for it.");
                return;
            }

            rebuilder.Rebuild();

            if (verboseLogging)
                Debug.Log($"{Tag} Replayed {rebuilder.Rebuilt?.Count ?? 0} boxes.");
        }
    }
}
