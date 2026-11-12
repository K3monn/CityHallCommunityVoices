using UnityEngine;
using TMPro;
using CityHall.Core;
using CityHall.Dialogue;

namespace CityHall.Systems
{
    /// <summary>
    /// One selectable row inside the dialogue choice panel (see
    /// DialogueChoicePanelController and DialogueUIController.PresentChoices).
    /// Reuses the exact same interaction path as every other clickable object
    /// in the scene — PlayerInteractor's SphereCast (desktop) or an XRI
    /// controller ray — rather than inventing a separate mouse-cursor/
    /// EventSystem UI input path just for this one feature. Aim at the row
    /// (crosshair / controller ray) and press interact, same as talking to an
    /// NPC or reading a board. Desktop also gets a 1/2/3 keyboard shortcut —
    /// see DialogueUIController.Update() — since aiming at a row that's this
    /// close to camera can be fiddly with mouselook.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class DialogueChoiceSlot : MonoBehaviour, IInteractable
    {
        public int index;
        public TMP_Text label;

        public bool CanInteract => gameObject.activeInHierarchy && DialogueUIController.Instance != null;
        public Vector3 PromptAnchor => transform.position;

        public void SetLabel(string text)
        {
            if (label != null) label.text = text;
        }

        public void Interact(Transform interactor)
        {
            DialogueUIController.Instance?.SelectChoice(index);
        }
    }
}
