using System.Collections.Generic;
using UnityEngine;
using CityHall.NPC;

namespace CityHall.Systems
{
    /// <summary>
    /// Central distance-based activation manager — the brief's "avoid unnecessary
    /// draw calls / distance-based NPC activation / animation culling / audio
    /// culling" performance requirements, implemented as one lightweight manager
    /// rather than per-NPC Update() distance checks (which would itself add up
    /// across a crowd).
    ///
    /// NPCs register on spawn (see NPCSpawner) and get a cheap periodic distance
    /// check on a staggered timer instead of every-frame, so a town of 40-60
    /// citizens costs a fraction of a full Update() pass per frame.
    ///
    /// Tune the three radii to your target headset's budget; on Quest-class
    /// hardware, keeping "full" simulation under ~15-20 concurrent NPCs is a
    /// reasonable starting point.
    /// </summary>
    public class PerformanceLODManager : MonoBehaviour
    {
        public static PerformanceLODManager Instance { get; private set; }

        [Header("Radii (meters from the player/camera rig)")]
        public float fullSimulationRadius = 18f;   // Animator + NavMeshAgent + AudioSource all active
        public float reducedSimulationRadius = 35f; // Animator ticks at low rate, no audio
        // Beyond reducedSimulationRadius: NPC freezes in place (no NavMeshAgent updates), effectively LOD'd out.

        [Header("Rendering-side culling (adds to the logic-side culling above)")]
        [Tooltip("Beyond reducedSimulationRadius, NPC renderers are disabled entirely (not just frozen) — this is what actually saves draw calls / skinned-mesh GPU time for a big crowd, since the logic-side culling above only stops CPU-side Animator/NavMesh work.")]
        public bool cullRenderersBeyondReducedRadius = true;
        [Tooltip("Inside fullSimulationRadius, NPCs cast shadows normally. Beyond it (still within reducedSimulationRadius), shadow casting is turned off — shadows are one of the more expensive parts of a skinned character on Quest-class hardware.")]
        public bool disableShadowsBeyondFullRadius = true;

        [Tooltip("How often (seconds) each registered NPC is re-checked. Staggered automatically so not all NPCs check the same frame.")]
        public float checkInterval = 0.5f;

        public Transform player;

        private struct Entry
        {
            public NPCRoutineController routine;
            public Animator animator;
            public AudioSource[] audioSources;
            public Renderer[] renderers;
            public float nextCheckTime;
            public bool wasFull;
            public bool wasReduced;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public void Register(GameObject npc)
        {
            var entry = new Entry
            {
                routine = npc.GetComponent<NPCRoutineController>(),
                animator = npc.GetComponentInChildren<Animator>(),
                audioSources = npc.GetComponentsInChildren<AudioSource>(),
                renderers = npc.GetComponentsInChildren<Renderer>(),
                nextCheckTime = Time.time + Random.Range(0f, checkInterval), // stagger
                wasFull = true,
                wasReduced = true
            };
            _entries.Add(entry);
        }

        private void Update()
        {
            if (player == null) return;
            float now = Time.time;

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (now < e.nextCheckTime) continue;
                e.nextCheckTime = now + checkInterval;
                _entries[i] = e;

                float dist = Vector3.Distance(player.position, e.routine != null ? e.routine.transform.position : transform.position);

                bool full = dist <= fullSimulationRadius;
                bool reduced = dist <= reducedSimulationRadius;

                if (e.routine != null) e.routine.enabled = full || reduced;
                if (e.animator != null) e.animator.enabled = full || reduced;
                if (e.animator != null) e.animator.cullingMode = full
                    ? AnimatorCullingMode.AlwaysAnimate
                    : AnimatorCullingMode.CullUpdateTransforms;

                if (e.audioSources != null)
                    foreach (var src in e.audioSources)
                        if (src != null) src.mute = !full;

                // Rendering-side culling: only touch renderers when this entry's
                // full/reduced state actually changed since the last check, so we
                // don't re-write renderer.enabled / shadowCastingMode every single
                // check tick for NPCs that haven't moved between bands.
                if (e.renderers != null && (full != e.wasFull || reduced != e.wasReduced))
                {
                    bool visible = !cullRenderersBeyondReducedRadius || full || reduced;
                    var shadowMode = (!disableShadowsBeyondFullRadius || full)
                        ? UnityEngine.Rendering.ShadowCastingMode.On
                        : UnityEngine.Rendering.ShadowCastingMode.Off;

                    foreach (var r in e.renderers)
                    {
                        if (r == null) continue;
                        r.enabled = visible;
                        r.shadowCastingMode = shadowMode;
                    }
                }

                e.wasFull = full;
                e.wasReduced = reduced;
                _entries[i] = e;
            }
        }
    }
}
