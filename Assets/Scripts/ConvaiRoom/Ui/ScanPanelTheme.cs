using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Every colour and corner radius the scan panel draws itself with, in one asset.
    ///
    /// This exists because of how the panel is authored. The prefab is GENERATED --
    /// ScanPanelPrefabBaker overwrites it -- so a colour tweaked by hand on the prefab survives
    /// exactly until the next re-bake, and colours are the thing most likely to be tweaked
    /// repeatedly. An asset the panel points at is outside the prefab, so it survives; and one
    /// asset holding the whole palette is also the only way a restyle stays coherent, because
    /// the readout's greens and ambers have to move with the background or a light panel comes
    /// out with pale green text on it.
    ///
    /// The panel applies this at runtime, in Awake. That means the Scene view still shows
    /// whatever the prefab was baked with -- square corners especially, since the rounded
    /// sprites are generated rather than authored -- and Play mode or the headset shows the
    /// truth. Colour changes therefore need no re-bake at all; the baker only reads this so a
    /// freshly baked prefab is not wildly wrong to look at.
    ///
    /// Create one from Assets > Create > Convai Room > Scan Panel Theme, or let the baker make
    /// the default. With no theme assigned the panel falls back to an instance of this class,
    /// so the values below are also the panel's shipped look.
    /// </summary>
    [CreateAssetMenu(menuName = "Convai Room/Scan Panel Theme", fileName = "Scan Panel Theme")]
    public class ScanPanelTheme : ScriptableObject
    {
        [Header("Panel")]
        [Tooltip("The panel's own background. The alpha matters as much as the colour -- this " +
                 "is drawn over passthrough, and a fully opaque panel is a hole in the room.")]
        public Color panelBackground = new Color(0.05f, 0.07f, 0.10f, 0.85f);

        [Tooltip("Corner radius of the panel background, in canvas units. The panel is 420 " +
                 "units across and 0.42 m wide, so 1 unit is 1 mm.")]
        [Range(0f, 80f)] public float panelCornerRadius = 26f;

        [Header("Buttons")]
        public Color buttonFace = new Color(0.22f, 0.28f, 0.34f, 0.92f);

        public Color buttonLabel = Color.white;

        [Tooltip("Muted red. Reads as destructive without shouting over the readout.")]
        public Color exitFace = new Color(0.42f, 0.18f, 0.20f, 0.92f);

        [Tooltip("Corner radius of a button, in canvas units. A main action button is 54 units " +
                 "tall, so anything past 27 is a stadium and stops looking like a button.")]
        [Range(0f, 40f)] public float buttonCornerRadius = 16f;

        [Header("Text")]
        public Color title = new Color(1f, 0.85f, 0.3f);

        [Tooltip("The default colour of the readout and the controls block. Individual words " +
                 "inside them are recoloured from the palette below.")]
        public Color bodyText = Color.white;

        [Tooltip("The big headline number, when it is reporting something usable.")]
        public Color headlineActive = new Color(0.5f, 0.9f, 1f);

        [Tooltip("...and when it is not. A first run with nothing saved, a scan that has not " +
                 "settled on anything yet.")]
        public Color headlineIdle = new Color(0.75f, 0.75f, 0.78f);

        [Header("Readout palette")]
        [Tooltip("Something is ready, baked, connected, present.")]
        public Color good = new Color(0.498f, 0.851f, 0.498f);

        [Tooltip("Something works but is not what you wanted -- stale, unaligned, paused.")]
        public Color warn = new Color(1f, 0.769f, 0.302f);

        [Tooltip("Something is missing or has failed.")]
        public Color bad = new Color(1f, 0.502f, 0.502f);

        [Tooltip("Hints and asides. Deliberately quiet -- these are read once and then ignored.")]
        public Color muted = new Color(0.604f, 0.604f, 0.627f);

        [Tooltip("Quieter still. The '...' either end of a windowed plan.")]
        public Color dim = new Color(0.416f, 0.416f, 0.439f);

        [Tooltip("The question the panel is asking, and the CONTROLS heading.")]
        public Color accent = new Color(1f, 0.835f, 0.302f);

        [Tooltip("The plan heading, so a plan reads as its own thing inside the readout.")]
        public Color planHeading = new Color(0.498f, 0.851f, 0.851f);

        // The rich-text hex for each palette entry, built once. The readout is rebuilt as often
        // as once a second and quotes six of these every time; ToHtmlStringRGB allocates a
        // string per call, and none of these change between Inspector edits.
        private bool _hexBuilt;
        private string _goodHex, _warnHex, _badHex, _mutedHex, _dimHex, _accentHex, _planHex;

        public string GoodHex { get { BuildHex(); return _goodHex; } }
        public string WarnHex { get { BuildHex(); return _warnHex; } }
        public string BadHex { get { BuildHex(); return _badHex; } }
        public string MutedHex { get { BuildHex(); return _mutedHex; } }
        public string DimHex { get { BuildHex(); return _dimHex; } }
        public string AccentHex { get { BuildHex(); return _accentHex; } }
        public string PlanHex { get { BuildHex(); return _planHex; } }

        private void BuildHex()
        {
            if (_hexBuilt) return;

            _goodHex = ColorUtility.ToHtmlStringRGB(good);
            _warnHex = ColorUtility.ToHtmlStringRGB(warn);
            _badHex = ColorUtility.ToHtmlStringRGB(bad);
            _mutedHex = ColorUtility.ToHtmlStringRGB(muted);
            _dimHex = ColorUtility.ToHtmlStringRGB(dim);
            _accentHex = ColorUtility.ToHtmlStringRGB(accent);
            _planHex = ColorUtility.ToHtmlStringRGB(planHeading);

            _hexBuilt = true;
        }

        /// <summary>
        /// Throws the cached hex away so an Inspector edit shows up on the next redraw. Editor
        /// only, which is the only place these change.
        /// </summary>
        private void OnValidate() => _hexBuilt = false;
    }
}
