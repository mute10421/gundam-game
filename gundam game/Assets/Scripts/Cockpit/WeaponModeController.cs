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
    /// Choosing another weapon undoes all of that, so the right hand can grab and
    /// aim with RightJoystick again. RightJoystick's own code is not modified -
    /// only these two references are swapped at runtime and put back. If the right
    /// hand is still holding RightJoystick when BEAM SABER is touched, the switch
    /// waits until it lets go (never yanks a held stick). LeftJoystick, the T lever
    /// and all movement are untouched.
    /// </summary>
    public class WeaponModeController : MonoBehaviour
    {
        public enum Mode { BeamRifle, BeamSaber }

        [Header("Right joystick (blocked in BEAM SABER)")]
        public JoystickLever rightStick;
        [Tooltip("The XRSimpleInteractable on RightJoystick's handle.")]
        public XRSimpleInteractable rightStickInteractable;

        [Header("BEAM SABER")]
        public BeamSaberControlStick saberStick;
        public BeamSaberArmController saberArm;

        public Mode CurrentMode { get; private set; } = Mode.BeamRifle;
        public bool HasPending => _pending.HasValue;
        public Mode? PendingMode => _pending;
        public event Action<Mode> ModeChanged;

        HandJointTracker _savedRightTracker;
        bool _rightBlocked;
        Mode? _pending;

        public static string DisplayName(Mode m) => m == Mode.BeamSaber ? "BEAM SABER" : "BEAM RIFLE";

        void Start()
        {
            ApplyMode(Mode.BeamRifle, force: true);
        }

        /// <summary>Called by the WEAPON display buttons.</summary>
        public void SelectWeapon(Mode m)
        {
            if (m == CurrentMode && !_pending.HasValue) return;
            if (m == Mode.BeamSaber && rightStick != null && rightStick.isGrabbed)
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

            SetRightJoystickBlocked(saber);

            if (saberStick != null)
            {
                saberStick.gameObject.SetActive(saber);
                if (saber) saberStick.SpawnAtHand();
            }
            if (saberArm != null) saberArm.SetActive(saber);

            ModeChanged?.Invoke(m);
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
