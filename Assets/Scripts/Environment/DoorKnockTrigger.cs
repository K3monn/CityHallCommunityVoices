using System.Collections;
using UnityEngine;
using CityHall.Core;
using CityHall.Dialogue;
using CityHall.Systems;

namespace CityHall.Environment
{
    /// <summary>Starts a respectful, choice-based door-to-door canvassing interaction.</summary>
    public class DoorKnockTrigger : MonoBehaviour, IInteractable, IContextualPrompt
    {
        [Range(0f, 1f)]
        public float residentAnswerChance = 0.8f;
        public float responseDelay = 1.1f;

        private Coroutine _responseRoutine;

        public bool CanInteract => DialogueUIController.Instance != null
            && !DialogueUIController.Instance.IsPlaying
            && CivicDataLoader.Instance != null;
        public string PromptText => "Knock on the door?";

        public Vector3 PromptAnchor => transform.position + Vector3.up * 2f;

        public void Interact(Transform interactor)
        {
            if (!CanInteract) return;
            var prompt = CivicDataLoader.Instance.GetConversation("door_knock_prompt");
            if (prompt != null)
                DialogueUIController.Instance.Play(prompt, transform, "Resident", "Neighborhood Resident");
        }

        public void HandleChoice(string choiceTag)
        {
            if (choiceTag != "door_knock" || _responseRoutine != null) return;
            _responseRoutine = StartCoroutine(ShowResidentResponse());
        }

        private IEnumerator ShowResidentResponse()
        {
            // Let the prompt and its choice outcome close before opening the response.
            yield return new WaitUntil(() => DialogueUIController.Instance == null
                || !DialogueUIController.Instance.IsPlaying);
            yield return new WaitForSeconds(responseDelay);

            if (DialogueUIController.Instance != null && CivicDataLoader.Instance != null)
            {
                string id = Random.value <= residentAnswerChance
                    ? "door_knock_answered"
                    : "door_knock_no_answer";
                var response = CivicDataLoader.Instance.GetConversation(id);
                if (response != null)
                    DialogueUIController.Instance.Play(response, transform, "Resident", "Neighborhood Resident");
            }

            _responseRoutine = null;
        }
    }
}
