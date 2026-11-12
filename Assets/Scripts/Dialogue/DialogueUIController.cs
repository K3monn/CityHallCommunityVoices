using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using CityHall.NPC;
using CityHall.Systems;
using CityHall.Environment;

namespace CityHall.Dialogue
{
    /// <summary>
    /// Plays a ConversationEntry as sequenced subtitles (optionally with VO audio
    /// per line). Deliberately NOT a persistent HUD element — per the brief
    /// ("No HUD, no minimap, no quest markers"), this panel is invisible until a
    /// conversation starts and disappears immediately after, so it reads as a
    /// contextual subtitle rather than a UI chrome element.
    ///
    /// Attach to a world-space Canvas that a script (see below) keeps positioned
    /// a comfortable reading distance in front of the player's head, billboarded
    /// to face them — the standard VR subtitle pattern.
    ///
    /// UI READABILITY OVERHAUL 2026-07-22 (user brief: "dialogue boxes... frequently
    /// become partially or completely hidden behind 3D objects"). Three independent
    /// fixes layered together:
    ///  1. Occlusion: this canvas now lives on UILayer's dedicated layer, rendered by a
    ///     depth-cleared overlay camera (see TownSceneBuilder.BuildOcclusionProofUICamera)
    ///     that paints on top of world geometry unconditionally, regardless of what's
    ///     physically in front of it — the actual fix for "hidden behind objects."
    ///  2. Smart positioning: still camera-relative (which already keeps it in front of the
    ///     player, unlike anchoring to the NPC's own world position), but now also biases
    ///     horizontally toward whichever side the speaking NPC is actually standing on
    ///     ("beside the NPC instead of directly over them"), clamped so it can never drift
    ///     out of comfortable view.
    ///  3. Speaker identification (procedurally generated per-speaker portrait + occupation
    ///     line), player-vs-NPC distinct styling, and live accessibility (font size / UI
    ///     scale / high contrast via AccessibilitySettings).
    /// </summary>
    public class DialogueUIController : MonoBehaviour
    {
        public static DialogueUIController Instance { get; private set; }

        [Header("UI refs")]
        public CanvasGroup canvasGroup;
        public TMP_Text speakerLabel;
        public TMP_Text occupationLabel;
        public TMP_Text subtitleLabel;
        public Image portraitImage;
        public TMP_Text portraitInitialLabel;
        public Image speakerIndicatorDot;
        public Image background;
        public float fadeSeconds = 0.25f;

        [Header("Follow (world-space canvas billboarding)")]
        public Transform playerCamera;
        public Vector3 offsetFromCamera = new Vector3(0f, -0.15f, 1.1f);
        [Tooltip("How far the panel is allowed to drift horizontally (local X, meters at the offset distance) toward whichever side the speaking NPC is standing on.")]
        public float maxHorizontalBias = 0.55f;
        public float positionSmoothing = 10f;

        [Header("Speaker styling")]
        public Color npcTextColor = Color.white;
        public Color playerTextColor = new Color(0.75f, 0.88f, 1f);

        private AudioSource _voSource;
        private Coroutine _playRoutine;
        private Transform _subject;
        private string _primarySpeakerName;
        private string _primarySpeakerOccupation;
        private bool _justShown;

        // 2026-07-22 governor/canvassing decision system — see PresentChoices/SelectChoice below.
        // ROUND 2: the choice rows used to live on a separate DialogueChoicePanelController
        // canvas; the user could never actually see it (kb shortcuts worked, so the logic was
        // fine — just the separate canvas's visibility). Rows now live directly on THIS
        // controller's own already-proven-visible canvas (built inline in
        // TownSceneBuilder.BuildDialogueUI) and are wired in here via choiceSlots.
        [Header("Choices (2026-07-22 governor/canvassing decision system, ROUND 2)")]
        [Tooltip("Exactly 3 rows, built inline in TownSceneBuilder.BuildDialogueUI and assigned there. Extra entries beyond a line's actual choice count are simply left hidden.")]
        public DialogueChoiceSlot[] choiceSlots;

