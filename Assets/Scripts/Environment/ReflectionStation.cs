using UnityEngine;
using CityHall.Core;
using CityHall.Systems;

namespace CityHall.Environment
{
    /// <summary>
    /// One reflection question station inside the Reflection Wall wing (2026-07-23 Town Hall
    /// interior redesign — "Reflection Wall as a quiet museum-style room with per-question
    /// stations"). Reading the question (via InfoPanelController, same read-at-your-own-pace
    /// pattern as any civic exhibit) marks it reflected-on, persisted to PlayerPrefs so it's
    /// still marked on a later run too — matches this project's established "real and testable
    /// today, not yet tied to a teacher-facing backend" stance for the whole Town Hall redesign
    /// (see project notes' scoping discussion). A real multi-student/teacher-facing save system
    /// would need a backend this scaffold doesn't have; PlayerPrefs is the honest local stand-in.
    ///
    /// Uses AccessibilitySettings.PositiveColor/NeutralColor (blue/gray, not green/red) for the
    /// checkmark state — the exact colorblind-safe pair AccessibilitySettings' own doc comment
    /// predicted this feature would be the first real user of, back when it was purely
    /// forward-looking.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class ReflectionStation : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public string questionId;
        [TextArea] public string questionText;
        [Tooltip("Small marker (sphere/disc) recolored on mark/unmark. Must use its own Material instance, not a shared cached one — see ApplyIconColor's comment.")]
        public MeshRenderer checkIcon;
        public Vector3 promptAnchorOffset = new Vector3(0f, 1.6f, 0f);

        private bool _marked;
        private string PrefsKey => "ReflectionStation_" + questionId;

        public bool CanInteract => InfoPanelController.Instance != null;
        public string PromptText => "Reflect on this question";
        public Vector3 PromptAnchor => transform.position + promptAnchorOffset;

        private void Awake()
        {
            _marked = !string.IsNullOrEmpty(questionId) && PlayerPrefs.GetInt(PrefsKey, 0) == 1;
            ApplyIconColor();
        }

        public void Interact(Transform interactor)
        {
            if (InfoPanelController.Instance == null) return;
            InfoPanelController.Instance.Show(
                "Reflection",
                questionText,
                "Take a moment with this one before moving on. Interacting again won't un-mark it — the Reflection Wall tracks what you've sat with, it isn't meant to be reset.");

            if (!_marked && !string.IsNullOrEmpty(questionId))
            {
                _marked = true;
                PlayerPrefs.SetInt(PrefsKey, 1);
                PlayerPrefs.Save();
                ApplyIconColor();
            }
        }

        private void ApplyIconColor()
        {
            if (checkIcon == null) return;
            Color c = AccessibilitySettings.Instance != null
                ? (_marked ? AccessibilitySettings.Instance.PositiveColor : AccessibilitySettings.Instance.NeutralColor)
                : (_marked ? new Color(0.2f, 0.55f, 0.85f) : new Color(0.55f, 0.55f, 0.58f));
            // Renderer.material (NOT sharedMaterial) auto-clones a per-instance copy the first
            // time it's accessed — TownSceneBuilder's GetMaterial(color) cache reuses one Material
            // object across every GameObject asking for the same color, so mutating .sharedMaterial
            // here would silently recolor every other prop in the scene using that same cached
            // material. .material sidesteps that entirely.
            checkIcon.material.color = c;
        }
    }
}
