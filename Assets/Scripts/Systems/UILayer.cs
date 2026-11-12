using UnityEngine;

namespace CityHall.Systems
{
    /// <summary>
    /// Occlusion-proof "always on top" UI layer, used by every world-space HUD element
    /// (dialogue windows, info panels, interaction prompts, NPC speech bubbles) — see
    /// TownSceneBuilder.BuildOcclusionProofUICamera() for the other half of this fix.
    ///
    /// BUG FOUND 2026-07-22 ("dialogue boxes, interaction prompts, and informational text
    /// frequently become partially or completely hidden behind 3D objects, vegetation,
    /// buildings, props, or NPCs"): every world-space UI canvas/icon in this project was
    /// rendered by the single main camera and depth-tested against ordinary world geometry
    /// like any other object — so anything physically closer to the camera than the UI
    /// element (a tree branch, a wall, another NPC) painted over it.
    ///
    /// Standard fix, no custom shaders required: render UI on its own Unity layer, have the
    /// main camera skip that layer entirely, and add a second "overlay" camera that renders
    /// ONLY that layer with its depth buffer cleared first (Camera.clearFlags = Depth) — so
    /// whatever it draws paints on top of the already-rendered world with no depth test
    /// against it at all.
    ///
    /// Reuses Unity's built-in layer 2 ("Ignore Raycast") rather than adding a new named
    /// layer via ProjectSettings/TagManager.asset (which would need Editor-side scripted
    /// project-settings edits that can't be verified without the Editor to open it in).
    /// Nothing else in this project assigns any GameObject's .layer at all (grepped every
    /// ".layer =" site before choosing this), and none of these UI elements carry 3D
    /// Colliders anyway, so its raycast-ignoring behavior is a harmless — even fitting —
    /// side effect, not a functional change to anything else.
    /// </summary>
    public static class UILayer
    {
        public const int Layer = 2; // Unity's built-in "Ignore Raycast" layer, repurposed for this

        /// <summary>Sets this GameObject and every descendant to UILayer.Layer. Call once per
        /// UI root (a dialogue canvas, an interaction-prompt icon, a speech bubble) — Unity does
        /// not propagate a parent's .layer to children automatically, so every world-space UI
        /// component in this project calls this on itself (and its own icon/canvas children) in
        /// Awake() rather than relying on TownSceneBuilder to set layers at every creation site.</summary>
        public static void ApplyRecursively(GameObject root)
        {
            if (root == null) return;
            root.layer = Layer;
            foreach (Transform child in root.transform)
                ApplyRecursively(child.gameObject);
        }
    }
}
