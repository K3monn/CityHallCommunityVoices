using UnityEngine;
using UnityEngine.InputSystem;

namespace CityHall.Dialogue
{
    /// <summary>
    /// One-time "what is this experience" briefing shown right in front of
    /// whichever camera is currently active (desktop or XR — see
    /// RigModeSelector; this reads Camera.main directly each frame rather than
    /// holding a fixed reference, so it works for either rig without needing to
    /// know which one is live).
    ///
    /// Added because a free-roam civic-education town with no score, fail
    /// state, or explicit prompt doesn't otherwise signal what the player is
    /// meant to do, or why — user feedback was "unclear what you're supposed to
    /// do." States the goal plainly (walk around, talk to residents, visit City
    /// Hall) and explicitly names this project's actual concept — canvassing
    /// during the off-season — which previously wasn't represented anywhere in
    /// the town's content at all (see exhibit_offseason_canvassing in
    /// CivicExhibits.json, canvassing_volunteer_signup in Boards.json, and the
    /// new canvasser_01 NPC).
    /// </summary>
    public class IntroBriefingController : MonoBehaviour
    {
        public Vector3 offsetFromCamera = new Vector3(0f, 0f, 2.2f);
        public float autoHideSeconds = 16f;

        private float _timer;
        private bool _dismissed;

        private void Update()
        {
            if (_dismissed) return;

            var cam = Camera.main;
            if (cam != null)
            {
                transform.position = cam.transform.TransformPoint(offsetFromCamera);
                transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            }

            _timer += Time.deltaTime;

            bool dismissInput = _timer >= autoHideSeconds;
            if (Keyboard.current != null && (
                    Keyboard.current.wKey.wasPressedThisFrame || Keyboard.current.aKey.wasPressedThisFrame ||
                    Keyboard.current.sKey.wasPressedThisFrame || Keyboard.current.dKey.wasPressedThisFrame ||
                    Keyboard.current.spaceKey.wasPressedThisFrame))
                dismissInput = true;
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                dismissInput = true;

            if (dismissInput) Dismiss();
        }

        public void Dismiss()
        {
            _dismissed = true;
            gameObject.SetActive(false);
        }
    }
}
