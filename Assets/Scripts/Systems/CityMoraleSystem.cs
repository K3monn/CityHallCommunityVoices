using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CityHall.Systems
{
    /// <summary>
    /// Tracks how residents in each part of town feel about being checked in on —
    /// the "morale" the Governor asks the player to help with. Added 2026-07-22
    /// per the user brief: "have it so as the user can go around and has to make
    /// decisions to help this fictional Politician/governor keep the morale of
    /// the city and citizens... make it educational and teach about canvassing."
    ///
    /// Persists local morale and the selected priority zone in PlayerPrefs so the
    /// player's canvassing progress survives scene reloads and app restarts.
    ///
    /// Deliberately NOT surfaced as an on-screen meter, per design brief: morale
    /// is meant to be discoverable diegetically. NPCSpeechBubble's ambient
    /// chatter tone, MoraleAmbientReflector's subtle ambient-light shift, the
    /// Governor's own dialogue (GovernorTrigger), and the Community Pulse board
    /// (CityMoraleBoardTrigger) all read from this — nothing renders a
    /// persistent number on the player's screen.
    /// </summary>
    public class CityMoraleSystem : MonoBehaviour
    {
        public static CityMoraleSystem Instance { get; private set; }

        public enum MoraleTier { Struggling, Steady, Thriving }

        // Matches the "zone" values already used in NPCRoster.json / NPCIdentity.zone.
        private static readonly string[] TrackedZones = { "residential", "commercial", "public_row", "city_hall_plaza" };

        private readonly Dictionary<string, float> _morale = new Dictionary<string, float>();

        /// <summary>Set by the Governor's "where should we focus next" choice
        /// (DialogueChoice.setPriorityZone). Canvassing choices in this zone earn
        /// a bonus multiplier — rewards actually following the guidance rather
        /// than making it cosmetic.</summary>
        public string PriorityZone { get; private set; } = "";
        public float priorityMultiplier = 1.5f;

        public event Action OnChanged;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            foreach (var z in TrackedZones)
                _morale[z] = PlayerPrefs.GetFloat("CityMorale_" + z, 50f);
            PriorityZone = PlayerPrefs.GetString("CityMorale_PriorityZone", "");
        }

        public float GetZoneMorale(string zone)
        {
            if (string.IsNullOrEmpty(zone) || !_morale.TryGetValue(zone, out var v)) return 50f;
            return v;
        }

        public MoraleTier GetZoneTier(string zone) => TierFor(GetZoneMorale(zone));

        public float GetCityMorale()
        {
            float total = 0f;
            int count = 0;
            foreach (var z in TrackedZones) { total += GetZoneMorale(z); count++; }
            return count > 0 ? total / count : 50f;
        }

        public MoraleTier GetCityMoraleTier() => TierFor(GetCityMorale());

        private static MoraleTier TierFor(float m)
        {
            if (m < 35f) return MoraleTier.Struggling;
            if (m < 70f) return MoraleTier.Steady;
            return MoraleTier.Thriving;
        }

        /// <summary>Called from the Governor's priority-choice line. Doesn't move
        /// morale by itself — it just marks where the bonus multiplier applies.</summary>
        public void SetPriorityZone(string zone)
        {
            PriorityZone = zone ?? "";
            PlayerPrefs.SetString("CityMorale_PriorityZone", PriorityZone);
            PlayerPrefs.Save();
            OnChanged?.Invoke();
        }

        /// <summary>Called from DialogueUIController.SelectChoice whenever the
        /// chosen DialogueChoice carries a non-zero moraleDelta. zone is the
        /// speaking NPC's own NPCIdentity.zone — i.e. "you did this while
        /// canvassing in that part of town."</summary>
        public void ApplyChoice(string zone, float baseDelta)
        {
            if (string.IsNullOrEmpty(zone)) zone = "residential";
            if (!_morale.ContainsKey(zone)) _morale[zone] = 50f;

            float multiplier = (!string.IsNullOrEmpty(PriorityZone) && PriorityZone == zone) ? priorityMultiplier : 1f;
            _morale[zone] = Mathf.Clamp(_morale[zone] + baseDelta * multiplier, 0f, 100f);
            PlayerPrefs.SetFloat("CityMorale_" + zone, _morale[zone]);
            PlayerPrefs.Save();
            OnChanged?.Invoke();
        }

        private static string TierLabel(MoraleTier t) => t switch
        {
            MoraleTier.Struggling => "Struggling — could use more visits",
            MoraleTier.Steady => "Steady — keep checking in",
            MoraleTier.Thriving => "Thriving — residents feel heard",
            _ => "Unknown"
        };

        public static string DisplayZoneName(string zone) => zone switch
        {
            "residential" => "Residential Streets",
            "commercial" => "Main Street / Commercial",
            "public_row" => "Transit & Public Row",
            "city_hall_plaza" => "City Hall Plaza",
            _ => zone
        };

        /// <summary>Live text for CityMoraleBoardTrigger's "Community Pulse" board —
        /// the diegetic, non-HUD way the player can check how canvassing is going.</summary>
        public string GetSummaryText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("City-wide standing: " + TierLabel(GetCityMoraleTier()) + $" ({Mathf.RoundToInt(GetCityMorale())}/100)");
            sb.AppendLine();
            foreach (var z in TrackedZones)
                sb.AppendLine(DisplayZoneName(z) + ": " + TierLabel(GetZoneTier(z)) + $" ({Mathf.RoundToInt(GetZoneMorale(z))}/100)");
            if (!string.IsNullOrEmpty(PriorityZone))
            {
                sb.AppendLine();
                sb.AppendLine("Current focus: " + DisplayZoneName(PriorityZone) + " — canvassing there counts extra toward morale.");
            }
            return sb.ToString();
        }
    }
}
