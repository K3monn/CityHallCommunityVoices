using UnityEngine;
using UnityEngine.UI;

namespace CityHall.Systems
{
    /// <summary>
    /// Small screen-center reticle for desktop play-testing. PlayerInteractor's raycast previously
    /// had zero visual aim indicator — combined with wandering NPCs and no crosshair, "click to
    /// talk" was reported as basically impossible to land ("can't really interact with the NPCs").
    /// This is a minimal aiming aid, not a wayfinding HUD element (no minimap, no quest markers,
    /// no compass) — it doesn't conflict with the project's "no HUD" wayfinding brief, it just gives
    /// the player something to aim with.
    ///
    /// Brightens and enlarges slightly whenever PlayerInteractor.IsHoveringInteractable is true, so
    /// the player gets positive confirmation ("yes, this will do something") before clicking, rather
    /// than clicking blind and getting no feedback either way.
    ///
    /// Not needed for the XR rig — XRControllerInput already draws a physical pointer ray from each
    /// hand controller (see its own class comment), which serves the same purpose in VR. This
    /// component is built as a child of the desktop rig specifically, so RigModeSelector disabling
    /// the desktop rig in XR mode automatically hides it along with everything else on that rig.
    /// </summary>
    public class CrosshairUI : MonoBehaviour
    {
        public PlayerInteractor interactor;
        public Image dot;
        public Color idleColor = new Color(1f, 1f, 1f, 0.55f);
        public Color activeColor = new Color(1f, 0.85f, 0.3f, 0.95f);
        public float idleScale = 1f;
        public float activeScale = 1.7f;

        private void Update()
        {
            if (dot == null || interactor == null) return;
            bool hovering = interactor.IsHoveringInteractable;
            dot.color = hovering ? activeColor : idleColor;
            dot.rectTransform.localScale = Vector3.one * (hovering ? activeScale : idleScale);
        }
    }
}
