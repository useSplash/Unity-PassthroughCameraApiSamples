using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Says what one graphic on the panel IS, so the theme can be applied to it without the
    /// panel holding a reference to every last image and label.
    ///
    /// The alternative was a serialized field per themed graphic, which is a dozen more things
    /// to wire and a dozen more ways a re-bake comes out half-connected. This way the baker
    /// tags each piece as it builds it, the panel does one GetComponentsInChildren in Awake,
    /// and anything added to the prefab later gets themed by dropping this on it and picking a
    /// role -- no code change.
    /// </summary>
    public class ScanPanelSkin : MonoBehaviour
    {
        public enum Role
        {
            /// <summary>The panel's own backing plate. Coloured and rounded.</summary>
            PanelBackground,

            /// <summary>An action or plan button's face. Coloured and rounded.</summary>
            ButtonFace,

            /// <summary>The exit button's face. Its own colour, the button radius.</summary>
            ExitFace,

            /// <summary>The panel title.</summary>
            Title,

            /// <summary>The readout, the controls block, the question line.</summary>
            BodyText,

            /// <summary>The text on a button.</summary>
            ButtonLabel
        }

        [Tooltip("What this graphic is, which is what decides the colour it gets. See " +
                 "ConvaiRoomModePanel.ApplyTheme.")]
        public Role role = Role.BodyText;
    }
}
