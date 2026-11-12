using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityHall.Core;
using CityHall.NPC;
using CityHall.Systems;

namespace CityHall.Dialogue
{
    /// <summary>
    /// The Governor — added 2026-07-22 per user brief: "have it so as the user
    /// can go around and has to make decisions to help this fictional
    /// Politician/governor keep the morale of the city and citizens... make it
    /// educational and teach about canvassing." Works like DialogueTrigger (same
    /// turn-to-face + camera nudge) but isn't one, because the conversation it
    /// plays isn't fixed: it picks one of three pre-written briefing variants
    /// (governor_briefing_low/medium/high in Conversations.json) based on the
    /// CURRENT city-wide morale tier from CityMoraleSystem, so the Governor's own
    /// words react to how canvassing has actually been going. Each variant ends
    /// in the same shared 3-option "where should we focus next" choice, which
    /// sets CityMoraleSystem.PriorityZone via DialogueChoice.setPriorityZone —
    /// no special choice-handling needed here, DialogueUIController already
    /// handles any conversation with choices the same way.
    ///
    /// Attached automatically by NPCSpawner when a roster entry has
    /// dialogueRole == "governor" and no conversationId (see NPCSpawner.Spawn) —
    /// governor_01 in NPCRoster.json is the only entry that currently matches.
    /// </summary>
    [RequireComponent(typeof(NPCIdentity))]
    public class GovernorTrigger : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public float turnSpeed = 220f;

        [Header("Findability label — 2026-07-22, so the Governor is easy to spot across the plaza")]
        [Tooltip("Unlike InteractionPrompt's icon (only appears within a few meters), this label is always visible and occlusion-proofed via UILayer, so it works as a landmark from across the plaza, not just up close.")]
        public string labelText = "GOVERNOR";
        public Vector3 labelWorldOffset = new Vector3(0f, 2.3f, 0f);

        private Coroutine _turnRoutine;
        private Transform _labelTransform;

        public bool CanInteract => DialogueUIController.Instance != null && !DialogueUIController.Instance.IsPlaying;
        public string PromptText => "Talk with the Governor";
        public Vector3 PromptAnchor => transform.position + Vector3.up * 2f;

        private void Awake()
        {
            BuildFindLabel();
        }

        /// <summary>Small always-on world-space label above the Governor's head — deliberately
        /// NOT distance-gated or fading like NPCSpeechBubble's ambient chatter, since the whole
        /// point is to be spottable from across the plaza. Occlusion-proofed via the same UILayer
        /// technique as every other world-space UI element (see UILayer.cs), so it reads through
        /// trees/other NPCs/plaza props too, not just line-of-sight.</summary>
        private void BuildFindLabel()
        {
            var canvasGO = new GameObject("GovernorLabel_Canvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(260f, 70f);
            // Slightly larger world scale than NPCSpeechBubble's 0.0016 (0.0022 here) — this is
            // meant to be legible from much farther away than an ambient chatter bubble ever needs to be.
            canvasGO.transform.localScale = Vector3.one * 0.0022f;

            var bg = new GameObject("Bg");
            bg.transform.SetParent(canvasGO.transform, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.176f, 0.29f, 0.369f, 0.88f);
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;

            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(canvasGO.transform, false);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.text = labelText;
            label.fontSize = 30f;
            label.fontStyle = FontStyles.Bold;
            label.color = new Color(0.9f, 0.85f, 0.6f);
            label.alignment = TextAlignmentOptions.Center;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero; labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = new Vector2(10f, 6f); labelRT.offsetMax = new Vector2(-10f, -6f);

            _labelTransform = canvasGO.transform;
            UILayer.ApplyRecursively(canvasGO);
        }

        private void LateUpdate()
        {
            if (_labelTransform == null) return;
            _labelTransform.position = transform.position + labelWorldOffset;
            if (Camera.main != null)
                _labelTransform.rotation = Quaternion.LookRotation(_labelTransform.position - Camera.main.transform.position);
        }

        public void Interact(Transform interactor)
        {
            if (!CanInteract || CivicDataLoader.Instance == null) return;

            string tierSuffix = "medium";
            if (CityMoraleSystem.Instance != null)
            {
                switch (CityMoraleSystem.Instance.GetCityMoraleTier())
                {
                    case CityMoraleSystem.MoraleTier.Struggling: tierSuffix = "low"; break;
                    case CityMoraleSystem.MoraleTier.Thriving: tierSuffix = "high"; break;
                    default: tierSuffix = "medium"; break;
                }
            }

            var entry = CivicDataLoader.Instance.GetConversation("governor_briefing_" + tierSuffix);
            if (entry == null) return;

            if (_turnRoutine != null) StopCoroutine(_turnRoutine);
            _turnRoutine = StartCoroutine(TurnToward(interactor.position));

            var identity = GetComponent<NPCIdentity>();
            DialogueUIController.Instance.Play(entry, transform,
                identity != null ? identity.displayName : "Governor",
                identity != null ? identity.occupation : "City Governor");

            var movement = interactor.GetComponentInParent<PlayerMovement>();
            if (movement != null) movement.NudgeYawToward(transform);
        }

        private System.Collections.IEnumerator TurnToward(Vector3 playerPos)
        {
            Vector3 dir = playerPos - transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.001f) yield break;
            Quaternion target = Quaternion.LookRotation(dir.normalized);
            while (Quaternion.Angle(transform.rotation, target) > 1f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
                yield return null;
            }
        }
    }
}
