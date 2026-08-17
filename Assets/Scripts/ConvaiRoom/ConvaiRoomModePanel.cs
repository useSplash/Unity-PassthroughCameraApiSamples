using System.Text;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.DynamicContext;
using Convai.Shared.Actions;
using RoomScan;
using UnityEngine;
using UnityEngine.AI;

namespace ConvaiRoom
{
    /// <summary>
    /// A floating debug panel for switching between scanning the room and talking to the
    /// character, without leaving the scene.
    ///
    /// One scene rather than two on purpose. A scene load re-initialises MRUK, which means
    /// re-running the anchor race that puts replayed boxes in raw world space, and it tears
    /// down the Convai session so every switch costs a reconnect. Staying put keeps the
    /// room anchored once and the session alive, which is also what makes pushing a freshly
    /// scanned catalogue to a live character possible at all.
    ///
    /// Scan mode hides the character by switching off its renderers rather than
    /// deactivating the GameObject -- the Convai components on it own the live session, and
    /// disabling them mid-call is a good way to lose it.
    /// </summary>
    public class ConvaiRoomModePanel : MonoBehaviour
    {
        public enum Mode
        {
            Scan,
            Talk
        }

        [Header("Wiring (left empty, these are found in the scene)")]
        public RoomScanRebuilder rebuilder;
        public RoomScanNavMeshBuilder navMeshBuilder;
        public RoomScanActionConfigBuilder actionConfigBuilder;
        public ConvaiCharacter character;
        public NavMeshAgent agent;

        [Tooltip("Live scanning components, switched off in Talk mode so they stop " +
                 "consuming depth and GPU while you are only holding a conversation.")]
        public ObjectScanRecorder recorder;

        public RoomScanController scanController;

        [Header("Panel placement")]
        [Tooltip("Keeps the panel floating in front of the player. Switch off to leave it " +
                 "wherever it spawned.")]
        public bool followPlayer = true;

        public float distanceFromPlayer = 1.2f;
        public float heightOffset = -0.25f;

        [Tooltip("Lower is lazier. A panel welded to the head is uncomfortable to read.")]
        public float followSpeed = 4f;

        public float characterSize = 0.012f;

        [Header("Input -- avoids A/B/X/Y, which RoomScanController already uses")]
        public OVRInput.Button toggleModeButton = OVRInput.Button.PrimaryThumbstick;
        public OVRInput.Button commitRescanButton = OVRInput.Button.SecondaryThumbstick;
        public KeyCode toggleModeKey = KeyCode.M;
        public KeyCode commitRescanKey = KeyCode.R;

        [Header("Startup")]
        public Mode startMode = Mode.Talk;

        /// <summary>The mode currently being displayed.</summary>
        public Mode Current { get; private set; }

        private TextMesh _label;
        private string _lastAction = "none yet";
        private readonly StringBuilder _builder = new StringBuilder();

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (navMeshBuilder == null) navMeshBuilder = FindAnyObjectByType<RoomScanNavMeshBuilder>();
            if (actionConfigBuilder == null)
                actionConfigBuilder = FindAnyObjectByType<RoomScanActionConfigBuilder>();
            if (character == null) character = FindAnyObjectByType<ConvaiCharacter>();
            if (agent == null) agent = FindAnyObjectByType<NavMeshAgent>();
            if (recorder == null) recorder = FindAnyObjectByType<ObjectScanRecorder>();
            if (scanController == null) scanController = FindAnyObjectByType<RoomScanController>();

            // The panel drives its own transform to follow the player. Rebuilt boxes are
            // parented to the rebuilder's transform, so sharing a GameObject with it would
            // drag the entire replayed room around with the panel -- which looks exactly
            // like an anchoring bug and is miserable to diagnose on a headset.
            if (followPlayer && GetComponent<RoomScanRebuilder>() != null)
            {
                Debug.LogError("[ConvaiRoomModePanel] This is on the same GameObject as the " +
                               "RoomScanRebuilder, and following the player would move every " +
                               "replayed box with it. Put the panel on its own empty GameObject. " +
                               "Disabling follow for now.");
                followPlayer = false;
            }

            _label = ScanLabel.Attach(transform, characterSize);
            _label.anchor = TextAnchor.UpperLeft;
            _label.alignment = TextAlignment.Left;
        }

        private void Start() => Apply(startMode);

        private void Update()
        {
            if (Input.GetKeyDown(toggleModeKey) || OVRInput.GetDown(toggleModeButton))
                Apply(Current == Mode.Scan ? Mode.Talk : Mode.Scan);

            if (Input.GetKeyDown(commitRescanKey) || OVRInput.GetDown(commitRescanButton))
                CommitRescan();

            if (followPlayer) FollowPlayer();

            Redraw();
        }

        /// <summary>Switches mode. Public so a button or another script can drive it.</summary>
        public void Apply(Mode mode)
        {
            Current = mode;
            var scanning = mode == Mode.Scan;

            SetCharacterVisible(!scanning);

            // The recorder is the expensive one -- it holds the depth pipeline open.
            if (recorder != null) recorder.enabled = scanning;
            if (scanController != null) scanController.enabled = scanning;

            Debug.Log($"[ConvaiRoomModePanel] Mode -> {mode}.");
        }

