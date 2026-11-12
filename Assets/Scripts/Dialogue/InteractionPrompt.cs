using System.Collections;
using UnityEngine;
using CityHall.Core;
using CityHall.Systems;

namespace CityHall.Dialogue
{
    /// <summary>
    /// The "subtle interaction icon" that appears when the player approaches an
    /// interactable (brief: "When the player approaches an NPC: A subtle
    /// interaction icon appears"). Works for citizens, civic exhibits, and boards
    /// alike since it only depends on IInteractable.
    ///
    /// Put a small world-space Canvas + Image (a simple speech-bubble or "i" glyph)
    /// as a child of the interactable and assign it to `iconRoot`.
    ///
    /// NOTE: deliberately has no [RequireComponent(typeof(Collider))]. Collider is
    /// an abstract type, and Unity can't auto-instantiate an abstract type to
    /// satisfy a RequireComponent — on a GameObject with no existing concrete
    /// Collider (e.g. TownSceneBuilder's plain "Citizen_Base" root, whose only
    /// collider lives on its child "Body" primitive), AddComponent<InteractionPrompt>()
    /// would silently fail and return null. This component never reads Collider
    /// directly (proximity is Vector3.Distance-based in Update(), not trigger
    /// events), so the requirement was never actually needed.
    ///
    /// UI READABILITY OVERHAUL 2026-07-22 (brief: interaction prompts should be "large
    /// enough to read... animated with a gentle fade... never clipped by world geometry").
    /// Two fixes: (1) occlusion-proofed via UILayer (see that file) so the icon renders
    /// through walls/foliage/NPCs the same way DialogueUIController now does; (2) the old
    /// instant SetActive(true/false) pop is now a smooth scale animation. True alpha-fade
    /// on a 3D mesh icon (most iconRoots here are primitive spheres, not Canvas Images)
    /// would need a transparent-material/render-queue setup that can't be verified without
    /// the Editor to look at — scale reads the same to the player (smoothly appears/
    /// disappears rather than popping) without that risk.
    /// </summary>
    public class InteractionPrompt : MonoBehaviour
    {
        public Transform iconRoot;
        public float showDistance = 3.5f;
        public float checkInterval = 0.2f;
        public string playerTag = "Player";
        public float popDuration = 0.15f;

        private Transform _player;
        private IInteractable _interactable;
        private float _nextCheck;
        private bool _visible;
        private bool _iconInitialized;
        private Vector3 _iconTargetScale;
        private Coroutine _popRoutine;

        private void Awake()
        {
            // Unity 2020.1+ resolves GetComponent<T> against interface types directly,
            // so this finds whichever concrete component (DialogueTrigger,
            // CivicExhibitTrigger, ...) implements IInteractable on this GameObject.
            // NOTE: NPCSpawner instantiates citizen prefabs and adds DialogueTrigger
            // *after* Instantiate() returns — which means Awake() here can run before
            // that AddComponent call, and GetComponent<IInteractable>() would come up
            // empty even though a DialogueTrigger is about to exist. Update() below
            // re-attempts the lookup each throttled tick until it succeeds, so spawn
            // order doesn't matter; ambient NPCs with no IInteractable simply never
            // resolve one, which is the correct "no prompt" behavior for them.
            _interactable = GetComponent<IInteractable>();
            // iconRoot itself can also arrive after Awake() (same spawn-order reasoning as
            // above, e.g. when a caller does AddComponent<InteractionPrompt>() then assigns
            // iconRoot on the next line) — deferred to Update()'s retry loop below rather
            // than handled here, so both orderings work.
        }

        private void Update()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + checkInterval;

            if (_interactable == null)
                _interactable = GetComponent<IInteractable>();

            if (!_iconInitialized && iconRoot != null)
            {
                _iconTargetScale = iconRoot.localScale.sqrMagnitude > 0.0001f ? iconRoot.localScale : Vector3.one;
                iconRoot.localScale = Vector3.zero;
                iconRoot.gameObject.SetActive(false);
                UILayer.ApplyRecursively(iconRoot.gameObject);
                _iconInitialized = true;
            }

            if (_player == null)
            {
                var go = GameObject.FindGameObjectWithTag(playerTag);
                if (go != null) _player = go.transform;
                else return;
            }

            bool shouldShow = _interactable != null
                               && _interactable.CanInteract
                               && Vector3.Distance(_player.position, transform.position) <= showDistance;

            if (shouldShow != _visible)
            {
                _visible = shouldShow;
                if (iconRoot != null && _iconInitialized)
                {
                    if (_popRoutine != null) StopCoroutine(_popRoutine);
                    _popRoutine = StartCoroutine(PopRoutine(_visible));
                }
            }

            if (_visible && iconRoot != null && Camera.main != null)
                iconRoot.rotation = Quaternion.LookRotation(iconRoot.position - Camera.main.transform.position);
        }

        private IEnumerator PopRoutine(bool show)
        {
            if (show) iconRoot.gameObject.SetActive(true);
            Vector3 from = iconRoot.localScale;
            Vector3 to = show ? _iconTargetScale : Vector3.zero;
            float t = 0f;
            while (t < popDuration)
            {
                t += Time.deltaTime;
                iconRoot.localScale = Vector3.Lerp(from, to, t / popDuration);
                yield return null;
            }
            iconRoot.localScale = to;
            if (!show) iconRoot.gameObject.SetActive(false);
        }
    }
}
