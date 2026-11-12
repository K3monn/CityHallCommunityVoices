using UnityEngine;
using UnityEngine.SceneManagement;
using CityHall.Core;

namespace CityHall.Environment
{
    /// <summary>
    /// "Return to Main Menu" half of the Final Reflection Hall's exit choice (2026-07-23 Town
    /// Hall interior redesign brief: "an exit choice — Return to Main Menu / Return to
    /// Neighborhood"). This project has no separate main-menu scene — everything lives in one
    /// Town.unity scene, procedurally built by TownSceneBuilder — and authoring a real title
    /// screen isn't something that can be done blind without the Editor. Reloading the active
    /// scene is the honest stand-in available today: it drops the player back at the very start
    /// of a fresh session, distinct from "Return to Neighborhood" (a PortalTravel back to the
    /// plaza that keeps the current session's CityMoraleSystem/priority-zone state intact).
    /// Flagged in the project notes as a decision worth revisiting once a real main menu exists.
    ///
    /// Deliberately requires a real interact (aim + click/Space) rather than an auto-trigger on
    /// walk-in like PortalTravel — reloading the whole scene is a much bigger, more surprising
    /// action than walking between rooms, so it shouldn't fire just because the player wandered
    /// near it.
    /// </summary>
    public class MainMenuReturnTrigger : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public Vector3 promptAnchorOffset = new Vector3(0f, 1.6f, 0f);

        public bool CanInteract => true;
        public string PromptText => "Return to the Main Menu";
        public Vector3 PromptAnchor => transform.position + promptAnchorOffset;

        public void Interact(Transform interactor)
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}