        /// <summary>
        /// Adopts whatever scan is currently on disk: replay it, re-bake the floor so the
        /// character can walk the new layout, rebuild the catalogue, and push it to the
        /// live session.
        /// </summary>
        public void CommitRescan()
        {
            if (rebuilder == null)
            {
                _lastAction = "rescan failed: no rebuilder";
                Debug.LogError("[ConvaiRoomModePanel] No RoomScanRebuilder to rescan with.");
                return;
            }

            rebuilder.Rebuild();

            var baked = navMeshBuilder != null && navMeshBuilder.Build();
            if (!baked)
                Debug.LogWarning("[ConvaiRoomModePanel] Re-bake failed, so the character can " +
                                 "still talk about the new scan but not walk it.");

            var config = actionConfigBuilder != null ? actionConfigBuilder.Build() : null;
            if (config == null)
            {
                _lastAction = "rescan: no catalogue built";
                Debug.LogWarning("[ConvaiRoomModePanel] Rescan produced no catalogue.");
                return;
            }

            var pushed = PushCatalogue(config);
            _lastAction = $"rescan: {config.Objects.Count} objects, " +
                          $"bake {(baked ? "ok" : "FAILED")}, " +
                          $"push {(pushed ? "sent" : "SKIPPED")}";
        }

        /// <summary>
        /// Sends the rebuilt catalogue to the already-connected character.
        ///
        /// Apply reports nothing back -- it validates and reconciles the patch internally
        /// and only warns to the log on rejection -- so the best this can confirm is that
        /// the call was made against a live conversation. Watch the console for
        /// "invalid_action_patch" to see the rejection case.
        /// </summary>
        private bool PushCatalogue(ConvaiActionConfig config)
        {
            if (character == null)
            {
                Debug.LogWarning("[ConvaiRoomModePanel] No ConvaiCharacter, so the new " +
                                 "catalogue cannot be delivered; it will apply on next connect.");
                return false;
            }

            // Apply drops the update with only a warning when the character is not yet in
            // conversation, which is easy to miss in a busy log.
            if (!character.IsInConversation)
            {
                Debug.LogWarning("[ConvaiRoomModePanel] Character is not in conversation yet, " +
                                 "so the new catalogue was not sent. It will be picked up when " +
                                 "the session next connects.");
                return false;
            }

            var patch = new ConvaiActionConfigPatch
            {
                Actions = config.Actions,
                Objects = config.Objects
            };

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                text: $"The room was rescanned and now contains {config.Objects.Count} objects.",
                mode: ConvaiContextUpdateMode.Replace,
                reaction: ConvaiRespondMode.Silent,
                actionConfig: patch));

            return true;
        }

        private void SetCharacterVisible(bool visible)
        {
            if (character == null) return;

            foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = visible;
        }

        private void FollowPlayer()
        {
            var head = Camera.main;
            if (head == null) return;

            var forward = head.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();

            var target = head.transform.position + forward * distanceFromPlayer
                                                 + Vector3.up * heightOffset;

            transform.position = Vector3.Lerp(transform.position, target,
                                              1f - Mathf.Exp(-followSpeed * Time.deltaTime));
        }

        private void Redraw()
        {
            if (_label == null) return;

            var scanning = Current == Mode.Scan;
            var catalogue = actionConfigBuilder != null ? actionConfigBuilder.LastObjects : null;

            _builder.Clear();
            _builder.AppendLine($"<< {Current.ToString().ToUpperInvariant()} MODE >>");
            _builder.AppendLine();
            _builder.AppendLine(scanning
                ? "Scanning live. A=export  B=clear  X=rebuild"
                : "Talking. Character visible, scanner off.");
            _builder.AppendLine();

            _builder.AppendLine($"boxes replayed : {rebuilder?.Rebuilt?.Count ?? 0}");
            _builder.AppendLine($"anchored to    : {(rebuilder?.Room != null ? "MRUK room" : "RAW WORLD SPACE")}");
            _builder.AppendLine($"navmesh        : {(navMeshBuilder != null && navMeshBuilder.HasNavMesh ? "valid" : "none")}"
                                + $" ({navMeshBuilder?.ObstacleCount ?? 0} obstacles)");
            _builder.AppendLine($"catalogue      : {catalogue?.Count ?? 0} objects");
            _builder.AppendLine($"agent on mesh  : {(agent != null && agent.enabled && agent.isOnNavMesh)}");
            _builder.AppendLine();
            _builder.AppendLine($"last: {_lastAction}");
            _builder.AppendLine();
            _builder.AppendLine("L-stick click / M = swap mode");
            _builder.AppendLine("R-stick click / R = commit rescan");

            _label.text = _builder.ToString();
            _label.color = scanning ? new Color(1f, 0.85f, 0.3f) : new Color(0.5f, 0.9f, 1f);
        }
    }
}
