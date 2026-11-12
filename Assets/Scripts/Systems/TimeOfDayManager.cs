using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityHall.Systems
{
    public enum DayPeriod { Morning, LateMorning, Afternoon, Evening, Night }

    /// <summary>
    /// Single source of truth for "what time of day is it" — drives sun angle,
    /// light color/intensity, skybox, streetlight emissives, and fires
    /// OnPeriodChanged so NPCRoutineController and AmbientAudioZone react.
    ///
    /// Two modes:
    ///  - Auto-cycle: dayLengthMinutes real-world minutes = one full day, for
    ///    passive ambience (lights "gradually illuminate during evening" per brief).
    ///  - Manual: call SetPeriod() directly, e.g. from a debug/dev menu, to jump
    ///    straight to a period for testing a specific scene without waiting.
    /// </summary>
    public class TimeOfDayManager : MonoBehaviour
    {
        public static TimeOfDayManager Instance { get; private set; }

        [Header("Cycle")]
        [Tooltip("Real-world minutes for one full Morning->Evening->Morning cycle. Set to 0 to disable auto-cycling and drive it manually (e.g. from a debug menu) instead.")]
        public float dayLengthMinutes = 12f;
        [Range(0f, 1f)] public float startNormalizedTime = 0.3f; // start mid-morning

        [Header("Sun")]
        public Light sunLight;
        public Gradient sunColorOverDay;
        // 2026-07-23, ROUND 3: replaced with real day/night keyframes now that Apply() sweeps
        // the full 360 degrees again (see the comment there). The old 2-key EaseInOut(0.2->1.0)
        // curve was authored for the no-true-night 180-degree design, where t=0/1 were
        // sunrise/sunset, not midnight — under the restored 360-degree sweep it would have made
        // midnight the BRIGHTEST point of the cycle. Keyed at the same t positions as
        // sunColorOverDay's gradient (0/0.25/0.55/0.82/1) for consistency, with a 0.08 floor at
        // the night keys instead of 0 — a deliberate "moonlight" minimum so night reads as
        // dim-and-blue rather than the total blackness the user's earlier "sky is dark" report
        // was about.
        public AnimationCurve sunIntensityOverDay = new AnimationCurve(
            new Keyframe(0f, 0.08f),
            new Keyframe(0.25f, 0.55f),
            new Keyframe(0.55f, 1f),
            new Keyframe(0.82f, 0.35f),
            new Keyframe(1f, 0.08f)
        );
        public Vector3 sunRotationAxis = Vector3.right;

        [Header("Sky / ambient")]
        public Material daySkybox;
        public Material eveningSkybox;
        public Light ambientFillLight;

        [Header("Street furniture")]
        [Tooltip("MeshRenderers using an emissive material for streetlamp bulbs, window glow, etc.")]
        public List<Renderer> emissiveAtNight = new List<Renderer>();
        public Color emissiveOnColor = new Color(1f, 0.85f, 0.5f) * 2f;
        [Range(0f, 1f)] public float eveningThreshold = 0.78f;

        public event Action<DayPeriod> OnPeriodChanged;
        public DayPeriod CurrentPeriod { get; private set; } = DayPeriod.Morning;

        private float _normalizedTime;
        private bool _nightLightsOn;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _normalizedTime = startNormalizedTime;
        }

        private void Start()
        {
            Apply(_normalizedTime);
            BroadcastPeriod(PeriodFor(_normalizedTime), force: true);
        }

        private void Update()
        {
            if (dayLengthMinutes <= 0f) return;
            _normalizedTime += Time.deltaTime / (dayLengthMinutes * 60f);
            if (_normalizedTime > 1f) _normalizedTime -= 1f;
            Apply(_normalizedTime);
            BroadcastPeriod(PeriodFor(_normalizedTime));
        }

        // 2026-07-23, ROUND 3: where Night gives way to Morning (t wraps 1 -> 0 through here).
        // Shared by PeriodFor() and Apply()'s night-lights toggle so both agree on the boundary.
        private const float kNightEndThreshold = 0.15f;

        /// <summary>Jump directly to a period — used by a debug menu or by design
        /// during playtesting to inspect one slice of the day without waiting.</summary>
        public void SetPeriod(DayPeriod period)
        {
            dayLengthMinutes = 0f; // stop auto-cycling once manually driven
            _normalizedTime = period switch
            {
                DayPeriod.Night => 0.95f,
                DayPeriod.Morning => 0.20f,
                DayPeriod.LateMorning => 0.35f,
                DayPeriod.Afternoon => 0.60f,
                DayPeriod.Evening => 0.82f,
                _ => 0.95f
            };
            Apply(_normalizedTime);
            BroadcastPeriod(period, force: true);
        }

        private void Apply(float t)
        {
            if (sunLight != null)
            {
                // 2026-07-23, ROUND 3 (user explicitly asked for "a day and night cycle" right
                // after confirming everything else looked good): the 180-degree sweep below
                // (superseded) was a deliberate fix for an earlier "sky is dark" report, but it
                // also meant the sun never dipped below the horizon — no true night at all.
                // User confirmed they want real night back, with the darkness re-tuned so it
                // doesn't repeat that earlier complaint. Two changes do that together: (1) the
                // full 360-degree sweep is restored here (t=0/1 = midnight, 0.25 = sunrise,
                // 0.55 = noon, 0.82 = sunset — sunColorOverDay's gradient keys were already
                // authored for exactly this mapping and never had to change), and (2) the
                // sunIntensityOverDay curve (see its field declaration above) now has a 0.08
                // "moonlight" floor at the night keys instead of fading toward true black, so
                // night reads as dim-and-blue rather than nothing-visible.
                sunLight.transform.rotation = Quaternion.Euler(sunRotationAxis * (t * 360f - 90f));
                sunLight.color = sunColorOverDay.Evaluate(t);
                sunLight.intensity = sunIntensityOverDay.Evaluate(t);
            }

            // Night lights (streetlamps/window glow/skybox swap) now cover the full dusk-through-
            // dawn span rather than just "evening" — wraps around t=0 the same way the night
            // trough in sunColorOverDay/sunIntensityOverDay does, using the same kNightEndThreshold
            // PeriodFor() uses so both are in agreement about when night actually ends.
            bool nightLightsShouldBeOn = t >= eveningThreshold || t < kNightEndThreshold;
            if (nightLightsShouldBeOn != _nightLightsOn)
            {
                _nightLightsOn = nightLightsShouldBeOn;
                foreach (var r in emissiveAtNight)
                {
                    if (r == null) continue;
                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);
                    mpb.SetColor("_EmissionColor", nightLightsShouldBeOn ? emissiveOnColor : Color.black);
                    r.SetPropertyBlock(mpb);
                }
                if (ambientFillLight != null)
                    // Bumped 0.15->0.2 alongside the sun's new moonlight floor — this is the
                    // main non-sun light source once real night returns, so it now carries more
                    // of the "don't go fully black" job than it did under the old 180-sweep
                    // design, which never actually needed it to cover true darkness.
                    ambientFillLight.intensity = nightLightsShouldBeOn ? 0.2f : 0.4f;
                RenderSettings.skybox = nightLightsShouldBeOn ? eveningSkybox : daySkybox;
            }
        }

        private DayPeriod PeriodFor(float t)
        {
            if (t < kNightEndThreshold) return DayPeriod.Night;
            if (t < 0.30f) return DayPeriod.Morning;
            if (t < 0.45f) return DayPeriod.LateMorning;
            if (t < eveningThreshold) return DayPeriod.Afternoon;
            if (t < 0.88f) return DayPeriod.Evening;
            return DayPeriod.Night;
        }

        private void BroadcastPeriod(DayPeriod period, bool force = false)
        {
            if (!force && period == CurrentPeriod) return;
            CurrentPeriod = period;
            OnPeriodChanged?.Invoke(period);
        }
    }
}
