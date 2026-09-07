using UnityEngine;
using UnityEngine.Rendering;

namespace RoomScan
{
    /// <summary>
    /// A soft drift of dust along a <see cref="WireBox"/>'s edges, for the moments a box needs to
    /// be NOTICED rather than merely drawn.
    ///
    /// A colour change alone is easy to miss in passthrough: the box is thin, the room behind it
    /// is busy and lit however the room is lit, and a participant looking somewhere else when the
    /// colour flips has no second chance at it. Motion is what the eye catches at the edge of
    /// vision, so this adds some of it without adding a solid surface -- the box stays an
    /// outline, which is the whole reason nothing here draws filled cubes.
    ///
    /// Built in code rather than authored as a prefab. There is no particle prefab in this
    /// project to extend, and an asset would be a second place to keep in step with the box it
    /// decorates; this way a shimmer cannot outlive or mis-size the WireBox it belongs to.
    ///
    /// The grains are drawn from a generated atlas, NOT from an untextured material. Sprites
    /// /Default with no main texture draws a flat, hard-edged square, which reads as a pixel
    /// rather than as a mote of dust however small it is made -- softness has to come from the
    /// texture's own alpha falloff, and there is nothing else in the frame to supply it. Four
    /// grains rather than one, because a single sprite repeated a hundred times reads as a
    /// pattern; dust does not.
    ///
    /// The atlas is generated at runtime for the same reason the particle system is: an imported
    /// texture would carry import settings, a .meta, and a second place for this to go wrong,
    /// to save arithmetic that costs well under a millisecond once per run.
    ///
    /// The material is a COPY of <see cref="WireMaterial"/>'s, taken so that the shader is
    /// literally the same object: Sprites/Default is unlit, alpha-blended, writes no depth, and
    /// is in this project's Always Included Shaders, so it survives a player build, and a copy
    /// cannot drift from that guarantee the way a second Shader.Find could. It must be a copy
    /// and never the shared instance itself -- setting a main texture on that one would texture
    /// every wireframe line in the scene, since every WireBox LineRenderer draws with it.
    /// A particle system left on Unity's default material renders magenta on device and fine in
    /// the editor, which is the worst way to find out.
    /// </summary>
    [RequireComponent(typeof(WireBox))]
    [DisallowMultipleComponent]
    public class WireBoxShimmer : MonoBehaviour
    {
        private const string Tag = "[Shimmer]";

        // Tuned for "dust caught in the light", not "sparks". Still deliberately unhurried --
        // this competes for attention with the real object inside the box, and the object is
        // what the participant is being asked to look at.
        //
        // Every one of these is a range rather than a value wherever a range was affordable.
        // Uniform particles read as a mechanism; varied ones read as material, and the variance
        // costs nothing at this count.
        private const float BaseRate = 58f;        // particles per second at intensity 1
        private const float SizeMin = 0.016f;      // metres
        private const float SizeMax = 0.030f;
        private const float LifetimeMin = 1.1f;    // seconds
        private const float LifetimeMax = 2.0f;
        private const float SpeedMin = 0.02f;      // metres per second of drift
        private const float SpeedMax = 0.09f;
        private const float Alpha = 0.7f;

        /// <summary>
        /// Radians per second, both directions. Radians because the particle rotation API takes
        /// them while the Inspector shows degrees -- a number that looks right in the editor and
        /// spins 57x too fast is the failure this comment exists to prevent.
        /// </summary>
        private const float RotateSpeed = 0.6f;

        /// <summary>
        /// The air the dust is sitting in. <see cref="NoiseStrength"/> is metres of displacement,
        /// which is only true with damping OFF -- with it on, Unity rescales strength by
        /// frequency and the number stops meaning anything you can reason about from here.
        /// </summary>
        private const float NoiseStrength = 0.06f;
        private const float NoiseFrequency = 0.35f;
        private const float NoiseScroll = 0.22f;

        /// <summary>
        /// Rate is scaled by the box's edge length so a wardrobe and a mug shimmer at roughly the
        /// same density rather than the same absolute count -- at a fixed rate a big box looks
        /// sparse and a small one looks like it is on fire.
        /// </summary>
        private const float RateReference = 1.2f;   // metres; a mid-sized object's mean edge
        private const float RateMin = 0.35f;
        private const float RateMax = 3f;

