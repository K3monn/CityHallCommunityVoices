using System;
using System.Collections.Generic;
using UnityEngine;
using CityHall.Dialogue;
using CityHall.NPC;

namespace CityHall.Systems
{
    /// <summary>
    /// Loads the town's written content — conversations, civic exhibits, bulletin
    /// boards/posters — from JSON in Resources/CivicData once, at startup, so
    /// writers can edit those .json files without touching any C# or recompiling
    /// scene-embedded text.
    ///
    /// Put this on a persistent "Systems" GameObject in your bootstrap scene,
    /// alongside TimeOfDayManager and PerformanceLODManager.
    /// </summary>
    public class CivicDataLoader : MonoBehaviour
    {
        public static CivicDataLoader Instance { get; private set; }

        private Dictionary<string, ConversationEntry> _conversations;
        private Dictionary<string, CivicExhibitEntry> _exhibits;
        private Dictionary<string, BoardEntry> _boards;
        private Dictionary<string, NPCRosterEntry> _roster;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadAll();
        }

        private void LoadAll()
        {
            _conversations = new Dictionary<string, ConversationEntry>();
            _exhibits = new Dictionary<string, CivicExhibitEntry>();
            _boards = new Dictionary<string, BoardEntry>();
            _roster = new Dictionary<string, NPCRosterEntry>();

            LoadResource("Conversations", _conversations,
                text => JsonUtility.FromJson<ConversationRoot>(text)?.conversations,
                entry => entry.id);
            LoadResource("CivicExhibits", _exhibits,
                text => JsonUtility.FromJson<CivicExhibitRoot>(text)?.exhibits,
                entry => entry.id);
            LoadResource("Boards", _boards,
                text => JsonUtility.FromJson<BoardRoot>(text)?.boards,
                entry => entry.id);
            LoadResource("NPCRoster", _roster,
                text => JsonUtility.FromJson<NPCRosterRoot>(text)?.roster,
                entry => entry.id);
        }

        private static void LoadResource<T>(string assetName, Dictionary<string, T> target,
            Func<string, List<T>> parseEntries, Func<T, string> getId) where T : class
        {
            string path = "CivicData/" + assetName;
            var asset = Resources.Load<TextAsset>(path);
            if (asset == null)
            {
                Debug.LogError($"CivicDataLoader: required resource '{path}' is missing from the player build.");
                return;
            }

            try
            {
                var entries = parseEntries(asset.text);
                if (entries == null)
                {
                    Debug.LogError($"CivicDataLoader: '{path}' parsed without an entries list.");
                    return;
                }

                foreach (var entry in entries)
                {
                    string id = entry != null ? getId(entry) : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        Debug.LogWarning($"CivicDataLoader: skipped a blank entry in '{path}'.");
                        continue;
                    }
                    if (target.ContainsKey(id))
                        Debug.LogWarning($"CivicDataLoader: duplicate id '{id}' in '{path}', keeping the last entry.");
                    target[id] = entry;
                }
                Debug.Log($"CivicDataLoader: loaded {target.Count} entries from '{path}'.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"CivicDataLoader: failed to parse required resource '{path}': {exception.Message}");
            }
        }

        public ConversationEntry GetConversation(string id)
        {
            if (_conversations != null && _conversations.TryGetValue(id, out var entry)) return entry;
            Debug.LogWarning($"CivicDataLoader: no conversation with id '{id}'.");
            return null;
        }

        public CivicExhibitEntry GetExhibit(string id)
        {
            if (_exhibits != null && _exhibits.TryGetValue(id, out var entry)) return entry;
            Debug.LogWarning($"CivicDataLoader: no civic exhibit with id '{id}'.");
            return null;
        }

        public BoardEntry GetBoard(string id)
        {
            if (_boards != null && _boards.TryGetValue(id, out var entry)) return entry;
            Debug.LogWarning($"CivicDataLoader: no board with id '{id}'.");
            return null;
        }

        public NPCRosterEntry GetRosterEntry(string id)
        {
            if (_roster != null && _roster.TryGetValue(id, out var entry)) return entry;
            Debug.LogWarning($"CivicDataLoader: no NPC roster entry with id '{id}'.");
            return null;
        }
    }
}
