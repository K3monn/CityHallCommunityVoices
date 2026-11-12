using System.Collections.Generic;
using UnityEngine;

namespace CityHall.NPC
{
    /// <summary>
    /// Designer-facing asset that holds the pools of mesh/material options for
    /// each modular slot (Body/Head/Hair/Top/Bottom/Shoes/Accessory) sourced from
    /// the Creative Characters pack, shared across every NPC instance rather than
    /// duplicated per-prefab. Create one via Assets > Create > City Hall >
    /// NPC Variation Library after importing the asset pack, then assign it to
    /// each NPCVariationController's `sharedLibrary` field.
    /// </summary>
    [CreateAssetMenu(fileName = "NPCVariationLibrary", menuName = "City Hall/NPC Variation Library")]
    public class NPCVariationLibrary : ScriptableObject
    {
        [System.Serializable]
        public class SlotPool
        {
            public string slotId;
            public Mesh[] meshOptions;
            public Material[] materialOptions;
        }

        public List<SlotPool> slotPools = new List<SlotPool>();

        public SlotPool GetPool(string slotId)
        {
            return slotPools.Find(p => p.slotId == slotId);
        }
    }
}
