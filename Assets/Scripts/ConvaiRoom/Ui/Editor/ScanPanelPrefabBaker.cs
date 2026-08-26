using System.IO;
using ConvaiRoom;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ConvaiRoomEditor
{
    /// <summary>
    /// Builds the room-flow control panel as a prefab.
    ///
    /// This is the authoring half of what <see cref="ConvaiRoomModePanel"/> used to do at
    /// runtime in its BuildUi method. The button heights are still the 54 units they were
    /// widened to from 46, because a hand ray jitters more than a controller ray and that is
    /// the number that made them reliable to hit.
    ///
    /// What changed with the flow is how many buttons there are: the panel used to carry every
    /// control at once -- scan, save, load, bake, next phase -- and now carries three SLOTS
    /// that the panel fills in from whichever stage it is in. So the layout below is shorter
    /// than the flow is, and the flow lives in the panel's LayOutActions rather than here.
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

        // Came back down from 760 when the five stacked actions became three slots. The
        // readout kept its height -- a plan is up to six steps drawn at once -- and the panel
        // got shorter anyway, which is worth having: a shorter panel sits further inside your
        // field of view at the metre or so it is placed at.
        private const float CanvasHeight = 716f;

        /// <summary>Left margin, and the width every full-width row is inset to.</summary>
        private const float Margin = 16f;

        private const float RowWidth = CanvasWidth - Margin * 2f;

        /// <summary>
        /// Height of a main action button. Widened from 46 because a hand ray jitters more than
        /// a controller ray, and tall enough for a second line -- a slot that is greyed out
        /// carries the reason underneath its label.
        /// </summary>
        private const float ActionHeight = 54f;

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

            // The drag handler goes here rather than on the canvas root. Drag events bubble up
            // from whatever was hit, so on the root every button would become draggable -- and
            // with a jittery ray a press that creeps past the drag threshold turns into a drag
            // instead of a click. On the background it catches only the parts with nothing on
            // top of them, which gives the panel a title bar and the buttons their presses.
            background.gameObject.AddComponent<ConvaiRoomPanelDragger>();

            // 290 wide rather than the full 388, to leave the title bar's right end free.
            // Written by the panel now that the flow has more than one stage to be in; this is
            // only what it says before the first redraw.
            var title = MakeText(canvasRect, "Title", Margin, 12f, 290f, 28f,
                                 22, TextAnchor.MiddleLeft);
            title.text = "ROOM FLOW";
            title.color = TitleColor;

            // Up in the title bar, deliberately nowhere near the action stack. Those get
            // pressed constantly with a ray that jitters, and the cost of catching this one by
            // accident is a scan you spent ten minutes collecting. It also asks twice.
            var exit = MakeButton(canvasRect, "Exit Button", "EXIT", 314f, 8f, 90f, 40f);

            var exitImage = exit.targetGraphic as Image;
            if (exitImage != null) exitImage.color = ExitButton;

            // Left empty on purpose -- these are overwritten every redraw, and seeding them
            // with placeholder copy only invites someone to edit the placeholder and wonder
            // why it never shows up.
            var counts = MakeText(canvasRect, "Counts", Margin, 52f, RowWidth, 38f,
                                  30, TextAnchor.MiddleLeft);

            // Five room lines at most in the setup stages, and in the character stage two lines
            // plus a plan: a blank, a header, and the six-step window the panel draws at most.
            var status = MakeText(canvasRect, "Status", Margin, 94f, RowWidth, 240f,
                                  16, TextAnchor.UpperLeft);

            // Directly under the readout rather than down with the actions, because these three
            // act on the plan drawn immediately above them. A control sitting next to the thing
            // it changes needs no label explaining which thing that is. The panel hides the row
            // outright except in the character stage -- three permanently greyed buttons under
            // the readout through the whole of a scan are three controls to wonder about.
            //
            // Three across one row: 124 wide each with 8 between, filling the same 388 the
            // full-width buttons use. Shorter than the actions below because stepping a plan is
            // reversible and cheap -- a mis-press costs one press back.
            var planBack = MakeButton(canvasRect, "Plan Back Button", "< BACK",
                                      Margin, 340f, 124f, 48f);

            var planNext = MakeButton(canvasRect, "Plan Next Button", "NEXT >",
                                      148f, 340f, 124f, 48f);

            var planClear = MakeButton(canvasRect, "Plan Clear Button", "CLEAR",
                                       280f, 340f, 124f, 48f);

            // Five lines: a header, the bindings, what they bypass, the hand-tracking caveat
            // and the drag hint. Written once at startup, and it never grows.
            var controls = MakeText(canvasRect, "Controls", Margin, 394f, RowWidth, 88f,
                                    15, TextAnchor.UpperLeft);

            // The question line, immediately above the answers to it. That adjacency is doing
            // the work a dialog box would: the panel has no modal window, so what makes these
            // read as a question and its two answers rather than as two more actions is that
            // they are touching.
            var prompt = MakeText(canvasRect, "Prompt", Margin, 488f, RowWidth, 28f,
                                  18, TextAnchor.MiddleLeft);

            // The three action slots. What each says and does comes from the panel's stage --
            // see ConvaiRoomModePanel.LayOutActions -- so the labels here are only what they
            // read before the first redraw. Full width, one per row: every one of them can
            // replace what is in the room, and none is worth fat-fingering with a jittery hand
            // ray aimed at a half-width target.
            var slots = new Button[ConvaiRoomModePanel.SlotCount];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = MakeButton(canvasRect, $"Action Button {i}", "",
                                      Margin, 522f + i * (ActionHeight + 6f),
                                      RowWidth, ActionHeight);
            }

            BindPanelFields(panel, raycaster, title, counts, status, prompt, controls,
                            slots, exit, planBack, planNext, planClear);
            return root;
        }

        /// <summary>
        /// Assigns the panel's serialized references through SerializedObject rather than
        /// making the fields public just so this can reach them. Nothing outside the panel has
        /// any business setting these at runtime.
        /// </summary>
        private static void BindPanelFields(ConvaiRoomModePanel panel, OVRRaycaster raycaster,
                                            Text title, Text counts, Text status,
                                            Text prompt, Text controls,
                                            Button[] slots, Button exit,
                                            Button planBack, Button planNext, Button planClear)
        {
            var so = new SerializedObject(panel);

            Assign(so, "_raycaster", raycaster);
            Assign(so, "_titleText", title);
            Assign(so, "_countsText", counts);
            Assign(so, "_statusText", status);
            Assign(so, "_promptText", prompt);
            Assign(so, "_controlsText", controls);
            Assign(so, "_exitButton", exit);
            Assign(so, "_planBackButton", planBack);
            Assign(so, "_planNextButton", planNext);
            Assign(so, "_planClearButton", planClear);

            AssignArray(so, "_actionButtons", slots);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Fills a serialized array field, sizing it to what it is handed.
        ///
        /// The size is set rather than assumed. A prefab baked before the slot count changed
        /// keeps the old length, and a panel that finds the right field at the wrong length is
        /// half-wired in the one way its own validation would otherwise have to guess at.
        /// </summary>
        private static void AssignArray(SerializedObject so, string field, Object[] values)
        {
            var property = so.FindProperty(field);

            if (property == null || !property.isArray)
            {
                Debug.LogError($"[ScanPanelBaker] ConvaiRoomModePanel has no serialized array " +
                               $"'{field}'. The baker and the panel have drifted apart; the " +
                               $"prefab will come out half-wired.");
                return;
            }

            property.arraySize = values.Length;

            for (var i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
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
