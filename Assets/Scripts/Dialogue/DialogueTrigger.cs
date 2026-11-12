using UnityEngine;
using CityHall.Core;
using CityHall.NPC;
using CityHall.Systems;

namespace CityHall.Dialogue
{
    /// <summary>
    /// Put this on a citizen NPC alongside NPCIdentity. Set conversationId to an
    /// entry in Conversations.json. On interact: the NPC turns to face the player
    /// (per brief: "NPC naturally turns toward player") and DialogueUIController
    /// plays the exchange as timed subtitles.
    ///
    /// Two citizens who are mid-conversation with each other (e.g. the "Parent &
    /// Neighbor" pair) can share the same conversationId — interacting with
    /// either one plays the same overheard exchange, which is how the town
    /// mockup's browser previz handled paired NPCs too.
    /// </summary>
    [RequireComponent(typeof(NPCIdentity))]
    public class DialogueTrigger : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public string conversationId;
        [Tooltip("Degrees per second the NPC rotates to face the player on interact.")]
        public float turnSpeed = 220f;
        public Transform faceTarget; // usually this.transform; separate field in case the visual root differs

        private Coroutine _turnRoutine;

        public bool CanInteract => DialogueUIController.Instance != null && !DialogueUIController.Instance.IsPlaying;
        public string PromptText
        {
            get
            {
                var identity = GetComponent<NPCIdentity>();
                return identity != null && !string.IsNullOrWhiteSpace(identity.displayName)
                    ? "Talk to " + identity.displayName
                    : "Talk to resident";
            }
        }
        public Vector3 PromptAnchor => transform.position + Vector3.up * 1.9f;

        private void Reset()
        {
            faceTarget = transform;
        }

        public void Interact(Transform interactor)
        {
            if (!CanInteract) return;
            var entry = CivicDataLoader.Instance != null ? CivicDataLoader.Instance.GetConversation(conversationId) : null;
            if (entry == null) return;

            if (_turnRoutine != null) StopCoroutine(_turnRoutine);
            _turnRoutine = StartCoroutine(TurnToward(interactor.position));

            // 2026-07-22 UI brief: pass this NPC's own identity through so the dialogue box can
            // show a real speaker portrait/occupation and bias its position toward this NPC
            // rather than sitting dead-center every time — see DialogueUIController.Play()'s doc
            // comment. Reads straight from NPCIdentity (already required on this GameObject) so
            // no Conversations.json content needs to change.
            var identity = GetComponent<NPCIdentity>();
            DialogueUIController.Instance.Play(entry, transform, identity != null ? identity.displayName : null, identity != null ? identity.occupation : null);

            // Dynamic camera framing (same brief: "slightly rotate the camera toward the NPC...
            // do not lock the camera or remove player agency") — a brief, cancellable nudge, not
            // a hard lock. See PlayerMovement.NudgeYawToward's own doc comment for the full
            // reasoning and safeguards. No-ops safely in XR mode (no PlayerMovement there, and
            // programmatically rotating a headset view would be wrong anyway).
            var movement = interactor.GetComponentInParent<PlayerMovement>();
            if (movement != null) movement.NudgeYawToward(transform);
        }

        private System.Collections.IEnumerator TurnToward(Vector3 playerPos)
        {
            Vector3 dir = playerPos - transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.001f) yield break;
            Quaternion target = Quaternion.LookRotation(dir.normalized);
            var pivot = faceTarget != null ? faceTarget : transform;
            while (Quaternion.Angle(pivot.rotation, target) > 1f)
            {
                pivot.rotation = Quaternion.RotateTowards(pivot.rotation, target, turnSpeed * Time.deltaTime);
                yield return null;
            }
        }
    }
}
