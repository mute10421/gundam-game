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
        [Tooltip("RightJoystick view controller. When set, shots go where the pilot is actually looking on the (turnable) Cockpit_Dome view, and converge from both muzzles onto that aim point. Leave null for the old plain mainCamera.forward direction.")]
        public CockpitViewController viewController;

        [Header("Fire trigger")]
        [Tooltip("RightJoystick's finger buttons. Pressing its THUMB button (while holding the stick) fires. Wired by GundamCockpitSetup.")]
        public JoystickFingerButtons fireButtons;
        [Tooltip("Also fire on a plain right-thumb bend (the older trigger). Off by default now that the thumb button fires; used automatically if fireButtons isn't wired.")]
        public bool fireOnThumbBend = false;

        [Header("Right thumb bend -> fire (optional, see fireOnThumbBend)")]
        [Tooltip("0 = thumb fully extended away from the palm, 1 = thumb tip curled all the way back near its own base/palm. Firing starts once the bend amount reaches this.")]
        [Range(0.1f, 1f)]
        public float thumbBendThreshold = 0.6f;
        [Tooltip("Minimum seconds between shots while the thumb is held bent - prevents runaway rapid-fire from a single held gesture.")]
        public float fireRate = 0.15f;
        [Tooltip("Total thumb-joint fold (degrees) that counts as 'straight' = bend 0. See OnRightHandJointsUpdated.")]
        public float straightThumbCurlDegrees = 25f;
        [Tooltip("Total thumb-joint fold (degrees) that counts as 'fully bent' = bend 1. With the default 0.6 threshold, firing starts at about 58 degrees.")]
        public float bentThumbCurlDegrees = 80f;

        [Header("Bullet")]
        public float bulletSpeed = 80f;
        public float bulletLifetime = 3f;
        [Tooltip("Bright/emissive material for the placeholder bullet (see SpawnBullet) so tracer fire actually reads at a distance. Falls back to the primitive's default material if left null.")]
        // Per report ("해드발칸이 발사가 되는지 안보임"): the muzzles sit on
        // ExternalGundam's own head (~20m from the pilot's own camera - see
        // PlaceExternalGundam's placement at world Z=20), and the placeholder
        // bullet was a tiny 0.04-scale, default-material sphere - at that
        // distance it was essentially an invisible gray speck. bulletMat
        // (wired by GundamCockpitSetup to a bright emissive orange, matching
        // this project's other MakeEmissiveMat accents) plus the bigger
        // bulletScale below fixes that.
        public Material bulletMat;
        [Tooltip("Visual scale of the placeholder bullet sphere. Bumped up from an earlier, barely-visible 0.04 for the same reason as bulletMat above.")]
        public float bulletScale = 0.12f;

        // Per report ("해드발칸에서 총알이 안나가는거 같아 총알을 발사하면 내가
        // 볼수있어야함"): the pilot only ever sees the outside world through
        // Cockpit_Dome, a 512px-per-face cubemap rendered from HeadCam, which
        // sits right next to these muzzles. A 0.12m sphere flying straight
        // away from that viewpoint is ~2px wide within a fraction of a second
        // and seen end-on, so it still read as "nothing fired". Fixes below:
        //   - tracer: the placeholder is now a long, thin glowing streak
        //     (tracerLength x tracerWidth) aligned with its flight direction;
        //   - convergence: both muzzles aim at the point the pilot is looking
        //     at (see Fire()), so the two tracers visibly sweep in from the
        //     sides toward the center of view instead of flying dead-away;
        //   - muzzle flash: a short bright flash at each muzzle per shot.
        [Tooltip("Length (m) of the placeholder tracer streak.")]
        public float tracerLength = 4f;
        [Tooltip("Thickness (m) of the placeholder tracer streak.")]
        public float tracerWidth = 0.25f;
        [Tooltip("Size (m) of the flash shown at each muzzle when firing. 0 = no flash.")]
        public float muzzleFlashSize = 0.8f;
        [Tooltip("How long (s) each muzzle flash stays visible.")]
        public float muzzleFlashDuration = 0.06f;
        [Tooltip("If the aim ray hits nothing, both muzzles converge on a point this far (m) along the pilot's gaze.")]
        public float convergenceDistance = 80f;
        [Tooltip("Max range (m) of the aim ray used to pick the convergence point.")]
        public float maxAimDistance = 300f;
        [Tooltip("true: each shot alternates HeadVulcanMuzzle_L / HeadVulcanMuzzle_R. false: both muzzles fire together every shot.")]
        // Per request ("총알 개수는 120발이야 머리 한쪽당 60발씩해서 총 120발이야
        // 그니까 동시에 양쪽에서 나가는거니까"): both muzzles fire together on
        // every shot rather than alternating - defaulted to false to match.
        public bool alternateMuzzles = false;

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
        // Per request ("총알 개수는 120발이야 머리 한쪽당 60발씩해서 총 120발이야
        // 그니까 동시에 양쪽에서 나가는거니까 60발로 표기하면될듯"): the suit
        // physically carries 120 rounds total (60 per head muzzle), but since
        // both muzzles always fire together as one shot (alternateMuzzles =
        // false above), one shot uses 2 physical rounds (1 per side) - so the
        // HUD-facing ammo count here tracks SHOTS, not individual rounds, and
        // maxAmmo = 60 shots x 2 rounds/shot = 120 rounds total, matching the
        // spec exactly.
        [Header("Ammo / HUD status (does not gate or change firing)")]
        public string weaponName = "HEAD VULCAN";
        public int maxAmmo = 60;
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

        // Read-only HUD status (per the same "총알이 안나가는거 같아" report):
        // lets FrontDisplay show whether the right thumb is even being seen
        // and how far it's bent vs. the fire threshold, so "not firing" can be
        // told apart from "firing but hard to see" at a glance, on the headset.
        public bool IsThumbTracked => _thumbTracked;
        public float ThumbBend => _thumbBendAmount;
        /// <summary>Raw total fold of the thumb's joints, degrees (for the HUD / tuning).</summary>
        public float ThumbCurlDegrees { get; private set; }
        /// <summary>Time.time of the most recent shot (-999 = never fired).</summary>
        public float LastFireTime { get; private set; } = -999f;

        float _cooldownTimer;
        int _muzzleToggle;

        // --- Right-hand event source ---
        // ROOT CAUSE of "발칸이 엄지를 움직였을때 발사가 되야하는데 발사가 안됨"
        // (confirmed by inspecting the saved GundamCockpit scene: RightHandTracker's
        // XRHandTrackingEvents.jointsUpdated had 0 listeners): SubscribeToRightHand
        // used to call jointsUpdated.AddListener(...) directly, but it only ever
        // runs inside the EDITOR, from GundamCockpitSetup, while the scene is being
        // built. AddListener adds a runtime-only listener - it is never saved into
        // the scene file - so by the time the saved scene is played (Editor Play
        // mode or the headset build) that listener no longer exists,
        // OnRightHandJointsUpdated never runs, the thumb is never "tracked", and
        // Fire() can never trigger. (HandJointTracker works because it subscribes
        // itself at runtime.)
        //
        // Fix: the event source is now a serialized field, and the subscription
        // happens at RUNTIME in OnEnable (removed again in OnDisable), same as
        // HandJointTracker. If the field is empty (e.g. a scene built before this
        // fix), Start-up falls back to finding the RightHandTracker's own
        // XRHandTrackingEvents in the scene, so this works even without
        // re-running Build Cockpit Scene. Still reuses that SAME existing
        // component - no new hand-tracking system, Right hand only.
        [Tooltip("RightHandTracker's existing XRHandTrackingEvents. Subscribed at runtime (OnEnable). If empty, it's found automatically.")]
        public XRHandTrackingEvents rightHandEvents;
        bool _subscribed;

        void Awake()
        {
            CurrentAmmo = maxAmmo;
            State = FireReadyState.Ready;
        }

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (rightHandEvents == null) rightHandEvents = FindRightHandEvents();
            if (rightHandEvents == null)
            {
                Debug.LogWarning("[Gundam] HeadVulcanController: no right-hand XRHandTrackingEvents found - right thumb firing will never trigger.");
                return;
            }
            if (!_subscribed)
            {
                rightHandEvents.jointsUpdated.AddListener(OnRightHandJointsUpdated);
                _subscribed = true;
                Debug.Log("[Gundam] Head Vulcan listening to right-hand joints on '" + rightHandEvents.gameObject.name + "'.");
            }
        }

        void OnDisable()
        {
            if (_subscribed && rightHandEvents != null)
            {
                rightHandEvents.jointsUpdated.RemoveListener(OnRightHandJointsUpdated);
            }
            _subscribed = false;
            _thumbTracked = false;
        }

        /// <summary>Prefers the XRHandTrackingEvents that sits next to a Right-handed
        /// HandJointTracker (i.e. "RightHandTracker"), falling back to any
        /// Right-handed XRHandTrackingEvents in the scene.</summary>
        static XRHandTrackingEvents FindRightHandEvents()
        {
            XRHandTrackingEvents fallback = null;
            foreach (XRHandTrackingEvents ev in FindObjectsByType<XRHandTrackingEvents>(FindObjectsInactive.Include))
            {
                if (ev.handedness != Handedness.Right) continue;
                HandJointTracker tracker = ev.GetComponent<HandJointTracker>();
                if (tracker != null && tracker.handedness == Handedness.Right) return ev;
                if (fallback == null) fallback = ev;
            }
            return fallback;
        }

        /// <summary>
        /// Called once by GundamCockpitSetup at scene-build time with the SAME
        /// XRHandTrackingEvents component RightHandTracker's HandJointTracker
        /// already uses. It now only STORES the reference (serialized with the
        /// scene); the actual listener is added at runtime in OnEnable - see the
        /// root-cause note above for why subscribing here never worked.
        /// </summary>
        public void SubscribeToRightHand(XRHandTrackingEvents events)
        {
            if (events == null)
            {
                Debug.LogWarning("[Gundam] HeadVulcanController.SubscribeToRightHand called with null - " +
                    "will try to find RightHandTracker automatically at runtime.");
                return;
            }
            rightHandEvents = events;
            // If this is ever called during Play (not the normal editor build
            // path), subscribe right away too.
            if (Application.isPlaying && isActiveAndEnabled && !_subscribed)
            {
                rightHandEvents.jointsUpdated.AddListener(OnRightHandJointsUpdated);
                _subscribed = true;
            }
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

            float distanceBend = Mathf.Clamp01(Mathf.InverseLerp(extendedDistance, curledDistance, tipToPalm));

            // Per report ("엄지를 구부려도 발사가안됨" - still not firing after
            // the listener fix, with the rebuilt scene confirmed to have
            // rightHandEvents wired): the distance-to-palm estimate above
            // assumes the thumb lies in a straight line out from the palm, so
            // its "extended" reference is too long and an ordinary thumb bend
            // (tip ~5-6 cm from the palm center) only reaches ~0.5 - below the
            // 0.6 fire threshold. So bend is now ALSO measured directly as how
            // much the thumb's own joint chain is folded: the angle between
            // metacarpal->proximal and proximal->distal, plus between
            // proximal->distal and distal->tip. A straight thumb is ~10-30
            // degrees total; a deliberate bend is 60+ degrees. This doesn't
            // depend on hand size or on where the palm joint sits. The larger
            // of the two estimates is used, so anything that fired before
            // still fires, and the fire threshold itself is unchanged.
            float angleBend = 0f;
            bool gotMetacarpal = args.hand.GetJoint(XRHandJointID.ThumbMetacarpal).TryGetPose(out Pose metacarpalPose);
            if (gotMetacarpal && gotProximal && gotDistal)
            {
                Vector3 seg1 = proximalPose.position - metacarpalPose.position;
                Vector3 seg2 = distalPose.position - proximalPose.position;
                Vector3 seg3 = tipPose.position - distalPose.position;
                float curl = Vector3.Angle(seg1, seg2) + Vector3.Angle(seg2, seg3);
                ThumbCurlDegrees = curl;
                angleBend = Mathf.Clamp01(Mathf.InverseLerp(straightThumbCurlDegrees, bentThumbCurlDegrees, curl));
            }
            else if (gotProximal && gotDistal)
            {
                // No metacarpal joint from this runtime - use just the last
                // (IP) joint's fold, scaled up since it's only half the chain.
                Vector3 seg2 = distalPose.position - proximalPose.position;
                Vector3 seg3 = tipPose.position - distalPose.position;
                float curl = Vector3.Angle(seg2, seg3) * 2f;
                ThumbCurlDegrees = curl;
                angleBend = Mathf.Clamp01(Mathf.InverseLerp(straightThumbCurlDegrees, bentThumbCurlDegrees, curl));
            }

            _thumbBendAmount = Mathf.Max(distanceBend, angleBend);
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
            //
            // Per request ("엄지 부분에 버튼이 눌렸을때 발칸이 나가게하자"):
            // the trigger is now the RightJoystick's THUMB BUTTON (see
            // JoystickFingerButtons - pressed while the right stick is held).
            // The thumb-bend trigger is kept (not deleted) behind
            // fireOnThumbBend, off by default; it's also used automatically
            // as a fallback if fireButtons isn't wired (an older scene).
            bool buttonFire = fireButtons != null && fireButtons.ThumbPressed;
            bool bendFire = (fireOnThumbBend || fireButtons == null)
                && _thumbTracked && _thumbBendAmount >= thumbBendThreshold;
            if ((buttonFire || bendFire) && _cooldownTimer <= 0f)
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

            // UPDATE (per "총알을 발사하면 내가 볼수있어야함"): the pilot never
            // sees the world from mainCamera itself - it's enclosed by the
            // opaque Cockpit_Dome, which shows the world as seen from HeadCam,
            // turned by the RightJoystick (CockpitViewController.LookRotation:
            // looking at dome direction d shows world direction LookRotation*d).
            // So the gaze the pilot actually aims with is LookRotation *
            // mainCamera.forward, starting at HeadCam. Both muzzles now fire
            // toward the point on that gaze ray (first thing it hits, else
            // convergenceDistance out), so the shots land where the pilot is
            // looking and the two tracers visibly converge from the sides.
            // With no viewController wired (older scene), this falls back to
            // exactly the old behavior: every bullet along mainCamera.forward.
            bool converge = false;
            Vector3 aimPoint = Vector3.zero;
            if (viewController != null && viewController.viewCamera != null)
            {
                Vector3 gazeDir = viewController.LookRotation * fireDirection;
                Vector3 gazeOrigin = viewController.viewCamera.transform.position;
                float aimDist = convergenceDistance;
                if (Physics.Raycast(gazeOrigin, gazeDir, out RaycastHit hit, maxAimDistance) && hit.distance > 5f)
                {
                    aimDist = hit.distance;
                }
                aimPoint = gazeOrigin + gazeDir * aimDist;
                fireDirection = gazeDir;
                converge = true;
            }

            if (alternateMuzzles && muzzleLeft != null && muzzleRight != null)
            {
                Transform muzzle = (_muzzleToggle % 2 == 0) ? muzzleLeft : muzzleRight;
                _muzzleToggle++;
                SpawnBullet(muzzle, DirectionFrom(muzzle, converge, aimPoint, fireDirection));
            }
            else
            {
                if (muzzleLeft != null) SpawnBullet(muzzleLeft, DirectionFrom(muzzleLeft, converge, aimPoint, fireDirection));
                if (muzzleRight != null) SpawnBullet(muzzleRight, DirectionFrom(muzzleRight, converge, aimPoint, fireDirection));
            }

            LastFireTime = Time.time;

            // HUD-only ammo bookkeeping - see ConsumeAmmoForShot's own doc
            // comment. Does not affect anything above.
            ConsumeAmmoForShot();
        }

        static Vector3 DirectionFrom(Transform muzzle, bool converge, Vector3 aimPoint, Vector3 fallbackDirection)
        {
            if (!converge) return fallbackDirection;
            Vector3 d = aimPoint - muzzle.position;
            return d.sqrMagnitude > 0.0001f ? d.normalized : fallbackDirection;
        }

        void SpawnBullet(Transform muzzle, Vector3 fireDirection)
        {
            Quaternion fireRotation = Quaternion.LookRotation(fireDirection);

            GameObject go;
            if (projectilePrefab != null)
            {
                go = Instantiate(projectilePrefab, muzzle.position, fireRotation);
            }
            else
            {
                // Temporary placeholder bullet - per request ("실제 탄환 모델이
                // 없다면 작은 Sphere 또는 Capsule을 임시 탄환으로 만들어도 된다.
                // 중요한 것은 먼저 기능을 확인하는 것이다."). Now a long thin
                // glowing tracer streak (see the tracer fields' comment) instead
                // of a tiny sphere: a Cylinder's long axis is its local Y, so
                // it's tipped 90 degrees to lie along the flight direction, and
                // offset forward by half its length so it starts at the muzzle.
                // Collider removed so it can't push the Gundam/cockpit around or
                // block its own straight-line flight.
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "HeadVulcanBullet";
                go.transform.SetPositionAndRotation(
                    muzzle.position + fireDirection * (tracerLength * 0.5f),
                    fireRotation * Quaternion.Euler(90f, 0f, 0f));
                go.transform.localScale = new Vector3(tracerWidth, tracerLength * 0.5f, tracerWidth);
                Collider col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);

                if (bulletMat != null)
                {
                    Renderer rend = go.GetComponent<Renderer>();
                    if (rend != null) rend.sharedMaterial = bulletMat;
                }
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.linearVelocity = fireDirection * bulletSpeed;

            Destroy(go, bulletLifetime);

            SpawnMuzzleFlash(muzzle);
        }

        void SpawnMuzzleFlash(Transform muzzle)
        {
            if (muzzleFlashSize <= 0f) return;
            GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flash.name = "HeadVulcanMuzzleFlash";
            flash.transform.SetPositionAndRotation(muzzle.position, Quaternion.identity);
            flash.transform.localScale = Vector3.one * muzzleFlashSize;
            Collider col = flash.GetComponent<Collider>();
            if (col != null) Destroy(col);
            if (bulletMat != null)
            {
                Renderer rend = flash.GetComponent<Renderer>();
                if (rend != null) rend.sharedMaterial = bulletMat;
            }
            Destroy(flash, muzzleFlashDuration);
        }
    }
}
