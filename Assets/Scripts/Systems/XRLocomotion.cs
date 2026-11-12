using UnityEngine;
using UnityEngine.InputSystem;

namespace CityHall.Systems
{
    /// <summary>
    /// Quest controller locomotion for the XR rig — left-stick move (relative to
    /// head facing, not rig facing, since in VR you look where you want to walk)
    /// and right-stick snap-turn (a fixed-degree rotation per flick past the
    /// deadzone, with a cooldown so one flick doesn't spin you repeatedly) —
    /// snap-turn is the more comfort-friendly default vs. smooth turn for a
    /// first-time VR audience, which is the target for a civic-education piece
    /// meant to be approachable rather than for VR veterans.
    ///
    /// Deliberately does NOT use XR Interaction Toolkit's own locomotion
    /// provider classes (ActionBasedContinuousMoveProvider / SnapTurnProvider /
    /// LocomotionSystem) — their exact field and class names have shifted across
    /// XRI 2.x point releases and this project's precise installed version
    /// couldn't be confirmed without the Editor. Reading raw thumbstick axes via
    /// a generic <XRController> binding path and moving the same CharacterController
    /// PlayerMovement.cs already uses for desktop is a smaller, more stable
    /// surface: if a binding path is wrong, the stick silently reads as zero
    /// (no crash) rather than a missing-class compile error.
    ///
    /// Reads input directly rather than going through XR Interaction Toolkit's
    /// ActionBasedController, for the same reason.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class XRLocomotion : MonoBehaviour
    {
        [Header("Move")]
        public float moveSpeed = 2.2f;
        public float gravity = -9.81f;
        [Tooltip("Head/camera transform — move direction is relative to this, not to the rig root, since the rig root doesn't rotate with your head in VR.")]
        public Transform headTransform;

        [Header("Snap turn")]
        public float snapTurnDegrees = 30f;
        public float snapTurnDeadzone = 0.6f;
        public float snapTurnCooldown = 0.35f;

        private CharacterController _controller;
        private InputAction _moveAction;
        private InputAction _turnAction;
        private float _verticalVelocity;
        private float _turnCooldownTimer;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _moveAction = new InputAction("XRMove", InputActionType.Value, "<XRController>{LeftHand}/primary2DAxis", expectedControlType: "Vector2");
            _turnAction = new InputAction("XRTurn", InputActionType.Value, "<XRController>{RightHand}/primary2DAxis", expectedControlType: "Vector2");
        }

        private void OnEnable()
        {
            _moveAction.Enable();
            _turnAction.Enable();
        }

        private void OnDisable()
        {
            _moveAction.Disable();
            _turnAction.Disable();
        }

        private void Update()
        {
            HandleMove();
            HandleSnapTurn();
        }

        private void HandleMove()
        {
            Vector2 axis = _moveAction.ReadValue<Vector2>();
            var forward = headTransform != null ? headTransform.forward : transform.forward;
            var right = headTransform != null ? headTransform.right : transform.right;
            forward.y = 0f; right.y = 0f;
            forward.Normalize(); right.Normalize();

            Vector3 move = (forward * axis.y + right * axis.x) * moveSpeed;

            if (_controller.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -1f;
            _verticalVelocity += gravity * Time.deltaTime;
            move.y = _verticalVelocity;

            _controller.Move(move * Time.deltaTime);
        }

        private void HandleSnapTurn()
        {
            if (_turnCooldownTimer > 0f)
            {
                _turnCooldownTimer -= Time.deltaTime;
                return;
            }

            float x = _turnAction.ReadValue<Vector2>().x;
            if (Mathf.Abs(x) < snapTurnDeadzone) return;

            transform.Rotate(Vector3.up, Mathf.Sign(x) * snapTurnDegrees);
            _turnCooldownTimer = snapTurnCooldown;
        }
    }
}
