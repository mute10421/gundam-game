using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Melee weapon for an AI mobile suit - per "자쿠가 사용할 도끼 ... 자쿠손에 잡고
    /// 휘두러서 공격하게해줘" and "지금 내 건담처럼 ai가 골라서 전투": the enemy ZAKU
    /// carries its machine gun AND a heat hawk (the enemy GUNDAM: beam rifle AND beam
    /// saber) and decides by itself which one to fight with, the way the pilot picks
    /// a weapon on the WEAPON screen:
    ///
    ///   * Every decideInterval seconds it looks at the situation - can it see the
    ///     target, how far away is it, is the gun reloading - and picks MELEE or
    ///     RANGED. Very close it always draws the melee weapon, far away it always
    ///     shoots; in between the closer it is the more likely it charges in. A
    ///     reloading gun makes melee more likely too.
    ///   * MELEE: the gun is put away (ZakuMachineGun.holstered), the melee weapon
    ///     appears in the right hand and ZakuCombatAI closes in (rangeOverride).
    ///     Once within strikeRange it swings - wind up over the shoulder, a fast
    ///     diagonal chop down across the body, recover - then waits cooldown.
    ///   * The blade (weapon +Y, from bladeStart to bladeStart + bladeLength) is
    ///     swept during the chop and damages the player's suit once per swing
    ///     (PlayerHealth.CapsuleHit / TakeDamage).
    ///
    /// The weapon object is a child of the right hand bone (its grip at the fist);
    /// this only turns the right arm (2-bone IK) after the walk / gun scripts.
    /// The swing sound is 2D, louder the closer it is (the cockpit is far away from
    /// the battlefield, so a 3D sound would not be heard).
    /// </summary>
    [DefaultExecutionOrder(110)] // after ZakuCombatAI's walk (0) and ZakuMachineGun (100)
    public class MeleeWeaponAI : MonoBehaviour
    {
        [Header("Wiring")]
        public ZakuCombatAI ai;
        public ZakuMachineGun gun;
        public PlayerHealth target;
        public Transform upperArm;   // RightArm
        public Transform foreArm;    // RightForeArm
        public Transform hand;       // RightHand
        [Tooltip("Melee weapon root, child of the right hand: +Y = along the handle toward the blade/beam, +Z = cutting edge.")]
        public Transform weapon;

        [Header("Choosing the weapon")]
        public float decideInterval = 2.5f;
        [Tooltip("Closer than this (m) it always fights with the melee weapon.")]
        public float alwaysMeleeRange = 45f;
        [Tooltip("Farther than this (m) it always uses the gun.")]
        public float alwaysRangedRange = 120f;
        [Range(0f, 1f)] public float meleePreference = 0.55f;

        [Header("Attack")]
        public int damage = 250;
        [Tooltip("Starts a swing when the target is this close (m, horizontal).")]
        public float strikeRange = 24f;
        [Tooltip("Distance (m) it closes to while the melee weapon is out.")]
        public float approachDistance = 14f;
        public float swingTime = 1.0f;
        public float cooldown = 0.9f;
        [Tooltip("Blade along weapon +Y (m): start / length / hit radius.")]
        public float bladeStart = 1.5f;
        public float bladeLength = 5f;
        public float hitRadius = 1.2f;
        public float drawTime = 0.3f;

        [Header("Sound")]
        public AudioClip swingSound;
        [Range(0f, 1f)] public float swingVolume = 0.9f;
        public float soundFullVolumeRange = 80f;
        public float soundMaxRange = 900f;

        public bool Melee { get; private set; }
        public bool Swinging => _swingT >= 0f;

        EnemyHealth _health;
        float _l1, _l2;
        Vector3 _gripInHand;           // weapon grip point from the hand bone, hand axes (m)
        Quaternion _weaponInHand;      // weapon rotation relative to the hand bone
        bool _init;
        float _decide;
        float _w;                      // 0 holstered .. 1 drawn
        float _swingT = -1f;
        float _cool;
        bool _hitDone;
        Vector3 _prevBase, _prevTip;
        bool _havePrev;
        AudioSource _audio;
        static Transform s_ear;

        void Awake()
        {
            _health = GetComponent<EnemyHealth>();
            if (ai == null) ai = GetComponent<ZakuCombatAI>();
            if (gun == null) gun = GetComponent<ZakuMachineGun>();
            if (upperArm != null && foreArm != null && hand != null && weapon != null)
            {
                _l1 = Vector3.Distance(upperArm.position, foreArm.position);
                _l2 = Vector3.Distance(foreArm.position, hand.position);
                _gripInHand = Quaternion.Inverse(hand.rotation) * (weapon.position - hand.position);
                _weaponInHand = Quaternion.Inverse(hand.rotation) * weapon.rotation;
                _init = true;
            }
            if (swingSound != null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0f;
            }
            if (weapon != null) weapon.gameObject.SetActive(false);
            _decide = Random.Range(0.5f, decideInterval);
        }

        void OnDisable()
        {
            SetMelee(false);
            _w = 0f;
            _swingT = -1f;
        }

        void SetMelee(bool on)
        {
            Melee = on;
            if (gun != null) gun.holstered = on;
            if (ai != null)
            {
                ai.rangeOverride = on ? approachDistance : -1f;
                ai.minDistanceOverride = on ? Mathf.Max(4f, approachDistance * 0.5f) : -1f;
            }
            if (!on) { _swingT = -1f; _havePrev = false; }
        }

        void LateUpdate()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            bool dead = _health != null && _health.IsDead;
            if (dead || target == null)
            {
                if (Melee) SetMelee(false);
                _w = 0f;
                if (weapon.gameObject.activeSelf) weapon.gameObject.SetActive(false);
                return;
            }

            Vector3 to = target.transform.position - transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            bool sees = ai == null || ai.CanSeeTarget;

            // --- Pick the weapon (not in the middle of a swing). ---
            _decide -= dt;
            if (_decide <= 0f && !Swinging)
            {
                _decide = decideInterval * Random.Range(0.7f, 1.3f);
                bool want;
                if (!sees || target.IsDown) want = false;
                else if (dist <= alwaysMeleeRange) want = true;
                else if (dist >= alwaysRangedRange) want = false;
                else
                {
                    float k = 1f - Mathf.InverseLerp(alwaysMeleeRange, alwaysRangedRange, dist);
                    float p = meleePreference * k;
                    if (gun != null && gun.Reloading) p = Mathf.Max(p, 0.75f);
                    want = Random.value < p;
                }
                if (want != Melee) SetMelee(want);
            }
            if (Melee && (!sees || target.IsDown) && !Swinging) SetMelee(false);

            _w = Mathf.MoveTowards(_w, Melee ? 1f : 0f, dt / Mathf.Max(0.05f, drawTime));
            bool show = _w > 0.01f;
            if (weapon.gameObject.activeSelf != show) weapon.gameObject.SetActive(show);
            if (!show) return;

            // --- Swing timing. ---
            _cool -= dt;
            if (Melee && !Swinging && _cool <= 0f && sees && dist <= strikeRange)
            {
                _swingT = 0f;
                _hitDone = false;
                _havePrev = false;
                PlaySwing();
            }
            float u = -1f;
            if (Swinging)
            {
                _swingT += dt;
                u = _swingT / Mathf.Max(0.1f, swingTime);
                if (u >= 1f) { _swingT = -1f; _cool = cooldown; u = -1f; }
            }

            // --- Arm pose (body frame turned toward the target). ---
            Vector3 f = dist > 0.1f ? to / dist : transform.forward;
            Vector3 up = Vector3.up;
            Vector3 r = Vector3.Cross(up, f);
            float L = _l1 + _l2;
            Vector3 sh = upperArm.position;

            // Key poses: grip offset from the shoulder (f, up, r in reach units) + blade dir + edge dir.
            Vector3 readyGrip = f * 0.55f - up * 0.35f + r * 0.05f;
            Vector3 readyBlade = (f * 0.55f + up * 0.85f).normalized;
            Vector3 raiseGrip = up * 0.5f - f * 0.05f + r * 0.2f;
            Vector3 raiseBlade = (-f * 0.75f + up * 0.55f + r * 0.1f).normalized;
            Vector3 downGrip = f * 0.75f - up * 0.5f - r * 0.3f;
            Vector3 downBlade = (f * 0.45f - up * 0.85f - r * 0.25f).normalized;

            Vector3 g, b;
            if (u < 0f) { g = readyGrip; b = readyBlade; }
            else if (u < 0.4f) { float k = Smooth(u / 0.4f); g = Vector3.Lerp(readyGrip, raiseGrip, k); b = Vector3.Slerp(readyBlade, raiseBlade, k); }
            else if (u < 0.62f) { float k = Smooth((u - 0.4f) / 0.22f); g = Vector3.Lerp(raiseGrip, downGrip, k); b = Vector3.Slerp(raiseBlade, downBlade, k); }
            else { float k = Smooth((u - 0.62f) / 0.38f); g = Vector3.Lerp(downGrip, readyGrip, k); b = Vector3.Slerp(downBlade, readyBlade, k); }

            // Cutting edge leads: perpendicular to the blade, toward the target / down the chop.
            Vector3 edge = Vector3.ProjectOnPlane(f - up * 0.3f, b);
            if (edge.sqrMagnitude < 1e-4f) edge = Vector3.ProjectOnPlane(f, b);
            Quaternion weaponRot = Quaternion.LookRotation(edge.normalized, b);
            Quaternion handRot = weaponRot * Quaternion.Inverse(_weaponInHand);
            Vector3 gripWorld = sh + g * L;
            Vector3 wrist = gripWorld - handRot * _gripInHand;
            Vector3 pole = (r * 0.4f - up * 0.8f - f * 0.2f).normalized;
            SolveTwoBone(upperArm, foreArm, hand, _l1, _l2, wrist, pole, out Quaternion uRot, out Quaternion fRot);
            float w = Smooth(_w);
            upperArm.rotation = Quaternion.Slerp(upperArm.rotation, uRot, w);
            foreArm.rotation = Quaternion.Slerp(foreArm.rotation, fRot, w);
            hand.rotation = Quaternion.Slerp(hand.rotation, handRot, w);

            // --- Hit: sweep the blade during the chop. ---
            Vector3 bBase = weapon.position + weapon.up * bladeStart;
            Vector3 bTip = weapon.position + weapon.up * (bladeStart + bladeLength);
            if (u >= 0.38f && u <= 0.72f && !_hitDone && !target.IsDown)
            {
                bool hit = target.CapsuleHit(bBase, bTip, hitRadius, out Vector3 p);
                if (!hit && _havePrev)
                {
                    hit = target.CapsuleHit(_prevTip, bTip, hitRadius, out p)
                       || target.CapsuleHit((_prevBase + _prevTip) * 0.5f, (bBase + bTip) * 0.5f, hitRadius, out p);
                }
                if (hit)
                {
                    _hitDone = true;
                    target.TakeDamage(damage, p);
                }
            }
            _prevBase = bBase;
            _prevTip = bTip;
            _havePrev = Swinging;
        }

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        void PlaySwing()
        {
            if (_audio == null || swingSound == null) return;
            if (s_ear == null)
            {
                GameObject hc = GameObject.Find("HeadCam");
                s_ear = hc != null ? hc.transform : target.transform;
            }
            if (s_ear == null) return;
            Vector3 d = transform.position - s_ear.position;
            float dist = d.magnitude;
            if (dist > soundMaxRange) return;
            float vol = swingVolume * Mathf.Clamp01(soundFullVolumeRange / Mathf.Max(dist, 1f));
            if (vol < 0.01f) return;
            _audio.panStereo = dist > 0.01f ? Mathf.Clamp(Vector3.Dot(d / dist, s_ear.right), -1f, 1f) * 0.8f : 0f;
            _audio.pitch = Random.Range(0.92f, 1.05f);
            _audio.PlayOneShot(swingSound, vol);
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
