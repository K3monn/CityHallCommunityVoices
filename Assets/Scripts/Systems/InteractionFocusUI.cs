using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CityHall.Systems
{
    /// <summary>Displays one small, control-accurate hint for the interactable under the reticle.</summary>
    [RequireComponent(typeof(PlayerInteractor))]
    public sealed class InteractionFocusUI : MonoBehaviour
    {
        public string controlHint = "CLICK / SPACE / A / TAP";

        private PlayerInteractor _interactor;
        private CanvasGroup _group;
        private TMP_Text _label;
        private string _lastText;

        private void Awake()
        {
            _interactor = GetComponent<PlayerInteractor>();

            var canvasObject = new GameObject("InteractionHint_Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
            canvasObject.transform.SetParent(transform, false);
            canvasObject.transform.localPosition = new Vector3(0f, -0.42f, 1.35f);
            canvasObject.transform.localRotation = Quaternion.identity;
            canvasObject.transform.localScale = Vector3.one * 0.001f;

            var rect = canvasObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(720f, 116f);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = GetComponent<Camera>() != null ? GetComponent<Camera>() : Camera.main;
            canvas.sortingOrder = 100;
            canvasObject.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
            _group = canvasObject.GetComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            var background = new GameObject("HintBackground", typeof(RectTransform), typeof(Image));
            background.transform.SetParent(canvasObject.transform, false);
            var bgRect = background.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            background.GetComponent<Image>().color = new Color(0.07f, 0.16f, 0.19f, 0.92f);

            var textObject = new GameObject("HintText", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(background.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(22f, 10f);
            textRect.offsetMax = new Vector2(-22f, -10f);
            _label = textObject.GetComponent<TextMeshProUGUI>();
            _label.fontSize = 34f;
            _label.color = Color.white;
            _label.alignment = TextAlignmentOptions.Center;
            _label.enableWordWrapping = false;
            _label.overflowMode = TextOverflowModes.Ellipsis;
        }

        private void Update()
        {
            bool focused = _interactor != null && _interactor.CurrentTarget != null && _interactor.CurrentTarget.CanInteract;
            float targetAlpha = focused ? 1f : 0f;
            _group.alpha = Mathf.MoveTowards(_group.alpha, targetAlpha, Time.deltaTime * 6f);
            if (!focused) return;

            string message = _interactor.CurrentPromptText + "   •   " + controlHint;
            if (message == _lastText) return;
            _lastText = message;
            _label.text = message;
        }
    }
}
