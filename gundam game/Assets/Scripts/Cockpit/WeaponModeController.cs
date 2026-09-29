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
        public enum Mode { BeamRifle = 0, BeamSaber = 1, HeadVulcan = 2 }

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

        [Header("BEAM RIFLE")]
        public BeamRifleController rifle;
        [Tooltip("Switched off while BEAM RIFLE is active (the right thumb fires the rifle instead).")]
        public HeadVulcanController vulcan;
        [Tooltip("Mode at start.")]
        public Mode startMode = Mode.HeadVulcan;

        public Mode CurrentMode { get; private set; } = Mode.HeadVulcan;
        public bool HasPending => _pending.HasValue;
        public Mode? PendingMode => _pending;
        public event Action<Mode> ModeChanged;

        HandJointTracker _savedRightTracker;
        bool _rightBlocked;
        bool _consumersDetached;
        bool _aimWasEnabled;
        JoystickLever _savedViewStick;
        Mode? _pending;

        public static string DisplayName(Mode m) =>
            m == Mode.BeamSaber ? "BEAM SABER" : m == Mode.BeamRifle ? "BEAM RIFLE" : "HEAD VULCAN";

        void Start()
        {
            ApplyMode(startMode, force: true);
        }

        /// <summary>Called by the WEAPON display buttons.</summary>
        public void SelectWeapon(Mode m)
        {
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
            if (rifle != null) rifle.SetActive(m == Mode.BeamRifle);
            if (vulcan != null) vulcan.enabled = m != Mode.BeamRifle;

            ModeChanged?.Invoke(m);
        }

        /// <summary>BEAM SABER with RightJoystick driving the saber: take the stick
        /// away from aiming and view turning (only references/enabled flags - their
        /// code is unchanged), and give it back afterwards.</summary>
        void SetConsumersDetached(bool detach)
        {
            if (detach && !_consumersDetached)
            {
                if (aim != null) { _aimWasEnabled = aim.enabled; aim.enabled = false; }
                if (viewController != null) { _savedViewStick = viewController.rightJoystick; viewController.rightJoystick = null; }
                _consumersDetached = true;
            }
            else if (!detach && _consumersDetached)
            {
                if (aim != null) aim.enabled = _aimWasEnabled;
                if (viewController != null && viewController.rightJoystick == null) viewController.rightJoystick = _savedViewStick;
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
