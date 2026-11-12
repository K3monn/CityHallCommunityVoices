using UnityEngine;
using UnityEngine.InputSystem;
using CityHall.Core;

namespace CityHall.Systems
{
    /// <summary>
    /// Minimal raycast interactor so the project is testable in the Editor with
    /// mouse + keyboard before any XR rig is wired up. Casts from `rayOrigin`
    /// (defaults to the main camera, which is exactly what you want for desktop
    /// play-testing) and calls Interact() on whatever IInteractable it hits.
    ///
    /// For the real VR build, prefer wiring XR Interaction Toolkit's
    /// XRSimpleInteractable.OnSelectEntered -> your IInteractable.Interact()
    /// directly rather than relying on this raycast — XRI's controller-ray
    /// interaction already handles hover feedback, haptics, and multi-controller
    /// input correctly. Keep this component active for Editor/desktop testing and
    /// simply disable it in the XR-enabled build, or leave both active since they
    /// don't conflict (both just call the same Interact()).
    ///
    /// Uses the new Input System package (UnityEngine.InputSystem) rather than the
    /// legacy UnityEngine.Input class: XR Interaction Toolkit switches the project's
    /// Active Input Handling to "Input System Package" in Player Settings, and once
    /// that's set, any call into UnityEngine.Input throws an InvalidOperationException
    /// instead of silently working.
    /// </summary>
    public class PlayerInteractor : MonoBehaviour
    {
        public Transform rayOrigin;
        public float maxDistance = 7f;
        // A pure Raycast (zero-radius) demands pixel-perfect aim at a moving/wandering NPC with
        // no crosshair to aim with — reported as "can't really interact with the NPCs." Switched
        // to a SphereCast with a small forgiving radius (see castRadius below) instead: same API
        // shape, just a fatter "ray," so a near-miss still registers.
        public float castRadius = 0.15f;
        public LayerMask interactableMask = ~0;
        public IInteractable CurrentTarget { get; private set; }
        public string CurrentPromptText => CurrentTarget is IContextualPrompt contextual && !string.IsNullOrWhiteSpace(contextual.PromptText)
            ? contextual.PromptText
            : "Interact";

        /// <summary>True when the cast currently lands on something interactable — read by
        /// CrosshairUI (desktop) to give the player positive aim feedback before they click,
        /// since there was previously zero visual indication of what "close enough" looked like.</summary>
        public bool IsHoveringInteractable { get; private set; }

        private void Reset()
        {
            if (Camera.main != null) rayOrigin = Camera.main.transform;
        }

        private void Update()
        {
            UpdateHoverState();

            // Desktop, gamepad, and touchscreen aim input. Quest controllers call TryInteract()
            // through XRControllerInput, using the same range and occlusion check below.
            // (For the real VR build, call TryInteract() directly from an XRI
            // UnityEvent — see the class summary above — rather than relying on this.)
            bool pressed = (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                        || (Keyboard.current != null && (Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.enterKey.wasPressedThisFrame))
                        || (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame)
                        || (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame);
            if (pressed)
                TryInteract();
        }

        private void UpdateHoverState()
        {
            var origin = rayOrigin != null ? rayOrigin : (Camera.main != null ? Camera.main.transform : transform);
            CurrentTarget = null;
            if (Physics.SphereCast(origin.position, castRadius, origin.forward, out var hoverHit, maxDistance, interactableMask)
                && TryGetInteractable(hoverHit, out var hovered)
                && hovered.CanInteract)
            {
                CurrentTarget = hovered;
                IsHoveringInteractable = true;
            }
            else
            {
                IsHoveringInteractable = false;
            }
        }

        /// <summary>Call this directly from an XRI UnityEvent (e.g. Activate/Select) for the VR build.</summary>
        public void TryInteract()
        {
            var origin = rayOrigin != null ? rayOrigin : (Camera.main != null ? Camera.main.transform : transform);
            if (Physics.SphereCast(origin.position, castRadius, origin.forward, out var hit, maxDistance, interactableMask)
                && TryGetInteractable(hit, out var interactable)
                && interactable.CanInteract)
            {
                interactable.Interact(transform);
            }
        }

        private static bool TryGetInteractable(RaycastHit hit, out IInteractable interactable)
        {
            // Unity 2020.1+ supports GetComponentInParent<T> directly against an
            // interface type, so this finds DialogueTrigger/CivicExhibitTrigger/etc.
            // without needing to know the concrete component type.
            interactable = hit.collider.GetComponentInParent<IInteractable>();
            return interactable != null;
        }
    }
}
