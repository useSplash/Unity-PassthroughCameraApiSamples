using System.Text;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.DynamicContext;
using Convai.Shared.Actions;
using RoomScan;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ConvaiRoom
{
    /// <summary>
    /// A world-space control panel for switching between scanning the room and talking to
    /// the character, without leaving the scene.
    ///
    /// One scene rather than two on purpose. A scene load re-initialises MRUK, which means
    /// re-running the anchor race that puts replayed boxes in raw world space, and it tears
    /// down the Convai session so every switch costs a reconnect. Staying put keeps the
    /// room anchored once and the session alive, which is also what makes pushing a freshly
    /// scanned catalogue to a live character possible at all.
    ///
    /// The panel is placed once and then left alone. An earlier version followed the head
    /// every frame, which is unreadable to look at and makes the panel impossible to aim
    /// at with a laser -- it moves with the thing you are aiming. Use Recenter to call it
    /// back instead.
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

        // The canvas is authored in these units and then scaled down to metres, so the
        // layout numbers below read like ordinary UI pixels instead of millimetres.
        private const float CanvasWidth = 420f;
        private const float CanvasHeight = 340f;

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
        [Tooltip("Drop the panel in front of the player on start. Once placed it stays " +
                 "put -- it never follows your head.")]
        public bool placeOnStart = true;

        public float distanceFromPlayer = 1.2f;
        public float heightOffset = -0.25f;

        [Tooltip("Physical width of the panel in metres. Height follows the same scale.")]
        public float panelWidth = 0.42f;

        [Header("Input")]
        [Tooltip("Clicks a panel button with the laser. The index trigger rather than A, " +
                 "because RoomScanController already owns A/B/X in Scan mode. Secondary is " +
                 "the right hand on Touch controllers, matching the hand the laser is on.")]
        public OVRInput.Button clickButton = OVRInput.Button.SecondaryIndexTrigger;

        [Tooltip("Shortcut for swapping mode without aiming at the panel.")]
        public OVRInput.Button toggleModeButton = OVRInput.Button.PrimaryThumbstick;

        public OVRInput.Button commitRescanButton = OVRInput.Button.SecondaryThumbstick;

        [Header("Startup")]
        public Mode startMode = Mode.Scan;

        /// <summary>The mode currently being displayed.</summary>
        public Mode Current { get; private set; }

        private static readonly Color ActiveButton = new Color(0.15f, 0.55f, 0.75f, 0.95f);
        private static readonly Color InactiveButton = new Color(0.18f, 0.20f, 0.24f, 0.90f);
        private static readonly Color ActionButton = new Color(0.22f, 0.28f, 0.34f, 0.92f);

        private Text _statusText;
        private Text _titleText;
        private Image _scanButtonImage;
        private Image _talkButtonImage;
        private ConvaiRoomLaserCursor _cursor;

        private bool _canMove = true;
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

            // The panel owns its transform and moves it on every recenter. Rebuilt boxes
            // are parented to the rebuilder's transform, so sharing a GameObject with it
            // would drag the entire replayed room along -- which looks exactly like an
            // anchoring bug and is miserable to diagnose on a headset.
            if (GetComponent<RoomScanRebuilder>() != null)
            {
                Debug.LogError("[ConvaiRoomModePanel] This is on the same GameObject as the " +
                               "RoomScanRebuilder, and moving the panel would move every " +
                               "replayed box with it. Put the panel on its own empty GameObject. " +
                               "Placement is disabled for now.");
                _canMove = false;
            }

            BuildUi();
        }

        private void Start()
        {
            Apply(startMode);
            if (placeOnStart) Recenter();
        }

        private void Update()
        {
            if (OVRInput.GetDown(toggleModeButton))
                Apply(Current == Mode.Scan ? Mode.Talk : Mode.Scan);

            if (OVRInput.GetDown(commitRescanButton))
                CommitRescan();

            Redraw();
        }

        // -----------------------------------------------------------------
        // Panel construction
        // -----------------------------------------------------------------

        private void BuildUi()
        {
            var canvasGo = new GameObject("Panel Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRect = (RectTransform)canvasGo.transform;
            canvasRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            canvasRect.localScale = Vector3.one * (panelWidth / CanvasWidth);

            // OVRRaycaster rather than the stock GraphicRaycaster: the stock one only
            // understands a screen-space mouse and cannot be driven by a tracked ray.
            var raycaster = canvasGo.AddComponent<OVRRaycaster>();

            var background = MakeRect(canvasRect, "Background", 0f, 0f, CanvasWidth, CanvasHeight)
                .gameObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.07f, 0.10f, 0.85f);

            _titleText = MakeText(canvasRect, "Title", 16f, 12f, CanvasWidth - 32f, 28f,
                                  24, TextAnchor.MiddleLeft);

            _statusText = MakeText(canvasRect, "Status", 16f, 48f, CanvasWidth - 32f, 170f,
                                   17, TextAnchor.UpperLeft);

            _scanButtonImage = MakeButton(canvasRect, "Scan Button", "SCAN",
                                          16f, 224f, 186f, 46f, () => Apply(Mode.Scan));
            _talkButtonImage = MakeButton(canvasRect, "Talk Button", "TALK",
                                          218f, 224f, 186f, 46f, () => Apply(Mode.Talk));

            MakeButton(canvasRect, "Rescan Button", "COMMIT RESCAN",
                       16f, 278f, 186f, 46f, CommitRescan);
            MakeButton(canvasRect, "Recenter Button", "RECENTER",
                       218f, 278f, 186f, 46f, Recenter);

            _cursor = ConvaiRoomLaserCursor.Create();
            EnsurePointer(raycaster);
        }

        /// <summary>
        /// Makes sure something in the scene is actually driving UI events from a tracked
        /// controller. Without an OVRInputModule the buttons render but never receive a
        /// click, which is indistinguishable from the panel being broken.
        /// </summary>
        private void EnsurePointer(OVRRaycaster raycaster)
        {
            var module = FindAnyObjectByType<OVRInputModule>();

            if (module == null)
            {
                var eventSystem = FindAnyObjectByType<EventSystem>();
                var host = eventSystem != null ? eventSystem.gameObject : new GameObject("EventSystem");

                if (eventSystem == null) host.AddComponent<EventSystem>();

                // Two input modules on one EventSystem fight over the pointer, and the
                // standalone one only understands mouse and keyboard.
                var standalone = host.GetComponent<StandaloneInputModule>();
                if (standalone != null) standalone.enabled = false;

                module = host.AddComponent<OVRInputModule>();
            }

            module.rayTransform = ControllerAnchor();
            module.joyPadClickButton = clickButton;
            module.m_Cursor = _cursor;

            // Assigned here rather than left to OVRRaycaster's OnPointerEnter: until some
            // pointer has entered a canvas the module holds no raycaster, so the very
            // first click of the session has nothing to hit.
            module.activeGraphicRaycaster = raycaster;
            raycaster.pointer = _cursor.gameObject;
        }

        private Transform ControllerAnchor()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();

            if (rig == null)
            {
                Debug.LogWarning("[ConvaiRoomModePanel] No OVRCameraRig, so the laser falls " +
                                 "back to the head. Panel buttons will follow your gaze.");
                return Camera.main != null ? Camera.main.transform : null;
            }

            if (rig.rightHandAnchor != null) return rig.rightHandAnchor;
            if (rig.leftHandAnchor != null) return rig.leftHandAnchor;
            return rig.centerEyeAnchor;
        }

        private RectTransform MakeRect(Transform parent, string name,
                                       float x, float y, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;

            // Anchored to the top-left corner so the layout numbers above read as an
            // ordinary top-down stack rather than offsets from the centre.
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private Text MakeText(Transform parent, string name, float x, float y,
                              float width, float height, int fontSize, TextAnchor alignment)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = ScanLabel.BuiltinFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // Labels never take the click; the button underneath them does.
            text.raycastTarget = false;
            return text;
        }

        private Image MakeButton(Transform parent, string name, string label,
                                 float x, float y, float width, float height, UnityAction onClick)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var image = rect.gameObject.AddComponent<Image>();
            image.color = ActionButton;

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = ButtonColors();
            button.onClick.AddListener(onClick);

            MakeText(rect, "label", 0f, 0f, width, height, 20, TextAnchor.MiddleCenter);
            return image;
        }

        private static ColorBlock ButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;

            // ColorTint multiplies the graphic's own colour, so white leaves the base
            // tint alone and the active/inactive colours below still show through.
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.06f;
            return colors;
        }

        // -----------------------------------------------------------------
        // Behaviour
        // -----------------------------------------------------------------

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

        /// <summary>Drops the panel back in front of the player, facing them.</summary>
        public void Recenter()
        {
            if (!_canMove)
            {
                _lastAction = "recenter refused: panel shares the rebuilder's object";
                return;
            }

            var head = Camera.main;
            if (head == null)
            {
                Debug.LogWarning("[ConvaiRoomModePanel] No main camera, so the panel cannot " +
                                 "be placed relative to the player.");
                return;
            }

            var forward = head.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();

            transform.position = head.transform.position + forward * distanceFromPlayer
                                                         + Vector3.up * heightOffset;

            // The canvas draws on its +Z face, so matching the player's forward turns the
            // readable side toward them rather than away.
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            _lastAction = "panel recentered";
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

        private void Redraw()
        {
            if (_statusText == null) return;

            var scanning = Current == Mode.Scan;
            var catalogue = actionConfigBuilder != null ? actionConfigBuilder.LastObjects : null;

            if (_titleText != null)
            {
                _titleText.text = scanning ? "SCAN MODE" : "TALK MODE";
                _titleText.color = scanning
                    ? new Color(1f, 0.85f, 0.3f)
                    : new Color(0.5f, 0.9f, 1f);
            }

            if (_scanButtonImage != null)
                _scanButtonImage.color = scanning ? ActiveButton : InactiveButton;
            if (_talkButtonImage != null)
                _talkButtonImage.color = scanning ? InactiveButton : ActiveButton;

            _builder.Clear();
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

            _statusText.text = _builder.ToString();
        }
    }
}
