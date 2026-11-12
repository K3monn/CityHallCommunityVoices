using System.Collections.Generic;
using UnityEngine;
using CityHall.NPC;
using CityHall.Dialogue;

namespace CityHall.Systems
{
    /// <summary>
    /// Ties NPCRoster.json, NPCVariationController, DialogueTrigger, and
    /// PerformanceLODManager together at scene load. Level designers place empty
    /// "spawn marker" Transforms in the scene (position + facing only) and match
    /// each one to a roster id here — no per-NPC scripting required.
    ///
    /// Put one NPCSpawner per zone (Residential, Commercial, PublicRow,
    /// CityHallPlaza) as a child of that zone's root GameObject, so zones can be
    /// loaded/unloaded independently later if the town is split into sub-scenes
    /// for performance.
    /// </summary>
    public class NPCSpawner : MonoBehaviour
    {
        [System.Serializable]
        public class SpawnEntry
        {
            public string rosterId;      // matches an id in NPCRoster.json
            public Transform point;      // position + rotation to spawn at
            public GameObject prefabOverride; // leave null to use defaultCitizenPrefab
        }

        public GameObject defaultCitizenPrefab;
        public List<SpawnEntry> spawnPoints = new List<SpawnEntry>();

        [Tooltip("Optional — propagated onto every spawned NPC's NPCRoutineController.keepClearAnchor. " +
                 "Set this on a zone's spawner (e.g. the City Hall plaza's) to stop ambient wander from " +
                 "ever parking an NPC in a doorway/threshold near this point. Left null for most zones.")]
        public Transform keepClearAnchor;
        public float keepClearRadius = 3f;

        [Tooltip("If true, uses the roster id string as the random seed so re-entering the scene produces the same-looking crowd (useful for photo-mode consistency / QA). If false, re-rolls every load.")]
        public bool deterministicAppearance = true;

        private readonly List<GameObject> _spawned = new List<GameObject>();

        private void Start()
        {
            foreach (var entry in spawnPoints)
                Spawn(entry);
        }

        private void Spawn(SpawnEntry entry)
        {
            if (entry.point == null)
            {
                Debug.LogWarning($"NPCSpawner: spawn entry for roster id '{entry.rosterId}' has no point assigned; skipped.");
                return;
            }

            var prefab = entry.prefabOverride != null ? entry.prefabOverride : defaultCitizenPrefab;
            if (prefab == null)
            {
                Debug.LogWarning("NPCSpawner: no prefab available (set defaultCitizenPrefab or a per-entry override).");
                return;
            }

            var roster = CivicDataLoader.Instance != null ? CivicDataLoader.Instance.GetRosterEntry(entry.rosterId) : null;

            var go = Instantiate(prefab, entry.point.position, entry.point.rotation, transform);
            go.name = $"NPC_{entry.rosterId}";

            var identity = go.GetComponent<NPCIdentity>();
            if (identity == null) identity = go.AddComponent<NPCIdentity>();
            if (roster != null)
            {
                identity.displayName = roster.displayName;
                identity.occupation = roster.occupation;
                identity.dialogueRole = roster.dialogueRole;
                identity.routineId = roster.routineId;
                identity.zone = roster.zone; // 2026-07-22 governor/canvassing decision system
            }

            var variation = go.GetComponent<NPCVariationController>();
            if (variation != null)
            {
                int? seed = deterministicAppearance ? entry.rosterId.GetHashCode() : (int?)null;
                variation.Randomize(seed);
            }

            if (keepClearAnchor != null)
            {
                var routine = go.GetComponent<NPCRoutineController>();
                if (routine != null)
                {
                    routine.keepClearAnchor = keepClearAnchor;
                    routine.keepClearRadius = keepClearRadius;
                }
            }

            if (roster != null && roster.dialogueRole == "mayor")
            {
                // Keep the mayor available in the office instead of sending this fixed civic
                // character on the same free-roam routine as neighborhood residents.
                var routine = go.GetComponent<NPCRoutineController>();
                if (routine != null)
                {
                    routine.wanderRadius = 1.25f;
                    routine.minDwellSeconds = 18f;
                    routine.maxDwellSeconds = 30f;
                }
            }

            if (roster != null && !string.IsNullOrEmpty(roster.conversationId))
            {
                var trigger = go.GetComponent<DialogueTrigger>();
                if (trigger == null) trigger = go.AddComponent<DialogueTrigger>();
                trigger.conversationId = roster.conversationId;
            }
            // 2026-07-22 governor/canvassing decision system: the Governor doesn't have a fixed
            // conversationId (roster.conversationId is left blank for governor_01) — GovernorTrigger
            // picks one of three morale-tier briefing variants at interact time instead. Matched by
            // dialogueRole rather than roster id so any future second governor-type NPC works too.
            else if (roster != null && roster.dialogueRole == "governor")
            {
                if (go.GetComponent<GovernorTrigger>() == null)
                    go.AddComponent<GovernorTrigger>();
            }

            // 2026-07-22 UI brief: "For ambient conversations happening throughout town, use
            // small speech bubbles instead of full dialogue windows." Every spawned citizen
            // gets one — it's cheap and self-limiting (random interval, distance check, and it
            // hides itself entirely whenever a real DialogueUIController conversation is
            // playing), so this is safe to add unconditionally rather than curating a subset.
            if (go.GetComponent<NPCSpeechBubble>() == null)
                go.AddComponent<NPCSpeechBubble>();

            if (PerformanceLODManager.Instance != null)
                PerformanceLODManager.Instance.Register(go);

            _spawned.Add(go);
        }

        private void OnDestroy()
        {
            _spawned.Clear();
        }
    }
}
