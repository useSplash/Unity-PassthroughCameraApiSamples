using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ConvaiRoom
{
    /// <summary>
    /// Puts one line of "what this button does" on the panel while the laser is resting on it.
    ///
    /// A pure relay. It knows nothing about what any button means: the description is a delegate
    /// the panel supplies, and it is called AT HOVER TIME rather than when the hint was attached.
    /// That matters because the three action slots are generic -- what slot 0 is changes with
    /// every stage and with every study sub-mode -- so a string captured at bind time would
    /// describe whatever the slot happened to hold when the scene loaded.
    ///
    /// Added in code rather than authored on the prefab, for the same reason the study slots
    /// were: a re-bake risks the prefab's GUIDs, and this needs no geometry of its own. It
    /// writes into the prompt line the panel already has.
    ///
    /// Greyed buttons still hint, and that is deliberate. interactable = false gates Selectable's
    /// own click handling, not the EventSystem's enter/exit dispatch, so a slot that is refusing
    /// still explains what it would do -- which is exactly when somebody wants to know.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConvaiRoomPanelHint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private Func<string> _describe;
        private Action<ConvaiRoomPanelHint, string> _entered;
        private Action<ConvaiRoomPanelHint> _left;

        /// <summary>
        /// Adds a hint to one button, or re-points the one already there.
        ///
        /// Re-points rather than stacks. BindButtons is the only caller and runs once, but two
        /// hints on one button would both write to the same line and only one of them would ever
        /// be cleared -- a bug that would not show up until somebody added a second bind.
        /// </summary>
        public static void Attach(GameObject target, Func<string> describe,
                                  Action<ConvaiRoomPanelHint, string> entered,
                                  Action<ConvaiRoomPanelHint> left)
        {
            if (target == null || describe == null) return;

            var hint = target.GetComponent<ConvaiRoomPanelHint>();
            if (hint == null) hint = target.AddComponent<ConvaiRoomPanelHint>();

            hint._describe = describe;
            hint._entered = entered;
            hint._left = left;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_describe == null || _entered == null) return;

            _entered(this, _describe());
        }

        public void OnPointerExit(PointerEventData eventData) => Release();

        /// <summary>
        /// A slot that empties while you are pointing at it never sends OnPointerExit -- the
        /// object is switched off mid-hover, which happens on nearly every press here because
        /// the stage rewrites the whole row. Without this the description of a button that no
        /// longer exists stays on the panel until something else is hovered.
        /// </summary>
        private void OnDisable() => Release();

        private void Release() => _left?.Invoke(this);
    }
}