        /// <summary>
        /// Grains across the generated atlas; the sheet holds this squared. Four is enough to
        /// break the repeat and keeps the sheet at 128px, which is nothing to upload once.
        /// </summary>
        private const int AtlasCols = 2;
        private const int GrainPixels = 64;

        /// <summary>
        /// How far across its tile a grain may reach, as a fraction. The rest is a transparent
        /// gutter, and it is not spare space: the sheet is mipmapped, and a grain drawn out to
        /// the tile edge bleeds into its neighbour at the small mips -- which is exactly the
        /// distance a box is usually seen from.
        /// </summary>
        private const float GrainGutter = 0.82f;

        private static Material _dust;
        private static Texture2D _dustAtlas;

        private ParticleSystem _ps;
        private ParticleSystemRenderer _renderer;
        private WireBox _box;

        /// <summary>Whether a shimmer is currently showing.</summary>
        public bool IsShowing => _ps != null && _ps.isEmitting;

        /// <summary>
        /// Finds the shimmer on a proxy, adding one if it has none.
        ///
        /// Returns null for anything that is not a WireBox, which is the same thing both callers
        /// already do with the colour highlight -- a proxy without a box is not a thing this
        /// pipeline knows how to mark.
        /// </summary>
        public static WireBoxShimmer AttachTo(GameObject proxy)
        {
            if (proxy == null || !proxy.TryGetComponent<WireBox>(out _)) return null;

            return proxy.TryGetComponent<WireBoxShimmer>(out var existing)
                ? existing
                : proxy.AddComponent<WireBoxShimmer>();
        }

        private void Awake()
        {
            _box = GetComponent<WireBox>();
            Build();
        }

        /// <summary>
        /// Starts the shimmer in the given colour.
        ///
        /// <paramref name="intensity"/> scales the emission rate only, not size or brightness:
        /// a persistent shimmer that follows the pointer around the room wants to be quieter
        /// than a five-second cue, and thinning it reads as quieter while keeping the two
        /// recognisably the same effect.
        /// </summary>
        public void Show(Color color, float intensity = 1f)
        {
            if (_ps == null) return;

            var main = _ps.main;

            // Alpha is forced rather than taken from the caller's colour. Both callers pass a
            // fully opaque highlight colour -- correct for a line, far too solid for a hundred
            // overlapping grains.
            main.startColor = new Color(color.r, color.g, color.b, Alpha);

            var shape = _ps.shape;
            shape.scale = _box != null ? _box.Size : Vector3.one;

            var emission = _ps.emission;
            emission.rateOverTime = BaseRate * Mathf.Max(0f, intensity) * DensityFor(shape.scale);

            // Cleared first so a re-show on a box that is already shimmering restarts cleanly
            // rather than layering a second burst over the tail of the first.
            _ps.Clear();
            _ps.Play();
        }

        /// <summary>
        /// Stops the shimmer and removes every grain already in the air, THIS FRAME.
        ///
        /// StopEmittingAndClear rather than a plain Stop, and that distinction is load-bearing
        /// for the trial cue. Particles outlive the emitter that made them, so a plain Stop
        /// leaves the target visibly marked for as long as the longest grain has left to live --
        /// up to <see cref="LifetimeMax"/>, two full seconds, past the instant the cue is
        /// recorded as having ended and well into the window where the participant is supposed
        /// to be answering from memory. A pointing trial whose target is still drifting is not
        /// measuring reference resolution; it is measuring whether somebody can point at the
        /// glowing thing.
        ///
        /// The longer, softer grains made this MORE important than it was, not less: the same
        /// mistake now buys twice the contamination it used to.
        /// </summary>
        public void Hide()
        {
            if (_ps == null) return;

            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _ps.Clear(true);
        }

        private void OnDisable() => Hide();

        /// <summary>
        /// Emission density for a box, so big and small objects read alike. Clamped at both ends:
        /// a room-sized false positive should not empty the particle budget, and a very small
        /// box still needs enough grains to register as motion at all.
        /// </summary>
        private static float DensityFor(Vector3 size)
        {
            var mean = (Mathf.Abs(size.x) + Mathf.Abs(size.y) + Mathf.Abs(size.z)) / 3f;
            if (mean <= 0f) return 1f;

            return Mathf.Clamp(mean / RateReference, RateMin, RateMax);
        }