        private bool _awaitingChoice;
        private List<DialogueChoice> _pendingChoices;
        private DialogueChoice _selectedChoice;

        private static readonly Dictionary<string, Sprite> AvatarCache = new Dictionary<string, Sprite>();

        public bool IsPlaying { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _voSource = gameObject.AddComponent<AudioSource>();
            _voSource.spatialBlend = 0f;
            UILayer.ApplyRecursively(gameObject);
            SetVisible(false);
        }

        private void Start()
        {
            // Deliberately deferred from Awake(): Unity does not guarantee Awake() runs in any
            // particular order across different root GameObjects, so AccessibilitySettings.Awake()
            // (which sets its own Instance) is not guaranteed to have run yet if this were done
            // there — reading a singleton set by another object's Awake() is only safe from Start(),
            // since Unity guarantees every Awake() in the scene completes before any Start() runs.
            if (AccessibilitySettings.Instance != null)
            {
                AccessibilitySettings.Instance.OnChanged += ApplyAccessibility;
                ApplyAccessibility();
            }
        }

        private void OnDestroy()
        {
            if (AccessibilitySettings.Instance != null)
                AccessibilitySettings.Instance.OnChanged -= ApplyAccessibility;
        }

        private void Update()
        {
            // Desktop convenience shortcut for choice selection (2026-07-22 governor/canvassing
            // decision system) — aiming a crosshair at a row this close to camera can be fiddly
            // with mouselook, so 1/2/3 does the same thing as looking at the row and pressing
            // interact (see DialogueChoiceSlot). No equivalent needed for XR: the controller ray
            // already works the same way it does for every other interactable.
            if (!_awaitingChoice || Keyboard.current == null) return;
            if (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame) SelectChoice(0);
            else if (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame) SelectChoice(1);
            else if (Keyboard.current.digit3Key.wasPressedThisFrame || Keyboard.current.numpad3Key.wasPressedThisFrame) SelectChoice(2);
        }

        private void LateUpdate()
        {
            if (!IsPlaying || playerCamera == null) return;

            Vector3 desiredOffset = offsetFromCamera;
            if (_subject != null)
            {
                Vector3 toSubjectLocal = playerCamera.InverseTransformPoint(_subject.position + Vector3.up * 1.5f);
                desiredOffset.x = Mathf.Clamp(toSubjectLocal.x, -maxHorizontalBias, maxHorizontalBias);
            }

            Vector3 targetPos = playerCamera.TransformPoint(desiredOffset);
            if (_justShown)
            {
                transform.position = targetPos;
                _justShown = false;
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, targetPos, positionSmoothing * Time.deltaTime);
            }
            transform.rotation = Quaternion.LookRotation(transform.position - playerCamera.position);
        }

        /// <summary>subject/speakerName/speakerOccupation are all optional — existing callers
        /// that only pass an entry keep working exactly as before, just without the
        /// smart-positioning bias or the portrait/occupation line for the primary speaker.</summary>
        public void Play(ConversationEntry entry, Transform subject = null, string speakerName = null, string speakerOccupation = null)
        {
            if (entry == null || entry.lines == null || entry.lines.Count == 0) return;
            _subject = subject;
            _primarySpeakerName = speakerName;
            _primarySpeakerOccupation = speakerOccupation;
            if (_playRoutine != null) StopCoroutine(_playRoutine);
            _playRoutine = StartCoroutine(PlayRoutine(entry));
        }

        public void Skip()
        {
            if (_playRoutine != null) StopCoroutine(_playRoutine);
            _voSource.Stop();
            IsPlaying = false;
            _awaitingChoice = false;
            HideChoiceRows();
            SetVisible(false);
        }

