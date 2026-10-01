using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Leg animation for the PILOT's own mobile suit body (GUNDAM or ZAKU) - per
    /// "건담도 걷는 애니메이션을 만들어야해" and "자쿠랑 건담 둘다 우주로 나가면
    /// 걷는거를 하면안됨". The FBX rigs have no animation clips, so the steps are
    /// procedural (the same idea as the enemy suits' walk in ZakuCombatAI):
    ///
    ///   * Standing on the colony floor (SuitCollision.Grounded) and moving: the
    ///     legs walk, step length matched to the real ground speed.
    ///   * Anywhere else - open space, or in the air inside the colony (T lever
    ///     climb, falling): NO walking - the legs hang relaxed, knees soft, swept
    ///     back with the flight speed.
    ///
    /// Only the leg bones are touched (never hips / spine / arms), so the head
    /// camera the pilot sees through never bobs, and the weapon arm IK is not
    /// disturbed. Runs after the body follower and before the weapon scripts.
    /// </summary>
    [DefaultExecutionOrder(-50)] // after ExternalGundamFollower (-60), before the weapon IK (0)
    public class MechWalkAnimator : MonoBehaviour
    {
        [Tooltip("Ground contact (colony floor) - walking only while grounded. No SuitCollision = never walks.")]
        public SuitCollision suit;
        [Tooltip("Metres covered per step.")]
        public float strideLength = 8f;
        public float legSwingDegrees = 26f;
        public float kneeBendDegrees = 38f;
        [Tooltip("Ground speed (m/s) at which the steps reach full size.")]
        public float fullStrideSpeed = 12f;
        [Header("Floating (space / airborne)")]
        public float floatKneeDegrees = 24f;
        public float floatTrailDegrees = 22f;
        [Tooltip("Flight speed (m/s) at which the legs are fully swept back.")]
        public float floatFullSpeed = 40f;
        public float blendTime = 0.35f;

        Transform _lUp, _lLeg, _rUp, _rLeg;
        Quaternion _lUpRest, _lLegRest, _rUpRest, _rLegRest;
        bool _init;
        Vector3 _lastPos;
        bool _haveLast;
        Vector3 _vel;
        float _phase;
        float _walk;   // 0 floating .. 1 walking

        void Awake()
        {
            _lUp = Find("LeftUpLeg"); _lLeg = Find("LeftLeg");
            _rUp = Find("RightUpLeg"); _rLeg = Find("RightLeg");
            _init = _lUp != null && _lLeg != null && _rUp != null && _rLeg != null;
            if (!_init) return;
            _lUpRest = _lUp.localRotation; _lLegRest = _lLeg.localRotation;
            _rUpRest = _rUp.localRotation; _rLegRest = _rLeg.localRotation;
        }

        void OnEnable() { _haveLast = false; }

        void LateUpdate()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            if (dt <= 1e-5f) return;

            Vector3 p = transform.position;
            if (_haveLast)
            {
                Vector3 v = (p - _lastPos) / dt;
                if (v.sqrMagnitude < 400f * 400f) _vel = Vector3.Lerp(_vel, v, 1f - Mathf.Exp(-dt * 10f)); // ignore teleports
            }
            _lastPos = p;
            _haveLast = true;

            _lUp.localRotation = _lUpRest; _lLeg.localRotation = _lLegRest;
            _rUp.localRotation = _rUpRest; _rLeg.localRotation = _rLegRest;

            bool grounded = suit != null && suit.Grounded;
            _walk = Mathf.MoveTowards(_walk, grounded ? 1f : 0f, dt / Mathf.Max(0.05f, blendTime));

            Vector3 hv = new Vector3(_vel.x, 0f, _vel.z);
            float speed = hv.magnitude;
            Vector3 moveDir = speed > 0.2f ? hv / speed : transform.forward;
            Vector3 swingAxis = Vector3.Cross(Vector3.up, moveDir).normalized;
            Vector3 right = transform.right;

            // Walking (on the colony floor).
            float amount = Mathf.Clamp01(speed / Mathf.Max(0.1f, fullStrideSpeed));
            _phase += dt * speed / Mathf.Max(0.1f, strideLength) * Mathf.PI;
            float s = Mathf.Sin(_phase), c = Mathf.Cos(_phase);
            float wk = _walk * amount;
            Rotate(_lUp, legSwingDegrees * wk * s, swingAxis);
            Rotate(_rUp, -legSwingDegrees * wk * s, swingAxis);
            Rotate(_lLeg, kneeBendDegrees * wk * Mathf.Max(0f, c), right);
            Rotate(_rLeg, kneeBendDegrees * wk * Mathf.Max(0f, -c), right);

            // Floating (space / in the air): relaxed legs trailing the flight.
            float fl = 1f - _walk;
            if (fl > 0.001f)
            {
                float flight = Mathf.Clamp01(_vel.magnitude / Mathf.Max(0.1f, floatFullSpeed));
                Vector3 fdir = _vel.sqrMagnitude > 0.04f ? _vel.normalized : transform.forward;
                Vector3 trailAxis = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(fdir, Vector3.up).sqrMagnitude > 0.01f
                    ? Vector3.ProjectOnPlane(fdir, Vector3.up).normalized : transform.forward).normalized;
                float drift = Mathf.Sin(Time.time * 0.8f) * 3f;
                Rotate(_lUp, fl * (floatTrailDegrees * flight + 5f + drift), trailAxis);
                Rotate(_rUp, fl * (floatTrailDegrees * flight - 2f - drift), trailAxis);
                Rotate(_lLeg, fl * floatKneeDegrees, right);
                Rotate(_rLeg, fl * floatKneeDegrees * 1.3f, right);
            }
        }

        static void Rotate(Transform bone, float degrees, Vector3 worldAxis)
        {
            if (bone == null || Mathf.Abs(degrees) < 0.0001f) return;
            bone.rotation = Quaternion.AngleAxis(degrees, worldAxis) * bone.rotation;
        }

        Transform Find(string n)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
            return null;
        }
    }
}
