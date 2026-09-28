using UnityEngine;
using UnityEngine.UI;

namespace Gundam.Cockpit
{
    /// <summary>FrontDisplay (real GameObject name: "SysCheck_Center") - the
    /// main tactical HUD: SPEED, THROTTLE/TURN/HEADING, a center reticle
    /// (built once, purely decorative, by GundamCockpitSetup.cs - not
    /// animated here), and a NORMAL/WARNING/CRITICAL status badge driven by
    /// the shared CockpitHUDManager. The head-cam feed's small corner inset
    /// also lives on this same screen now (see BuildSystemCheckDisplay) but
    /// needs no per-frame script of its own - it's just a RawImage sampling a
    /// RenderTexture that GundamHeadCam360/PlaceExternalGundam already fill.
    ///
    /// EXTENDED (not replaced) for the 3-display Cockpit HUD request - this
    /// class already existed and drove THROTTLE/TURN/AIM plus an optional
    /// weapon name/ammo readout; the weapon readout now lives on RightDisplay
    /// instead (see CockpitWeaponHUD, driven by HeadVulcanController rather
    /// than the old cockpit-turret WeaponAimFireController), so `weapon`/
    /// `weaponNameText`/`ammoText` below are kept for backward compatibility
    /// but are no longer wired up by GundamCockpitSetup.cs - Update() still
    /// guards on them being null, so this is a no-op, not a break.</summary>
    public class CockpitHUD : MonoBehaviour
    {
        public JoystickLever leftStick;
        public JoystickLever rightStick;
        public Text infoText;

        [Header("Speed / status (added for the 3-display Cockpit HUD)")]
        public ShipMovementController ship;
        public CockpitHUDManager hudManager;
        public Text speedText;
        public Text statusText;

        [Header("Weapon status (legacy/optional - RightDisplay's CockpitWeaponHUD is the current weapon readout)")]
        public WeaponAimFireController weapon;
        public Text weaponNameText;
        public Text ammoText;

        void Update()
        {
            if (infoText != null)
            {
                Vector2 l = leftStick != null ? leftStick.tiltInput : Vector2.zero;

                float headingDeg = ship != null ? NormalizeDegrees(ship.HeadingYaw) : 0f;

                infoText.text =
                    "GUNDAM COCKPIT\n" +
                    $"THROTTLE {l.y * 100f:0}%   TURN {l.x * 100f:0}%\n" +
                    $"HEADING {headingDeg:000}°" +
                    (leftStick != null && leftStick.isGrabbed ? "  [L GRIP]" : "");
            }

            if (speedText != null && ship != null)
            {
                speedText.text = $"SPEED {Mathf.Abs(ship.CurrentSpeed):0.0} m/s";
            }

            if (statusText != null && hudManager != null)
            {
                GundamVitals v = hudManager.Vitals;
                string label = v.OverallStatus switch
                {
                    GundamVitals.Status.Critical => "STATUS: CRITICAL",
                    GundamVitals.Status.Warning => "STATUS: WARNING",
                    _ => "STATUS: NORMAL",
                };
                statusText.text = label;
                statusText.color = GundamVitals.StatusColor(v.HPFraction);
            }

            if (weapon != null)
            {
                if (weaponNameText != null) weaponNameText.text = weapon.CurrentWeaponName;
                if (ammoText != null) ammoText.text = $"AMMO {weapon.CurrentAmmo:000}/{weapon.CurrentMaxAmmo:000}";
            }
        }

        static float NormalizeDegrees(float deg)
        {
            deg %= 360f;
            if (deg < 0f) deg += 360f;
            return deg;
        }
    }
}
