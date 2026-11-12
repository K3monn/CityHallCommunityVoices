using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CityHall.Systems
{
    /// <summary>
    /// Desktop WASD + mouselook controller for Editor/keyboard play-testing.
    /// Uses the New Input System (Mouse/Keyboard), the same pattern already used
    /// by PlayerInteractor.cs — XR Interaction Toolkit forces Active Input
    /// Handling to the new system, so legacy UnityEngine.Input throws once
    /// that's set instead of silently working.
    ///
    /// This was missing from the original scaffold: BuildPlayerRig() wired up a
    /// camera and PlayerInteractor (click-to-interact) but nothing that actually
    /// translates the rig, so Play mode rendered and let you click on NPCs/boards
    /// but never let you walk anywhere.
    ///
    /// For the real VR build, this component can stay enabled alongside XR
    /// Interaction Toolkit's locomotion providers (Continuous Move/Turn or
    /// Teleportation) on the same rig — it only reads Mouse/Keyboard state, which
    /// a headset + controllers simply never produce, so it's inert in VR rather
    /// than conflicting. Disable it explicitly for the XR build only if you want
    /// to be extra explicit about which controller owns movement.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMovement : MonoBehaviour
    {
        [Header("Move")]
        public float moveSpeed = 4.5f;
        public float sprintMultiplier = 1.8f;
        public float gravity = -9.81f;

        [Header("Look")]
        public Transform cameraTransform;
        public float mouseSensitivity = 2f;
        public float minPitch = -80f;
        public float maxPitch = 80f;

        private CharacterController _controller;
        private float _pitch;
        private float _verticalVelocity;
        private bool _cursorLocked;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        }

        /// <summary>Called by PortalTravel right after teleporting this rig (see its Interact()
        /// doc comment). Without this, whatever fall speed had accumulated on the OLD side of the
        /// portal carries straight over to the new position, which combined with the CharacterController
        /// reposition footgun documented there is what produced "walked in fine, then fell through
        /// the floor" the moment the entrance itself finally stopped blocking movement.</summary>
        public void ResetVerticalVelocity()
        {
            _verticalVelocity = -1f;
        }

        private Coroutine _nudgeRoutine;

        /// <summary>Dynamic camera framing (2026-07-22 UI brief: "slightly rotate the camera
        /// toward the NPC... do not lock the camera or remove player agency"). A ONE-TIME,
        /// brief, cancellable yaw nudge on conversation start — not continuous tracking or a
        /// lock. Two safeguards keep this from ever fighting the player: it only nudges if the
        /// subject is already outside a comfortable central view cone (no adjustment needed
        /// otherwise, so most interactions — where you're already facing the NPC to click on
        /// them — never nudge at all), and it aborts immediately the moment the player moves the
        /// mouse during the nudge, so a deliberate look-away always wins instantly. Only ever
        /// touches yaw (never pitch), and only on this rig's own Transform — cameraTransform's
        /// local pitch rotation (set every frame by HandleLook) is untouched.</summary>
        public void NudgeYawToward(Transform subject, float triggerAngle = 35f, float duration = 0.35f)
        {
            if (subject == null) return;
            Vector3 toSubject = subject.position - transform.position;
            toSubject.y = 0f;
            if (toSubject.sqrMagnitude < 0.01f) return;

            float currentYaw = transform.eulerAngles.y;
            float targetYaw = Quaternion.LookRotation(toSubject.normalized).eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(currentYaw, targetYaw)) <= triggerAngle) return; // already facing them comfortably

            if (_nudgeRoutine != null) StopCoroutine(_nudgeRoutine);
            _nudgeRoutine = StartCoroutine(NudgeYawRoutine(targetYaw, duration));
        }

        private IEnumerator NudgeYawRoutine(float targetYaw, float duration)
        {
            float startYaw = transform.eulerAngles.y;
            float t = 0f;
            while (t < duration)
            {
                // Player agency comes first: any deliberate mouse look during the nudge cancels
                // it immediately rather than fighting their input for even one frame.
                if (Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude > 0.01f) yield break;

                t += Time.deltaTime;
                float yaw = Mathf.LerpAngle(startYaw, targetYaw, t / duration);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                yield return null;
            }
        }

        private void OnEnable()
        {
            SetCursorLock(true);
        }

        private void OnDisable()
        {
            SetCursorLock(false);
        }

        private void Update()
        {
            HandleCursorToggle();
            HandleLook();
            HandleMove();
        }

        private void HandleLook()
        {
            if (Mouse.current == null || !_cursorLocked) return;
            Vector2 delta = Mouse.current.delta.ReadValue();
            transform.Rotate(Vector3.up, delta.x * mouseSensitivity * 0.05f);
            _pitch = Mathf.Clamp(_pitch - delta.y * mouseSensitivity * 0.05f, minPitch, maxPitch);
            if (cameraTransform != null)
                cameraTransform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void HandleMove()
        {
            if (Keyboard.current == null || _controller == null) return;
            var kb = Keyboard.current;
            Vector2 axis = Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) axis.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) axis.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) axis.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) axis.x -= 1f;
            axis = Vector2.ClampMagnitude(axis, 1f);

            float speed = moveSpeed * (kb.leftShiftKey.isPressed ? sprintMultiplier : 1f);
            Vector3 move = (transform.right * axis.x + transform.forward * axis.y) * speed;

            if (_controller.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -1f;
            _verticalVelocity += gravity * Time.deltaTime;
            move.y = _verticalVelocity;

            _controller.Move(move * Time.deltaTime);
        }

        private void HandleCursorToggle()
        {
            // Esc releases the mouse (to click UI / switch windows); clicking back
            // into the Game view re-locks it — the standard Editor play-test
            // pattern, and it does not interfere with PlayerInteractor's own
            // left-click-to-interact handling once the cursor is locked again.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                SetCursorLock(false);
            else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && !_cursorLocked)
                SetCursorLock(true);
        }

        private void SetCursorLock(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
