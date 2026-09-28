using UnityEngine;
using UnityEngine.UI;

namespace Gundam.Cockpit
{
    /// <summary>
    /// RightDisplay (real GameObject name: "SysCheck_Right") - WEAPON screen.
    /// Shows the Head Vulcan's HUD-facing status: weapon name, ammo count,
    /// an ammo gauge, and READY/EMPTY/RELOADING.
    ///
    /// Reads HeadVulcanController's ammo/state properties (CurrentAmmo,
    /// maxAmmo, AmmoFraction, State) added specifically for this HUD - see
    /// that class's own doc comment. This script never calls into
    /// HeadVulcanController's firing logic and never references
    /// WeaponAimFireController/RightJoystick at all, so the old turret weapon
    /// stays fully decoupled from Head Vulcan, exactly as before.
    ///
    /// UI (Text/Image refs) is created and wired by GundamCockpitSetup.cs's
    /// BuildWeaponScreenUI - this class only reads them every frame.
    /// </summary>
    public class CockpitWeaponHUD : MonoBehaviour
    {
        public HeadVulcanController headVulcan;

        public Text nameText;
        public Text ammoText;
        public Image ammoFillImage;
        public Text stateText;

        void Update()
        {
            if (headVulcan == null) return;

            if (nameText != null) nameText.text = headVulcan.weaponName;
            if (ammoText != null) ammoText.text = $"AMMO {headVulcan.CurrentAmmo:000}/{headVulcan.maxAmmo:000}";

            if (ammoFillImage != null)
            {
                float frac = Mathf.Clamp01(headVulcan.AmmoFraction);
                ammoFillImage.fillAmount = frac;
                ammoFillImage.color = GundamVitals.StatusColor(frac);
            }

            if (stateText != null)
            {
                switch (headVulcan.State)
                {
                    case HeadVulcanController.FireReadyState.Ready:
                        stateText.text = "READY";
                        stateText.color = new Color(0.3f, 1f, 0.4f);
                        break;
                    case HeadVulcanController.FireReadyState.Empty:
                        stateText.text = "EMPTY";
                        stateText.color = new Color(1f, 0.25f, 0.2f);
                        break;
                    case HeadVulcanController.FireReadyState.Reloading:
                        stateText.text = "RELOADING";
                        stateText.color = new Color(1f, 0.8f, 0.2f);
                        break;
                }
            }
        }
    }
}
