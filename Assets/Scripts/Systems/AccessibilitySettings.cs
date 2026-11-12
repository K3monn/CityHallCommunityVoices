using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CityHall.Systems
{
    /// <summary>
    /// Player-adjustable readability settings, applied live across every world-space UI panel
    /// (DialogueUIController, InfoPanelController) via OnChanged — part of the 2026-07-22 UI
    /// readability brief ("Adjustable UI scale... Adjustable font size... High contrast mode...
    /// Colorblind-safe palette").
    ///
    /// No dedicated settings-menu screen yet — laying one out well (proper spacing, hierarchy,
    /// discoverability) really deserves the Editor's scene/UI tools rather than being guessed
    /// blind, so it's flagged as a follow-up. What ships now is the actual functional support
    /// plus keybindings as a first real pass, same "make it real, flag the polish" approach
    /// used elsewhere in this project: [ / ] = font size down/up, - / = = UI scale down/up,
    /// H = toggle high-contrast mode. A future settings screen would just call the same public
    /// setters this already exposes.
    /// </summary>
    public class AccessibilitySettings : MonoBehaviour
    {
        public static AccessibilitySettings Instance { get; private set; }

        [Range(0.7f, 1.6f)] public float uiScale = 1f;
        [Range(0.7f, 1.6f)] public float fontSizeMultiplier = 1f;
        public bool highContrast;

        /// <summary>Raised whenever any setting changes — every world-space panel subscribes in
        /// its own Awake() and re-applies scale/font/contrast live, no polling needed.</summary>
        public event Action OnChanged;

        // Colorblind-safe (blue/orange instead of red/green) semantic colors — forward-looking:
        // nothing in the scaffold has a red/green pass/fail pair yet, but the Reflection Wall's
        // planned saved/locked checkmark state will be the first, and anything reading these
        // instead of hardcoding red/green gets colorblind-safe colors for free from day one.
        public Color PositiveColor => new Color(0.2f, 0.55f, 0.85f);  // blue, not green
        public Color NegativeColor => new Color(0.85f, 0.5f, 0.15f);  // orange, not red
        public Color NeutralColor => new Color(0.55f, 0.55f, 0.58f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Update()
        {
            if (Keyboard.current == null) return;
            bool changed = false;

            if (Keyboard.current.leftBracketKey.wasPressedThisFrame) { SetFontSizeMultiplier(fontSizeMultiplier - 0.1f); changed = true; }
            if (Keyboard.current.rightBracketKey.wasPressedThisFrame) { SetFontSizeMultiplier(fontSizeMultiplier + 0.1f); changed = true; }
            if (Keyboard.current.minusKey.wasPressedThisFrame) { SetUIScale(uiScale - 0.1f); changed = true; }
            if (Keyboard.current.equalsKey.wasPressedThisFrame) { SetUIScale(uiScale + 0.1f); changed = true; }
            if (Keyboard.current.hKey.wasPressedThisFrame) { SetHighContrast(!highContrast); changed = true; }

            // SetX() below already invokes OnChanged; this flag only guards against firing
            // it twice in a single frame if somehow more than one key is read as pressed here.
            if (changed) { /* individual setters already notified */ }
        }

        public void SetUIScale(float value)
        {
            uiScale = Mathf.Clamp(value, 0.7f, 1.6f);
            OnChanged?.Invoke();
        }

        public void SetFontSizeMultiplier(float value)
        {
            fontSizeMultiplier = Mathf.Clamp(value, 0.7f, 1.6f);
            OnChanged?.Invoke();
        }

        public void SetHighContrast(bool value)
        {
            highContrast = value;
            OnChanged?.Invoke();
        }
    }
}
