using System;
using Convai.Runtime.Components;
using Convai.Runtime.Facades;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Holds a subscription to <c>ConvaiManager.Events</c> open across the manager coming up,
    /// going away, and being replaced.
    ///
    /// WHY THIS IS NOT INLINE IN ITS CALLERS. Two things in the study listen to the SDK -- the
    /// request counter and the speech watch -- and the awkward part is identical for both and
    /// is not the subscribing. It is that <c>ConvaiManager.Events</c> THROWS while
    /// initialisation is incomplete, that there is no ready event to hang a subscription on, and
    /// that a manager replaced across a scene load takes its facade with it and leaves a
    /// listener holding a reference to a conversation nobody is having. Two copies of that would
    /// drift the first time one of them was fixed.
    ///
    /// Polled rather than evented, for the reason above: <c>IsInitialized</c> is the documented
    /// gate and nothing announces it. The attempt is rate-limited so a scene with no Convai in
    /// it at all -- an editor harness, a scan-only build -- does not do this work every frame
    /// forever.
    ///
    /// Subscribers wire their own handlers in <see cref="Bound"/> and take them off again in
    /// <see cref="Unbinding"/>, which is raised while the facade is still live so the
    /// unsubscribe actually lands.
    /// </summary>
    public class ConvaiEventBinder
    {
        private const string Tag = "[ConvaiBind]";

        /// <summary>Seconds between attempts while nothing is bound.</summary>
        private const float RetryInterval = 1f;

        public bool verboseLogging;

        /// <summary>Raised once per successful bind, with the facade to subscribe to.</summary>
        public event Action<ConvaiEvents> Bound;

        /// <summary>
        /// Raised before a facade is let go, while it is still live. Unsubscribe here -- doing
        /// it after the reference is dropped leaves handlers attached to a facade that may
        /// outlive this, and a second bind would then deliver every event twice.
        /// </summary>
        public event Action<ConvaiEvents> Unbinding;

        /// <summary>Whether a facade is currently attached.</summary>
        public bool IsBound => Events != null;

        /// <summary>The bound facade, or null. Exposed so a subscriber can assert against it.</summary>
        public ConvaiEvents Events { get; private set; }

        private ConvaiManager _manager;
        private float _nextAttempt;

        /// <summary>
        /// Binds if it can. Call every frame; it is a no-op on all but the frames that matter.
        /// </summary>
        public void Tick()
        {
            var manager = ConvaiManager.ActiveManager;

            // Unity's null check, deliberately: a manager destroyed between scenes compares
            // equal to null while the C# reference is still there, and treating that as live is
            // how a listener ends up bound to nothing for the rest of the session.
            if (Events != null && (manager == null || manager != _manager)) Release();

            if (Events != null || manager == null) return;
            if (Time.unscaledTime < _nextAttempt) return;

            _nextAttempt = Time.unscaledTime + RetryInterval;

            if (!manager.IsInitialized) return;

            ConvaiEvents events;
            try
            {
                events = manager.Events;
            }
            catch (InvalidOperationException ex)
            {
                // IsInitialized said yes and the getter disagreed, which can only happen if
                // teardown began between the two. Not worth a warning every second.
                if (verboseLogging) Debug.Log($"{Tag} Events not ready yet: {ex.Message}");
                return;
            }

            _manager = manager;
            Events = events;

            Bound?.Invoke(events);

            if (verboseLogging) Debug.Log($"{Tag} Bound to the Convai event facade.");
        }

        /// <summary>Lets the facade go, telling subscribers first. Safe to call repeatedly.</summary>
        public void Release()
        {
            if (Events == null)
            {
                _manager = null;
                return;
            }

            var events = Events;

            // Cleared before the callback rather than after, so a subscriber that re-enters
            // this from its own handler sees the released state instead of recursing.
            Events = null;
            _manager = null;

            Unbinding?.Invoke(events);

            if (verboseLogging) Debug.Log($"{Tag} Released the Convai event facade.");
        }
    }
}
