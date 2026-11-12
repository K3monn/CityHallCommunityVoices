using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityHall.Dialogue;
using CityHall.NPC;

namespace CityHall.Systems
{
    /// <summary>
    /// Small ambient speech-bubble for background NPC chatter — short flavor snippets shown
    /// occasionally while an NPC just goes about its routine, distinct from the full
    /// DialogueUIController conversation window that only appears when the player actually
    /// interacts. Part of the 2026-07-22 UI brief's "NPC Speech Bubbles" section: "small
    /// speech bubbles instead of full dialogue windows... face the camera... fade in and
    /// out naturally... show only short snippets."
    ///
    /// Builds its own tiny world-space canvas at Awake() rather than depending on
    /// TownSceneBuilder to hand-construct one per spawned NPC instance (there can be dozens
    /// across the roster) — same reasoning as CrosshairUI/PerformanceLODManager being
    /// self-contained, just applied to a per-instance component instead of a scene-wide
    /// singleton. NPCSpawner adds this to every spawned citizen.
    ///
    /// Occlusion-proofed via UILayer (see that file's doc comment) and hidden entirely
    /// whenever DialogueUIController has a real conversation on screen, so ambient chatter
    /// never competes with an actual interaction.
    /// </summary>
    public class NPCSpeechBubble : MonoBehaviour
    {
        public string[] linePool =
        {
            "Nice day for a walk.",
            "Have you seen the new bulletin board?",
            "I really should get to that meeting.",
            "Lovely weather we're having.",
            "I keep meaning to check out that new exhibit.",
            "Busy day around here today.",
        };

        [Header("Morale-aware flavor lines (2026-07-22 governor/canvassing decision system)")]
        [Tooltip("Used instead of linePool when this NPC's zone (NPCIdentity.zone) is Thriving in CityMoraleSystem.")]
        public string[] thrivingLines =
        {
            "Someone from the city actually checked in on us last week.",
            "It's nice knowing canvassers still come around, even when there's no election.",
            "Feels like the city's actually listening these days.",
        };
        [Tooltip("Used instead of linePool when this NPC's zone is Struggling in CityMoraleSystem.")]
        public string[] strugglingLines =
        {
            "Haven't seen anyone from the city in a while.",
            "Feels like nobody checks in unless it's election season.",
            "Wish someone would just ask how things are actually going around here.",
        };
        public float minInterval = 16f;
        public float maxInterval = 34f;
        public float showSeconds = 3.5f;
        public float showDistance = 9f;
        public Vector3 worldOffset = new Vector3(0f, 1.95f, 0f);

        private CanvasGroup _canvasGroup;
        private TMP_Text _label;
        private Transform _canvasTransform;
        private Transform _player;
        private Coroutine _loopRoutine;
        private NPCIdentity _identity;

        private void Awake()
        {
            BuildBubble();
            UILayer.ApplyRecursively(_canvasTransform.gameObject);
            _identity = GetComponent<NPCIdentity>();
        }

        /// <summary>2026-07-22 governor/canvassing decision system: picks the tiered pool matching
        /// this NPC's own zone morale, falling back to the original neutral linePool for
        /// Steady morale, a missing CityMoraleSystem, or an NPC with no zone set.</summary>
        private string[] CurrentPool()
        {
            if (CityMoraleSystem.Instance == null || _identity == null || string.IsNullOrEmpty(_identity.zone))
                return linePool;

            return CityMoraleSystem.Instance.GetZoneTier(_identity.zone) switch
            {
                CityMoraleSystem.MoraleTier.Thriving when thrivingLines != null && thrivingLines.Length > 0 => thrivingLines,
                CityMoraleSystem.MoraleTier.Struggling when strugglingLines != null && strugglingLines.Length > 0 => strugglingLines,
                _ => linePool
            };
        }

        private void OnEnable()
        {
            _loopRoutine = StartCoroutine(ChatterLoop());
        }

        private void OnDisable()
        {
            if (_loopRoutine != null) StopCoroutine(_loopRoutine);
        }

        private void BuildBubble()
        {
            var canvasGO = new GameObject("SpeechBubble_Canvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            _canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(320f, 90f);
            canvasGO.transform.localScale = Vector3.one * 0.0016f;
            _canvasTransform = canvasGO.transform;

            var bg = new GameObject("Bg");
            bg.transform.SetParent(canvasGO.transform, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.08f, 0.08f, 0.09f, 0.72f);
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;

            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(canvasGO.transform, false);
            _label = labelGO.AddComponent<TextMeshProUGUI>();
            _label.fontSize = 20f;
            _label.color = Color.white;
            _label.alignment = TextAlignmentOptions.Center;
            _label.enableWordWrapping = true;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero; labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = new Vector2(14f, 10f); labelRT.offsetMax = new Vector2(-14f, -10f);

            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;
        }

        private IEnumerator ChatterLoop()
        {
            // Stagger the very first line per-instance so a crowd of NPCs doesn't all speak
            // in sync the moment the scene loads.
            yield return new WaitForSeconds(Random.Range(0f, maxInterval));

            while (true)
            {
                yield return new WaitForSeconds(Random.Range(minInterval, maxInterval));

                var pool = CurrentPool();
                if (pool == null || pool.Length == 0) continue;
                if (DialogueUIController.Instance != null && DialogueUIController.Instance.IsPlaying) continue;
                if (!WithinPlayerRange()) continue;

                _label.text = pool[Random.Range(0, pool.Length)];
                yield return FadeTo(1f);
                yield return new WaitForSeconds(showSeconds);
                yield return FadeTo(0f);
            }
        }

        private bool WithinPlayerRange()
        {
            if (_player == null)
            {
                var go = GameObject.FindGameObjectWithTag("Player");
                if (go != null) _player = go.transform;
            }
            return _player != null && Vector3.Distance(_player.position, transform.position) <= showDistance;
        }

        private void LateUpdate()
        {
            if (_canvasTransform == null) return;
            _canvasTransform.position = transform.position + worldOffset;
            if (Camera.main != null)
                _canvasTransform.rotation = Quaternion.LookRotation(_canvasTransform.position - Camera.main.transform.position);
        }

        private IEnumerator FadeTo(float target)
        {
            float duration = target > 0f ? 0.3f : 0.4f;
            // If a real conversation started while this bubble was about to fade in, bail
            // rather than popping an ambient snippet on top of it.
            if (target > 0f && DialogueUIController.Instance != null && DialogueUIController.Instance.IsPlaying) yield break;
            float start = _canvasGroup.alpha;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(start, target, t / duration);
                yield return null;
            }
            _canvasGroup.alpha = target;
        }
    }
}