        private IEnumerator PlayRoutine(ConversationEntry entry)
        {
            IsPlaying = true;
            _justShown = true;
            SetVisible(true);

            foreach (var line in entry.lines)
            {
                ApplyLine(line);

                if (line.choices != null && line.choices.Count > 0)
                {
                    yield return PresentChoices(line.choices);
                    if (_selectedChoice != null)
                        yield return ShowChoiceOutcome(_selectedChoice);
                    continue;
                }

                float hold = line.displaySeconds;
                if (!string.IsNullOrEmpty(line.voClipResourcePath))
                {
                    var clip = Resources.Load<AudioClip>(line.voClipResourcePath);
                    if (clip != null)
                    {
                        _voSource.clip = clip;
                        _voSource.Play();
                        hold = clip.length;
                    }
                }

                yield return new WaitForSeconds(Mathf.Max(1.2f, hold));
            }

            IsPlaying = false;
            SetVisible(false);
        }

        /// <summary>Shows the response options as rows on this same canvas (see choiceSlots)
        /// and waits until SelectChoice() has been called — from a DialogueChoiceSlot interact,
        /// or the 1/2/3 keyboard shortcut in Update() above.</summary>
        private IEnumerator PresentChoices(List<DialogueChoice> choices)
        {
            _pendingChoices = choices;
            _selectedChoice = null;
            _awaitingChoice = true;

            ShowChoiceRows(choices);

            yield return new WaitUntil(() => !_awaitingChoice);
        }

        /// <summary>Applies the chosen option's morale/priority effect, hides the choice rows,
        /// and unblocks PresentChoices' WaitUntil. Called by DialogueChoiceSlot.Interact() or
        /// the desktop number-key shortcut — never called directly by anything else.</summary>
        public void SelectChoice(int index)
        {
            if (!_awaitingChoice || _pendingChoices == null || index < 0 || index >= _pendingChoices.Count) return;
            _selectedChoice = _pendingChoices[index];

            var doorKnock = _subject != null ? _subject.GetComponent<DoorKnockTrigger>() : null;
            if (doorKnock != null)
                doorKnock.HandleChoice(_selectedChoice.tag);

            if (CityMoraleSystem.Instance != null)
            {
                if (!string.IsNullOrEmpty(_selectedChoice.setPriorityZone))
                    CityMoraleSystem.Instance.SetPriorityZone(_selectedChoice.setPriorityZone);

                if (Mathf.Abs(_selectedChoice.moraleDelta) > 0.001f)
                {
                    var identity = _subject != null ? _subject.GetComponentInParent<NPCIdentity>() : null;
                    string zone = identity != null && !string.IsNullOrEmpty(identity.zone) ? identity.zone : "residential";
                    CityMoraleSystem.Instance.ApplyChoice(zone, _selectedChoice.moraleDelta);
                }
            }

            HideChoiceRows();
            _awaitingChoice = false;
        }

        /// <summary>Activates and labels one choiceSlots row per option (1-indexed in the label
        /// text so it matches the 1/2/3 keyboard shortcut), hiding any unused rows — e.g. a
        /// 2-option choice leaves row index 2 inactive.</summary>
        private void ShowChoiceRows(List<DialogueChoice> choices)
        {
            if (choiceSlots == null) return;
            for (int i = 0; i < choiceSlots.Length; i++)
            {
                if (choiceSlots[i] == null) continue;
                bool has = choices != null && i < choices.Count;
                choiceSlots[i].gameObject.SetActive(has);
                if (has) choiceSlots[i].SetLabel((i + 1) + ".  " + choices[i].text);
            }
        }

        private void HideChoiceRows()
        {
            if (choiceSlots == null) return;
            foreach (var slot in choiceSlots)
                if (slot != null) slot.gameObject.SetActive(false);
        }

        /// <summary>Briefly shows the NPC's reaction line, then the real-world canvassing tip
        /// behind the choice — the actual "make it educational" part of the brief.</summary>
        private IEnumerator ShowChoiceOutcome(DialogueChoice choice)
        {
            if (!string.IsNullOrEmpty(choice.npcResponseText))
            {
                if (subtitleLabel != null) subtitleLabel.text = choice.npcResponseText;
                yield return new WaitForSeconds(3.2f);
            }
            if (!string.IsNullOrEmpty(choice.educationalNote))
            {
                if (speakerLabel != null) speakerLabel.text = "Canvassing Note";
                if (occupationLabel != null) occupationLabel.text = "";
                if (subtitleLabel != null) subtitleLabel.text = choice.educationalNote;
                yield return new WaitForSeconds(4f);
            }
        }

