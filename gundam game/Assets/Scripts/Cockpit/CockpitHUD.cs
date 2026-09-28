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

        [Header("Head Vulcan ammo (added per report - \"내 앞에 디스플레이에 표시가 안됨\": RightDisplay's CockpitWeaponHUD already shows this off to the side, this duplicates it directly on FrontDisplay/SysCheck_Center - the screen straight ahead of the pilot - so it's visible without looking away while aiming with the head)")]
        public HeadVulcanController headVulcan;
        public Text vulcanAmmoText;

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

            if (vulcanAmmoText != null && headVulcan != null)
            {
                string stateLabel = headVulcan.State switch
                {
                    HeadVulcanController.FireReadyState.Empty => "EMPTY",
                    HeadVulcanController.FireReadyState.Reloading => "RELOADING",
                    _ => "READY",
                };
                // Thumb readout (per "총알이 안나가는거 같아"): shows whether the
                // right thumb is tracked at all and its bend vs. the fire
                // threshold, plus a brief FIRE! flash on each shot - so it's
                // visible on the headset whether a shot actually triggered.
                string thumb;
                JoystickFingerButtons btn = headVulcan.fireButtons;
                if (btn != null)
                {
                    // Thumb-BUTTON trigger (per "엄지 부분에 버튼이 눌렸을때 발칸이
                    // 나가게하자"): shows whether the right stick is held and its
                    // buttons fitted, and how far the thumb is pushed in (mm) vs.
                    // the press depth.
                    if (btn.lever == null || !btn.lever.isGrabbed) thumb = "THUMB BTN: GRAB R-STICK";
                    else if (!btn.ButtonsFitted) thumb = "THUMB BTN: FITTING...";
                    else thumb = $"THUMB BTN {(btn.ThumbPressed ? "ON" : "--")}  " +
                        $"{Mathf.RoundToInt(btn.PressAmount(JoystickFingerButtons.Finger.Thumb) * 1000f)}/{Mathf.RoundToInt(btn.pressDepth * 1000f)}mm  " +
                        $"{Mathf.RoundToInt(btn.CurlAmount(JoystickFingerButtons.Finger.Thumb))}/{Mathf.RoundToInt(btn.pressCurlDegrees)}deg";
                }
                else
                {
                    thumb = headVulcan.IsThumbTracked
                        ? $"THUMB {Mathf.RoundToInt(headVulcan.ThumbBend * 100f)}%/{Mathf.RoundToInt(headVulcan.thumbBendThreshold * 100f)}%"
                        : "THUMB --";
                }
                string fired = Time.time - headVulcan.LastFireTime < 0.2f ? "  FIRE!" : "";
                vulcanAmmoText.text = $"{headVulcan.weaponName}  AMMO {headVulcan.CurrentAmmo:00}/{headVulcan.maxAmmo:00}  [{stateLabel}]\n{thumb}{fired}";
                vulcanAmmoText.color = GundamVitals.StatusColor(headVulcan.AmmoFraction);
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
