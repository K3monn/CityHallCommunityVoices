using UnityEngine;
using TMPro;
using CityHall.Systems;

namespace CityHall.Environment
{
    /// <summary>
    /// Physical animated bar-chart for the Community Issues Board wing (2026-07-23 Town Hall
    /// interior redesign — "Community Issues Board as an animated-bar data gallery"). Reads
    /// CityMoraleSystem's live per-zone morale numbers every frame and grows/shrinks a set of
    /// physical 3D bars to match, updating a text label on each with the current number.
    ///
    /// Deliberately plain 3D boxes scaled via Transform, NOT a UI Canvas/Image.fillAmount setup —
    /// this project's earlier dialogue-choice-panel bug (see project notes: rows on a separate
    /// world-space Canvas were invisible for reasons static analysis couldn't pin down) was
    /// specifically a Canvas-rendering problem, so a bar chart that doesn't need Canvas UI at all
    /// sidesteps that entire class of risk. CityMoraleSystem already existed and was flagged as
    /// "a candidate to eventually feed the Community Issues Board wing" back when it was built for
    /// the governor/canvassing system — this is that follow-through, no new data plumbing needed.
    /// </summary>
    public class MoraleBarChartDisplay : MonoBehaviour
    {
        [System.Serializable]
        public class Bar
        {
            [Tooltip("One of CityMoraleSystem's tracked zone strings (\"residential\", \"commercial\", \"public_row\", \"city_hall_plaza\"), or empty for the city-wide average bar.")]
            public string zone;
            [Tooltip("The growing/shrinking cube itself. Its parent transform's local origin must sit exactly at the bar's ground/base point — this script drives both localScale.y and localPosition.y together every frame so the bar always grows upward from that fixed base rather than from its own center.")]
            public Transform fillTransform;
            public TMP_Text label;
            [Tooltip("World-space bar height, in local units, that corresponds to 100 morale.")]
            public float maxHeight = 3f;
        }

        public Bar[] bars;
        public float smoothing = 3f;

        private void Update()
        {
            if (bars == null || CityMoraleSystem.Instance == null) return;

            foreach (var bar in bars)
            {
                if (bar == null || bar.fillTransform == null) continue;

                float morale = string.IsNullOrEmpty(bar.zone)
                    ? CityMoraleSystem.Instance.GetCityMorale()
                    : CityMoraleSystem.Instance.GetZoneMorale(bar.zone);

                float targetHeight = Mathf.Max(0.05f, bar.maxHeight * Mathf.Clamp01(morale / 100f));

                var scale = bar.fillTransform.localScale;
                float newHeight = Mathf.Lerp(scale.y, targetHeight, smoothing * Time.deltaTime);
                scale.y = newHeight;
                bar.fillTransform.localScale = scale;

                // Keeps the bar's bottom edge pinned to its parent's local origin (the ground/base
                // point) while only the top edge moves — a plain uniform-pivot scale would instead
                // grow the box equally in both directions from its own center, sinking half of it
                // into the floor.
                var pos = bar.fillTransform.localPosition;
                pos.y = newHeight / 2f;
                bar.fillTransform.localPosition = pos;

                if (bar.label != null)
                {
                    string displayName = string.IsNullOrEmpty(bar.zone) ? "City-Wide" : CityMoraleSystem.DisplayZoneName(bar.zone);
                    bar.label.text = displayName + "\n" + Mathf.RoundToInt(morale);
                }
            }
        }
    }
}
