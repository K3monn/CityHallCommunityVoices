using System;
using System.Collections.Generic;

namespace CityHall.NPC
{
    // Matches Assets/Data/Resources/CivicData/NPCRoster.json — one entry per
    // named citizen in the town (the ~20 NPCs called out in the design brief:
    // student, teacher, business owner, engineer, construction worker, city
    // planner, elderly resident, new resident, parent, transit commuter, cyclist,
    // library employee, park volunteer, restaurant owner, city maintenance
    // worker, plus the paired/single conversation speakers).

    [Serializable]
    public class NPCRosterEntry
    {
        public string id;              // "parent_01" — referenced by NPCSpawner's scene spawn points
        public string displayName;
        public string occupation;
        public string dialogueRole;    // free-text category, e.g. "parent", "cyclist"
        public string conversationId;  // links to Conversations.json, blank = ambient/no dialogue
        public string routineId;       // links to a route set on NPCRoutineController, or "generic_pedestrian"
        public string zone;            // "residential" | "commercial" | "public_row" | "city_hall_plaza" — design/authoring hint
    }

    [Serializable]
    public class NPCRosterRoot
    {
        public List<NPCRosterEntry> roster = new List<NPCRosterEntry>();
    }
}
