using UnityEngine;

namespace CityHall.Systems
{
    /// <summary>
    /// Diegetic reflection of city-wide morale (2026-07-22 governor/canvassing
    /// decision system). Per the design brief, morale has no on-screen meter, so
    /// this is one of the ways it's still legible in the world: the overall
    /// ambient light warmth of the town very subtly rises when canvassing is
    /// going well and dims when it isn't, alongside NPCSpeechBubble's tiered
    /// ambient chatter and the Governor's own reaction.
    ///
    /// Nudges RenderSettings.ambientIntensity — a global multiplier
    /// TimeOfDayManager never touches — rather than fighting over a Light
    /// component's own intensity, which TimeOfDayManager already owns every
    /// frame (see its Apply() method). [DefaultExecutionOrder(50)] just keeps
    /// this comfortably after TimeOfDayManager's default-order Update, though
    /// the two shouldn't actually collide either way since they touch different
    /// lighting knobs.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class MoraleAmbientReflector : MonoBehaviour
    {
        public float smoothing = 0.6f;
        public float thrivingIntensity = 1.15f;
        public float steadyIntensity = 1.0f;
        public float strugglingIntensity = 0.85f;

        private float _target = 1.0f;
        private float _current = 1.0f;

        private void Start()
        {
            _current = RenderSettings.ambientIntensity > 0f ? RenderSettings.ambientIntensity : 1f;
            _target = _current;
            if (CityMoraleSystem.Instance != null)
            {
                CityMoraleSystem.Instance.OnChanged += RecalculateTarget;
                RecalculateTarget();
            }
        }

        private void OnDestroy()
        {
            if (CityMoraleSystem.Instance != null)
                CityMoraleSystem.Instance.OnChanged -= RecalculateTarget;
        }

        private void RecalculateTarget()
        {
            if (CityMoraleSystem.Instance == null) return;
            _target = CityMoraleSystem.Instance.GetCityMoraleTier() switch
            {
                CityMoraleSystem.MoraleTier.Thriving => thrivingIntensity,
                CityMoraleSystem.MoraleTier.Struggling => strugglingIntensity,
                _ => steadyIntensity
            };
        }

        private void Update()
        {
            _current = Mathf.Lerp(_current, _target, smoothing * Time.deltaTime);
            RenderSettings.ambientIntensity = _current;
        }
    }
}
