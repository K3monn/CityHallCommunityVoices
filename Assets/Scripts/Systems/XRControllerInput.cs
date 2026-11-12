using UnityEngine;
using UnityEngine.InputSystem;

namespace CityHall.Systems
{
    /// <summary>
    /// Quest controller trigger -> interact, reusing PlayerInteractor's existing
    /// raycast-and-call-IInteractable logic (see PlayerInteractor.cs) instead of
    /// converting every NPC/board/exhibit in town into an XR Interaction Toolkit
    /// XRSimpleInteractable. PlayerInteractor's own class doc comment already
    /// calls this "raycast fallback" out as a valid way to drive it for XR, so
    /// this is one PlayerInteractor per hand with rayOrigin pointed at that
    /// hand, triggered by the controller's trigger button instead of a mouse
    /// click / space bar.
    ///
    /// Also draws a short visual ray from the controller so a first-time VR user
    /// can see where they're pointing before pulling the trigger — plain
    /// LineRenderer, no XR Interaction Toolkit dependency.
    /// </summary>
    public class XRControllerInput : MonoBehaviour
    {
        public PlayerInteractor interactor;
        public bool isLeftHand;
        public float rayVisualLength = 5f;

        private InputAction _triggerAction;
        private LineRenderer _line;

        private void Awake()
        {
            string hand = isLeftHand ? "LeftHand" : "RightHand";
            _triggerAction = new InputAction("XRTrigger_" + hand, InputActionType.Button, "<XRController>{" + hand + "}/triggerButton");

            _line = gameObject.AddComponent<LineRenderer>();
            _line.startWidth = 0.006f;
            _line.endWidth = 0.006f;
            _line.positionCount = 2;
            _line.useWorldSpace = true;
            // Shader.Find can return null in a stripped Quest build if nothing else
            // in the project references this shader (Android builds strip unused
            // shader variants by default) — guard rather than risk constructing a
            // Material with a null shader for what's only a cosmetic pointer ray.
            var lineShader = Shader.Find("Sprites/Default");
            if (lineShader != null) _line.material = new Material(lineShader);
            _line.startColor = new Color(0.85f, 0.9f, 1f, 0.55f);
            _line.endColor = new Color(0.85f, 0.9f, 1f, 0.05f);
        }

        private void OnEnable() => _triggerAction.Enable();
        private void OnDisable() => _triggerAction.Disable();

        private void Update()
        {
            if (_triggerAction.WasPressedThisFrame() && interactor != null)
                interactor.TryInteract();

            if (_line != null)
            {
                _line.SetPosition(0, transform.position);
                _line.SetPosition(1, transform.position + transform.forward * rayVisualLength);
            }
        }
    }
}
