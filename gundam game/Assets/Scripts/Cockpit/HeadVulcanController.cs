using UnityEngine;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Head Vulcan control, per request:
    ///
    ///   "오른손은 조준 장치를 잡아서 조준하는 것이 아니라, '내가 바라보는 방향'으로
    ///    건담 머리의 헤드발칸을 조준하고, 오른손 엄지를 구부리는 동작으로 발사한다."
    ///
    /// Aim direction is the HMD/Main Camera's forward vector - NOT the right
    /// hand's position, NOT RightJoystick, NOT WeaponAimFireController. This
    /// script has zero reference to JoystickLever, RightJoystick or
    /// WeaponAimFireController anywhere in it - that existing system drives a
    /// completely separate cockpit-mounted turret (GunPivot/MuzzlePoint under
    /// GunTurret - see GundamCockpitSetup.cs) and is left fully intact,
    /// per "삭제하지 말고 기존 코드는 보존한다... 이번에는 Head Vulcan 조작에서
    /// 사용하지 않게 분리한다": there was nothing to disconnect, since that
    /// system was never wired to the Gundam's own head to begin with
    /// (confirmed by reading GundamCockpitSetup.cs's turret-wiring block).
    ///
    /// Hand tracking: does NOT create a new/separate hand-tracking system
    /// (per "새로운 MediaPipe나 별도 손 추적 시스템을 추가하지 않는다"). Instead,
    /// GundamCockpitSetup.cs calls SubscribeToRightHand(...) once, at scene
    /// build time, passing in the SAME XRHandTrackingEvents component that is
    /// already sitting on the "RightHandTracker" GameObject (the exact
    /// component RightHandTracker's own HandJointTracker already uses for its
    /// GripAmount/PinchAmount). This just adds a second listener to that
    /// already-running event - it does not add a new XRHandTrackingEvents,
    /// does not touch HandJointTracker.cs, and only ever reads Right hand data
    /// (never Left), matching the project's existing "Left/Right hand
    /// exclusivity" rule from the earlier joystick fix.
    /// </summary>
    public class HeadVulcanController : MonoBehaviour
    {
        [Header("References (wired by GundamCockpitSetup - do not need to set these by hand)")]
        [Tooltip("The pilot's own HMD/Main Camera. Its transform.forward is the aim/fire direction - its position is only ever read, never modified.")]
        public Camera mainCamera;
        public Transform muzzleLeft;
        public Transform muzzleRight;
        [Tooltip("Optional real bullet prefab. If left empty, a small temporary Sphere is spawned instead - per request, functionality first.")]
        public GameObject projectilePrefab;

        [Header("Right thumb bend -> fire")]
        [Tooltip("0 = thumb fully extended away from the palm, 1 = thumb tip curled all the way back near its own base/palm. Firing starts once the bend amount reaches this.")]
        [Range(0.1f, 1f)]
        public float thumbBendThreshold = 0.6f;
        [Tooltip("Minimum seconds between shots while the thumb is held bent - prevents runaway rapid-fire from a single held gesture.")]
        public float fireRate = 0.15f;

        [Header("Bullet")]
        public float bulletSpeed = 80f;
        public float bulletLifetime = 3f;
        [Tooltip("true: each shot alternates HeadVulcanMuzzle_L / HeadVulcanMuzzle_R. false: both muzzles fire together every shot.")]
        public bool alternateMuzzles = true;

        // --- Ammo/state (ADDED for the Cockpit HUD - RightDisplay's WEAPON
        // screen, per request "Head Vulcan 발사 시 탄약 수가 감소하도록...
        // 단, 기존 발사 동작 자체는 변경하지 말고, HUD가 읽을 수 있는 상태값만
        // 노출"). This is a pure side-channel: ConsumeAmmoForShot() is called
        // once at the end of Fire() (after bullets are already spawned) and
        // the Empty/Reloading timers below run as their own independent
        // Update() branch. Nothing here gates or alters WHEN/HOW a shot fires
        // - the thumb-bend detection, cooldown, muzzle choice and fire
        // direction above are untouched. If ammo runs out, the gun keeps
        // firing on every bent-thumb frame exactly as before; only the HUD-
        // facing CurrentAmmo/State values reflect it running dry. ---
        [Header("Ammo / HUD status (does not gate or change firing)")]
        public string weaponName = "HEAD VULCAN";
        public int maxAmmo = 200;
        [Tooltip("Seconds the HUD shows EMPTY before automatically switching to RELOADING.")]
        public float emptyHoldDuration = 0.4f;
        [Tooltip("Seconds RELOADING lasts before ammo refills to maxAmmo and state returns to READY.")]
        public float reloadDuration = 2.5f;

        public enum FireReadyState { Ready, Empty, Reloading }

        public int CurrentAmmo { get; private set; }
        public FireReadyState State { get; private set; } = FireReadyState.Ready;
        public float AmmoFraction => maxAmmo > 0 ? (float)CurrentAmmo / maxAmmo : 0f;

        float _stateTimer;

        // --- thumb-bend state, written only from OnRightHandJointsUpdated (the
        // XR Hands event callback), read only from Update - both run on the
        // main thread, so no locking is needed. ---
        bool _thumbTracked;
        float _thumbBendAmount;

        float _cooldownTimer;
        int _muzzleToggle;

        void Awake()
        {
            CurrentAmmo = maxAmmo;
            State = FireReadyState.Ready;
        }

        /// <summary>
        /// Subscribes this controller to an ALREADY EXISTING XRHandTrackingEvents
        /// component (the one on RightHandTracker) instead of creating its own.
        /// Call this once, from GundamCockpitSetup, right after that component
        /// is created. Safe to call with the shared instance - UnityEvents
        /// support any number of independent listeners, so this does not
        /// interfere with HandJointTracker's own listener on the same event.
        /// </summary>
        public void SubscribeToRightHand(XRHandTrackingEvents rightHandEvents)
        {
            if (rightHandEvents == null)
            {
                Debug.LogWarning("[Gundam] HeadVulcanController.SubscribeToRightHand called with null - " +
                    "right thumb firing will never trigger.");
                return;
            }
            rightHandEvents.jointsUpdated.AddListener(OnRightHandJointsUpdated);
        }

        void OnRightHandJointsUpdated(XRHandJointsUpdatedEventArgs args)
        {
            // Per request: judge the bend using the thumb's own joint chain
            // (ThumbTip/ThumbDistal/ThumbProximal) relative to Palm - not just
            // a single raw ThumbTip position/distance, which would vary a lot
            // with hand size, wrist angle and how far the hand is from the
            // head. ("IndexMetacarpal 또는 Palm" - either is an acceptable
            // reference point per the request; Palm is used here since
            // HandJointTracker's own GripAmount already establishes Palm as
            // this project's reference joint for "how curled is this finger".)
            bool gotTip = args.hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose tipPose);
            bool gotPalm = args.hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose);
            if (!gotTip || !gotPalm)
            {
                _thumbTracked = false;
                return;
            }

            bool gotProximal = args.hand.GetJoint(XRHandJointID.ThumbProximal).TryGetPose(out Pose proximalPose);
            bool gotDistal = args.hand.GetJoint(XRHandJointID.ThumbDistal).TryGetPose(out Pose distalPose);

            // "thumbReach" = how far this specific hand's thumb extends out
            // from its own base when fully straight - the scale bend amount
            // is measured against, so it self-calibrates per hand/headset
            // instead of using one fixed world-space distance for everyone.
            float thumbReach;
            if (gotProximal) thumbReach = Vector3.Distance(proximalPose.position, tipPose.position);
            else if (gotDistal) thumbReach = Vector3.Distance(distalPose.position, tipPose.position) * 1.6f;
            else thumbReach = 0.06f;
            thumbReach = Mathf.Max(thumbReach, 0.02f);

            float baseToPalm;
            if (gotProximal) baseToPalm = Vector3.Distance(proximalPose.position, palmPose.position);
            else if (gotDistal) baseToPalm = Vector3.Distance(distalPose.position, palmPose.position);
            else baseToPalm = thumbReach;

            float tipToPalm = Vector3.Distance(tipPose.position, palmPose.position);

            // Extended: tip is out at (base-to-palm + full thumb reach) from
            // the palm. Curled: tip has folded back in close to the thumb's
            // own base (well short of that). InverseLerp maps
            // extended->0, curled->1, so bend = 1 only once the thumb has
            // genuinely folded in toward the palm, not just moved slightly.
            float extendedDistance = baseToPalm + thumbReach;
            float curledDistance = baseToPalm * 0.35f;

            _thumbBendAmount = Mathf.Clamp01(Mathf.InverseLerp(extendedDistance, curledDistance, tipToPalm));
            _thumbTracked = true;
        }

        void Update()
        {
            if (_cooldownTimer > 0f)
            {
                _cooldownTimer -= Time.deltaTime;
            }

            // Plain cooldown gate (no separate "was bent last frame" latch):
            // this already satisfies both halves of the request on its own -
            // holding the thumb bent fires repeatedly at most once every
            // fireRate seconds (no runaway rapid-fire), and releasing then
            // re-bending fires again immediately once the cooldown has
            // elapsed (no extra state to get stuck).
            if (_thumbTracked && _thumbBendAmount >= thumbBendThreshold && _cooldownTimer <= 0f)
            {
                Fire();
                _cooldownTimer = fireRate;
            }

            // Ammo/state timers - a fully independent branch from the firing
            // logic above (see the field header comment). Ready needs no
            // timer; Empty auto-advances to Reloading after a short HUD-
            // readable beat; Reloading refills ammo and returns to Ready.
            if (State == FireReadyState.Empty)
            {
                _stateTimer -= Time.deltaTime;
                if (_stateTimer <= 0f)
                {
                    State = FireReadyState.Reloading;
                    _stateTimer = reloadDuration;
                }
            }
            else if (State == FireReadyState.Reloading)
            {
                _stateTimer -= Time.deltaTime;
                if (_stateTimer <= 0f)
                {
                    CurrentAmmo = maxAmmo;
                    State = FireReadyState.Ready;
                }
            }
        }

        /// <summary>HUD-facing ammo bookkeeping only - called once per shot from
        /// the end of Fire(), after bullets are already spawned. Never blocks or
        /// delays an actual shot; see the field header comment.</summary>
        void ConsumeAmmoForShot()
        {
            if (CurrentAmmo > 0) CurrentAmmo--;
            if (CurrentAmmo <= 0 && State == FireReadyState.Ready)
            {
                CurrentAmmo = 0;
                State = FireReadyState.Empty;
                _stateTimer = emptyHoldDuration;
            }
        }

        void Fire()
        {
            if (mainCamera == null) return;

            // Per request, verbatim:
            //   Vector3 fireDirection = mainCamera.transform.forward;
            //   spawn position: muzzle.position
            //   spawn rotation: Quaternion.LookRotation(fireDirection)
            // The muzzle's OWN forward is never used for the bullet's
            // direction - only its position (where the bullet visually
            // originates from).
            Vector3 fireDirection = mainCamera.transform.forward;
            Quaternion fireRotation = Quaternion.LookRotation(fireDirection);

            if (alternateMuzzles && muzzleLeft != null && muzzleRight != null)
            {
                Transform muzzle = (_muzzleToggle % 2 == 0) ? muzzleLeft : muzzleRight;
                _muzzleToggle++;
                SpawnBullet(muzzle, fireDirection, fireRotation);
            }
            else
            {
                if (muzzleLeft != null) SpawnBullet(muzzleLeft, fireDirection, fireRotation);
                if (muzzleRight != null) SpawnBullet(muzzleRight, fireDirection, fireRotation);
            }

            // HUD-only ammo bookkeeping - see ConsumeAmmoForShot's own doc
            // comment. Does not affect anything above.
            ConsumeAmmoForShot();
        }

        void SpawnBullet(Transform muzzle, Vector3 fireDirection, Quaternion fireRotation)
        {
            GameObject go;
            if (projectilePrefab != null)
            {
                go = Instantiate(projectilePrefab, muzzle.position, fireRotation);
            }
            else
            {
                // Temporary placeholder bullet - per request ("실제 탄환 모델이
                // 없다면 작은 Sphere 또는 Capsule을 임시 탄환으로 만들어도 된다.
                // 중요한 것은 먼저 기능을 확인하는 것이다."). Collider removed so
                // it can't push the Gundam/cockpit around or block its own
                // straight-line flight.
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "HeadVulcanBullet";
                go.transform.SetPositionAndRotation(muzzle.position, fireRotation);
                go.transform.localScale = Vector3.one * 0.04f;
                Collider col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.linearVelocity = fireDirection * bulletSpeed;

            Destroy(go, bulletLifetime);
        }
    }
}
