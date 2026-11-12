using UnityEngine;

namespace CityHall.Core
{
    /// <summary>
    /// Common contract for anything the player can trigger-click: a citizen
    /// (DialogueTrigger), a civic exhibit kiosk, or a bulletin board/poster
    /// (both CivicExhibitTrigger). PlayerInteractor talks to everything through
    /// this one interface so it doesn't need to know which kind of object it hit.
    ///
    /// If you prefer to wire directly into XR Interaction Toolkit's
    /// XRSimpleInteractable instead of PlayerInteractor's raycast fallback, call
    /// Interact() from that component's OnSelectEntered UnityEvent — everything
    /// downstream (dialogue UI, exhibit panels) works the same either way.
    /// </summary>
    public interface IInteractable
    {
        void Interact(Transform interactor);
        bool CanInteract { get; }
        /// <summary>World-space point the interaction icon should hover above.</summary>
        Vector3 PromptAnchor { get; }
    }

    /// <summary>Optional concise label for the focused interaction hint.</summary>
    public interface IContextualPrompt
    {
        string PromptText { get; }
    }
}
