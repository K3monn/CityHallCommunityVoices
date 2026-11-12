using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using CityHall.Systems;

namespace CityHall.NPC
{
    /// <summary>
    /// Gives each citizen a believable, non-scripted-looking routine that changes
    /// with TimeOfDayManager's current period (Morning / LateMorning / Afternoon /
    /// Evening). Reads its waypoint sets from a RoutineDefinition so the same
    /// script drives a dog walker, a shopkeeper, or a bench-sitter.
    ///
    /// Requires a baked NavMesh covering the walkable town area (Window > AI >
    /// Navigation in the Editor, then "Bake" once street/sidewalk geometry exists).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NPCRoutineController : MonoBehaviour
    {
        [System.Serializable]
        public class RoutinePoint
        {
            public Transform waypoint;
            [Tooltip("Seconds to idle here before moving to the next point. 0 = keep walking through.")]
            public float dwellSeconds = 3f;
            [Tooltip("Optional Animator trigger to play while dwelling, e.g. \"SitBench\", \"TalkGesture\", \"ReadBook\".")]
            public string dwellAnimTrigger = "";
        }

        [Header("Routine source")]
        [Tooltip("Looked up in RoutineDefinitions.json by routineId on NPCIdentity if this list is left empty.")]
        public List<RoutinePoint> morningRoute = new List<RoutinePoint>();
        public List<RoutinePoint> lateMorningRoute = new List<RoutinePoint>();
        public List<RoutinePoint> afternoonRoute = new List<RoutinePoint>();
        public List<RoutinePoint> eveningRoute = new List<RoutinePoint>();

        [Header("Movement")]
        public float walkSpeed = 1.35f;
        public float arriveThreshold = 0.3f;

        [Header("Ambient wander (used automatically for any period whose route above is left empty)")]
        [Tooltip("No hand-placed waypoints are required for this — it samples random points on the baked NavMesh within wanderRadius of where the NPC spawned. This is what actually makes NPCs walk around: morningRoute/lateMorningRoute/afternoonRoute/eveningRoute are empty by default (TownSceneBuilder never hand-places waypoint Transforms — that would need the same per-NPC placement care as house lots), so without this fallback every NPC just stands still forever.")]
        public float wanderRadius = 9f;
        public float minDwellSeconds = 2f;
        public float maxDwellSeconds = 6f;

        [Header("Keep-clear zone (optional)")]
        [Tooltip("Set by NPCSpawner at spawn time for zones that need it (e.g. the City Hall " +
                 "plaza's spawner points this at the entrance portal) so ambient wander never " +
                 "picks a destination that would leave an NPC standing in a doorway/threshold. " +
                 "Left null for most NPCs — wander is unrestricted by default.")]
        public Transform keepClearAnchor;
        public float keepClearRadius = 3f;

        private NavMeshAgent _agent;
        private Animator _animator;
        private List<RoutinePoint> _currentRoute;
        private int _routeIndex;
        private float _dwellTimer;
        private DayPeriod _lastPeriod = (DayPeriod)(-1);
        private Vector3 _spawnPosition;
        private bool _wandering;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();
            _agent.speed = walkSpeed;
            _spawnPosition = transform.position;
        }

        private void OnEnable()
        {
            if (TimeOfDayManager.Instance != null)
                TimeOfDayManager.Instance.OnPeriodChanged += HandlePeriodChanged;
        }

        private void OnDisable()
        {
            if (TimeOfDayManager.Instance != null)
                TimeOfDayManager.Instance.OnPeriodChanged -= HandlePeriodChanged;
        }

        private void Start()
        {
            if (TimeOfDayManager.Instance != null)
                HandlePeriodChanged(TimeOfDayManager.Instance.CurrentPeriod);
        }