        private void Build()
        {
            _ps = gameObject.AddComponent<ParticleSystem>();

            // Stopped before anything else touches it. A ParticleSystem plays on awake by
            // default, so without this every box in the room shimmers from the moment the scan
            // is replayed.
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(LifetimeMin, LifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(SpeedMin, SpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(SizeMin, SizeMax);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startColor = new Color(1f, 1f, 1f, Alpha);

            // Headroom over the worst case rather than a guess. The biggest box the scanner will
            // export is capped at ObjectScanRecorder.maxObjectSize (3 m), which lands on density
            // 2.5 and so about 225 alive at the longest lifetime -- a cap of 220 would have
            // silently thinned exactly the largest objects, which are the ones already hardest
            // to read as marked.
            main.maxParticles = 500;

            // Edges, not volume. The wireframe IS the object as far as this pipeline draws it,
            // so tracing the same twelve edges keeps the dust reading as part of the box rather
            // than as fog sitting inside it -- which over passthrough would obscure the very
            // thing the box is pointing at. The noise below pushes grains a few centimetres off
            // the edge, which softens the line without filling the middle.
            var shape = _ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.BoxEdge;
            shape.scale = Vector3.one;

            var emission = _ps.emission;
            emission.enabled = true;
            emission.rateOverTime = BaseRate;

            // Fades in and out rather than popping. A grain that appears and vanishes at full
            // alpha reads as flicker; the same grain faded at both ends reads as drift, which is
            // the difference between "noticeable" and "broken".
            var colorOverLifetime = _ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            // What makes it dust rather than a fountain. Straight-line drift at these speeds
            // reads as debris falling off the box; a slow curl reads as something suspended in
            // the air of the room, which is the whole idea.
            var noise = _ps.noise;
            noise.enabled = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            noise.damping = false;
            noise.strength = NoiseStrength;
            noise.frequency = NoiseFrequency;
            noise.scrollSpeed = NoiseScroll;

            // Slow tumble. Invisible on a radially symmetric grain, which is why the atlas has
            // asymmetric ones in it.
            var rotation = _ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-RotateSpeed, RotateSpeed);

            // One grain per particle, held for its whole life. frameOverTime is a constant zero
            // so the sheet never advances: this is a variety picker, not an animation, and a
            // grain that morphed into a different grain mid-drift would read as flicker.
            var sheet = _ps.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = AtlasCols;
            sheet.numTilesY = AtlasCols;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
            sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, AtlasCols * AtlasCols);

            _renderer = GetComponent<ParticleSystemRenderer>();
            if (_renderer != null)
            {
                _renderer.renderMode = ParticleSystemRenderMode.Billboard;
                _renderer.sharedMaterial = DustMaterial();
                _renderer.shadowCastingMode = ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _renderer.lightProbeUsage = LightProbeUsage.Off;
                _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                // Above the boxes, same as the rest of the scan's overlays. Left at the default
                // a shimmer sorts against the wireframes arbitrarily and flickers through them.
                _renderer.sortingOrder = 1;
            }
            else
            {
                Debug.LogWarning($"{Tag} No ParticleSystemRenderer on '{name}'. The shimmer will " +
                                 "run and draw nothing.");
            }
        }

        /// <summary>
        /// The one material every shimmer in the scene draws with, built once.
        ///
        /// Copied from <see cref="WireMaterial"/>'s rather than resolved through a second
        /// Shader.Find, so it carries the same shader instance the wireframes already proved is
        /// in Always Included Shaders. Never the shared instance itself -- see the class remark.
        /// </summary>
        private static Material DustMaterial()
        {
            if (_dust != null) return _dust;

            var basis = WireMaterial.Shared;
            if (basis == null)
            {
                // WireMaterial has already logged which shader it could not find; saying it
                // twice per box would bury it.
                return null;
            }

            _dustAtlas = BuildDustAtlas();

            _dust = new Material(basis)
            {
                name = "WireBoxShimmerDust",
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = _dustAtlas
            };

            return _dust;
        }

