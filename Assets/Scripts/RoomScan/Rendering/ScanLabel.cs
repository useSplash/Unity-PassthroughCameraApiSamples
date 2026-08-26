using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// The floating caption over a scanned box: what the thing is, and how well it was seen.
    ///
    /// This used to be a static factory handing back a bare TextMesh, with each caller then
    /// doing its own counter-scaling, its own vertical offset and its own string building --
    /// at different character sizes, so a live box and the replay of the same box were
    /// captioned at noticeably different sizes. It is a component now, so all of that lives in
    /// one place and every caption in the room reads the same.
    ///
    /// Three rules keep a cluttered room legible, and all three are about one problem: forty
    /// captions in a small room overlap into a wall of text you cannot read any of.
    ///
    ///   - The caption is sized in SCREEN terms rather than world terms between
    ///     <see cref="nearestScaled"/> and <see cref="furthestScaled"/>, so the box across the
    ///     room is as readable as the one at your feet without the one at your feet being
    ///     enormous. Past the far end it goes back to shrinking with distance like ordinary
    ///     world text, which is what lets the far corner of a room recede instead of crowding
    ///     the middle of your view.
    ///   - Past <see cref="detailWithin"/> the numbers are dropped and only the category is
    ///     drawn. The numbers are for judging one object you are looking at; the category is
    ///     what you read at a glance across a room.
    ///   - Past <see cref="hideBeyond"/> the caption goes entirely. The wireframe stays -- a
    ///     box on its own still says something is there, and it is the text that collides.
    ///
    /// The caption hangs off an anchor child of the box rather than off the box itself. A
    /// replayed box built from a prefab is scaled BY the object's size, and a caption parented
    /// straight to that comes out stretched; the anchor undoes the box's scale in the box's own
    /// axes, BEFORE the caption billboards away from them, which a counter-scale on the
    /// billboarded caption itself cannot do. A WireBox keeps its scale at 1, where the anchor
    /// is an identity transform and costs nothing.
    ///
    /// A TextMesh added via AddComponent has no font and no renderer material, so it draws
    /// nothing -- the font has to be pulled from the built-in resources and its material pushed
    /// onto the MeshRenderer by hand. Unity 2022 renamed that resource from Arial.ttf to
    /// LegacyRuntime.ttf, so both names are tried.
    /// </summary>
    public class ScanLabel : MonoBehaviour
    {
        /// <summary>
        /// Height of one line of caption, in metres, at <see cref="referenceDistance"/>.
        ///
        /// Down from the 0.02 the live boxes used and the 0.03 the replayed ones did. Those
        /// worked out at four to five degrees of view per line, which is enormous for text --
        /// two lines of it stood taller than most of the furniture being labelled, and any two
        /// objects near each other had captions that overlapped before you could read either.
        /// This is closer to two degrees, which on a Quest 3 is still around thirty pixels tall.
        /// </summary>
        public const float DefaultSize = 0.008f;

        /// <summary>
        /// Rasterisation size. Not the on-screen size -- <see cref="DefaultSize"/> is -- this
        /// only decides how much detail the font atlas holds, so it stays high while the
        /// caption gets smaller.
        /// </summary>
        private const int FontSize = 96;

        /// <summary>The numbers line, as a fraction of the category line.</summary>
        private const float SecondLineScale = 0.62f;

        /// <summary>How far the numbers are faded back from the category's colour.</summary>
        private const float SecondLineAlpha = 0.6f;

        [Tooltip("Distance the caption is sized for. At exactly this range it is drawn at its " +
                 "authored size, and between the two clamps below it holds that apparent size.")]
        public float referenceDistance = 2f;

        [Tooltip("Closer than this the caption stops growing. Without a near clamp, leaning in " +
                 "to look at one box fills your view with its caption.")]
        public float nearestScaled = 1f;

        [Tooltip("Past this the caption stops being held at a constant apparent size and " +
                 "shrinks with distance like ordinary world text, so the far side of the room " +
                 "recedes rather than competing with what is in front of you.")]
        public float furthestScaled = 4f;

        [Tooltip("Past this only the category is drawn -- the confidence and observation count " +
                 "are dropped. They are for judging one object you are looking at.")]
        public float detailWithin = 2.5f;

        [Tooltip("Past this the caption is not drawn at all. The wireframe box stays.")]
        public float hideBeyond = 7f;

        /// <summary>
        /// The headset, resolved at most once a frame however many captions are in the room.
        ///
        /// Camera.main is a tagged search, and a room with forty boxes in it would otherwise run
        /// forty of them every frame.
        /// </summary>
        private static Transform _head;

        private static int _headFrame = -1;

        private TextMesh _text;
        private MeshRenderer _renderer;

        /// <summary>The unscaled child of the box that the caption hangs from.</summary>
        private Transform _anchor;

        private string _category = "";
        private string _numbers = "";
        private string _dimHex = "ffffffff";
        private int _smallFont = 60;

        // What Set was last handed, so a redraw on an unchanged cluster builds no strings.
        private string _lastCategory;
        private float _lastConfidence = -1f;
        private int _lastObservations = -1;
        private Color _lastTint;

        private bool _detailed = true;

        /// <summary>
        /// Builds a caption above <paramref name="box"/> and returns it ready to be told what
        /// it says. Nothing is drawn until <see cref="Set"/> is called.
        /// </summary>
        public static ScanLabel Attach(Transform box, float size = DefaultSize)
        {
            var anchor = new GameObject("label").transform;
            anchor.SetParent(box, false);

            var go = new GameObject("caption");
            go.transform.SetParent(anchor, false);

            var text = go.AddComponent<TextMesh>();
            text.characterSize = size;
            text.fontSize = FontSize;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.richText = true;
            text.color = Color.white;

            var font = BuiltinFont();
            if (font != null)
            {
                text.font = font;
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }

            // Last, so Awake below finds a TextMesh that is already configured.
            var label = go.AddComponent<ScanLabel>();
            label._anchor = anchor;
            return label;
        }

        private void Awake()
        {
            _text = GetComponent<TextMesh>();
            _renderer = GetComponent<MeshRenderer>();

            if (_anchor == null) _anchor = transform.parent;
            _smallFont = Mathf.Max(1, Mathf.RoundToInt(_text.fontSize * SecondLineScale));
        }

        /// <summary>
        /// Hangs the caption above a box of the given height.
        ///
        /// Safe to call every refresh -- the live boxes change size as a cluster grows, and the
        /// caption has to ride up with them.
        /// </summary>
        public void Place(float boxHeight, float gap)
        {
            if (_anchor == null) return;

            var box = _anchor.parent;
            var scale = box != null ? box.localScale : Vector3.one;

            var inverse = new Vector3(1f / NonZero(scale.x),
                                      1f / NonZero(scale.y),
                                      1f / NonZero(scale.z));

            _anchor.localScale = inverse;
            _anchor.localPosition = new Vector3(0f, (boxHeight * 0.5f + gap) * inverse.y, 0f);
        }

        /// <summary>
        /// What the caption says. Ignores a call that would redraw the same thing, which is most
        /// of them -- the live visualiser refreshes every cluster several times a second and
        /// only a handful of them have changed.
        /// </summary>
        public void Set(string category, float confidence, int observations, Color tint)
        {
            if (category == _lastCategory && Mathf.Approximately(confidence, _lastConfidence)
                                          && observations == _lastObservations && tint == _lastTint)
                return;

            _lastCategory = category;
            _lastConfidence = confidence;
            _lastObservations = observations;
            _lastTint = tint;

            _category = Prettify(category);
            _numbers = $"{confidence:P0} · {observations}x";

            // Opaque whatever the box is. A faint pending box is helpful; faint text is not.
            var solid = new Color(tint.r, tint.g, tint.b, 1f);
            _text.color = solid;

            _dimHex = ColorUtility.ToHtmlStringRGBA(
                new Color(solid.r, solid.g, solid.b, SecondLineAlpha));

            Write();
        }

        private void LateUpdate()
        {
            var head = Head();
            if (head == null) return;

            var away = transform.position - head.position;
            var distance = away.magnitude;

            var visible = distance <= hideBeyond;
            if (_renderer.enabled != visible) _renderer.enabled = visible;
            if (!visible) return;

            // Turned to face AWAY from the head rather than toward it: the text draws on its
            // +Z face, so pointing +Z down the line of sight is what puts the readable side
            // toward you.
            if (away.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(away, Vector3.up);

            // Uniform, and it has to be: the caption has just been rotated out of its parent's
            // axes, so a per-axis correction here would shear the text. The parent's own scale
            // is undone by the anchor, which is not billboarded.
            transform.localScale = Vector3.one *
                (Mathf.Clamp(distance, nearestScaled, furthestScaled) / NonZero(referenceDistance));

            var detailed = distance <= detailWithin;
            if (detailed == _detailed) return;

            _detailed = detailed;
            Write();
        }

        private void Write()
        {
            _text.text = _detailed
                ? $"{_category}\n<size={_smallFont}><color=#{_dimHex}>{_numbers}</color></size>"
                : _category;
        }

        /// <summary>
        /// The category as it should be read rather than as it is stored.
        ///
        /// Display only -- <c>ScannedObject.label</c> keeps the raw class name, because that is
        /// what the room context names objects from and what the task vocabulary matches
        /// against. This only decides what floats over the box.
        ///
        /// The model's classes are already ordinary spaced nouns ("dining table", "potted
        /// plant"), so there is nothing to split apart; they are simply all lowercase, and a
        /// lowercase word floating in a room reads like a variable name.
        /// </summary>
        private static string Prettify(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return "object";

            var trimmed = category.Trim();
            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
        }

        private static Transform Head()
        {
            if (_headFrame == Time.frameCount) return _head;
            _headFrame = Time.frameCount;

            if (_head == null)
            {
                var camera = Camera.main;
                _head = camera != null ? camera.transform : null;
            }

            return _head;
        }

        private static float NonZero(float value)
            => Mathf.Abs(value) < 1e-3f ? 1e-3f : value;

        private static Font _font;

        internal static Font BuiltinFont()
        {
            if (_font != null) return _font;

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            if (_font == null)
                Debug.LogWarning("[ScanLabel] No built-in font available; labels will be invisible.");

            return _font;
        }
    }
}
