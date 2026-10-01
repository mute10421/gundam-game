using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Puts the pilot in the GUNDAM or the ZAKU - per "자쿠를 플레이할수있게해줘야해
    /// 게임을 시작하고 자쿠로 할지 건담으로 할지 고르게 하는거야". Both suits are built
    /// into the scene; the GUNDAM set is what's active by default (exactly the game as
    /// before), the ZAKU set waits switched off. Choosing ZAKU (MechSelectPanel):
    ///
    ///   * the pilot's body becomes the ZAKU: it is switched on where the Gundam
    ///     stands, the head camera (HeadCam - the whole outside view) moves into the
    ///     Zaku's head (its mono-eye), and the head-turn tracking, view turning,
    ///     collision, sortie teleport and hit points all follow the Zaku body;
    ///     the Gundam body is switched off;
    ///   * the WEAPON screen becomes MACHINE GUN + HEAT HAWK + BAZOOKA (no Head
    ///     Vulcan) - the same controls as BEAM RIFLE / BEAM SABER (lock + right
    ///     thumb to fire, RightJoystick to swing);
    ///   * the enemies change sides: the Zakus are switched off and the enemy
    ///     GUNDAMs (beam rifle + beam saber) are switched on;
    ///   * the cockpit is re-tinted Zeon green (CockpitTheme) and labelled ZAKU II.
    ///
    /// Nothing is destroyed or rebuilt - only references are swapped and objects
    /// switched on/off, so choosing GUNDAM leaves everything as it always was.
    /// </summary>
    public class PilotMechSwitcher : MonoBehaviour
    {
        public enum Mech { Gundam = 0, Zaku = 1 }

        [Header("Bodies")]
        public Transform suitRoot;
        public GameObject gundamBody;
        public GameObject zakuBody;
        public Transform zakuHead;
        [Tooltip("Empty under the Zaku's Head bone at its mono-eye: HeadCam is parented here.")]
        public Transform zakuHeadCamMount;

        [Header("View")]
        public Transform headCam;
        public GundamHeadCam360 headCam360;
        public CockpitViewController viewController;

        [Header("Weapons / HUD")]
        public WeaponModeController modes;
        public WeaponTouchPanel weaponPanel;
        public BeamRifleController zakuGun;
        public BeamSaberArmController zakuHawk;
        [Tooltip("ZAKU BAZOOKA - third ZAKU weapon, beam-rifle controls.")]
        public BeamRifleController zakuBazooka;
        public CockpitHUD hud;
        public CockpitWeaponHUD weaponHUD;
        public OrbitHUDTargetLock targetLock;

        [Header("Other systems that follow the body")]
        public SuitCollision suitCollision;
        public SpawnSelectPanel spawnPanel;
        public MechSelectPanel mechPanel;

        [Header("Enemies")]
        [Tooltip("Active while flying the GUNDAM (the Zakus).")]
        public GameObject[] gundamModeEnemies;
        [Tooltip("Active while flying the ZAKU (the enemy Gundams).")]
        public GameObject[] zakuModeEnemies;

        [Header("Cockpit")]
        public Transform cockpitInterior;

        public Mech Current { get; private set; } = Mech.Gundam;

        Vector3 _zakuOffset;     // Zaku body position relative to MobileSuitRoot, as built
        Quaternion _zakuRot;
        bool _haveOffset;

        void Awake()
        {
            CaptureOffset();
            if (zakuBody != null) zakuBody.SetActive(false);
            SetActive(zakuModeEnemies, false);
        }

        void CaptureOffset()
        {
            if (_haveOffset || zakuBody == null || suitRoot == null) return;
            _zakuOffset = zakuBody.transform.position - suitRoot.position;
            _zakuRot = zakuBody.transform.rotation;
            _haveOffset = true;
        }

        public void Apply(Mech mech)
        {
            Current = mech;
            if (mech == Mech.Gundam)
            {
                SetActive(zakuModeEnemies, false);
                SetActive(gundamModeEnemies, true);
                return;
            }
            if (zakuBody == null) { Debug.LogWarning("[Gundam] ZAKU body missing - staying in the GUNDAM."); Current = Mech.Gundam; return; }

            CaptureOffset();
            // 1) The Zaku body takes the Gundam's place (switched on first, so its
            //    weapon scripts initialise from its rest pose before they're used).
            zakuBody.transform.SetPositionAndRotation(suitRoot.position + _zakuOffset, _zakuRot);
            zakuBody.SetActive(true);

            // 2) The outside view moves into the Zaku's head.
            if (headCam != null && zakuHeadCamMount != null)
            {
                headCam.SetParent(zakuHeadCamMount, false);
                headCam.localPosition = Vector3.zero;
                headCam.localRotation = Quaternion.identity;
            }
            if (headCam360 != null && zakuHead != null) headCam360.SetHead(zakuHead);
            if (viewController != null) viewController.RecaptureCameraStart();

            // 3) Gundam weapons down (its body still exists), the Zaku's set up.
            if (modes != null)
            {
                modes.weaponHUD = weaponHUD;
                modes.SetLoadout(zakuGun, zakuHawk, null, true, WeaponModeController.Mode.BeamRifle, zakuBazooka);
            }
            if (weaponPanel != null) weaponPanel.ApplyMechLayout();

            if (gundamBody != null) gundamBody.SetActive(false);

            // 4) Everything that tracked the Gundam body.
            if (suitCollision != null) suitCollision.SetBody(zakuBody.transform);
            if (spawnPanel != null) spawnPanel.body = zakuBody.transform;
            if (hud != null)
            {
                hud.mechName = "ZAKU II";
                hud.headVulcan = null;
                if (hud.vulcanAmmoText != null) hud.vulcanAmmoText.text = "";
            }
            if (weaponHUD != null) weaponHUD.headVulcan = null;

            // 5) Enemies change sides.
            SetActive(gundamModeEnemies, false);
            SetActive(zakuModeEnemies, true);

            // 6) Zeon colors.
            if (cockpitInterior != null) CockpitTheme.ApplyZeon(cockpitInterior);
            if (weaponPanel != null)
            {
                weaponPanel.normalColor = CockpitTheme.ToZeon(weaponPanel.normalColor);
                weaponPanel.hoverColor = CockpitTheme.ToZeon(weaponPanel.hoverColor);
            }
            if (spawnPanel != null)
            {
                spawnPanel.normalColor = CockpitTheme.ToZeon(spawnPanel.normalColor);
                spawnPanel.hoverColor = CockpitTheme.ToZeon(spawnPanel.hoverColor);
            }
            if (targetLock != null) targetLock.RefreshMaterials();

            Debug.Log("[Gundam] Pilot is now in the ZAKU II (machine gun + heat hawk + bazooka); enemies: GUNDAMs.");
        }

        static void SetActive(GameObject[] objs, bool on)
        {
            if (objs == null) return;
            foreach (GameObject g in objs) if (g != null && g.activeSelf != on) g.SetActive(on);
        }
    }
}
