using System.IO;
using ConvaiRoom;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ConvaiRoomEditor
{
    /// <summary>
    /// Builds the phase 1 scan panel as a prefab.
    ///
    /// This is the authoring half of what <see cref="ConvaiRoomModePanel"/> used to do at
    /// runtime in its BuildUi method. The layout numbers below are that method's, carried over
    /// unchanged so the baked prefab starts out identical to the panel that was being built on
    /// device -- including the 54-unit button heights, which were widened from 46 because a
    /// hand ray jitters more than a controller ray.
    ///
    /// Run it once to get a prefab, then edit the prefab by hand and never come back. It is
    /// kept rather than deleted so there is a way to regenerate a known-good panel if the
    /// prefab is lost or edited into a corner -- but note that re-baking OVERWRITES, so any
    /// hand-editing is gone. It asks first.
    /// </summary>
    public static class ScanPanelPrefabBaker
    {
        private const string PrefabDirectory = "Assets/Prefabs/ConvaiRoom";
        private const string PrefabPath = PrefabDirectory + "/Scan Panel.prefab";

        // Authoring units. The canvas is laid out in these and then scaled to metres, so the
        // numbers below read like ordinary UI pixels rather than millimetres.
        private const float CanvasWidth = 420f;
        private const float CanvasHeight = 660f;

        /// <summary>Physical width of the panel in metres. Height follows the same scale.</summary>
        private const float PanelWidth = 0.42f;

        /// <summary>
        /// Draw order for the panel canvas: above the scan wireframes at 0, below the laser
        /// and its cursor dot at 200. See ConvaiRoomLaserCursor.sortingOrder.
        /// </summary>
        private const int PanelSortingOrder = 100;

        private static readonly Color Background = new Color(0.05f, 0.07f, 0.10f, 0.85f);
        private static readonly Color TitleColor = new Color(1f, 0.85f, 0.3f);
        private static readonly Color ActionButton = new Color(0.22f, 0.28f, 0.34f, 0.92f);
        private static readonly Color LockedButton = new Color(0.16f, 0.16f, 0.18f, 0.75f);
        private static readonly Color LockedLabel = new Color(0.55f, 0.55f, 0.58f);

        /// <summary>Muted red. Reads as destructive without shouting over the readout.</summary>
        private static readonly Color ExitButton = new Color(0.42f, 0.18f, 0.20f, 0.92f);

        [MenuItem("Tools/Convai Room/Bake Scan Panel Prefab")]
        public static void Bake()
        {
            if (File.Exists(PrefabPath)
                && !EditorUtility.DisplayDialog(
                    "Overwrite the scan panel prefab?",
                    $"{PrefabPath} already exists.\n\nRe-baking replaces it with a freshly " +
                    $"generated panel. Any changes you have made to it by hand will be lost.",
                    "Overwrite", "Cancel"))
                return;

            BakeTo(PrefabPath);
        }

        /// <summary>
        /// Bakes the panel without asking anything.
        ///
        /// Split out from the menu item so the bake can be driven from a script or an editor
        /// automation step. A modal confirmation in the middle of one of those does not prompt
        /// anybody, it just hangs the editor until somebody happens to walk past.
        ///
        /// Overwrites an existing asset at the path in place, which keeps its GUID -- and that
        /// matters, because the scene refers to this prefab by GUID. Deleting and recreating
        /// would hand back a new one and orphan every instance.
        /// </summary>
        public static void BakeTo(string prefabPath)
        {
            var root = BuildHierarchy();

            try
            {
                // Only when the folder is genuinely new. Refresh kicks off an import, and from
                // inside an automated call that means a domain reload part-way through -- which
                // tears down the assembly the caller is running in and unwinds the bake with it.
                var directory = Path.GetDirectoryName(prefabPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    AssetDatabase.Refresh();
                }

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var success);

                if (!success || prefab == null)
                {
                    Debug.LogError($"[ScanPanelBaker] Failed to write {prefabPath}.");
                    return;
                }

                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);

                Debug.Log($"[ScanPanelBaker] Baked the scan panel to {prefabPath}. Existing " +
                          $"instances pick this up automatically; a fresh one needs its " +
                          $"recorder, rebuilder and scan controller assigned.");
            }
            finally
            {
                // The hierarchy only ever existed to be saved. Leaving it behind would put a
                // second, un-instanced panel in whatever scene happened to be open.
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject BuildHierarchy()
        {
            var root = new GameObject("Scan Panel");
            var panel = root.AddComponent<ConvaiRoomModePanel>();

            var canvasGo = new GameObject("Panel Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(root.transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            // Above the scan wireframes, which draw at 0. Both this canvas and those boxes are
            // alpha-blended and write no depth, so whichever happens to be nearer the headset
            // wins on distance alone -- which meant a box between you and the panel painted
            // straight over the readout. Sorting order decides it explicitly instead. The
            // laser and its cursor dot sit above both, at 200.
            canvas.sortingOrder = PanelSortingOrder;

            var canvasRect = (RectTransform)canvasGo.transform;
            canvasRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            canvasRect.localScale = Vector3.one * (PanelWidth / CanvasWidth);

            // OVRRaycaster rather than the stock GraphicRaycaster: the stock one only
            // understands a screen-space mouse and cannot be driven by a tracked ray.
            var raycaster = canvasGo.AddComponent<OVRRaycaster>();

            var background = MakeRect(canvasRect, "Background", 0f, 0f, CanvasWidth, CanvasHeight)
                .gameObject.AddComponent<Image>();
            background.color = Background;

            // 290 wide rather than the full 388, to leave the title bar's right end free.
            var title = MakeText(canvasRect, "Title", 16f, 12f, 290f, 28f,
                                 24, TextAnchor.MiddleLeft);
            title.text = "PHASE 1 - SCAN";
            title.color = TitleColor;

            // Up in the title bar, deliberately nowhere near SAVE, LOAD and BAKE. Those get
            // pressed constantly with a ray that jitters, and the cost of catching this one by
            // accident is a scan you spent ten minutes collecting. It also asks twice.
            var exit = MakeButton(canvasRect, "Exit Button", "EXIT", 314f, 8f, 90f, 40f);

            var exitImage = exit.targetGraphic as Image;
            if (exitImage != null) exitImage.color = ExitButton;

            // Left empty on purpose -- both of these are overwritten every redraw, and seeding
            // them with placeholder copy only invites someone to edit the placeholder and
            // wonder why it never shows up.
            var counts = MakeText(canvasRect, "Counts", 16f, 44f, CanvasWidth - 32f, 44f,
                                  34, TextAnchor.MiddleLeft);

            // Seven lines now: scanning, ready-rule, scan file, anchored, navmesh, a blank,
            // and the last-action line.
            var status = MakeText(canvasRect, "Status", 16f, 92f, CanvasWidth - 32f, 154f,
                                  16, TextAnchor.UpperLeft);

            var controls = MakeText(canvasRect, "Controls", 16f, 252f, CanvasWidth - 32f, 88f,
                                    16, TextAnchor.UpperLeft);

            // Top of the stack, because it is a mode rather than an action -- everything below
            // it operates on whatever this one has or has not been collecting. The label is
            // rewritten at runtime to name the action; this is only the starting text.
            var scanToggle = MakeButton(canvasRect, "Scan Toggle Button", "STOP SCANNING",
                                        16f, 348f, CanvasWidth - 32f, 54f);

            var save = MakeButton(canvasRect, "Save Button", "SAVE SCAN", 16f, 408f, 186f, 54f);
            var recenter = MakeButton(canvasRect, "Recenter Button", "RECENTER", 218f, 408f, 186f, 54f);

            // Full width, one per row, and above the phase button rather than beside the save
            // pair. Each of these replaces what is in the room -- the loaded boxes, then the
            // navmesh over them -- which is a different kind of act from the two above, and
            // worth not fat-fingering with a jittery hand ray aimed at a half-width target.
            var load = MakeButton(canvasRect, "Load Button", "LOAD SAVED SCAN",
                                  16f, 468f, CanvasWidth - 32f, 54f);

            var bake = MakeButton(canvasRect, "Bake Button", "BAKE NAVMESH",
                                  16f, 528f, CanvasWidth - 32f, 54f);

            var nextPhase = MakeButton(canvasRect, "Next Phase Button",
                                       "NEXT PHASE <color=#7a7a80>(not wired)</color>",
                                       16f, 588f, CanvasWidth - 32f, 54f);

            // Left interactable on purpose. A non-interactable Selectable receives no pointer
            // events at all -- no hover, no press, nothing -- which is exactly how a broken
            // button behaves. It is styled as locked instead, and says so when pressed.
            var nextPhaseImage = nextPhase.targetGraphic as Image;
            if (nextPhaseImage != null) nextPhaseImage.color = LockedButton;

            var nextPhaseLabel = nextPhase.GetComponentInChildren<Text>();
            if (nextPhaseLabel != null) nextPhaseLabel.color = LockedLabel;

            BindPanelFields(panel, raycaster, counts, status, controls, scanToggle,
                            save, recenter, load, bake, exit, nextPhase);
            return root;
        }

        /// <summary>
        /// Assigns the panel's serialized references through SerializedObject rather than
        /// making the fields public just so this can reach them. Nothing outside the panel has
        /// any business setting these at runtime.
        /// </summary>
        private static void BindPanelFields(ConvaiRoomModePanel panel, OVRRaycaster raycaster,
                                            Text counts, Text status, Text controls,
                                            Button scanToggle,
                                            Button save, Button recenter, Button load,
                                            Button bake, Button exit, Button nextPhase)
        {
            var so = new SerializedObject(panel);

            // No title field on purpose -- the prefab owns that label outright.
            Assign(so, "_raycaster", raycaster);
            Assign(so, "_countsText", counts);
            Assign(so, "_statusText", status);
            Assign(so, "_controlsText", controls);
            Assign(so, "_scanToggleButton", scanToggle);
            Assign(so, "_saveButton", save);
            Assign(so, "_recenterButton", recenter);
            Assign(so, "_loadButton", load);
            Assign(so, "_bakeButton", bake);
            Assign(so, "_exitButton", exit);
            Assign(so, "_nextPhaseButton", nextPhase);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(SerializedObject so, string field, Object value)
        {
            var property = so.FindProperty(field);

            if (property == null)
            {
                Debug.LogError($"[ScanPanelBaker] ConvaiRoomModePanel has no serialized field " +
                               $"'{field}'. The baker and the panel have drifted apart; the " +
                               $"prefab will come out half-wired.");
                return;
            }

            property.objectReferenceValue = value;
        }

        // -----------------------------------------------------------------
        // Layout helpers
        // -----------------------------------------------------------------

        private static RectTransform MakeRect(Transform parent, string name,
                                              float x, float y, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;

            // Anchored to the top-left corner so the layout numbers above read as an ordinary
            // top-down stack rather than offsets from the centre.
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private static Text MakeText(Transform parent, string name, float x, float y,
                                     float width, float height, int fontSize, TextAnchor alignment)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // Labels never take the click; the button underneath them does.
            text.raycastTarget = false;
            return text;
        }

        private static Button MakeButton(Transform parent, string name, string label,
                                         float x, float y, float width, float height)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var image = rect.gameObject.AddComponent<Image>();
            image.color = ActionButton;

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = ButtonColors();

            // onClick is deliberately left empty. The panel binds its own handlers in Awake,
            // so a method rename is a compile error rather than a button that silently stops
            // working on a headset.
            MakeText(rect, "label", 0f, 0f, width, height, 20, TextAnchor.MiddleCenter).text = label;
            return button;
        }

        private static ColorBlock ButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;

            // ColorTint multiplies the graphic's own colour, so white leaves the base tint
            // alone and the per-button colours above still show through.
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.06f;
            return colors;
        }

        /// <summary>
        /// The same font the runtime panel used to pick up. This is what Unity assigns to a new
        /// Text component anyway, so hand-added labels will match without any effort.
        /// </summary>
        private static Font BuiltinFont()
        {
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                   ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }
}
