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
    /// It builds TWO canvases: the main panel, and the details panel beside it that INFO shows
    /// and hides. What separates them is not importance, it is whether you read it on the way
    /// past or go looking for it.
    ///
    /// Colours and corner radii are NOT baked in. Every graphic is tagged with a
    /// <see cref="ScanPanelSkin"/> role and painted at runtime from the ScanPanelTheme asset
    /// this creates on first bake, so restyling is an asset edit and survives a re-bake -- which
    /// is the point, because re-baking OVERWRITES and used to take any hand-tuned colour with
    /// it. What is baked is the layout. Change that here; change the look on the theme.
    ///
    /// Re-baking still overwrites everything else you may have done to the prefab by hand. It
    /// asks first.
    /// </summary>
    public static class ScanPanelPrefabBaker
    {
        private const string PrefabDirectory = "Assets/Prefabs/ConvaiRoom";
        private const string PrefabPath = PrefabDirectory + "/Scan Panel.prefab";
        private const string ThemePath = PrefabDirectory + "/Scan Panel Theme.asset";

        // Authoring units. The canvas is laid out in these and then scaled to metres, so the
        // numbers below read like ordinary UI pixels rather than millimetres.
        private const float CanvasWidth = 420f;

        /// <summary>Left margin, and the width every full-width row is inset to.</summary>
        private const float Margin = 16f;

        private const float RowWidth = CanvasWidth - Margin * 2f;

        /// <summary>
        /// Height of a main action button. Widened from 46 because a hand ray jitters more than
        /// a controller ray, and tall enough for a second line -- a slot that is greyed out
        /// carries the reason underneath its label.
        /// </summary>
        private const float ActionHeight = 54f;

        private const float ActionGap = 6f;

        /// <summary>
        /// The listening light and the word beside it, between the question and the actions.
        ///
        /// Its own row rather than tucked into the title bar or the end of the headline, and
        /// that costs the panel 34 units of height. Both of the free-looking places would have
        /// put it behind text that grows: the title bar is full to 404 with INFO and EXIT, and
        /// the headline runs to most of the panel's width at 34 point -- "12 ready / 34 tracked"
        /// reaches the right margin on its own. A light that is sometimes underneath a number is
        /// worse than no light.
        /// </summary>
        private const float VoiceRowY = 150f;

        private const float VoiceRowHeight = 26f;

        /// <summary>
        /// Diameter of the light. Rounded into a circle at runtime from this -- see
        /// ConvaiRoomModePanel.RoundVoiceDot -- so it stays round at whatever size this is.
        /// </summary>
        private const float VoiceDotSize = 22f;

        /// <summary>Gap between the light and its word.</summary>
        private const float VoiceLabelGap = 10f;

        /// <summary>Top of the first action button, under the voice row.</summary>
        private const float FirstActionY = 190f;

        /// <summary>
        /// The main panel: the action stack plus a bottom margin, and nothing else below it.
        ///
        /// Half what it was. It carried the readout and the controller bindings at 716 units,
        /// and both of those moved to the details panel -- which is the point of the split, and
        /// also the nicest thing about it: the panel you look at all the time is now small
        /// enough to sit well inside your field of view at the metre it is placed at. The voice
        /// row put 34 of those units back; it is still comfortably inside the visor.
        /// </summary>
        private const float MainHeight =
            FirstActionY + 3f * ActionHeight + 2f * ActionGap + Margin;

        /// <summary>The details panel, sized to the readout plus the controls block.</summary>
        private const float DetailsHeight = 456f;

        /// <summary>Space between the two panels, in metres.</summary>
        private const float DetailsGap = 0.02f;

        /// <summary>Physical width of the panel in metres. Height follows the same scale.</summary>
        private const float PanelWidth = 0.42f;

        /// <summary>
        /// Draw order for the panel canvas: above the scan wireframes at 0, below the laser
        /// and its cursor dot at 200. See ConvaiRoomLaserCursor.sortingOrder.
        /// </summary>
        private const int PanelSortingOrder = 100;

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
                          $"recorder, rebuilder and scan controller assigned. Colours and " +
                          $"corners come from {ThemePath} at runtime, so the Scene view will " +
                          $"show square corners until you press Play.");
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

            var theme = LoadOrCreateTheme();

            var main = MakeCanvas(root.transform, "Panel Canvas", MainHeight, theme);
            var details = MakeCanvas(root.transform, "Details Canvas", DetailsHeight, theme);

            // Side by side rather than stacked, so both stay at eye level -- a details panel
            // hung underneath the main one ends up pointing at the floor at the height the
            // panel is placed. To the LEFT because the laser is on the right hand and the
            // buttons you actually press are on the main panel: the reference material goes on
            // the side your arm is not sweeping across.
            details.Root.localPosition = new Vector3(
                -(PanelWidth + DetailsGap),
                (MainHeight - DetailsHeight) * 0.5f * (PanelWidth / CanvasWidth),
                0f);

            BuildMain(main, out var title, out var counts, out var prompt,
                      out var voiceDot, out var voiceLabel,
                      out var slots, out var info, out var exit);

            BuildDetails(details, out var status, out var controls,
                         out var planBack, out var planNext, out var planClear);

            BindPanelFields(panel, theme, main.Raycaster, title, counts, prompt,
                            voiceDot, voiceLabel, slots,
                            info, exit, details.Root.gameObject, details.Raycaster,
                            status, controls, planBack, planNext, planClear);
            return root;
        }

        /// <summary>One of the two panels: its canvas, its raycaster and its backing plate.</summary>
        private readonly struct Surface
        {
            public readonly RectTransform Root;
            public readonly OVRRaycaster Raycaster;

            public Surface(RectTransform root, OVRRaycaster raycaster)
            {
                Root = root;
                Raycaster = raycaster;
            }
        }

        /// <summary>
        /// A world-space canvas with a backing plate and a drag handle, sized in authoring units
        /// and scaled to metres.
        /// </summary>
        private static Surface MakeCanvas(Transform parent, string name, float height,
                                          ScanPanelTheme theme)
        {
            var canvasGo = new GameObject(name, typeof(RectTransform));
            canvasGo.transform.SetParent(parent, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            // Above the scan wireframes, which draw at 0. Both this canvas and those boxes are
            // alpha-blended and write no depth, so whichever happens to be nearer the headset
            // wins on distance alone -- which meant a box between you and the panel painted
            // straight over the readout. Sorting order decides it explicitly instead. The
            // laser and its cursor dot sit above both, at 200.
            canvas.sortingOrder = PanelSortingOrder;

            var canvasRect = (RectTransform)canvasGo.transform;

            // Spelled out rather than left to the defaults, because there are two canvases now
            // and one of them is offset from the root. A centred pivot is what makes that offset
            // mean "this panel's middle sits here" -- which is what the side-by-side arithmetic
            // below assumes, and what a default this code never used to depend on would decide
            // silently.
            canvasRect.anchorMin = new Vector2(0.5f, 0.5f);
            canvasRect.anchorMax = new Vector2(0.5f, 0.5f);
            canvasRect.pivot = new Vector2(0.5f, 0.5f);

            canvasRect.sizeDelta = new Vector2(CanvasWidth, height);
            canvasRect.localScale = Vector3.one * (PanelWidth / CanvasWidth);

            // OVRRaycaster rather than the stock GraphicRaycaster: the stock one only
            // understands a screen-space mouse and cannot be driven by a tracked ray.
            var raycaster = canvasGo.AddComponent<OVRRaycaster>();

            var background = MakeRect(canvasRect, "Background", 0f, 0f, CanvasWidth, height)
                .gameObject.AddComponent<Image>();

            // Coloured and rounded again at runtime from the theme -- the sprite that rounds it
            // is generated, so it cannot be stored in the prefab. This is only so the Scene view
            // shows something close to the truth.
            background.color = theme.panelBackground;
            Tag(background, ScanPanelSkin.Role.PanelBackground);

            // The drag handler goes here rather than on the canvas root. Drag events bubble up
            // from whatever was hit, so on the root every button would become draggable -- and
            // with a jittery ray a press that creeps past the drag threshold turns into a drag
            // instead of a click. On the background it catches only the parts with nothing on
            // top of them, which gives each panel a title bar and the buttons their presses.
            // Both panels get one, and both move the shared root.
            background.gameObject.AddComponent<ConvaiRoomPanelDragger>();

            return new Surface(canvasRect, raycaster);
        }

        /// <summary>
        /// The main panel: where you are, one number, the question, and the buttons.
        ///
        /// Everything that used to sit under these -- the readout and the controller bindings --
        /// is on the details panel now, which is why this one comes out barely half the height
        /// it was. That is the whole change: what is left here is the flow, and the flow does
        /// not need reading.
        /// </summary>
        private static void BuildMain(Surface surface, out Text title, out Text counts,
                                      out Text prompt, out Image voiceDot, out Text voiceLabel,
                                      out Button[] slots, out Button info, out Button exit)
        {
            // 180 wide rather than the full 388, to leave the title bar's right end for the two
            // buttons. Written by the panel now that the flow has more than one stage to be in;
            // this is only what it says before the first redraw.
            title = MakeText(surface.Root, "Title", Margin, 12f, 180f, 28f,
                             22, TextAnchor.MiddleLeft);
            title.text = "ROOM FLOW";
            Tag(title, ScanPanelSkin.Role.Title);

            // Shows and hides the details panel. In the title bar because it is about the panel
            // rather than about the room -- it belongs with EXIT, not in the action stack where
            // it would take a slot from the flow.
            info = MakeButton(surface.Root, "Info Button", "INFO", 204f, 8f, 100f, 40f, 16);

            // Deliberately nowhere near the action stack. Those get pressed constantly with a
            // ray that jitters, and the cost of catching this one by accident is a scan you
            // spent ten minutes collecting. It also asks twice.
            exit = MakeButton(surface.Root, "Exit Button", "EXIT", 314f, 8f, 90f, 40f, 16);
            Tag(exit.targetGraphic, ScanPanelSkin.Role.ExitFace);

            // Left empty on purpose -- these are overwritten every redraw, and seeding them
            // with placeholder copy only invites someone to edit the placeholder and wonder
            // why it never shows up. Bigger than it was: with the readout gone this is the only
            // number on the panel, and it is the one you read without focusing on the panel.
            counts = MakeText(surface.Root, "Counts", Margin, 58f, RowWidth, 44f,
                              34, TextAnchor.MiddleLeft);

            // The question line, immediately above the answers to it. That adjacency is doing
            // the work a dialog box would: the panel has no modal window, so what makes these
            // read as a question and its two answers rather than as two more actions is that
            // they are touching.
            prompt = MakeText(surface.Root, "Prompt", Margin, 112f, RowWidth, 30f,
                              19, TextAnchor.MiddleLeft);
            Tag(prompt, ScanPanelSkin.Role.BodyText);

            // Whether she can hear you: green light, red light, and the reason beside it. The
            // panel shows and hides the pair -- there is nothing to report until a session is up
            // -- and paints both every redraw, which is why neither is tagged with a skin role.
            // A role paints one colour on in Awake and leaves it there, and these two ARE the
            // readout. See ConvaiRoomModePanel.RedrawVoice.
            voiceDot = MakeRect(surface.Root, "Voice Dot", Margin,
                                VoiceRowY + (VoiceRowHeight - VoiceDotSize) * 0.5f,
                                VoiceDotSize, VoiceDotSize)
                .gameObject.AddComponent<Image>();

            // The light is decoration in the way a label is: the panel behind it should still
            // take the click, and a 22-unit target that swallows a jittery ray on its way to a
            // drag is a target nobody aimed at.
            voiceDot.raycastTarget = false;

            // Rounded into a circle by the panel at runtime, from the same generated sprite the
            // buttons get their corners from. It is square here, which is what the Scene view
            // shows -- the same deal as every other radius on this panel.
            voiceDot.color = Color.white;

            voiceLabel = MakeText(surface.Root, "Voice Label",
                                  Margin + VoiceDotSize + VoiceLabelGap, VoiceRowY,
                                  RowWidth - VoiceDotSize - VoiceLabelGap, VoiceRowHeight,
                                  17, TextAnchor.MiddleLeft);

            // The three action slots. What each says and does comes from the panel's stage --
            // see ConvaiRoomModePanel.LayOutActions -- so the labels here are only what they
            // read before the first redraw. Full width, one per row: every one of them can
            // replace what is in the room, and none is worth fat-fingering with a jittery hand
            // ray aimed at a half-width target.
            slots = new Button[ConvaiRoomModePanel.SlotCount];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = MakeButton(surface.Root, $"Action Button {i}", "",
                                      Margin, FirstActionY + i * (ActionHeight + ActionGap),
                                      RowWidth, ActionHeight);
            }
        }

        /// <summary>
        /// The details panel: everything you would go looking for rather than read on the way
        /// past.
        /// </summary>
        private static void BuildDetails(Surface surface, out Text status, out Text controls,
                                         out Button planBack, out Button planNext,
                                         out Button planClear)
        {
            var heading = MakeText(surface.Root, "Details Title", Margin, 12f, RowWidth, 24f,
                                   18, TextAnchor.MiddleLeft);
            heading.text = "ROOM DETAILS";
            Tag(heading, ScanPanelSkin.Role.Title);

            // Five room lines at most in the setup stages, and in the character stage two lines
            // plus a plan: a blank, a header, and the six-step window the panel draws at most.
            status = MakeText(surface.Root, "Status", Margin, 46f, RowWidth, 240f,
                              16, TextAnchor.UpperLeft);
            Tag(status, ScanPanelSkin.Role.BodyText);

            // Directly under the readout, because these three act on the plan drawn immediately
            // above them. A control sitting next to the thing it changes needs no label
            // explaining which thing that is. The panel hides the row outright except in the
            // character stage -- three permanently greyed buttons through the whole of a scan
            // are three controls to wonder about.
            //
            // Three across one row: 124 wide each with 8 between, filling the same 388 the
            // full-width buttons use. Shorter than the main actions because stepping a plan is
            // reversible and cheap -- a mis-press costs one press back.
            planBack = MakeButton(surface.Root, "Plan Back Button", "< BACK",
                                  Margin, 296f, 124f, 48f);

            planNext = MakeButton(surface.Root, "Plan Next Button", "NEXT >",
                                  148f, 296f, 124f, 48f);

            planClear = MakeButton(surface.Root, "Plan Clear Button", "CLEAR",
                                   280f, 296f, 124f, 48f);

            // Five lines: a header, the bindings, what they bypass, the hand-tracking caveat
            // and the drag hint. Written once at startup, and it never grows.
            controls = MakeText(surface.Root, "Controls", Margin, 352f, RowWidth, 88f,
                                15, TextAnchor.UpperLeft);
            Tag(controls, ScanPanelSkin.Role.BodyText);
        }

        /// <summary>
        /// Marks a graphic so the panel can theme it at runtime without holding a reference to
        /// it. See <see cref="ScanPanelSkin"/>.
        /// </summary>
        private static void Tag(Graphic graphic, ScanPanelSkin.Role role)
        {
            if (graphic == null) return;

            // Re-tags rather than adds. MakeButton marks every face as a plain button and the
            // exit button is then re-marked by its caller; a second component on the same
            // object would leave both roles applied, in whichever order the panel happened to
            // walk them, and the EXIT button would come out its own colour only by luck.
            var skin = graphic.GetComponent<ScanPanelSkin>();
            if (skin == null) skin = graphic.gameObject.AddComponent<ScanPanelSkin>();

            skin.role = role;
        }

        /// <summary>
        /// The theme asset, created on first bake.
        ///
        /// Made here rather than left to the user because the panel needs one to look like
        /// anything, and an asset that has to be created by hand before the thing works is a
        /// step nobody knows about until the panel comes out grey. A CreateInstance fallback is
        /// returned when the write fails, so the bake still produces a correct prefab.
        /// </summary>
        private static ScanPanelTheme LoadOrCreateTheme()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ScanPanelTheme>(ThemePath);
            if (existing != null) return existing;

            var theme = ScriptableObject.CreateInstance<ScanPanelTheme>();

            var directory = Path.GetDirectoryName(ThemePath);
            if (string.IsNullOrEmpty(directory)) return theme;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            AssetDatabase.CreateAsset(theme, ThemePath);
            Debug.Log($"[ScanPanelBaker] Created {ThemePath}. Every colour and corner radius " +
                      $"the panel draws itself with lives on it, and it is read at runtime -- " +
                      $"edit it and press Play, no re-bake needed.");

            return theme;
        }

        /// <summary>
        /// Assigns the panel's serialized references through SerializedObject rather than
        /// making the fields public just so this can reach them. Nothing outside the panel has
        /// any business setting these at runtime.
        /// </summary>
        private static void BindPanelFields(ConvaiRoomModePanel panel, ScanPanelTheme theme,
                                            OVRRaycaster raycaster, Text title, Text counts,
                                            Text prompt, Image voiceDot, Text voiceLabel,
                                            Button[] slots, Button info, Button exit,
                                            GameObject detailsRoot, OVRRaycaster detailsRaycaster,
                                            Text status, Text controls,
                                            Button planBack, Button planNext, Button planClear)
        {
            var so = new SerializedObject(panel);

            // Assigned here rather than left for someone to drag in, and only the asset gets
            // assigned -- a theme created with CreateInstance because the write failed is not
            // one the prefab can hold a reference to.
            if (AssetDatabase.Contains(theme)) Assign(so, "_theme", theme);

            Assign(so, "_raycaster", raycaster);
            Assign(so, "_titleText", title);
            Assign(so, "_countsText", counts);
            Assign(so, "_promptText", prompt);
            Assign(so, "_voiceDot", voiceDot);
            Assign(so, "_voiceLabel", voiceLabel);
            Assign(so, "_infoButton", info);
            Assign(so, "_exitButton", exit);

            Assign(so, "_detailsRoot", detailsRoot);
            Assign(so, "_detailsRaycaster", detailsRaycaster);
            Assign(so, "_statusText", status);
            Assign(so, "_controlsText", controls);
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
                                         float x, float y, float width, float height,
                                         int fontSize = 20)
        {
            var rect = MakeRect(parent, name, x, y, width, height);

            var image = rect.gameObject.AddComponent<Image>();

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = ButtonColors();

            // Tagged rather than coloured. Every face and every label is painted from the theme
            // at runtime -- including the rounded corners, which are a generated sprite the
            // prefab cannot hold -- so setting a colour here would only be overwritten a moment
            // later. The exit button is re-tagged by its caller.
            Tag(image, ScanPanelSkin.Role.ButtonFace);

            // onClick is deliberately left empty. The panel binds its own handlers in Awake,
            // so a method rename is a compile error rather than a button that silently stops
            // working on a headset.
            var text = MakeText(rect, "label", 0f, 0f, width, height, fontSize,
                                TextAnchor.MiddleCenter);

            text.text = label;
            Tag(text, ScanPanelSkin.Role.ButtonLabel);
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
