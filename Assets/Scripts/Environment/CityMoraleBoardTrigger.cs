using UnityEngine;
using CityHall.Core;
using CityHall.Systems;

namespace CityHall.Environment
{
    /// <summary>
    /// The "Community Pulse" board — added 2026-07-22 alongside the governor/
    /// canvassing decision system. Unlike CivicExhibitTrigger (static JSON
    /// text), this board's body text is generated live from CityMoraleSystem
    /// every time it's read, so the effect of the player's canvassing choices
    /// is discoverable somewhere in the world without adding an on-screen HUD
    /// meter (design brief: no HUD, no minimap, no quest markers — morale
    /// should be legible in the world instead, not on the screen).
    /// </summary>
    public class CityMoraleBoardTrigger : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public Vector3 promptAnchorOffset = new Vector3(0f, 1.6f, 0f);

        public bool CanInteract => InfoPanelController.Instance != null;
        public string PromptText => "Read community priorities";
        public Vector3 PromptAnchor => transform.position + promptAnchorOffset;

        public void Interact(Transform interactor)
        {
            if (InfoPanelController.Instance == null) return;
            string body = CityMoraleSystem.Instance != null
                ? CityMoraleSystem.Instance.GetSummaryText()
                : "No canvassing reports yet — get out and talk to some residents.";
            InfoPanelController.Instance.Show("Community Pulse", "How Is Canvassing Going?", body);
        }
    }
}
