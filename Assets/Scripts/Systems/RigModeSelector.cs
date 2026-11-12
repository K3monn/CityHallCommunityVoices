using UnityEngine;
using UnityEngine.XR.Management;

namespace CityHall.Systems
{
    /// <summary>
    /// Chooses between the desktop rig (mouse + keyboard, PlayerMovement.cs) and
    /// the XR rig (head/controller tracking, XRLocomotion.cs) at runtime, based
    /// on whether an XR loader actually initialized this session — NOT based on
    /// build target, since the same Town.unity scene is used for both Editor
    /// desktop play-testing and the eventual Quest build.
    /// XRGeneralSettings.Instance.Manager.activeLoader is the standard way to
    /// detect this: it's non-null only when an XR runtime (OpenXR on Quest, or
    /// Quest Link/Air Link if a headset is connected while testing in the
    /// Editor) actually started, and is the same check XR Plug-in
    /// Management-based projects use generally.
    ///
    /// Lives on the always-active "Systems" GameObject (not on either rig
    /// itself) and runs in Awake(), added as the last component on that object
    /// so PerformanceLODManager.Instance is already set by the time it runs
    /// (Unity calls Awake() for multiple components on one GameObject in the
    /// order they were added). Disabling a rig here, this early, means that
    /// losing rig's own Start()/OnEnable() never fires at all.
    /// </summary>
    public class RigModeSelector : MonoBehaviour
    {
        public GameObject desktopRig;
        public GameObject xrRig;

        private void Awake()
        {
            bool xrActive = XRGeneralSettings.Instance != null
                            && XRGeneralSettings.Instance.Manager != null
                            && XRGeneralSettings.Instance.Manager.activeLoader != null;

            if (desktopRig != null) desktopRig.SetActive(!xrActive);
            if (xrRig != null) xrRig.SetActive(xrActive);

            if (xrActive && xrRig != null && PerformanceLODManager.Instance != null)
            {
                var xrCamera = xrRig.GetComponentInChildren<Camera>(true);
                if (xrCamera != null) PerformanceLODManager.Instance.player = xrCamera.transform;
            }
        }
    }
}
