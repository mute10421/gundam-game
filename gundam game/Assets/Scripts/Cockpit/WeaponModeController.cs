using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Current hand weapon + who owns the pilot's RIGHT hand - per request ("빔샤벨
    /// 모드"). Selected by touching the WEAPON display (WeaponTouchPanel).
    ///
    ///   BEAM RIFLE (default) - right hand drives RightJoystick exactly as before
    ///                          (aim/view, thumb button -> Head Vulcan). No rifle
    ///                          firing yet - this mode is the "joystick" mode.
    ///   BEAM SABER           - RightJoystick stops receiving the right hand:
    ///                            * its hand tracker slot is emptied (so its own
    ///                              proximity/grip grab can't trigger), and
    ///                            * the XRSimpleInteractable on its handle is
    ///                              disabled (so the XRI grab can't either);
    ///                          it eases back to center and outputs 0. The hand-held
    ///                          BeamSaberControlStick appears at the right palm, and
    ///                          the Gundam's right arm + beam saber are shown and
    ///                          driven from that stick (BeamSaberArmController).
    ///
    /// NOW (rightStickDrivesSaber, per "빔샤벨 조종기를 빼고 오른쪽 조종관으로 밀고
    /// 당기기는 찌르기 옆으로 당기면 옆으로 휘둘러"): in BEAM SABER the right hand
    /// keeps RightJoystick and it drives the saber instead (BeamSaberArmController
    /// reads its tiltInput: push/pull = thrust, sideways = swing). RightJoystick is
    /// NOT blocked then; instead its normal consumers are detached for the mode -
    /// WeaponAimFireController is disabled (so a forward thrust can't fire the
    /// rifle) and CockpitViewController's rightJoystick reference is emptied (the
    /// view follows the target instead, SaberLookAssist) - and put back afterwards.
    /// No hand-held stick is used, and no wait for the right hand to let go.
    ///
    /// THREE MODES now (per "무기에 빔라이플을 추가하고 ... 빔라이플 모드에서는
    /// 해드발칸은 사용안함", option "HEAD VULCAN 버튼 추가"):
    ///   HEAD VULCAN (default) - exactly the old joystick mode: RightJoystick
    ///                           aims/turns the view, right thumb = Head Vulcan.
    ///   BEAM RIFLE            - same RightJoystick look, but the Gundam holds the
    ///                           rifle in both hands (BeamRifleController) and the
    ///                           right thumb fires it at the locked target; the
    ///                           Head Vulcan component is switched off.
    ///   BEAM SABER            - as described above (thumb = Head Vulcan).
    ///
    /// SABER: LOOK vs ATTACK (per "빔샤벨 상태에서 아무도 락온 안되어있을때는
    /// 화면조작 락온이 되면 그때 공격기능으로"): in BEAM SABER, while nothing is
    /// locked RightJoystick turns the view as usual (the saber stays in guard); as
    /// soon as the lock-on ring captures an enemy the view follows it
    /// (SaberLookAssist) and RightJoystick becomes the saber (thrust / swing). If
    /// the stick is still deflected at that moment, it has to come back near
    /// center first (so the turn you were making doesn't turn into a slash) - the
    /// same when the lock is lost: the saber drops to guard at once and the view
    /// takes the stick again once it's re-centered.
    ///
    /// Choosing another weapon undoes all of that, so the right hand can grab and
    /// aim with RightJoystick again. RightJoystick's own code is not modified -
    /// only these two references are swapped at runtime and put back. If the right
    /// hand is still holding RightJoystick when BEAM SABER is touched, the switch
    /// waits until it lets go (never yanks a held stick). LeftJoystick, the T lever
    /// and all movement are untouched.
    /// </summary>
    public class WeaponModeController : MonoBehaviour
    {
        // Values kept stable (BeamRifle = 0, BeamSaber = 1) for already-built scenes.
        public enum Mode { BeamRifle = 0, BeamSaber = 1, HeadVulcan = 2, Bazooka = 3 }

        [Header("Right joystick (blocked in BEAM SABER)")]
        public JoystickLever rightStick;
        [Tooltip("The XRSimpleInteractable on RightJoystick's handle.")]
        public XRSimpleInteractable rightStickInteractable;

        [Header("BEAM SABER")]
        [Tooltip("RightJoystick drives the saber in BEAM SABER (thrust / swing). Off = old behaviour: RightJoystick blocked + hand-held saberStick.")]
        public bool rightStickDrivesSaber = true;
        [Tooltip("Disabled while BEAM SABER is active (so a thrust doesn't fire the rifle).")]
        public WeaponAimFireController aim;
        [Tooltip("Its rightJoystick reference is emptied while BEAM SABER is active (view follows the target instead).")]
        public CockpitViewController viewController;
        public BeamSaberControlStick saberStick;
        public BeamSaberArmController saberArm;

        [Tooltip("Lock-on ring - decides SABER look (nothing locked) vs attack (locked). Found automatically if empty.")]
        public OrbitHUDTargetLock targetLock;
        [Tooltip("RightJoystick changes owner (view <-> saber) only once its deflection is below this.")]
        [Range(0.05f, 1f)] public float saberRecenterThreshold = 0.35f;

        [Header("BEAM RIFLE")]
        public BeamRifleController rifle;
        [Tooltip("Switched off while BEAM RIFLE is active (the right thumb fires the rifle instead).")]
        public HeadVulcanController vulcan;
        [Tooltip("Mode at start.")]
        public Mode startMode = Mode.HeadVulcan;

        [Header("ZAKU loadout (per \"자쿠로 고르면 무기창에 히트호크랑 머신건이 있는거야\")")]
        [Tooltip("True while the pilot flies the ZAKU: BeamRifle slot = MACHINE GUN, BeamSaber slot = HEAT HAWK, no HEAD VULCAN.")]
        public bool zakuMode;
        [Tooltip("WEAPON screen - shows HEAT HAWK while it's out in ZAKU mode (the vulcan readout is gone there).")]
        public CockpitWeaponHUD weaponHUD;
        [Tooltip("ZAKU BAZOOKA (per \"자쿠 바주카 ... 조작법은 빔라이플이랑 같아\"): a second rifle-type weapon, same controls as BEAM RIFLE (lock + right thumb). ZAKU only.")]
        public BeamRifleController bazooka;

        public Mode CurrentMode { get; private set; } = Mode.HeadVulcan;
        /// <summary>BEAM SABER: RightJoystick currently drives the saber (an enemy is locked).</summary>
        public bool SaberAttackActive => _saberInput;
        public bool HasPending => _pending.HasValue;
        public Mode? PendingMode => _pending;
        public event Action<Mode> ModeChanged;

        HandJointTracker _savedRightTracker;
        bool _rightBlocked;
        bool _consumersDetached;
        bool _aimWasEnabled;
        JoystickLever _savedViewStick;
        Mode? _pending;
        bool _viewDetached;
        bool _saberInput;

        public static string DisplayName(Mode m) =>
            m == Mode.BeamSaber ? "BEAM SABER" : m == Mode.BeamRifle ? "BEAM RIFLE" : m == Mode.Bazooka ? "BAZOOKA" : "HEAD VULCAN";

        /// <summary>Weapon name for the current mobile suit (GUNDAM or ZAKU).</summary>
        public string NameOf(Mode m) =>
            !zakuMode ? DisplayName(m) : m == Mode.BeamSaber ? "HEAT HAWK" : m == Mode.BeamRifle ? "MACHINE GUN" : m == Mode.Bazooka ? "BAZOOKA" : "-";

        /// <summary>Does the current mobile suit have this weapon? (the ZAKU has no Head
        /// Vulcan; the BAZOOKA belongs to the ZAKU only)</summary>
        public bool Has(Mode m) => m == Mode.Bazooka ? zakuMode && bazooka != null : !(zakuMode && m == Mode.HeadVulcan);

        /// <summary>Swap the whole hand-weapon set (mobile suit selection at game start):
        /// puts the current weapons away, takes the new ones and selects startWith.</summary>
        public void SetLoadout(BeamRifleController newRifle, BeamSaberArmController newSaberArm, HeadVulcanController newVulcan,
            bool zaku, Mode startWith, BeamRifleController newBazooka = null)
        {
            _pending = null;
            SetConsumersDetached(false);
            SetRightJoystickBlocked(false);
            if (saberArm != null) saberArm.SetActive(false);
            if (rifle != null) rifle.SetActive(false);
            if (bazooka != null) bazooka.SetActive(false);
            if (vulcan != null && newVulcan != vulcan) vulcan.enabled = false;
            rifle = newRifle;
            saberArm = newSaberArm;
            vulcan = newVulcan;
            bazooka = newBazooka;
            zakuMode = zaku;
            startMode = startWith;
            ApplyMode(startWith, force: true);
        }

        void Start()
        {
            if (targetLock == null) targetLock = FindFirstObjectByType<OrbitHUDTargetLock>();
            ApplyMode(startMode, force: true);
        }

        /// <summary>Called by the WEAPON display buttons.</summary>
        public void SelectWeapon(Mode m)
        {
            if (!Has(m)) return;
            if (m == CurrentMode && !_pending.HasValue) return;
            if (m == Mode.BeamSaber && !rightStickDrivesSaber && rightStick != null && rightStick.isGrabbed)
            {
                _pending = m; // wait for the right hand to let go of RightJoystick
                return;
            }
            _pending = null;
            ApplyMode(m, force: false);
        }

        void Update()
        {
            if (_pending.HasValue && (rightStick == null || !rightStick.isGrabbed))
            {
                Mode m = _pending.Value;
                _pending = null;
                ApplyMode(m, force: false);
            }
            UpdateSaberOwner();
        }

        /// <summary>BEAM SABER: who gets RightJoystick - the view (nothing locked) or
        /// the saber (locked). See the class comment.</summary>
        void UpdateSaberOwner()
        {
            if (!_consumersDetached) return; // not in BEAM SABER (RightJoystick mode)
            Transform t = targetLock != null ? targetLock.CurrentTarget : null;
            bool locked = t != null && t.gameObject.activeInHierarchy;
            if (locked)
            {
                EnemyHealth hp = t.GetComponent<EnemyHealth>();
                if (hp != null && hp.IsDead) locked = false;
            }
            bool centered = rightStick == null || rightStick.tiltInput.magnitude < saberRecenterThreshold;
            if (locked)
            {
                SetViewStickDetached(true);          // the view follows the target now
                if (!_saberInput && centered) _saberInput = true;
            }
            else
            {
                _saberInput = false;                 // saber back to guard at once
                if (_viewDetached && centered) SetViewStickDetached(false);
            }
            if (saberArm != null) saberArm.inputEnabled = _saberInput;
        }

        void SetViewStickDetached(bool detach)
        {
            if (viewController == null) { _viewDetached = detach; return; }
            if (detach && !_viewDetached)
            {
                _savedViewStick = viewController.rightJoystick;
                viewController.rightJoystick = null;
                _viewDetached = true;
            }
            else if (!detach && _viewDetached)
            {
                if (viewController.rightJoystick == null) viewController.rightJoystick = _savedViewStick;
                _viewDetached = false;
            }
        }

        void ApplyMode(Mode m, bool force)
        {
            if (!force && m == CurrentMode) return;
            CurrentMode = m;
            bool saber = m == Mode.BeamSaber;

            if (rightStickDrivesSaber)
            {
                SetRightJoystickBlocked(false);
                SetConsumersDetached(saber);
            }
            else
            {
                SetConsumersDetached(false);
                SetRightJoystickBlocked(saber);
            }

            if (saberStick != null)
            {
                bool useStick = saber && !rightStickDrivesSaber;
                saberStick.gameObject.SetActive(useStick);
                if (useStick) saberStick.SpawnAtHand();
            }
            if (saberArm != null) saberArm.SetActive(saber);
            // Put the unused rifle-type weapon away BEFORE taking the new one out
            // (each one saves / restores the lock-on time while it's out).
            if (rifle != null && m != Mode.BeamRifle) rifle.SetActive(false);
            if (bazooka != null && m != Mode.Bazooka) bazooka.SetActive(false);
            if (rifle != null && m == Mode.BeamRifle) rifle.SetActive(true);
            if (bazooka != null && m == Mode.Bazooka) bazooka.SetActive(true);
            if (vulcan != null) vulcan.enabled = m != Mode.BeamRifle && m != Mode.Bazooka;
            if (zakuMode && saber && weaponHUD != null)
            {
                if (weaponHUD.nameText != null) weaponHUD.nameText.text = "HEAT HAWK";
                if (weaponHUD.ammoText != null) weaponHUD.ammoText.text = "MELEE";
                if (weaponHUD.ammoFillImage != null) { weaponHUD.ammoFillImage.fillAmount = 1f; weaponHUD.ammoFillImage.color = new Color(1f, 0.55f, 0.15f); }
                if (weaponHUD.stateText != null) { weaponHUD.stateText.text = "READY"; weaponHUD.stateText.color = new Color(0.3f, 1f, 0.4f); }
            }

            ModeChanged?.Invoke(m);
        }

        /// <summary>BEAM SABER with RightJoystick driving the saber: take the stick
        /// away from aiming and view turning (only references/enabled flags - their
        /// code is unchanged), and give it back afterwards.</summary>
        void SetConsumersDetached(bool detach)
        {
            // The rifle aim/fire is off for the whole of BEAM SABER; the view stick
            // is handed back and forth by UpdateSaberOwner (look vs attack).
            if (detach && !_consumersDetached)
            {
                if (aim != null) { _aimWasEnabled = aim.enabled; aim.enabled = false; }
                _saberInput = false;
                if (saberArm != null) saberArm.inputEnabled = false;
                _consumersDetached = true;
                UpdateSaberOwner();
            }
            else if (!detach && _consumersDetached)
            {
                if (aim != null) aim.enabled = _aimWasEnabled;
                SetViewStickDetached(false);
                _saberInput = false;
                if (saberArm != null) saberArm.inputEnabled = true;
                _consumersDetached = false;
            }
        }

        void SetRightJoystickBlocked(bool block)
        {
            if (rightStick == null) return;
            if (block && !_rightBlocked)
            {
                _savedRightTracker = rightStick.rightHandTracker;
                rightStick.rightHandTracker = null;
                if (rightStickInteractable != null) rightStickInteractable.enabled = false;
                _rightBlocked = true;
            }
            else if (!block && _rightBlocked)
            {
                rightStick.rightHandTracker = _savedRightTracker;
                if (rightStickInteractable != null) rightStickInteractable.enabled = true;
                _rightBlocked = false;
            }
        }
    }
}
