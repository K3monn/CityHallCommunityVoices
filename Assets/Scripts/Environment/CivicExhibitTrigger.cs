using UnityEngine;
using CityHall.Core;
using CityHall.Systems;

namespace CityHall.Environment
{
    /// <summary>
    /// One clean, museum-style exhibit ("How does a city budget work?") or a
    /// piece of environmental storytelling (bulletin board, bus stop poster,
    /// Register to Vote board). Which JSON source it reads from is just a matter
    /// of setting sourceType + entryId — the same component covers both, since
    /// the brief treats them as visually distinct but functionally identical
    /// (walk up, read, walk away).
    /// </summary>
    public class CivicExhibitTrigger : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public enum SourceType { CivicExhibit, EnvironmentalBoard }

        public SourceType sourceType = SourceType.CivicExhibit;
        public string entryId;
        public string promptText = "Read this exhibit";
        public Vector3 promptAnchorOffset = new Vector3(0f, 1.6f, 0f);

        public bool CanInteract => InfoPanelController.Instance != null;
        public string PromptText => promptText;
        public Vector3 PromptAnchor => transform.position + promptAnchorOffset;

        public void Interact(Transform interactor)
        {
            if (CivicDataLoader.Instance == null || InfoPanelController.Instance == null) return;

            if (sourceType == SourceType.CivicExhibit)
            {
                var exhibit = CivicDataLoader.Instance.GetExhibit(entryId);
                if (exhibit != null)
                    InfoPanelController.Instance.Show("Civic Exhibit", exhibit.title, exhibit.text);
            }
            else
            {
                var board = CivicDataLoader.Instance.GetBoard(entryId);
                if (board != null)
                    InfoPanelController.Instance.Show("Environmental Storytelling", board.title, board.body);
            }
        }
    }
}