        private void ApplyLine(ConversationLine line)
        {
            if (speakerLabel != null) speakerLabel.text = line.speaker;
            if (subtitleLabel != null) subtitleLabel.text = line.text;

            bool isPrimarySpeaker = !string.IsNullOrEmpty(_primarySpeakerName) && line.speaker == _primarySpeakerName;
            if (occupationLabel != null)
                occupationLabel.text = isPrimarySpeaker ? _primarySpeakerOccupation : string.Empty;

            bool isPlayerLine = line.isPlayerLine;
            var textColor = isPlayerLine ? playerTextColor : npcTextColor;
            var textAlign = isPlayerLine ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft;
            if (subtitleLabel != null) { subtitleLabel.color = textColor; subtitleLabel.alignment = textAlign; }
            if (speakerLabel != null) speakerLabel.alignment = isPlayerLine ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft;

            string avatarName = isPlayerLine ? "You" : line.speaker;
            if (portraitImage != null)
            {
                portraitImage.sprite = GetAvatarSprite(avatarName);
                portraitImage.color = Color.white;
            }
            if (portraitInitialLabel != null)
                portraitInitialLabel.text = string.IsNullOrEmpty(avatarName) ? "?" : avatarName.Substring(0, 1).ToUpperInvariant();
            if (speakerIndicatorDot != null)
                speakerIndicatorDot.color = isPlayerLine ? playerTextColor : GetAvatarColor(avatarName);
        }

        private void ApplyAccessibility()
        {
            var settings = AccessibilitySettings.Instance;
            if (settings == null) return;

            transform.localScale = Vector3.one * 0.0016f * settings.uiScale;
            if (speakerLabel != null) speakerLabel.fontSize = 34f * settings.fontSizeMultiplier;
            if (occupationLabel != null) occupationLabel.fontSize = 20f * settings.fontSizeMultiplier;
            if (subtitleLabel != null) subtitleLabel.fontSize = 30f * settings.fontSizeMultiplier;

            if (background != null)
                background.color = settings.highContrast ? new Color(0f, 0f, 0f, 0.92f) : new Color(0.06f, 0.07f, 0.06f, 0.78f);
            // High contrast forces pure white regardless of the player/NPC color distinction —
            // readability wins over that subtlety in this mode. Normal mode leaves whatever
            // ApplyLine() last set (npc/player color) alone rather than fighting it here.
            if (subtitleLabel != null && settings.highContrast)
                subtitleLabel.color = Color.white;
        }

        private void SetVisible(bool visible)
        {
            if (canvasGroup == null) return;
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.blocksRaycasts = visible;
        }

        private static Color GetAvatarColor(string speakerName)
        {
            if (string.IsNullOrEmpty(speakerName)) speakerName = "?";
            int hash = 0;
            foreach (char c in speakerName) hash = hash * 31 + c;
            float hue = (Mathf.Abs(hash) % 360) / 360f;
            return Color.HSVToRGB(hue, 0.45f, 0.85f);
        }

        /// <summary>Procedurally generates (and caches) a small colored-circle avatar per
        /// speaker name — a deterministic hash picks the hue, so the same person always gets
        /// the same avatar color across however many lines/conversations they appear in.
        /// Stands in for real portrait art, which would need the Editor to author/import.</summary>
        private static Sprite GetAvatarSprite(string speakerName)
        {
            if (string.IsNullOrEmpty(speakerName)) speakerName = "?";
            if (AvatarCache.TryGetValue(speakerName, out var cached) && cached != null) return cached;

            Color color = GetAvatarColor(speakerName);
            const int size = 48;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float a = Mathf.Clamp01(radius - dist + 0.5f);
                    tex.SetPixel(x, y, new Color(color.r, color.g, color.b, a));
                }
            }
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            AvatarCache[speakerName] = sprite;
            return sprite;
        }
    }
}
