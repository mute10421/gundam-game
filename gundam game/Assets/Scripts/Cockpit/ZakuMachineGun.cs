using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The Zaku's machine gun - per request ("자쿠의 머신건을 ... 자쿠손에 들려주고
    /// 사격을 하게 해줘 자쿠 머신건에 데미지는 50이야 탄환은 100발이야 재장전속도는
    /// 5초야 분당 280발에 속도로 총알이 나가"), held in a real FIRING STANCE (per
    /// "자쿠가 총쏘는 자세로 자쿠머신건을 잡고 쏘는거야"):
    ///   * right fist on the pistol grip, LEFT fist on the fore grip under the
    ///     barrel (two-hand hold, both real arms by two-bone IK);
    ///   * stock tucked in at the right shoulder, barrel at shoulder height,
    ///     pointed at the Gundam's chest;
    ///   * bladed stance - the upper body twists so the left shoulder comes
    ///     forward, and leans slightly into the gun.
    /// If the fore grip is out of the left arm's reach the gun slides back
    /// toward the body until it isn't, so the left hand never floats.
    ///
    /// While the player's Gundam is within engageRange the Zaku raises the gun
    /// into that stance (blended over the walk animation from ZakuCombatAI) and
    /// fires in bursts at 280 rounds/min while the barrel is on target. 100-round
    /// magazine, 5 s reload, 50 damage per round (ZakuBullet -> PlayerHealth).
    /// Out of range it lowers the gun and carries it in the right hand.
    ///
    /// Runs after ZakuCombatAI (execution order 100) so the stance sits on top
    /// of the procedural walk. Stops while the Zaku is destroyed.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ZakuMachineGun : MonoBehaviour
    {
        [Header("Bones (Zaku)")]
        public Transform upperArm;      // RightArm
        public Transform foreArm;       // RightForeArm
        public Transform hand;          // RightHand
        public Transform leftUpperArm;  // LeftArm
        public Transform leftForeArm;   // LeftForeArm
        public Transform leftHand;      // LeftHand
        [Tooltip("Upper-body bone twisted for the bladed stance (Spine2, else Spine1).")]
        public Transform chest;
        [Tooltip("Head - turned back by the stance twist so it keeps looking at the target.")]
        public Transform head;

        [Header("Gun (gun-local, m: +Z muzzle, +Y top)")]
        public Transform gun;
        public Vector3 gripPoint = new Vector3(0f, -0.9f, -1.36f);
        public Vector3 gripThumb = new Vector3(0f, 0.91f, 0.41f);
        public Vector3 gripFingers = new Vector3(0f, -0.41f, 0.91f);
        [Tooltip("Fore grip under the barrel - the left hand.")]
        public Vector3 supportPoint = new Vector3(0f, -0.72f, 1.75f);
        [Tooltip("Left hand on the fore grip: thumb along the barrel, fingers wrapped across (palm up).")]
        public Vector3 supportThumb = new Vector3(0f, 0f, 1f);
        public Vector3 supportFingers = new Vector3(1f, 0f, 0f);
        public Vector3 muzzlePoint = new Vector3(0f, -0.12f, 4f);
        [Tooltip("Grip point inside the Zaku's closed right fist (hand bone axes, m). The left fist's is mirrored from it.")]
        public Vector3 fistGrip = new Vector3(-0.09f, 0.08f, 0.88f);

        [Header("Firing stance (m, from the right shoulder, along the aim)")]
        [Tooltip("Right hand on the grip: this far ahead of the right shoulder...")]
        public float gripForward = 2.1f;
        [Tooltip("...this far below it (barrel ends up about shoulder height)...")]
        public float gripDrop = 1.0f;
        [Tooltip("...and this far in toward the body's center line (stock at the shoulder pocket).")]
        public float gripInward = 0.9f;
        [Tooltip("Upper body twist (deg) bringing the left shoulder forward.")]
        public float stanceTwist = 30f;
        [Tooltip("Upper body lean into the gun (deg).")]
        public float stanceLean = 6f;

        [Header("Target")]
        public PlayerHealth target;
        [Tooltip("Aim this high above the target's root (m) - chest.")]
        public float aimHeight = 11f;
        public float engageRange = 260f;
        [Tooltip("Only fires while the barrel is within this angle (deg) of the target.")]
        public float fireCone = 10f;
        public float raiseTime = 0.5f;

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
        Quaternion _handRest, _leftHandRest, _chestRest;
        float _l1, _l2, _ll1, _ll2;
        Vector3 _leftFist;            // left fist grip point (left hand bone axes, m)
        Quaternion _leftFistRot = Quaternion.identity;
        bool _init, _haveLeft;
        float _aim;                   // 0 lowered .. 1 in stance
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
                _handRest = hand.localRotation;
                _l1 = Vector3.Distance(upperArm.position, foreArm.position);
                _l2 = Vector3.Distance(foreArm.position, hand.position);
                _init = true;
            }
            if (_init && leftUpperArm != null && leftForeArm != null && leftHand != null)
            {
                _leftHandRest = leftHand.localRotation;
                _ll1 = Vector3.Distance(leftUpperArm.position, leftForeArm.position);
                _ll2 = Vector3.Distance(leftForeArm.position, leftHand.position);
                // Mirror the right fist's grip across the Zaku's left/right plane (rest pose).
                Vector3 rp = hand.position + hand.rotation * fistGrip;
                Vector3 lp = Mirror(rp, true);
                Vector3 ly = Mirror(hand.up, false);
                Vector3 lz = Mirror(hand.forward, false);
                _leftFist = Quaternion.Inverse(leftHand.rotation) * (lp - leftHand.position);
                _leftFistRot = Quaternion.Inverse(leftHand.rotation) * Quaternion.LookRotation(lz, ly);
                _haveLeft = true;
            }
            if (chest != null) _chestRest = chest.localRotation;
            _burstLeft = Random.Range(burstRounds.x, burstRounds.y + 1);
            _burstResume = Time.time + Random.Range(0.5f, 2f);
        }

        Vector3 Mirror(Vector3 v, bool point)
        {
            Vector3 l = point ? transform.InverseTransformPoint(v) : transform.InverseTransformDirection(v);
            l.x = -l.x;
            return point ? transform.TransformPoint(l) : transform.TransformDirection(l);
        }

        void LateUpdate()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            bool dead = _health != null && _health.IsDead;

            if (Reloading && Time.time >= _reloadEnd) { Reloading = false; Ammo = magazine; }
            // ZakuCombatAI re-poses the arms from rest every frame, but not the
            // hands or Spine2.
            hand.localRotation = _handRest;
            if (_haveLeft) leftHand.localRotation = _leftHandRest;
            bool chestIsOwn = chest != null && chest.name != "Spine1" && chest.name != "Spine";
            if (chestIsOwn) chest.localRotation = _chestRest;

            Vector3 aimPoint = target != null ? target.transform.position + Vector3.up * aimHeight : Vector3.zero;
            bool engaged = !dead && target != null && !target.IsDown
                && (aimPoint - upperArm.position).sqrMagnitude < engageRange * engageRange;
            _aim = Mathf.MoveTowards(_aim, engaged ? 1f : 0f, dt / Mathf.Max(0.05f, raiseTime));
            float w = Mathf.SmoothStep(0f, 1f, _aim);

            Quaternion gripFrame = Quaternion.LookRotation(gripFingers.normalized, gripThumb.normalized);

            if (w > 0f && target != null)
            {
                // Bladed stance: left shoulder forward, lean into the gun.
                if (chest != null)
                {
                    chest.rotation = Quaternion.AngleAxis(stanceTwist * w, Vector3.up) * chest.rotation;
                    chest.rotation = Quaternion.AngleAxis(stanceLean * w, transform.right) * chest.rotation;
                    if (head != null) head.rotation = Quaternion.AngleAxis(-stanceTwist * w, Vector3.up) * head.rotation;
                }

                Vector3 want = (aimPoint - upperArm.position).normalized;
                float k = 1f - Mathf.Exp(-dt * 10f);
                _aimDir = _haveAim ? Vector3.Slerp(_aimDir, want, k).normalized : want;
                _haveAim = true;

                Quaternion gunRot = Quaternion.LookRotation(_aimDir, Vector3.up);
                Vector3 side = Vector3.Cross(Vector3.up, _aimDir);
                side = side.sqrMagnitude > 1e-4f ? side.normalized : transform.right;
                Vector3 grip = upperArm.position + _aimDir * gripForward - Vector3.up * gripDrop - side * gripInward;
                Vector3 gunPos = grip - gunRot * gripPoint;

                // Keep the fore grip within the left arm's reach: slide the gun back.
                if (_haveLeft)
                {
                    Quaternion supRot = gunRot * Quaternion.LookRotation(supportFingers.normalized, supportThumb.normalized);
                    Quaternion lHandRot = supRot * Quaternion.Inverse(_leftFistRot);
                    float maxReach = 0.96f * (_ll1 + _ll2);
                    for (int it = 0; it < 4; it++)
                    {
                        Vector3 lWrist = gunPos + gunRot * supportPoint - lHandRot * _leftFist;
                        float over = Vector3.Distance(leftUpperArm.position, lWrist) - maxReach;
                        if (over <= 0f) break;
                        gunPos -= _aimDir * (over + 0.05f);
                    }
                }
                grip = gunPos + gunRot * gripPoint;

                // Right hand onto the grip.
                Quaternion handRot = gunRot * gripFrame;
                Vector3 wrist = grip - handRot * fistGrip;
                Vector3 pole = (transform.right * 0.6f - transform.up * 0.7f - transform.forward * 0.2f).normalized;
                SolveTwoBone(upperArm, foreArm, hand, _l1, _l2, wrist, pole, out Quaternion uRot, out Quaternion fRot);
                upperArm.rotation = Quaternion.Slerp(upperArm.rotation, uRot, w);
                foreArm.rotation = Quaternion.Slerp(foreArm.rotation, fRot, w);
                hand.rotation = Quaternion.Slerp(hand.rotation, handRot, w);
            }
            else _haveAim = false;

            // The gun always sits in the right fist, wherever the hand is.
            if (gun != null)
            {
                Quaternion gunRot = hand.rotation * Quaternion.Inverse(gripFrame);
                gun.rotation = gunRot;
                gun.position = hand.position + hand.rotation * fistGrip - gunRot * gripPoint;

                // Left hand onto the fore grip of wherever the gun now is.
                if (_haveLeft && w > 0f)
                {
                    Quaternion supRot = gunRot * Quaternion.LookRotation(supportFingers.normalized, supportThumb.normalized);
                    Quaternion lHandRot = supRot * Quaternion.Inverse(_leftFistRot);
                    Vector3 lWrist = gun.TransformPoint(supportPoint) - lHandRot * _leftFist;
                    Vector3 lPole = (-transform.right * 0.6f - transform.up * 0.7f - transform.forward * 0.1f).normalized;
                    SolveTwoBone(leftUpperArm, leftForeArm, leftHand, _ll1, _ll2, lWrist, lPole, out Quaternion luRot, out Quaternion lfRot);
                    leftUpperArm.rotation = Quaternion.Slerp(leftUpperArm.rotation, luRot, w);
                    leftForeArm.rotation = Quaternion.Slerp(leftForeArm.rotation, lfRot, w);
                    leftHand.rotation = Quaternion.Slerp(leftHand.rotation, lHandRot, w);
                }
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

        static void SolveTwoBone(Transform upper, Transform fore, Transform hnd, float l1, float l2, Vector3 target, Vector3 pole,
            out Quaternion upperRot, out Quaternion foreRot)
        {
            Vector3 a = upper.position;
            Vector3 d = target - a;
            float dist = Mathf.Clamp(d.magnitude, 0.15f * (l1 + l2), 0.999f * (l1 + l2));
            Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.down;
            Vector3 bendAxis = Vector3.ProjectOnPlane(pole, dir);
            if (bendAxis.sqrMagnitude < 1e-6f) bendAxis = Vector3.ProjectOnPlane(Vector3.down, dir);
            bendAxis.Normalize();
            float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 elbow = a + dir * (l1 * cosA) + bendAxis * (l1 * sinA);
            Vector3 wrist = a + dir * dist;
            Vector3 curUpper = fore.position - a;
            upperRot = Quaternion.FromToRotation(curUpper, elbow - a) * upper.rotation;
            Quaternion upperDelta = upperRot * Quaternion.Inverse(upper.rotation);
            Vector3 elbowPos = a + upperDelta * curUpper;
            Vector3 curFore = upperDelta * (hnd.position - fore.position);
            foreRot = Quaternion.FromToRotation(curFore, wrist - elbowPos) * (upperDelta * fore.rotation);
        }
    }
}
