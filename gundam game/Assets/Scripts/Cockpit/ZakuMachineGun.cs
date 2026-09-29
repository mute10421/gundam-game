using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The Zaku's machine gun - per request ("자쿠의 머신건을 ... 자쿠손에 들려주고
    /// 사격을 하게 해줘 자쿠 머신건에 데미지는 50이야 탄환은 100발이야 재장전속도는
    /// 5초야 분당 280발에 속도로 총알이 나가").
    ///
    /// The downloaded gun model is held in the Zaku's right fist (the gun is
    /// posed from the hand every frame, so it moves with the walk and the arm).
    /// While the player's Gundam is within engageRange the Zaku raises the gun
    /// (two-bone IK on its real right arm, blended over the walk animation from
    /// ZakuCombatAI) and aims at the Gundam's body; it fires in bursts at 280
    /// rounds/min while the barrel is on target. 100-round magazine, 5 s reload,
    /// 50 damage per round (ZakuBullet -> PlayerHealth). A little spread keeps
    /// it from being a laser - a moving Gundam dodges a lot of it.
    ///
    /// Runs after ZakuCombatAI (execution order 100) so the aim sits on top of
    /// the procedural walk. Stops while the Zaku is destroyed.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ZakuMachineGun : MonoBehaviour
    {
        [Header("Bones (Zaku right arm)")]
        public Transform upperArm;
        public Transform foreArm;
        public Transform hand;

        [Header("Gun (gun-local, m: +Z muzzle, +Y top)")]
        public Transform gun;
        public Vector3 gripPoint = new Vector3(0f, -1.0f, -1.45f);
        public Vector3 gripThumb = new Vector3(0f, 0.91f, 0.41f);
        public Vector3 gripFingers = new Vector3(0f, -0.41f, 0.91f);
        public Vector3 muzzlePoint = new Vector3(0f, -0.14f, 4.5f);
        [Tooltip("Grip point inside the Zaku's closed right fist (hand bone axes, m).")]
        public Vector3 fistGrip = new Vector3(-0.09f, 0.08f, 0.88f);

        [Header("Target")]
        public PlayerHealth target;
        [Tooltip("Aim this high above the target's root (m) - chest.")]
        public float aimHeight = 11f;
        public float engageRange = 260f;
        [Tooltip("Only fires while the barrel is within this angle (deg) of the target.")]
        public float fireCone = 10f;
        public float raiseTime = 0.5f;
        [Tooltip("Grip position when aiming: along the aim from the shoulder (fraction of arm reach), and down.")]
        public float aimReach = 0.72f;
        public float aimDrop = 0.12f;

        [Header("Fire")]
        public int damage = 50;
        public int magazine = 100;
        public float reloadTime = 5f;
        public float roundsPerMinute = 280f;
        public float bulletSpeed = 320f;
        [Tooltip("Random spread (deg).")]
        public float spread = 1.4f;
        [Tooltip("Rounds per burst (min..max).")]
        public Vector2Int burstRounds = new Vector2Int(8, 22);
        [Tooltip("Pause between bursts (s, min..max).")]
        public Vector2 burstPause = new Vector2(0.8f, 2.4f);
        public Material tracerMaterial;
        public Material flashMaterial;
        public float tracerLength = 5f;
        public float tracerWidth = 0.35f;

        public int Ammo { get; private set; }
        public bool Reloading { get; private set; }

        EnemyHealth _health;
        Quaternion _upperRest, _foreRest, _handRest;
        float _l1, _l2;
        bool _init;
        float _aim;             // 0 lowered .. 1 raised
        float _nextShot;
        float _reloadEnd;
        int _burstLeft;
        float _burstResume;
        Vector3 _aimDir;
        bool _haveAim;

        void Awake()
        {
            _health = GetComponent<EnemyHealth>();
            Ammo = magazine;
            if (upperArm != null && foreArm != null && hand != null)
            {
                _upperRest = upperArm.localRotation;
                _foreRest = foreArm.localRotation;
                _handRest = hand.localRotation;
                _l1 = Vector3.Distance(upperArm.position, foreArm.position);
                _l2 = Vector3.Distance(foreArm.position, hand.position);
                _init = true;
            }
            _burstLeft = Random.Range(burstRounds.x, burstRounds.y + 1);
            _burstResume = Time.time + Random.Range(0.5f, 2f);
        }

        void LateUpdate()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            bool dead = _health != null && _health.IsDead;

            if (Reloading && Time.time >= _reloadEnd) { Reloading = false; Ammo = magazine; }
            // ZakuCombatAI re-poses the arm from rest every frame, but not the hand.
            hand.localRotation = _handRest;

            Vector3 aimPoint = target != null ? target.transform.position + Vector3.up * aimHeight : Vector3.zero;
            bool engaged = !dead && target != null && !target.IsDown
                && (aimPoint - upperArm.position).sqrMagnitude < engageRange * engageRange;
            _aim = Mathf.MoveTowards(_aim, engaged ? 1f : 0f, dt / Mathf.Max(0.05f, raiseTime));

            if (_aim > 0f && target != null)
            {
                Vector3 want = (aimPoint - upperArm.position).normalized;
                float k = 1f - Mathf.Exp(-dt * 10f);
                _aimDir = _haveAim ? Vector3.Slerp(_aimDir, want, k).normalized : want;
                _haveAim = true;

                // Grip target in front of the shoulder along the aim, a bit low.
                float reach = _l1 + _l2 + fistGrip.magnitude;
                Vector3 grip = upperArm.position + _aimDir * (reach * aimReach) - Vector3.up * (reach * aimDrop);
                Quaternion gunRot = Quaternion.LookRotation(_aimDir, Vector3.up);
                Quaternion handRot = gunRot * Quaternion.LookRotation(gripFingers.normalized, gripThumb.normalized);
                Vector3 wrist = grip - handRot * fistGrip;

                Vector3 pole = (transform.right * 0.5f - transform.up * 0.7f - transform.forward * 0.3f).normalized;
                SolveTwoBone(upperArm.position, wrist, pole, out Quaternion uRot, out Quaternion fRot);
                upperArm.rotation = Quaternion.Slerp(upperArm.rotation, uRot, _aim);
                foreArm.rotation = Quaternion.Slerp(foreArm.rotation, fRot, _aim);
                hand.rotation = Quaternion.Slerp(hand.rotation, handRot, _aim);
            }
            else _haveAim = false;

            // The gun always sits in the fist, wherever the hand is.
            if (gun != null)
            {
                Quaternion gunRot = hand.rotation * Quaternion.Inverse(Quaternion.LookRotation(gripFingers.normalized, gripThumb.normalized));
                gun.rotation = gunRot;
                gun.position = hand.position + hand.rotation * fistGrip - gunRot * gripPoint;
            }

            if (engaged && _aim >= 0.95f) TryFire(aimPoint);
        }

        void TryFire(Vector3 aimPoint)
        {
            if (Reloading || gun == null) return;
            if (Time.time < _burstResume || Time.time < _nextShot) return;

            Vector3 muzzle = gun.TransformPoint(muzzlePoint);
            Vector3 to = aimPoint - muzzle;
            if (Vector3.Angle(gun.forward, to) > fireCone) return;

            Vector3 dir = Quaternion.AngleAxis(Random.Range(0f, spread), Quaternion.AngleAxis(Random.Range(0f, 360f), to) * Vector3.Cross(to, Vector3.up).normalized) * to.normalized;
            SpawnBullet(muzzle, dir);
            _nextShot = Time.time + 60f / Mathf.Max(1f, roundsPerMinute);

            Ammo--;
            if (Ammo <= 0)
            {
                Ammo = 0;
                Reloading = true;
                _reloadEnd = Time.time + reloadTime;
            }
            if (--_burstLeft <= 0)
            {
                _burstLeft = Random.Range(burstRounds.x, burstRounds.y + 1);
                _burstResume = Time.time + Random.Range(burstPause.x, burstPause.y);
            }
        }

        void SpawnBullet(Vector3 muzzle, Vector3 dir)
        {
            GameObject b = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            b.name = "ZakuBullet";
            Collider c = b.GetComponent<Collider>();
            if (c != null) Destroy(c);
            Renderer r = b.GetComponent<Renderer>();
            if (tracerMaterial != null) r.sharedMaterial = tracerMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            b.transform.position = muzzle;
            b.transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(90f, 0f, 0f);
            b.transform.localScale = new Vector3(tracerWidth, tracerLength * 0.5f, tracerWidth);
            ZakuBullet zb = b.AddComponent<ZakuBullet>();
            zb.direction = dir;
            zb.speed = bulletSpeed;
            zb.damage = damage;
            zb.target = target;
            zb.sparkMaterial = flashMaterial;

            if (flashMaterial != null)
            {
                GameObject f = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                f.name = "ZakuMuzzleFlash";
                Collider fc = f.GetComponent<Collider>();
                if (fc != null) Destroy(fc);
                f.GetComponent<Renderer>().sharedMaterial = flashMaterial;
                f.transform.SetParent(gun, true);
                f.transform.position = muzzle;
                f.transform.localScale = Vector3.one * 1.6f;
                Destroy(f, 0.05f);
            }
        }

        void SolveTwoBone(Vector3 a, Vector3 target, Vector3 pole, out Quaternion upperRot, out Quaternion foreRot)
        {
            Vector3 d = target - a;
            float dist = Mathf.Clamp(d.magnitude, 0.15f * (_l1 + _l2), 0.999f * (_l1 + _l2));
            Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.down;
            Vector3 bendAxis = Vector3.ProjectOnPlane(pole, dir);
            if (bendAxis.sqrMagnitude < 1e-6f) bendAxis = Vector3.ProjectOnPlane(Vector3.down, dir);
            bendAxis.Normalize();
            float cosA = Mathf.Clamp((_l1 * _l1 + dist * dist - _l2 * _l2) / (2f * _l1 * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 elbow = a + dir * (_l1 * cosA) + bendAxis * (_l1 * sinA);
            Vector3 wrist = a + dir * dist;
            Vector3 curUpper = foreArm.position - a;
            upperRot = Quaternion.FromToRotation(curUpper, elbow - a) * upperArm.rotation;
            Quaternion upperDelta = upperRot * Quaternion.Inverse(upperArm.rotation);
            Vector3 elbowPos = a + upperDelta * curUpper;
            Vector3 curFore = upperDelta * (hand.position - foreArm.position);
            foreRot = Quaternion.FromToRotation(curFore, wrist - elbowPos) * (upperDelta * foreArm.rotation);
        }
    }
}
