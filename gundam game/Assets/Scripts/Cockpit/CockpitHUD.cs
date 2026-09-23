using UnityEngine;
using UnityEngine.UI;

namespace Gundam.Cockpit
{
    /// <summary>Front-display readout: throttle/turn/aim telemetry, plus (optionally) a
    /// weapon name + ammo readout for the System Check dashboard screen.</summary>
    public class CockpitHUD : MonoBehaviour
    {
        public JoystickLever leftStick;
        public JoystickLever rightStick;
        public Text infoText;

        [Header("Weapon status (optional - leave unset to skip)")]
        public WeaponAimFireController weapon;
        public Text weaponNameText;
        public Text ammoText;

        void Update()
        {
            if (infoText != null)
            {
                Vector2 l = leftStick != null ? leftStick.tiltInput : Vector2.zero;
                Vector2 r = rightStick != null ? rightStick.tiltInput : Vector2.zero;

                infoText.text =
                    "GUNDAM COCKPIT\n" +
                    $"THROTTLE {l.y * 100f:0}%   TURN {l.x * 100f:0}%\n" +
                    $"AIM  X {r.x * 100f:0}   Y {r.y * 100f:0}\n" +
                    (leftStick != null && leftStick.isGrabbed ? "[L GRIP]" : "") +
                    (rightStick != null && rightStick.isGrabbed ? " [R GRIP]" : "");
            }

            if (weapon != null)
            {
                if (weaponNameText != null) weaponNameText.text = weapon.CurrentWeaponName;
                if (ammoText != null) ammoText.text = $"AMMO {weapon.CurrentAmmo:000}/{weapon.CurrentMaxAmmo:000}";
            }
        }
    }
}