        private void HandlePeriodChanged(DayPeriod period)
        {
            if (period == _lastPeriod) return;
            _lastPeriod = period;

            _currentRoute = period switch
            {
                DayPeriod.Morning => morningRoute,
                DayPeriod.LateMorning => lateMorningRoute,
                DayPeriod.Afternoon => afternoonRoute,
                DayPeriod.Evening => eveningRoute,
                // 2026-07-23, ROUND 3: new DayPeriod.Night (real night restored in
                // TimeOfDayManager) reuses eveningRoute rather than a dedicated field — both are
                // empty by default anyway (see wanderRadius comment above), so this just keeps
                // whatever hand-authored evening waypoints exist active through the night too,
                // instead of silently falling back to morningRoute via the default arm below.
                DayPeriod.Night => eveningRoute,
                _ => morningRoute
            };
            _routeIndex = 0;
            _dwellTimer = 0f;
            _wandering = _currentRoute == null || _currentRoute.Count == 0;

            if (_wandering)
                PickNewWanderDestination();
            else if (_currentRoute[0].waypoint != null)
                _agent.SetDestination(_currentRoute[0].waypoint.position);
        }

        private void Update()
        {
            if (_wandering)
            {
                UpdateWander();
            }
            else
            {
                if (_currentRoute == null || _currentRoute.Count == 0) return;
                UpdateAuthoredRoute();
            }

            if (_animator != null)
                _animator.SetFloat("Speed", _agent.velocity.magnitude);
        }

        // Distance-based culling (see PerformanceLODManager) disables this
        // component outside the activation radius, so Update only runs for
        // NPCs actually worth simulating this frame — applies to both paths below.

        private void UpdateAuthoredRoute()
        {
            if (_dwellTimer > 0f)
            {
                _dwellTimer -= Time.deltaTime;
                return;
            }

            if (!_agent.pathPending && _agent.remainingDistance <= arriveThreshold)
            {
                var point = _currentRoute[_routeIndex];
                if (point.dwellSeconds > 0f)
                {
                    _dwellTimer = point.dwellSeconds;
                    if (_animator != null && !string.IsNullOrEmpty(point.dwellAnimTrigger))
                        _animator.SetTrigger(point.dwellAnimTrigger);
                }

                _routeIndex = (_routeIndex + 1) % _currentRoute.Count;
                var next = _currentRoute[_routeIndex];
                if (next.waypoint != null)
                    _agent.SetDestination(next.waypoint.position);
            }
        }

        private void UpdateWander()
        {
            if (_dwellTimer > 0f)
            {
                _dwellTimer -= Time.deltaTime;
                return;
            }

            if (!_agent.pathPending && _agent.remainingDistance <= arriveThreshold)
            {
                // Deliberately no animator trigger fired here (unlike the authored-route
                // path, which only fires a trigger when dwellAnimTrigger is explicitly
                // set): we don't know what idle-animation trigger names, if any, exist
                // on the imported character Animator Controllers, and Animator.SetTrigger
                // on a nonexistent parameter logs a console warning every dwell cycle for
                // every wandering NPC. The Speed float set below still drives whatever
                // walk/idle blend the controller already has wired to it.
                _dwellTimer = Random.Range(minDwellSeconds, maxDwellSeconds);
                PickNewWanderDestination();
            }
        }

        /// <summary>Samples a random point within wanderRadius of the spawn spot and
        /// snaps it onto the baked NavMesh so the destination is always walkable —
        /// falls back to the spawn point itself if no valid point is found nearby
        /// (e.g. wanderRadius pushed the sample off the NavMesh entirely).</summary>
        private void PickNewWanderDestination()
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Vector2 offset2D = Random.insideUnitCircle * wanderRadius;
                Vector3 candidate = _spawnPosition + new Vector3(offset2D.x, 0f, offset2D.y);

                // Skip candidates inside the keep-clear zone (if one is set) — horizontal-only
                // distance check, since keepClearAnchor's Y (e.g. a portal frame's collider
                // center) doesn't need to match ground level for this to work correctly.
                if (keepClearAnchor != null)
                {
                    float dx = candidate.x - keepClearAnchor.position.x;
                    float dz = candidate.z - keepClearAnchor.position.z;
                    if (dx * dx + dz * dz < keepClearRadius * keepClearRadius) continue;
                }

                if (NavMesh.SamplePosition(candidate, out var hit, 3f, NavMesh.AllAreas))
                {
                    _agent.SetDestination(hit.position);
                    return;
                }
            }
            _agent.SetDestination(_spawnPosition);
        }
    }
}