        /// <summary>
        /// Draws the grain sheet: white throughout, with all the shape in the alpha.
        ///
        /// White RGB everywhere is not laziness -- it is what makes the mipmaps safe. Only alpha
        /// varies, so a filtered texel can never pull a neighbour's colour into a grain's edge,
        /// and <see cref="Show"/> is free to tint the whole thing to the caller's cue colour.
        /// </summary>
        private static Texture2D BuildDustAtlas()
        {
            const int side = GrainPixels * AtlasCols;

            var tex = new Texture2D(side, side, TextureFormat.RGBA32, true)
            {
                name = "WireBoxShimmerDustAtlas",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[side * side];

            for (var tileY = 0; tileY < AtlasCols; tileY++)
            for (var tileX = 0; tileX < AtlasCols; tileX++)
            {
                var grain = Grains[tileY * AtlasCols + tileX];

                for (var y = 0; y < GrainPixels; y++)
                for (var x = 0; x < GrainPixels; x++)
                {
                    // -1..1 across the tile, so the radius is 0 at its centre and 1 at the
                    // middle of an edge. Dividing by the gutter pushes zero alpha inside the
                    // tile rather than exactly on its border; multiplying dy by the aspect
                    // squashes the grain without letting it reach any further across.
                    var dx = (x + 0.5f) / GrainPixels * 2f - 1f - grain.OffsetX;
                    var dy = ((y + 0.5f) / GrainPixels * 2f - 1f - grain.OffsetY) * grain.Aspect;
                    var r = Mathf.Sqrt(dx * dx + dy * dy) / GrainGutter;

                    var a = r <= grain.Core
                        ? 1f
                        : Mathf.Pow(Mathf.Clamp01(1f - (r - grain.Core) / (1f - grain.Core)),
                                    grain.Falloff);

                    pixels[(tileY * GrainPixels + y) * side + tileX * GrainPixels + x] =
                        new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            tex.SetPixels32(pixels);

            // Mips built, then the CPU copy dropped -- nothing reads this back, and a 128px
            // readable texture is memory held for the life of the app for no reason.
            tex.Apply(true, true);

            return tex;
        }

        /// <summary>
        /// One grain of the sheet.
        ///
        /// <see cref="Aspect"/> squashes the grain on one axis; 1 is round. It only ever
        /// squashes and never stretches, because a grain widened past the gutter would run off
        /// its tile and into its neighbour's. Elongation is what makes the set genuinely four
        /// shapes rather than one shape at four sizes -- size is already randomised per particle,
        /// so a sheet that varied only in size would have been the same grain four times -- and
        /// it is also what makes <see cref="RotateSpeed"/> do anything at all: a radially
        /// symmetric grain looks identical however far it has turned.
        /// </summary>
        private readonly struct Grain
        {
            public readonly float Core;      // fraction of the radius held at full alpha
            public readonly float Falloff;   // how sharply it fades beyond the core
            public readonly float Aspect;    // 1 is round; above that, squashed vertically
            public readonly float OffsetX;
            public readonly float OffsetY;

            public Grain(float core, float falloff, float aspect, float offsetX, float offsetY)
            {
                Core = core;
                Falloff = falloff;
                Aspect = aspect;
                OffsetX = offsetX;
                OffsetY = offsetY;
            }
        }

        /// <summary>
        /// The grains, in sheet order. Length must stay <see cref="AtlasCols"/> squared.
        ///
        /// They differ in how much solid core they hold before the falloff starts, how sharply
        /// it then falls, and how far from round they are -- which is what separates a mote you
        /// can see the edge of from a haze you can only see the middle of. The lopsided one is
        /// off-centre in its tile on purpose: rotation turns a grain about the quad's centre,
        /// so an off-centre grain tumbles rather than merely spinning in place.
        /// </summary>
        private static readonly Grain[] Grains =
        {
            new Grain(0.10f, 1.7f, 1.00f,  0f,     0f),      // a round, defined mote
            new Grain(0.02f, 3.2f, 1.60f,  0f,     0f),      // a soft oval puff
            new Grain(0.17f, 2.3f, 1.25f,  0.08f, -0.06f),   // lopsided, bright in the middle
            new Grain(0.00f, 5.0f, 2.20f,  0f,     0f)       // a faint wisp, barely there
        };

        private static Gradient FadeGradient()
        {
            var gradient = new Gradient();

            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.25f),
                    new GradientAlphaKey(1f, 0.65f),
                    new GradientAlphaKey(0f, 1f)
                });

            return gradient;
        }
    }
}
