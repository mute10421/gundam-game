using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// BEAM RIFLE mode - per request ("무기에 빔라이플을 추가하고 무기를 빔라이플로
    /// 변경하면 빔라이플을 양손으로 잡는거야 그리고 락온한 상대를 쏠거야 쏘는거는
    /// 오른손 엄지", "빔 데미지는 500").
    ///
    /// The Gundam holds the downloaded rifle model with BOTH hands: the right fist
    /// on the pistol grip, the left fist around the fore-end. Both real arms are
    /// driven by two-bone IK and shown through arm-only views of the Gundam mesh
    /// (the rest of the pilot's own body stays hidden).
    ///
    /// Aim: the stock sits against the chest (anchor, in the pilot's view frame
    /// from the right shoulder) and the rifle points at the target the OrbitHUD
    /// ring has LOCKED ON (its visible center), within maxAimAngle of where the
    /// view is looking; with no lock it points straight ahead. The view itself is
    /// still turned with RightJoystick as usual (the Gundam body turns with it -
    /// ExternalGundamFollower).
    ///
    /// Fire: the RightJoystick THUMB button (JoystickFingerButtons) - the same
    /// button the Head Vulcan uses, which WeaponModeController switches off in this
    /// mode. Holding it fires once every fireCooldown. Bolts fly at the locked
    /// target (BeamRifleShot, damage 500).
    ///
    /// Energy (per "빔라이플은 15발이고 재장전까지 쿨타임은 1분이야 충전식이거든"):
    /// 15 shots; when the last one is fired the rifle RECHARGES for 60 s and then
    /// is full again. Recharging keeps running even while another weapon is
    /// selected.
    ///
    /// Runs after BeamSaberArmController (which resets the right arm to rest every
    /// frame) so the rifle pose wins while this mode is active; blends in/out.
    /// </summary>
    [DefaultExecutionOrder(-45)]
    public class BeamRifleController : MonoBehaviour
    {
        [Header("Gundam bones (real model)")]
        public Transform rightUpperArm;
        public Transform rightForeArm;
        public Transform rightHand;
        public Transform leftUpperArm;
        public Transform leftForeArm;
        public Transform leftHand;

        [Header("Visuals")]
        [Tooltip("Rifle root: +Z = muzzle direction, +Y = rifle top, 1 unit = 1 m.")]
        public Transform rifle;
        public Renderer rightArmView;
        public Renderer leftArmView;

        [Header("Rifle points (rifle-local, m)")]
        public Vector3 stockPoint = new Vector3(0f, 0.39f, -2.72f);
        public Vector3 muzzlePoint = new Vector3(0f, 0.33f, 2.75f);
        public Vector3 rightGripPoint = new Vector3(0f, -0.72f, -1.09f);
        [Tooltip("Right grip: thumb-side axis (from the grip bottom toward the rifle body).")]
        public Vector3 rightGripThumb = new Vector3(0f, 0.706f, 0.709f);
        [Tooltip("Right grip: wrist-to-knuckles direction.")]
        public Vector3 rightGripFingers = new Vector3(0f, -0.709f, 0.705f);
        public Vector3 leftGripPoint = new Vector3(0f, 0.22f, 0.83f);
        public Vector3 leftGripThumb = new Vector3(0f, 0f, 1f);
        public Vector3 leftGripFingers = new Vector3(1f, 0f, 0f);

        [Header("Fist (right hand bone space, unscaled m; left is mirrored)")]
        public Vector3 rightFistGrip = new Vector3(-0.24f, 0.13f, 0.80f);

        [Header("Aim")]
        public CockpitViewController viewController;
        [Tooltip("The cockpit interior - its axes turned by the view rotation give the pilot's forward/up/right.")]
        public Transform cockpitSpace;
        public OrbitHUDTargetLock targetLock;
        [Tooltip("Stock position from the right shoulder joint, view frame (x right, y up, z forward), m.")]
        public Vector3 stockFromRightShoulder = new Vector3(-1.3f, -0.6f, 0.3f);
        [Tooltip("The rifle never turns further than this from the view direction (deg).")]
        public float maxAimAngle = 50f;
        public float aimSmoothing = 0.06f;
        [Range(0f, 1f)] public float forearmTwistShare = 0.5f;
        public float blendTime = 0.35f;

        [Header("Fire (right thumb)")]
        public JoystickFingerButtons fireButtons;
        public int damage = 500;
        public float fireCooldown = 0.8f;
        [Tooltip("Shots per full charge.")]
        public int maxShots = 15;
        [Tooltip("Seconds to recharge once the rifle is empty.")]
        public float rechargeTime = 60f;
        public float shotSpeed = 350f;
        public float shotLength = 14f;
        public float shotRadius = 0.35f;
        public float shotHitRadius = 1.0f;
        public Material shotGlowMaterial;
        public Material shotCoreMaterial;
        public float muzzleFlashSize = 2.5f;

        [Header("WEAPON screen (optional)")]
        public CockpitWeaponHUD weaponHUD;

        public bool Active { get; private set; }
        public float LastFireTime { get; private set; } = -999f;
        public int ShotsLeft { get; private set; }
        public bool Recharging { get; private set; }
        public float RechargeRemaining => Recharging ? Mathf.Max(0f, _rechargeEnd - Time.time) : 0f;
        public bool Ready => !Recharging && ShotsLeft > 0 && Time.time - LastFireTime >= fireCooldown;
        float _rechargeEnd;

        struct Arm
        {
            public Transform upper, fore, hand;
            public Quaternion upperRest, foreRest, handRest;
            public float l1, l2;
            public Vector3 fistPos;      // grip point in hand-bone space (unscaled m)
            public Quaternion fistRot;   // grip frame (Y thumb, Z fingers) in hand-bone space
            public bool ok;
        }

        Arm _right, _left;
        bool _init;
        float _blend;
        Vector3 _aimDir;
        bool _haveAim;

        void Awake()
        {
            ShotsLeft = maxShots;
            Init();
            SetVisuals(false);
        }

        void Init()
        {
            if (_init) return;
            _right = MakeArm(rightUpperArm, rightForeArm, rightHand);
            _left = MakeArm(leftUpperArm, leftForeArm, leftHand);
            if (_right.ok)
            {
                _right.fistPos = rightFistGrip;
                _right.fistRot = Quaternion.identity; // grip frame == right hand bone axes
            }
            if (_left.ok && _right.ok)
            {
                // Mirror the right fist's grip across the Gundam's own left/right plane
                // (rest pose) to get the left fist's.
                Transform body = transform;
                Vector3 rp = rightHand.position + rightHand.rotation * rightFistGrip;
                Vector3 lp = Mirror(body, rp, true);
                Vector3 ly = Mirror(body, rightHand.up, false);
                Vector3 lz = Mirror(body, rightHand.forward, false);
                _left.fistPos = Quaternion.Inverse(leftHand.rotation) * (lp - leftHand.position);
                _left.fistRot = Quaternion.Inverse(leftHand.rotation) * Quaternion.LookRotation(lz, ly);
            }
            _init = _right.ok;
        }

        static Arm MakeArm(Transform u, Transform f, Transform h)
        {
            Arm a = new Arm { upper = u, fore = f, hand = h };
            if (u == null || f == null || h == null) return a;
            a.upperRest = u.localRotation;
            a.foreRest = f.localRotation;
            a.handRest = h.localRotation;
            a.l1 = Vector3.Distance(u.position, f.position);
            a.l2 = Vector3.Distance(f.position, h.position);
            a.ok = true;
            return a;
        }

        static Vector3 Mirror(Transform body, Vector3 v, bool point)
        {
            Vector3 l = point ? body.InverseTransformPoint(v) : body.InverseTransformDirection(v);
            l.x = -l.x;
            return point ? body.TransformPoint(l) : body.TransformDirection(l);
        }

        public void SetActive(bool on)
        {
            Init();
            Active = on;
            _haveAim = false;
            if (on) SetVisuals(true);
        }

        void SetVisuals(bool on)
        {
            if (rifle != null) rifle.gameObject.SetActive(on);
            if (rightArmView != null) rightArmView.enabled = on;
            if (leftArmView != null) leftArmView.enabled = on;
        }

        void LateUpdate()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            _blend = Mathf.MoveTowards(_blend, Active ? 1f : 0f, dt / Mathf.Max(0.01f, blendTime));

            if (Recharging && Time.time >= _rechargeEnd)
            {
                Recharging = false;
                ShotsLeft = maxShots;
            }

            // The left arm is only ever animated by this script - start from rest.
            if (_left.ok)
            {
                _left.upper.localRotation = _left.upperRest;
                _left.fore.localRotation = _left.foreRest;
                _left.hand.localRotation = _left.handRest;
            }

            if (_blend <= 0f)
            {
                if (!Active) SetVisuals(false);
                return;
            }
            if (rifle == null || cockpitSpace == null) return;

            // Pilot's view frame.
            Quaternion look = viewController != null ? viewController.LookRotation : Quaternion.identity;
            Vector3 F = look * cockpitSpace.forward;
            Vector3 U = look * cockpitSpace.up;
            Vector3 R = look * cockpitSpace.right;

            Vector3 stockWorld = rightUpperArm.position
                + R * stockFromRightShoulder.x + U * stockFromRightShoulder.y + F * stockFromRightShoulder.z;

            // Aim at the locked target (within the cone), else straight ahead.
            Vector3 want = F;
            Transform tgt = LockedTarget();
            if (tgt != null)
            {
                Vector3 to = AimPoint(tgt) - stockWorld;
                if (to.sqrMagnitude > 1f) want = Vector3.RotateTowards(F, to.normalized, maxAimAngle * Mathf.Deg2Rad, 0f);
            }
            float ka = aimSmoothing > 0.0001f ? 1f - Mathf.Exp(-dt / aimSmoothing) : 1f;
            _aimDir = _haveAim ? Vector3.Slerp(_aimDir, want, ka).normalized : want;
            _haveAim = true;

            Quaternion rifleRot = Quaternion.LookRotation(_aimDir, Vector3.ProjectOnPlane(U, _aimDir).sqrMagnitude > 1e-4f ? U : cockpitSpace.up);
            rifle.rotation = rifleRot;
            rifle.position = stockWorld - rifleRot * stockPoint;

            // Hands onto the rifle.
            Transform body = transform;
            Vector3 rPole = (body.right * 0.5f - body.up * 0.7f - body.forward * 0.3f).normalized;
            Vector3 lPole = (-body.right * 0.5f - body.up * 0.7f - body.forward * 0.3f).normalized;
            PoseArm(ref _right, rifle.TransformPoint(rightGripPoint),
                rifleRot * Quaternion.LookRotation(rightGripFingers.normalized, rightGripThumb.normalized), rPole);
            if (_left.ok)
                PoseArm(ref _left, rifle.TransformPoint(leftGripPoint),
                    rifleRot * Quaternion.LookRotation(leftGripFingers.normalized, leftGripThumb.normalized), lPole);

            // Fire with the right thumb button.
            if (Active && _blend >= 0.95f && fireButtons != null && fireButtons.ThumbPressed && Ready)
                Fire(tgt);

            UpdateHUD();
        }

        void PoseArm(ref Arm arm, Vector3 gripWorld, Quaternion gripRot, Vector3 pole)
        {
            // Hand rotation so its fist's grip frame matches gripRot; wrist placed so
            // the fist's grip point lands on gripWorld.
            Quaternion handRot = gripRot * Quaternion.Inverse(arm.fistRot);
            Vector3 wrist = gripWorld - handRot * arm.fistPos;

            Vector3 a = arm.upper.position;
            Quaternion upperIK, foreIK;
            SolveTwoBone(arm, a, wrist, pole, out upperIK, out foreIK);
            arm.upper.rotation = Quaternion.Slerp(arm.upper.rotation, upperIK, _blend);
            arm.fore.rotation = Quaternion.Slerp(arm.fore.rotation, foreIK, _blend);

            Vector3 foreAxis = arm.hand.position - arm.fore.position;
            if (foreAxis.sqrMagnitude > 1e-6f && forearmTwistShare > 0f)
            {
                foreAxis.Normalize();
                Quaternion delta = handRot * Quaternion.Inverse(arm.hand.rotation);
                if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
                Vector3 proj = Vector3.Project(new Vector3(delta.x, delta.y, delta.z), foreAxis);
                float mag = Mathf.Sqrt(proj.sqrMagnitude + delta.w * delta.w);
                if (mag > 1e-5f)
                {
                    Quaternion twist = new Quaternion(proj.x / mag, proj.y / mag, proj.z / mag, delta.w / mag);
                    arm.fore.rotation = Quaternion.Slerp(Quaternion.identity, twist, forearmTwistShare * _blend) * arm.fore.rotation;
                }
            }
            arm.hand.rotation = Quaternion.Slerp(arm.hand.rotation, handRot, _blend);
        }

        static void SolveTwoBone(Arm arm, Vector3 a, Vector3 target, Vector3 pole, out Quaternion upperRot, out Quaternion foreRot)
        {
            float l1 = arm.l1, l2 = arm.l2;
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

            Vector3 curUpper = arm.fore.position - a;
            upperRot = Quaternion.FromToRotation(curUpper, elbow - a) * arm.upper.rotation;
            Quaternion upperDelta = upperRot * Quaternion.Inverse(arm.upper.rotation);
            Vector3 elbowPos = a + upperDelta * curUpper;
            Vector3 curFore = upperDelta * (arm.hand.position - arm.fore.position);
            Quaternion foreWorldAfterUpper = upperDelta * arm.fore.rotation;
            foreRot = Quaternion.FromToRotation(curFore, wrist - elbowPos) * foreWorldAfterUpper;
        }

        Transform LockedTarget()
        {
            if (targetLock == null || !targetLock.IsLocked) return null;
            Transform t = targetLock.CurrentTarget;
            if (t == null || !t.gameObject.activeInHierarchy) return null;
            EnemyHealth hp = t.GetComponent<EnemyHealth>();
            if (hp != null && hp.IsDead) return null;
            return t;
        }

        static Vector3 AimPoint(Transform t)
        {
            Renderer[] rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return t.position;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b.center;
        }

        void Fire(Transform tgt)
        {
            LastFireTime = Time.time;
            ShotsLeft = Mathf.Max(0, ShotsLeft - 1);
            if (ShotsLeft == 0)
            {
                Recharging = true;
                _rechargeEnd = Time.time + rechargeTime;
            }
            Vector3 muzzle = rifle.TransformPoint(muzzlePoint);
            Vector3 dir = rifle.forward;
            if (tgt != null)
            {
                // Straight at the locked target - unless it's outside where the
                // rifle can point (then the bolt just goes where the barrel points).
                Vector3 to = AimPoint(tgt) - muzzle;
                if (to.sqrMagnitude > 1f && Vector3.Angle(to, rifle.forward) <= 15f) dir = to.normalized;
            }

            GameObject shot = new GameObject("BeamRifleShot");
            shot.transform.position = muzzle;
            shot.transform.rotation = Quaternion.LookRotation(dir);
            // Bolt visuals: bright core inside a wider glow, trailing behind the tip.
            AddBoltPart(shot.transform, shotGlowMaterial, shotRadius * 2.2f);
            AddBoltPart(shot.transform, shotCoreMaterial != null ? shotCoreMaterial : shotGlowMaterial, shotRadius);
            BeamRifleShot s = shot.AddComponent<BeamRifleShot>();
            s.direction = dir;
            s.speed = shotSpeed;
            s.damage = damage;
            s.hitRadius = shotHitRadius;
            s.flashMaterial = shotGlowMaterial;

            // Muzzle flash.
            GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flash.name = "BeamRifleMuzzleFlash";
            Collider fc = flash.GetComponent<Collider>();
            if (fc != null) Destroy(fc);
            if (shotGlowMaterial != null) flash.GetComponent<Renderer>().sharedMaterial = shotGlowMaterial;
            flash.transform.SetParent(rifle, true);
            flash.transform.position = muzzle;
            flash.transform.localScale = Vector3.one * muzzleFlashSize;
            Destroy(flash, 0.08f);
        }

        void AddBoltPart(Transform parent, Material mat, float radius)
        {
            GameObject c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            c.name = "Bolt";
            Collider col = c.GetComponent<Collider>();
            if (col != null) Destroy(col);
            if (mat != null) c.GetComponent<Renderer>().sharedMaterial = mat;
            Renderer r = c.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            c.transform.SetParent(parent, false);
            // Capsule is 2 units tall along Y: lay it along +Z, tip at the origin.
            c.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            c.transform.localPosition = new Vector3(0f, 0f, -shotLength * 0.5f);
            c.transform.localScale = new Vector3(radius * 2f, shotLength * 0.5f, radius * 2f);
        }

        void UpdateHUD()
        {
            if (!Active || weaponHUD == null) return;
            if (weaponHUD.nameText != null) weaponHUD.nameText.text = "BEAM RIFLE";
            if (weaponHUD.ammoText != null)
                weaponHUD.ammoText.text = "ENERGY " + ShotsLeft.ToString("00") + "/" + maxShots.ToString("00") +
                    (LockedTarget() != null ? "  LOCK" : "");
            float fill = Recharging
                ? 1f - RechargeRemaining / Mathf.Max(0.01f, rechargeTime)
                : (maxShots > 0 ? (float)ShotsLeft / maxShots : 0f);
            if (weaponHUD.ammoFillImage != null)
            {
                weaponHUD.ammoFillImage.fillAmount = Mathf.Clamp01(fill);
                weaponHUD.ammoFillImage.color = Recharging ? new Color(1f, 0.8f, 0.2f) : GundamVitals.StatusColor(fill);
            }
            if (weaponHUD.stateText != null)
            {
                if (Recharging)
                {
                    weaponHUD.stateText.text = "RECHARGING " + Mathf.CeilToInt(RechargeRemaining) + "s";
                    weaponHUD.stateText.color = new Color(1f, 0.8f, 0.2f);
                }
                else
                {
                    bool ready = Time.time - LastFireTime >= fireCooldown;
                    weaponHUD.stateText.text = ready ? "READY" : "FIRE";
                    weaponHUD.stateText.color = ready ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.55f, 0.8f);
                }
            }
        }
    }
}
