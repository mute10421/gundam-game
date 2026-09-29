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

        // Bones (all optional - missing ones are just skipped).
        Transform _hips, _spine, _lUpLeg, _lLeg, _rUpLeg, _rLeg, _lArm, _lForeArm, _rArm, _rForeArm;
        Transform[] _bones;
        Quaternion[] _rest;
        Vector3 _hipsRestLocalPos;
        float _phase;
        float _flinch;

        void Awake()
        {
            _groundY = transform.position.y;
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

            // Parent-before-child order, so each child's world rotation already
            // includes its parent's offset when it's applied.
            _bones = new[] { _hips, _spine, _lUpLeg, _lLeg, _rUpLeg, _rLeg, _lArm, _lForeArm, _rArm, _rForeArm };
            _rest = new Quaternion[_bones.Length];
            for (int i = 0; i < _bones.Length; i++) if (_bones[i] != null) _rest[i] = _bones[i].localRotation;
            if (_hips != null) _hipsRestLocalPos = _hips.localPosition;

            PickManeuver();
        }

        void OnEnable()
        {
            if (_health == null) _health = GetComponent<EnemyHealth>();
            if (_health != null)
            {
                _health.Respawned += OnRespawned;
                _health.Damaged += OnDamaged;
            }
        }

        void OnDisable()
        {
            if (_health != null)
            {
                _health.Respawned -= OnRespawned;
                _health.Damaged -= OnDamaged;
            }
        }

        void OnRespawned(EnemyHealth h)
        {
            Velocity = Vector3.zero;
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
            Vector3 radial = dist > 0.01f ? toTarget / dist : transform.forward;
            Vector3 tangent = Vector3.Cross(Vector3.up, radial); // Zaku's own right when facing the target

            float wanted = Mathf.Max(minDistance, varyDistance ? CurrentPreferredDistance : preferredDistance);
            float approach = Mathf.Clamp((dist - wanted) / Mathf.Max(0.1f, distanceTolerance), -1f, 1f) * approachSpeed * _approachMul;
            if (dist < minDistance) approach = -approachSpeed * 1.5f; // too close - back off hard
            Vector3 desired = tangent * (_strafeDir * strafeSpeed * _speedMul) + radial * approach;

            Velocity = Vector3.MoveTowards(Velocity, desired, acceleration * dt);

            Vector3 p = transform.position + Velocity * dt;
            p.y = _groundY;
            transform.position = p;

            if (dist > 0.01f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(radial, Vector3.up), turnSpeed * dt);
            }
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

            float idleSway = Mathf.Sin(Time.time * 1.3f) * 1.2f * (1f - amount);
            Rotate(_spine, torsoLeanDegrees * amount + idleSway, swingAxis);
            // Hit flinch: torso jolts back, away from the target it's facing.
            Rotate(_spine, -hitFlinchDegrees * _flinch, bodyRight);

            if (_hips != null && _hips.parent != null)
            {
                float bob = hipBob * amount * Mathf.Abs(c) - hipBob * amount * 0.5f;
                _hips.localPosition = _hipsRestLocalPos + _hips.parent.InverseTransformVector(Vector3.up * bob);
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
