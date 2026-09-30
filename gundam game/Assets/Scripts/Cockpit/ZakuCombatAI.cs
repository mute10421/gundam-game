using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Combat maneuvering for the enemy Zaku - per request ("자쿠에게 움직임을
    /// 주고", option "전투 기동"): fights at a distance that changes with every
    /// maneuver (charges in close, mid-range, pulls far back - see varyDistance,
    /// per "자쿠가 너무 거리유지함") from the player's Gundam, sidesteps left/right in bursts (with the odd pause or
    /// dash, re-rolled every few seconds so it's hard to track), and always turns
    /// to face the Gundam.
    ///
    /// The Zaku FBX ships with a skeleton but no animation clips, so the walk is
    /// procedural (LateUpdate): legs swing and knees bend along the direction of
    /// travel, arms counter-swing, the torso leans into the motion and the hips
    /// bob - all proportional to speed, fading to a light idle sway when still.
    /// Every bone is reset to its rest pose each frame first, so nothing drifts.
    ///
    /// SMOOTHER MOTION (per "자쿠에 움직임을 조금더 자연스럽게 해주고 3마리정도"):
    ///   * velocity eases in/out (critically damped, velocitySmoothTime) instead
    ///     of changing at a fixed rate, and a sidestep reversal slows through a
    ///     stop instead of flipping;
    ///   * a slow per-Zaku noise (wander) varies strafe/approach speed so no two
    ///     Zakus - and no two passes - move identically;
    ///   * several Zakus keep apart (separationRadius) instead of stacking up;
    ///   * the body turns with a damped turn and leans its heading a little into
    ///     the direction it's moving, while the torso and head twist back to
    ///     keep watching the Gundam;
    ///   * the walk adds a hip roll / weight shift with each step.
    ///
    /// COLONY BATTLEFIELD (arena, per "콜로니 ... 거기를 전장으로"): the Zaku
    /// guards its spot in the colony city - it patrols slowly around where it
    /// spawned until the Gundam comes within aggroRange, then fights as above.
    /// It stays on the city floor inside the colony and walks around buildings
    /// (ColonyStructure.ResolveWalker), so it never leaves the colony to chase
    /// the Gundam across open space.
    ///
    /// Only moves/poses its OWN transform and bones - never the player's suit,
    /// cameras or XR rig. Stops while EnemyHealth reports it destroyed and
    /// resets its velocity when it respawns.
    /// </summary>
    public class ZakuCombatAI : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The player's visible Gundam (ExternalGundam). If empty, found by name at runtime.")]
        public Transform target;
        public string targetName = "ExternalGundam";

        [Header("Engagement distance")]
        [Tooltip("Distance (m) the Zaku tries to hold from the target.")]
        public float preferredDistance = 60f;
        [Tooltip("How far (m) off the preferred distance before it closes/opens at full approach speed.")]
        public float distanceTolerance = 8f;

        [Header("Distance variation (per \"자쿠가 너무 거리유지함\")")]
        [Tooltip("When on, every maneuver also picks a NEW distance to fight at, so the Zaku keeps closing in and pulling back instead of holding one fixed range.")]
        public bool varyDistance = true;
        [Tooltip("Normal range (m) a new fighting distance is picked from.")]
        public Vector2 distanceRange = new Vector2(30f, 85f);
        [Range(0f, 1f)] [Tooltip("Chance a maneuver is a CHARGE - rushes in close.")]
        public float chargeChance = 0.25f;
        public Vector2 chargeDistance = new Vector2(18f, 26f);
        [Range(0f, 1f)] [Tooltip("Chance a maneuver is a RETREAT - backs far off.")]
        public float retreatChance = 0.15f;
        public Vector2 retreatDistance = new Vector2(95f, 120f);
        [Tooltip("Never comes closer than this (m), so it can't walk into the Gundam.")]
        public float minDistance = 15f;
        [Tooltip("Approach speed multiplier while charging.")]
        public float chargeSpeedMultiplier = 2f;

        /// <summary>The distance the Zaku is currently trying to fight at.</summary>
        public float CurrentPreferredDistance { get; private set; }

        [Header("Maneuvering")]
        public float strafeSpeed = 9f;
        public float approachSpeed = 11f;
        [Tooltip("How quickly (m/s per second) velocity changes - lower = heavier-feeling suit.")]
        public float acceleration = 8f;
        [Tooltip("Turn rate (deg/s) toward the target.")]
        public float turnSpeed = 90f;
        [Tooltip("Random time range (s) between changes of sidestep direction.")]
        public Vector2 maneuverInterval = new Vector2(1.5f, 4f);
        [Range(0f, 1f)] public float pauseChance = 0.2f;
        [Range(0f, 1f)] public float dashChance = 0.25f;
        public float dashMultiplier = 1.8f;

        [Header("Natural motion")]
        [Tooltip("Time (s) velocity takes to settle on a new maneuver - higher = heavier, smoother.")]
        public float velocitySmoothTime = 0.8f;
        [Tooltip("Seconds a sidestep takes to reverse direction (passes through a stop).")]
        public float strafeReverseTime = 1.2f;
        [Tooltip("Random speed variation (0..1 of the base speeds).")]
        [Range(0f, 1f)] public float wanderAmount = 0.35f;
        public float wanderFrequency = 0.22f;
        [Tooltip("Other Zakus closer than this (m) push this one away.")]
        public float separationRadius = 40f;
        public float separationStrength = 12f;
        [Tooltip("Damped turn time (s) toward the target.")]
        public float turnSmoothTime = 0.45f;
        [Tooltip("How far (deg) the heading leans into a full-speed sidestep.")]
        public float headingLeadDegrees = 25f;
        [Tooltip("Share of that lean the torso twists back toward the target.")]
        [Range(0f, 1f)] public float torsoCounterTwist = 0.7f;
        public float hipRollDegrees = 4f;
        [Tooltip("Head keeps looking at the target (up to this many degrees off the body).")]
        public float headTrackDegrees = 45f;

        [Header("Colony battlefield")]
        [Tooltip("Keeps the Zaku on the colony's city floor and out of its buildings (optional).")]
        public ColonyStructure arena;
        [Tooltip("Body radius (m) for staying out of buildings.")]
        public float walkerRadius = 4.5f;
        [Tooltip("Only fights when the Gundam is closer than this (m); otherwise patrols around its spawn point.")]
        public float aggroRange = 450f;
        public float patrolRadius = 70f;
        public float patrolSpeed = 5f;

        [Header("Procedural walk")]
        public bool animateBody = true;
        [Tooltip("Metres covered per step.")]
        public float strideLength = 7f;
        public float legSwingDegrees = 28f;
        public float kneeBendDegrees = 40f;
        public float armSwingDegrees = 18f;
        public float elbowBendDegrees = 15f;
        public float torsoLeanDegrees = 6f;
        [Tooltip("Vertical hip bob (m) at full walking speed.")]
        public float hipBob = 0.35f;
        [Tooltip("Backward jolt (deg) of the torso on each hit.")]
        public float hitFlinchDegrees = 3f;

        public Vector3 Velocity { get; private set; }

        EnemyHealth _health;
        float _groundY;
        float _maneuverTimer;
        float _strafeDir = 1f;
        float _speedMul = 1f;
        float _approachMul = 1f;
        float _findTimer;
        float _strafeCur;
        Vector3 _home;
        Vector3 _patrolPoint;
        float _patrolTimer;
        Vector3 _velRef;
        float _yawVel;
        float _lead;
        float _seed;
        static readonly System.Collections.Generic.List<ZakuCombatAI> s_all = new System.Collections.Generic.List<ZakuCombatAI>();

        // Bones (all optional - missing ones are just skipped).
        Transform _head;
        Quaternion _headRest;
        Transform _hips, _spine, _lUpLeg, _lLeg, _rUpLeg, _rLeg, _lArm, _lForeArm, _rArm, _rForeArm;
        Transform[] _bones;
        Quaternion[] _rest;
        Vector3 _hipsRestLocalPos;
        float _phase;
        float _flinch;

        void Awake()
        {
            _groundY = transform.position.y;
            _home = transform.position;
            _patrolPoint = _home;
            _health = GetComponent<EnemyHealth>();

            _hips = FindBone("Hips");
            _spine = FindBone("Spine1") ?? FindBone("Spine");
            _lUpLeg = FindBone("LeftUpLeg");
            _lLeg = FindBone("LeftLeg");
            _rUpLeg = FindBone("RightUpLeg");
            _rLeg = FindBone("RightLeg");
            _lArm = FindBone("LeftArm");
            _lForeArm = FindBone("LeftForeArm");
            _rArm = FindBone("RightArm");
            _rForeArm = FindBone("RightForeArm");
            _head = FindBone("Head");
            if (_head != null) _headRest = _head.localRotation;
            _seed = Random.Range(0f, 1000f);

            // Parent-before-child order, so each child's world rotation already
            // includes its parent's offset when it's applied.
            _bones = new[] { _hips, _spine, _lUpLeg, _lLeg, _rUpLeg, _rLeg, _lArm, _lForeArm, _rArm, _rForeArm };
            _rest = new Quaternion[_bones.Length];
            for (int i = 0; i < _bones.Length; i++) if (_bones[i] != null) _rest[i] = _bones[i].localRotation;
            if (_hips != null) _hipsRestLocalPos = _hips.localPosition;

            PickManeuver();
            _maneuverTimer *= Random.Range(0.3f, 1f); // stagger several Zakus
        }

        void OnEnable()
        {
            if (!s_all.Contains(this)) s_all.Add(this);
            if (_health == null) _health = GetComponent<EnemyHealth>();
            if (_health != null)
            {
                _health.Respawned += OnRespawned;
                _health.Damaged += OnDamaged;
            }
        }

        void OnDisable()
        {
            s_all.Remove(this);
            if (_health != null)
            {
                _health.Respawned -= OnRespawned;
                _health.Damaged -= OnDamaged;
            }
        }

        void OnRespawned(EnemyHealth h)
        {
            Velocity = Vector3.zero;
            _velRef = Vector3.zero;
            _strafeCur = 0f;
            _phase = 0f;
            PickManeuver();
        }

        void OnDamaged(EnemyHealth h, int amount)
        {
            _flinch = 1f;
        }

        void Update()
        {
            if (target == null)
            {
                _findTimer -= Time.deltaTime;
                if (_findTimer <= 0f)
                {
                    _findTimer = 1f;
                    GameObject g = GameObject.Find(targetName);
                    if (g != null && g != gameObject) target = g.transform;
                }
            }

            if (_health != null && _health.IsDead)
            {
                Velocity = Vector3.zero;
                return;
            }
            if (target == null) return;

            float dt = Time.deltaTime;

            _maneuverTimer -= dt;
            if (_maneuverTimer <= 0f) PickManeuver();

            Vector3 toTarget = target.position - transform.position;
            toTarget.y = 0f;
            float dist = toTarget.magnitude;

            if (aggroRange > 0f && (target.position - transform.position).magnitude > aggroRange)
            {
                Patrol(dt);
                return;
            }
            Vector3 radial = dist > 0.01f ? toTarget / dist : transform.forward;
            Vector3 tangent = Vector3.Cross(Vector3.up, radial); // Zaku's own right when facing the target

            float wanted = Mathf.Max(minDistance, varyDistance ? CurrentPreferredDistance : preferredDistance);
            // Smooth (not linear-clamped) approach: eases off as it nears the wanted range.
            float err = (dist - wanted) / Mathf.Max(0.1f, distanceTolerance);
            float approach = (float)System.Math.Tanh(err) * approachSpeed * _approachMul;
            if (dist < minDistance) approach = -approachSpeed * 1.5f; // too close - back off hard

            // Sidestep: eases through a stop when reversing.
            float strafeTarget = _strafeDir * _speedMul;
            _strafeCur = Mathf.MoveTowards(_strafeCur, strafeTarget, dt * 2f / Mathf.Max(0.05f, strafeReverseTime));

            // Slow individual wander on both axes.
            float t = Time.time * wanderFrequency;
            float wStrafe = (Mathf.PerlinNoise(_seed, t) - 0.5f) * 2f * wanderAmount;
            float wApproach = (Mathf.PerlinNoise(_seed + 37.1f, t * 0.8f) - 0.5f) * 2f * wanderAmount;

            Vector3 desired = tangent * ((_strafeCur + wStrafe) * strafeSpeed)
                            + radial * (approach + wApproach * approachSpeed * 0.5f)
                            + Separation();
            desired.y = 0f;

            Velocity = Vector3.SmoothDamp(Velocity, desired, ref _velRef, Mathf.Max(0.05f, velocitySmoothTime));

            Vector3 p = transform.position + Velocity * dt;
            p.y = _groundY;
            if (arena != null) p = arena.ResolveWalker(p, 18f, walkerRadius);
            transform.position = p;

            if (dist > 0.01f)
            {
                // Heading: toward the target, leaning a little into a sidestep.
                float lateral = Vector3.Dot(Velocity, tangent) / Mathf.Max(0.1f, strafeSpeed);
                _lead = Mathf.Lerp(_lead, Mathf.Clamp(lateral, -1f, 1f) * headingLeadDegrees, 1f - Mathf.Exp(-dt * 3f));
                float wantYaw = Mathf.Atan2(radial.x, radial.z) * Mathf.Rad2Deg + _lead;
                float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, wantYaw, ref _yawVel,
                    Mathf.Max(0.05f, turnSmoothTime), Mathf.Max(1f, turnSpeed * 2f));
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        /// <summary>Out of the fight: stroll between random points around home.</summary>
        void Patrol(float dt)
        {
            _patrolTimer -= dt;
            Vector3 to = _patrolPoint - transform.position;
            to.y = 0f;
            if (_patrolTimer <= 0f || to.magnitude < 6f)
            {
                Vector2 rnd = Random.insideUnitCircle * patrolRadius;
                _patrolPoint = _home + new Vector3(rnd.x, 0f, rnd.y);
                _patrolTimer = Random.Range(6f, 14f);
                to = _patrolPoint - transform.position;
                to.y = 0f;
            }
            Vector3 desired = to.magnitude > 1f ? to.normalized * patrolSpeed : Vector3.zero;
            desired += Separation();
            desired.y = 0f;
            Velocity = Vector3.SmoothDamp(Velocity, desired, ref _velRef, Mathf.Max(0.05f, velocitySmoothTime) * 1.5f);
            Vector3 p = transform.position + Velocity * dt;
            p.y = _groundY;
            if (arena != null) p = arena.ResolveWalker(p, 18f, walkerRadius);
            transform.position = p;
            _lead = Mathf.Lerp(_lead, 0f, 1f - Mathf.Exp(-dt * 3f));
            if (Velocity.sqrMagnitude > 0.5f)
            {
                float wantYaw = Mathf.Atan2(Velocity.x, Velocity.z) * Mathf.Rad2Deg;
                float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, wantYaw, ref _yawVel, 0.8f, 60f);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        /// <summary>Push away from other Zakus that are too close.</summary>
        Vector3 Separation()
        {
            Vector3 push = Vector3.zero;
            for (int i = 0; i < s_all.Count; i++)
            {
                ZakuCombatAI o = s_all[i];
                if (o == null || o == this) continue;
                if (o._health != null && o._health.IsDead) continue;
                Vector3 d = transform.position - o.transform.position;
                d.y = 0f;
                float m = d.magnitude;
                if (m >= separationRadius) continue;
                Vector3 dir = m > 0.01f ? d / m : Quaternion.Euler(0f, _seed, 0f) * Vector3.forward;
                push += dir * separationStrength * (1f - m / separationRadius);
            }
            return push;
        }

        void PickManeuver()
        {
            _maneuverTimer = Random.Range(maneuverInterval.x, Mathf.Max(maneuverInterval.x, maneuverInterval.y));
            float roll = Random.value;
            if (roll < pauseChance)
            {
                _strafeDir = 0f;
                _speedMul = 1f;
            }
            else
            {
                _strafeDir = Random.value < 0.5f ? -1f : 1f;
                _speedMul = Random.value < dashChance ? dashMultiplier : 1f;
            }

            // New fighting distance for this maneuver: mostly anywhere in the
            // normal range, sometimes a close-in charge or a far pull-back.
            _approachMul = 1f;
            float d = Random.value;
            if (d < chargeChance)
            {
                CurrentPreferredDistance = Random.Range(chargeDistance.x, chargeDistance.y);
                _approachMul = chargeSpeedMultiplier;
            }
            else if (d < chargeChance + retreatChance)
            {
                CurrentPreferredDistance = Random.Range(retreatDistance.x, retreatDistance.y);
            }
            else
            {
                CurrentPreferredDistance = Random.Range(distanceRange.x, distanceRange.y);
            }
        }

        void LateUpdate()
        {
            if (!animateBody || _bones == null) return;

            float dt = Time.deltaTime;

            // Reset to rest pose first so offsets never accumulate.
            for (int i = 0; i < _bones.Length; i++) if (_bones[i] != null) _bones[i].localRotation = _rest[i];
            if (_hips != null) _hips.localPosition = _hipsRestLocalPos;

            if (_health != null && _health.IsDead) return;

            Vector3 v = Velocity;
            v.y = 0f;
            float speed = v.magnitude;
            float amount = Mathf.Clamp01(speed / Mathf.Max(0.1f, strafeSpeed));
            _phase += dt * speed / Mathf.Max(0.1f, strideLength) * Mathf.PI;
            _flinch = Mathf.MoveTowards(_flinch, 0f, dt * 8f);

            Vector3 moveDir = speed > 0.1f ? v / speed : transform.forward;
            // Rotating about this axis by +angle tips a downward limb backward
            // (and an upward torso forward) relative to the direction of travel.
            Vector3 swingAxis = Vector3.Cross(Vector3.up, moveDir).normalized;
            Vector3 bodyRight = transform.right;

            float s = Mathf.Sin(_phase);
            float c = Mathf.Cos(_phase);
            float leg = legSwingDegrees * amount * s;

            Rotate(_lUpLeg, leg, swingAxis);
            Rotate(_rUpLeg, -leg, swingAxis);
            Rotate(_lLeg, kneeBendDegrees * amount * Mathf.Max(0f, c), bodyRight);
            Rotate(_rLeg, kneeBendDegrees * amount * Mathf.Max(0f, -c), bodyRight);

            float arm = armSwingDegrees * amount * s;
            Rotate(_lArm, -arm, swingAxis);
            Rotate(_rArm, arm, swingAxis);
            float elbow = elbowBendDegrees * (0.4f + 0.6f * amount);
            Rotate(_lForeArm, -elbow, bodyRight);
            Rotate(_rForeArm, -elbow, bodyRight);

            float idleSway = Mathf.Sin(Time.time * 1.3f + _seed) * 1.2f * (1f - amount);
            Rotate(_spine, torsoLeanDegrees * amount + idleSway, swingAxis);
            // Torso twists back toward the target against the heading lean.
            Rotate(_spine, -_lead * torsoCounterTwist, Vector3.up);
            // Weight shift: hips roll toward the planted foot.
            Rotate(_hips, hipRollDegrees * amount * s, transform.forward);
            // Hit flinch: torso jolts back, away from the target it's facing.
            Rotate(_spine, -hitFlinchDegrees * _flinch, bodyRight);

            if (_hips != null && _hips.parent != null)
            {
                float bob = hipBob * amount * Mathf.Abs(c) - hipBob * amount * 0.5f;
                _hips.localPosition = _hipsRestLocalPos + _hips.parent.InverseTransformVector(Vector3.up * bob);
            }

            // Head keeps watching the target (yaw only, limited).
            if (_head != null)
            {
                _head.localRotation = _headRest;
                if (target != null)
                {
                    Vector3 to = target.position - _head.position;
                    to.y = 0f;
                    Vector3 fwd = transform.forward;
                    fwd.y = 0f;
                    if (to.sqrMagnitude > 0.01f && fwd.sqrMagnitude > 0.01f)
                    {
                        float a = Mathf.Clamp(Vector3.SignedAngle(fwd, to, Vector3.up) + _lead * torsoCounterTwist, -headTrackDegrees, headTrackDegrees);
                        Rotate(_head, a, Vector3.up); // what the spine's counter-twist left over
                    }
                }
            }
        }

        static void Rotate(Transform bone, float degrees, Vector3 worldAxis)
        {
            if (bone == null || Mathf.Abs(degrees) < 0.0001f) return;
            bone.rotation = Quaternion.AngleAxis(degrees, worldAxis) * bone.rotation;
        }

        Transform FindBone(string boneName)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName) return t;
            }
            return null;
        }
    }
}
