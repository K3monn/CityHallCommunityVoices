using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using CityHall.Systems;

namespace CityHall.Environment
{
    /// <summary>
    /// Museum-style static info panel used for civic exhibits ("How does a city
    /// budget work?") and environmental storytelling boards (bulletin boards, bus
    /// stop posters, the Register to Vote board). Distinct from DialogueUIController
    /// because this content is read at the player's own pace rather than
    /// timed/sequenced — it stays open until the player dismisses it.
    ///
    /// UI READABILITY OVERHAUL 2026-07-22: same occlusion-proofing (UILayer + the depth-
    /// clear overlay camera) and live accessibility hooks (font size / UI scale / high
    /// contrast via AccessibilitySettings) as DialogueUIController — see those files. Smart
    /// positioning here is simpler than the conversation window (exhibit panels are read at
    /// a fixed kiosk, not beside a moving NPC) but still supports an optional subject bias
    /// for parity and any future caller that wants it.
    /// </summary>
    public class InfoPanelController : MonoBehaviour
    {
        public static InfoPanelController Instance { get; private set; }

        public CanvasGroup canvasGroup;
        public TMP_Text kickerLabel;   // small "Civic Exhibit" / "Environmental Storytelling" category text
        public TMP_Text titleLabel;
        public TMP_Text bodyLabel;
        public Image background;

        public Transform playerCamera;
        public Vector3 offsetFromCamera = new Vector3(0f, 0f, 1.4f);
        public float maxHorizontalBias = 0.5f;

        private Transform _subject;
        private InputAction _dismissAction;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            UILayer.ApplyRecursively(gameObject);
            _dismissAction = new InputAction("DismissInfoPanel", InputActionType.Button, "<XRController>/secondaryButton");
            SetVisible(false);
        }

        private void OnEnable() => _dismissAction?.Enable();

        private void OnDisable() => _dismissAction?.Disable();

        private void Update()
        {
            if (canvasGroup == null || canvasGroup.alpha <= 0.01f) return;
            bool dismiss = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame)
                || (_dismissAction != null && _dismissAction.WasPressedThisFrame());
            if (dismiss) Hide();
        }

        private void Start()
        {
            // Deferred from Awake() — see DialogueUIController.Start()'s doc comment for why
            // reading another object's singleton Instance is only safe here, not in Awake().
            if (AccessibilitySettings.Instance != null)
            {
                AccessibilitySettings.Instance.OnChanged += ApplyAccessibility;
                ApplyAccessibility();
            }
        }

        private void OnDestroy()
        {
            _dismissAction?.Dispose();
            if (AccessibilitySettings.Instance != null)
                AccessibilitySettings.Instance.OnChanged -= ApplyAccessibility;
        }

        public void Show(string kicker, string title, string body, Transform subject = null)
        {
            if (kickerLabel != null) kickerLabel.text = kicker + "   •   B / ESC TO CLOSE";
            if (titleLabel != null) titleLabel.text = title;
            if (bodyLabel != null) bodyLabel.text = body;
            _subject = subject;

            Reposition();
            SetVisible(true);
        }

        public void Hide() => SetVisible(false);

        private void Reposition()
        {
            if (playerCamera == null) return;
            Vector3 desiredOffset = offsetFromCamera;
            if (_subject != null)
            {
                Vector3 toSubjectLocal = playerCamera.InverseTransformPoint(_subject.position + Vector3.up * 1.2f);
                desiredOffset.x = Mathf.Clamp(toSubjectLocal.x, -maxHorizontalBias, maxHorizontalBias);
            }
            transform.position = playerCamera.TransformPoint(desiredOffset);
            transform.rotation = Quaternion.LookRotation(transform.position - playerCamera.position);
        }

        private void ApplyAccessibility()
        {
            var settings = AccessibilitySettings.Instance;
            if (settings == null) return;

            transform.localScale = Vector3.one * 0.0018f * settings.uiScale;
            if (kickerLabel != null) kickerLabel.fontSize = 20f * settings.fontSizeMultiplier;
            if (titleLabel != null) titleLabel.fontSize = 36f * settings.fontSizeMultiplier;
            if (bodyLabel != null) bodyLabel.fontSize = 26f * settings.fontSizeMultiplier;

            if (background != null)
                background.color = settings.highContrast ? new Color(1f, 1f, 1f, 1f) : new Color(0.98f, 0.97f, 0.95f, 0.92f);
            if (bodyLabel != null)
                bodyLabel.color = settings.highContrast ? Color.black : new Color(0.14f, 0.14f, 0.11f);
        }

        private void SetVisible(bool visible)
        {
            if (canvasGroup == null) return;
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.blocksRaycasts = visible;
            canvasGroup.interactable = visible;
        }
    }
}
