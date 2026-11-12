using System.Collections.Generic;
using UnityEngine;

namespace CityHall.Systems
{
    /// <summary>
    /// A trigger volume that crossfades a layered ambience mix when the player
    /// (XR rig's CharacterController/trigger collider) enters or exits.
    /// Cover the town with a handful of overlapping BoxColliders (isTrigger) tagged
    /// "Residential", "Commercial", "CityHallExterior", "CityHallInterior" and this
    /// handles the crossfade — no per-layer scripting needed elsewhere.
    ///
    /// Layers map directly to the brief's ambient audio spec, e.g. Residential =
    /// birds + wind + distant lawn mower + children + dogs; Commercial = traffic +
    /// crosswalk beeps + restaurant chatter + coffee machines + doors.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class AmbientAudioZone : MonoBehaviour
    {
        [System.Serializable]
        public class Layer
        {
            public string label;              // "birds", "distant lawn mower", "crosswalk beeps"...
            public AudioClip clip;
            [Range(0f, 1f)] public float targetVolume = 0.5f;
            public bool loop = true;
            [Tooltip("Small random pitch/volume jitter so looped layers don't feel metronomic.")]
            public bool humanizeLoop = true;
        }

        public List<Layer> layers = new List<Layer>();
        public float crossfadeSeconds = 2.5f;
        public string playerTag = "Player";

        private readonly List<AudioSource> _sources = new List<AudioSource>();
        private float _targetMix; // 0 = silent (player outside), 1 = full mix (player inside)
        private float _currentMix;

        private void Awake()
        {
            var col = GetComponent<Collider>();
            col.isTrigger = true;

            foreach (var layer in layers)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.clip = layer.clip;
                src.loop = layer.loop;
                src.spatialBlend = 0f; // ambience beds are typically non-positional; set >0 per-layer if desired
                src.volume = 0f;
                src.playOnAwake = false;
                if (layer.humanizeLoop) src.pitch = Random.Range(0.97f, 1.03f);
                src.Play();
                _sources.Add(src);
            }
        }

        private void Update()
        {
            if (_sources.Count == 0) return;
            _currentMix = Mathf.MoveTowards(_currentMix, _targetMix, Time.deltaTime / Mathf.Max(0.01f, crossfadeSeconds));
            for (int i = 0; i < _sources.Count; i++)
                _sources[i].volume = layers[i].targetVolume * _currentMix;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag(playerTag)) _targetMix = 1f;
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.CompareTag(playerTag)) _targetMix = 0f;
        }
    }
}
