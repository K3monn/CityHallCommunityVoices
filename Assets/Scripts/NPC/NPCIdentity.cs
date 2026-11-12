using UnityEngine;

namespace CityHall.NPC
{
    /// <summary>
    /// Lightweight identity tag every citizen carries. Populated at spawn time by
    /// NPCVariationController and read by DialogueTrigger / NPCRoutineController.
    /// Keeping this separate from the variation logic means designers can hand-place
    /// an NPC in a scene, fill these fields in the Inspector, and everything else
    /// (dialogue lookup, routine selection) still works without the randomizer.
    /// </summary>
    public class NPCIdentity : MonoBehaviour
    {
        [Header("Who this citizen is")]
        public string displayName = "Resident";
        public string occupation = "Resident";

        [Tooltip("Matches a 'speakerRole' entry in Conversations.json, e.g. \"parent\", \"cyclist\", \"city_employee\". Leave blank for ambient (non-dialogue) NPCs.")]
        public string dialogueRole = "";

        [Tooltip("2026-07-22 governor/canvassing decision system: copied from NPCRoster.json's zone field at spawn time (see NPCSpawner). Matches CityMoraleSystem's tracked zones (\"residential\", \"commercial\", \"public_row\", \"city_hall_plaza\") — lets DialogueUIController and NPCSpeechBubble know which zone's morale this NPC belongs to without a separate roster lookup at runtime.")]
        public string zone = "";

        [Header("Variation summary (set by NPCVariationController)")]
        public int ageBand;      // 0 = child, 1 = adult, 2 = elder
        public string bodyType;
        public string clothingSet;

        [Header("Routine")]
        [Tooltip("Which NPCRoutineController schedule this citizen follows. See RoutineDefinitions.json.")]
        public string routineId = "generic_pedestrian";
    }
}
