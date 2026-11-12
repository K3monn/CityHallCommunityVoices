using UnityEngine;
using CityHall.Core;

namespace CityHall.Systems
{
    /// <summary>
    /// Walkable portal between two areas of the town — used here for the City
    /// Hall plaza &lt;-&gt; City Hall interior transition, matching the same
    /// show/hide-and-teleport pattern used in the browser previz mockup
    /// (`city-hall-mockup.html`) rather than a full multi-scene load, to keep the
    /// scaffold simple. Two PortalTravel instances point at each other: one at
    /// the exterior entrance (hides the town, shows the interior), one at the
    /// interior exit (reverses it).
    ///
    /// BUG FOUND 2026-07-22: despite this class's own doc comment calling it a
    /// "walkable portal," it was IInteractable-only (click/Space to use) — and
    /// the archway's "TriggerVolume" BoxCollider (see TownSceneBuilder's
    /// BuildCityHallExterior/BuildInterior) was left as a SOLID collider, not an
    /// actual Unity trigger. Since that collider exactly fills the gap between
    /// the archway's two frame posts, it physically blocked the CharacterController
    /// from ever walking into the doorway at all — reported as "unable to enter
    /// the building" with a screenshot showing the archway looking like a solid
    /// wall. Fixed two ways: TownSceneBuilder now sets that collider's
    /// isTrigger = true (so it no longer blocks movement), and this class gained
    /// OnTriggerEnter so simply walking into the archway opening triggers the
    /// transition automatically, matching what "walkable portal" always implied.
    /// Interact() (click/Space) still works too — SphereCast/Raycast queries hit
    /// trigger colliders by default, so this is additive, not a replacement.
    ///
    /// BUG FOUND 2026-07-29 ("enter City Hall, get a void"): Interact() took an
    /// `interactor` parameter but never actually used it — every line acted on
    /// the fixed `playerRig` Inspector field instead. That field was wired to the
    /// original desktop-testing rig (CharacterController + PlayerMovement) from
    /// before this project had a working XR rig. areaToHide/areaToShow don't
    /// depend on which rig is involved, so those always toggled correctly,
    /// masking the bug — but the teleport itself was silently moving a rig
    /// nobody was looking through, while whichever rig actually triggered the
    /// portal (now XRRig, once OpenXR was properly enabled for Android) never
    /// moved. Fixed by teleporting whichever transform actually entered the
    /// trigger/clicked interact, falling back to the Inspector field only if
    /// Interact() is ever called without one.
    /// </summary>
    public class PortalTravel : MonoBehaviour, IInteractable, IContextualPrompt
    {
        public GameObject areaToHide;
        public GameObject areaToShow;
        public Transform playerRig;
        public Vector3 arrivalPosition;
        public Vector3 arrivalEulerRotation;
        public string playerTag = "Player";

        public bool CanInteract => true;
        public string PromptText => areaToShow != null && areaToShow.name.Contains("Interior")
            ? "Enter Town Hall"
            : "Return to the Plaza";
        public Vector3 PromptAnchor => transform.position + Vector3.up * 1.6f;

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(playerTag)) return;
            Interact(other.transform);
        }

        public void Interact(Transform interactor)
        {
            if (areaToHide != null) areaToHide.SetActive(false);
            if (areaToShow != null) areaToShow.SetActive(true);

            var rig = interactor != null ? interactor : playerRig;
            if (rig != null)
            {
                var controller = rig.GetComponent<CharacterController>();
                if (controller != null) controller.enabled = false;

                rig.position = arrivalPosition;
                rig.rotation = Quaternion.Euler(arrivalEulerRotation);

                var movement = rig.GetComponent<PlayerMovement>();
                if (movement != null) movement.ResetVerticalVelocity();

                if (controller != null) controller.enabled = true;
            }
        }
    }
}
