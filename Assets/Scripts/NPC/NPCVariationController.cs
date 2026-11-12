using System.Collections.Generic;
using UnityEngine;

namespace CityHall.NPC
{
    /// <summary>
    /// Drives the "no two NPCs feel identical" requirement by randomizing (or
    /// designer-pinning) the modular pieces exposed by the Creative Characters —
    /// Modular Animated Pack (Unity Asset Store #304838).
    ///
    /// IMPORTANT — asset-pack wiring:
    /// This script cannot know the exact mesh/material naming the pack ships with
    /// (that requires inspecting the imported .fbx/prefab hierarchy in the Editor,
    /// which isn't possible from outside Unity). It is written against the *pattern*
    /// almost all "modular humanoid" packs use — a set of SkinnedMeshRenderer slots
    /// (body, head, hair, top, bottom, shoes, accessory) each with a pool of
    /// swappable mesh/material options. After importing the pack:
    ///   1. Open one of its sample prefabs and note the actual child GameObject
    ///      names for each slot.
    ///   2. Either rename this script's [SerializeField] slot list to match, or
    ///      (recommended) rename the pack's GameObjects to match the slot IDs
    ///      below (Body / Head / Hair / Top / Bottom / Shoes / Accessory) so this
    ///      script works unmodified.
    ///   3. Drag the pack's mesh/material variants into the appropriate
    ///      NPCVariationLibrary asset (see NPCVariationLibrary.cs) in the Inspector.
    /// </summary>
    [DisallowMultipleComponent]
    public class NPCVariationController : MonoBehaviour
    {
        [System.Serializable]
        public class Slot
        {
            public string slotId;                 // "Body", "Head", "Hair", "Top", "Bottom", "Shoes", "Accessory"
            public SkinnedMeshRenderer renderer;   // assign in Inspector once hierarchy is known
            public Mesh[] meshOptions;
            public Material[] materialOptions;
        }

        [Header("Modular slots (wire up after importing the asset pack)")]
        [Tooltip("Per-instance overrides. Leave empty and assign sharedLibrary below to pull pools from one shared asset instead — recommended for a crowd of many NPCs sharing the same asset pack.")]
        public List<Slot> slots = new List<Slot>();

        [Tooltip("Shared pools of mesh/material options (see NPCVariationLibrary.cs). Renderer references must still be set per-instance since they point at this specific prefab's hierarchy — only the mesh/material pools are shared.")]
        public NPCVariationLibrary sharedLibrary;
        [Tooltip("Renderer references matched by slotId when using sharedLibrary. e.g. {\"Hair\", hairRenderer}, {\"Top\", topRenderer}.")]
        public List<Slot> sharedLibraryRendererMap = new List<Slot>();

        [Header("Skin tone variation")]
        public Renderer[] skinRenderers;
        public Color[] skinTonePalette = new Color[]
        {
            new Color(0.91f, 0.69f, 0.53f),
            new Color(0.79f, 0.55f, 0.37f),
            new Color(0.55f, 0.35f, 0.24f),
            new Color(0.94f, 0.79f, 0.63f),
            new Color(0.66f, 0.42f, 0.28f),
            new Color(0.36f, 0.23f, 0.16f)
        };

        [Header("Scale variation (age / body type)")]
        public float minHeightScale = 0.9f;
        public float maxHeightScale = 1.12f;
        public float childHeightScale = 0.62f;
        public float elderHeightScale = 0.95f;

        [Header("Animator variation")]
        public Animator animator;
        [Tooltip("Blend-tree float or Animator parameter used to pick between idle variants, if the pack exposes one (e.g. \"IdleVariant\" 0-3).")]
        public string idleVariantParam = "IdleVariant";
        public int idleVariantCount = 4;
        [Tooltip("Animator parameter that offsets walk-cycle start time so synced NPCs don't all step in unison.")]
        public string walkCycleOffsetParam = "CycleOffset";

        private NPCIdentity _identity;

        private void Awake()
        {
            _identity = GetComponent<NPCIdentity>();
            if (_identity == null) _identity = gameObject.AddComponent<NPCIdentity>();
        }

        /// <summary>Call once at spawn time (see NPCSpawner). Idempotent-safe to call again to re-roll.</summary>
        public void Randomize(int? seed = null)
        {
            var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
            ResolveSlotsFromLibrary();

            // 1. Age band drives scale + which routine pool this NPC is eligible for.
            int ageBand = RollAgeBand(rng);
            float heightScale = ageBand == 0 ? childHeightScale
                               : ageBand == 2 ? elderHeightScale
                               : Mathf.Lerp(minHeightScale, maxHeightScale, (float)rng.NextDouble());
            transform.localScale = Vector3.one * heightScale;

            // 2. Skin tone.
            Color skin = skinTonePalette[rng.Next(skinTonePalette.Length)];
            foreach (var r in skinRenderers)
            {
                if (r == null) continue;
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", skin); // URP/Lit. Swap to "_Color" if using Built-in shader.
                r.SetPropertyBlock(mpb);
            }

            // 3. Modular mesh/material slots (hair, top, bottom, shoes, accessory...).
            string clothingSummary = "";
            foreach (var slot in slots)
            {
                if (slot.renderer == null) continue;
                if (slot.meshOptions != null && slot.meshOptions.Length > 0)
                    slot.renderer.sharedMesh = slot.meshOptions[rng.Next(slot.meshOptions.Length)];
                if (slot.materialOptions != null && slot.materialOptions.Length > 0)
                {
                    var mat = slot.materialOptions[rng.Next(slot.materialOptions.Length)];
                    slot.renderer.sharedMaterial = mat;
                    if (slot.slotId == "Top" || slot.slotId == "Bottom") clothingSummary += mat.name + " ";
                }
            }

            // 4. Animator variety so a crowd doesn't move in lockstep.
            if (animator != null)
            {
                if (idleVariantCount > 0 && !string.IsNullOrEmpty(idleVariantParam))
                    animator.SetInteger(idleVariantParam, rng.Next(idleVariantCount));
                if (!string.IsNullOrEmpty(walkCycleOffsetParam))
                    animator.SetFloat(walkCycleOffsetParam, (float)rng.NextDouble());
            }

            _identity.ageBand = ageBand;
            _identity.clothingSet = clothingSummary.Trim();
        }

        /// <summary>Merges sharedLibrary pools into `slots` (matched by slotId via
        /// sharedLibraryRendererMap) the first time Randomize() runs, so per-instance
        /// prefabs only need to wire up Renderer references, not duplicate every
        /// mesh/material option from the pack.</summary>
        private void ResolveSlotsFromLibrary()
        {
            if (sharedLibrary == null || sharedLibraryRendererMap.Count == 0) return;
            if (slots.Count >= sharedLibraryRendererMap.Count) return; // already resolved

            slots.Clear();
            foreach (var mapping in sharedLibraryRendererMap)
            {
                var pool = sharedLibrary.GetPool(mapping.slotId);
                if (pool == null || mapping.renderer == null) continue;
                slots.Add(new Slot
                {
                    slotId = mapping.slotId,
                    renderer = mapping.renderer,
                    meshOptions = pool.meshOptions,
                    materialOptions = pool.materialOptions
                });
            }
        }

        private int RollAgeBand(System.Random rng)
        {
            double r = rng.NextDouble();
            if (r < 0.12) return 0;  // child
            if (r < 0.85) return 1;  // adult
            return 2;                // elder
        }
    }
}
