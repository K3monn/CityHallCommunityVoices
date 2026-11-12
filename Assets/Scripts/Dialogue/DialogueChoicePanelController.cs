using System.Collections.Generic;
using UnityEngine;
using CityHall.Systems;

namespace CityHall.Dialogue
{
    /// <summary>
    /// ORPHANED as of 2026-07-22 ROUND 2 — no longer instantiated anywhere. Originally a
    /// separate always-below canvas for the player's response options (see
    /// DialogueUIController.PresentChoices / SelectChoice, and DialogueChoiceSlot for the
    /// actual clickable rows), kept apart from DialogueUIController's main panel so that
    /// panel's already-tuned, user-confirmed layout would never be at risk. In practice the
    /// player could never actually SEE this separate canvas (the underlying choice logic —
    /// keyboard 1/2/3 selection, morale/priority effects — worked fine, proving the bug was
    /// specific to this canvas's own visibility, not the data or selection logic). Rather than
    /// keep debugging an unproven second canvas, the choice rows were rebuilt directly inside
    /// DialogueUIController's main canvas instead (see TownSceneBuilder.BuildDialogueUI and
    /// DialogueUIController.ShowChoiceRows/HideChoiceRows), reusing that panel's already-proven
    /// rendering/occlusion/camera-tracking pipeline. This file is left in place (still compiles,
    /// harmless) rather than deleted, in case its approach is useful again later, but nothing
    /// wires it into the scene anymore.
    /// </summary>
    public class DialogueChoicePanelController : MonoBehaviour
    {
        public static DialogueChoicePanelController Instance { get; private set; }

        public Transform playerCamera;
        public Vector3 offsetFromCamera = new Vector3(0f, -0.58f, 1.12f);
        public DialogueChoiceSlot[] slots;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            UILayer.ApplyRecursively(gameObject);
        }

        private void LateUpdate()
        {
            if (playerCamera == null) return;
            transform.position = playerCamera.TransformPoint(offsetFromCamera);
            transform.rotation = Quaternion.LookRotation(transform.position - playerCamera.position);
        }

        public void Show(List<DialogueChoice> choices)
        {
            gameObject.SetActive(true);
            if (slots == null) return;
            for (int i = 0; i < slots.Length; i++)
            {
                bool has = choices != null && i < choices.Count;
                slots[i].gameObject.SetActive(has);
                if (has) slots[i].SetLabel((i + 1) + ".  " + choices[i].text);
            }
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }
    }
}
