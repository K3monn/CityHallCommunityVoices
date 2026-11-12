using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityHall.Dialogue
{
    // Plain serializable classes matching Assets/Data/Resources/CivicData/*.json.
    // Kept separate from any MonoBehaviour so they can be unit-tested / edited by
    // a writer without touching Unity at all.

    [Serializable]
    public class ConversationLine
    {
        public string speaker;
        public string text;
        [Tooltip("Optional Resources path to a VO clip, e.g. \"Audio/VO/parent_neighbor_01\". Leave blank to play subtitles only.")]
        public string voClipResourcePath;
        [Tooltip("Seconds to hold this line if voClipResourcePath is blank (VO-driven lines use the clip's own length instead).")]
        public float displaySeconds = 3.5f;
        [Tooltip("2026-07-22 UI brief (\"Player dialogue should be visually distinct from NPC dialogue\"): marks this line as spoken by the player rather than a resident, so DialogueUIController styles it differently (right-aligned, accent color). Optional — defaults false, so none of the existing 27 conversations need any changes.")]
        public bool isPlayerLine;
        [Tooltip("2026-07-22 governor/canvassing decision system: if non-empty, DialogueUIController pauses on this line and presents these as player response options instead of auto-advancing — see DialogueUIController.PresentChoices/choiceSlots. Optional — empty/null for every line that isn't a decision point, so no existing conversation needs any changes.")]
        public List<DialogueChoice> choices;
    }

    [Serializable]
    public class DialogueChoice
    {
        [Tooltip("The response option text shown to the player.")]
        public string text;
        [Tooltip("Free-form label for design/analytics use (e.g. \"listen\", \"rush\", \"overpromise\"). Not read by any code path yet.")]
        public string tag;
        [Tooltip("Added to CityMoraleSystem's morale for the speaking NPC's zone when this choice is picked. 0 for choices that don't move morale (e.g. the Governor's priority picks).")]
        public float moraleDelta;
        [Tooltip("Optional line shown briefly after the choice is made, as if the NPC is reacting to it.")]
        public string npcResponseText;
        [Tooltip("Optional short real-world canvassing tip shown after npcResponseText — the actual teaching moment behind the choice.")]
        public string educationalNote;
        [Tooltip("If non-empty, sets CityMoraleSystem.PriorityZone to this value (e.g. \"residential\", \"commercial\", \"public_row\") instead of/alongside a morale change. Used by the Governor's \"where should we focus next\" choice.")]
        public string setPriorityZone;
    }

    [Serializable]
    public class ConversationEntry
    {
        public string id;            // "parent_neighbor_sidewalks"
        public string kicker;        // "Sidewalks & School Pickup" — shown above the title in the UI
        public string title;         // "Parent & Neighbor"
        public List<ConversationLine> lines = new List<ConversationLine>();
    }

    [Serializable]
    public class ConversationRoot
    {
        public List<ConversationEntry> conversations = new List<ConversationEntry>();
    }

    [Serializable]
    public class CivicExhibitEntry
    {
        public string id;
        public string title;         // "How does a city budget work?"
        public string text;
    }

    [Serializable]
    public class CivicExhibitRoot
    {
        public List<CivicExhibitEntry> exhibits = new List<CivicExhibitEntry>();
    }

    [Serializable]
    public class BoardEntry
    {
        public string id;
        public string title;
        public string body;
        public string location;      // free-text hint for level designers, e.g. "bus stop - residential"
    }

    [Serializable]
    public class BoardRoot
    {
        public List<BoardEntry> boards = new List<BoardEntry>();
    }
}
