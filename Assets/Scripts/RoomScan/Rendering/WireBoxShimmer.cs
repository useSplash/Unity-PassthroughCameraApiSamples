using UnityEngine;
using UnityEngine.Rendering;

namespace RoomScan
{
    /// <summary>
    /// A soft shimmer along a <see cref="WireBox"/>'s edges, for the moments a box needs to be
    /// NOTICED rather than merely drawn.
    ///
    /// A colour change alone is easy to miss in passthrough: the box is thin, the room behind it
    /// is busy and lit however the room is lit, and a participant looking somewhere else when the
    /// colour flips has no second chance at it. Motion is what the eye catches at the edge of
    /// vision, so this adds a little of it without adding brightness -- the box stays an outline,
    /// which is the whole reason nothing here draws solid cubes.
    ///
    /// Built in code rather than authored as a prefab. There is no particle prefab in this
    /// project to extend, and an asset would be a second place to keep in step with the box it
    /// decorates; this way a shimmer cannot outlive or mis-size the WireBox it belongs to.
    ///
    /// The material comes from <see cref="WireMaterial"/> for the reason given there:
    /// Sprites/Default is unlit, alpha-blended, writes no depth, and is in this project's Always
    /// Included Shaders, so it survives a player build. A particle system left on the default
    /// material renders magenta on device and fine in the editor, which is the worst way to
    /// find out.
    /// </summary>
    [RequireComponent(typeof(WireBox))]
    [DisallowMultipleComponent]
    public class WireBoxShimmer : MonoBehaviour
    {
        private const string Tag = "[Shimmer]";

        // Tuned for "present, not distracting" over passthrough. Deliberately small and slow:
        // this competes for attention with the real object inside the box, and the object is
        // what the participant is being asked to look at.
        private const float BaseRate = 26f;      // particles per second at intensity 1
        private const float Size = 0.013f;       // metres
        private const float Lifetime = 0.95f;    // seconds
        private const float Speed = 0.035f;      // metres per second of drift
        private const float Alpha = 0.55f;

        /// <summary>
        /// Rate is scaled by the box's edge length so a wardrobe and a mug shimmer at roughly the
        /// same density rather than the same absolute count -- at a fixed rate a big box looks
        /// sparse and a small one looks like it is on fire.
        /// </summary>
        private const float RateReference = 1.2f;   // metres; a mid-sized object's mean edge
        private const float RateMin = 0.35f;
        private const float RateMax = 3f;

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
        /// than a two-second cue, and thinning it reads as quieter while keeping the two
        /// recognisably the same effect.
        /// </summary>
        public void Show(Color color, float intensity = 1f)
        {
            if (_ps == null) return;

            var main = _ps.main;

            // Alpha is forced rather than taken from the caller's colour. Both callers pass a
            // fully opaque highlight colour -- correct for a line, far too solid for a hundred
            // overlapping sprites.
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
        /// Stops the shimmer and removes every particle already in the air, THIS FRAME.
        ///
        /// StopEmittingAndClear rather than a plain Stop, and that distinction is load-bearing
        /// for the trial cue. Particles outlive the emitter that made them, so a plain Stop
        /// leaves the target visibly marked for another second -- past the instant the cue is
        /// recorded as having ended, and into the window where the participant is supposed to be
        /// answering from memory. A pointing trial whose target is still sparkling is not
        /// measuring reference resolution; it is measuring whether somebody can point at the
        /// glowing thing.
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
        /// box still needs enough particles to register as motion at all.
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
            main.startLifetime = Lifetime;
            main.startSpeed = Speed;
            main.startSize = Size;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 220;
            main.startColor = new Color(1f, 1f, 1f, Alpha);

            // Edges, not volume. The wireframe IS the object as far as this pipeline draws it,
            // so tracing the same twelve edges keeps the shimmer reading as part of the box
            // rather than as fog sitting inside it -- which over passthrough would obscure the
            // very thing the box is pointing at.
            var shape = _ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.BoxEdge;
            shape.scale = Vector3.one;

            var emission = _ps.emission;
            emission.enabled = true;
            emission.rateOverTime = BaseRate;

            // Fades in and out rather than popping. A particle that appears and vanishes at full
            // alpha reads as flicker; the same particle faded at both ends reads as a shimmer,
            // which is the difference between "noticeable" and "broken".
            var colorOverLifetime = _ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            _renderer = GetComponent<ParticleSystemRenderer>();
            if (_renderer != null)
            {
                _renderer.renderMode = ParticleSystemRenderMode.Billboard;
                _renderer.sharedMaterial = WireMaterial.Shared;
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
