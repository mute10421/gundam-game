using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Samples.VisualizerSample;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Unity.XR.CoreUtils;
using Gundam.Cockpit;

namespace Gundam.EditorTools
{
    /// <summary>
    /// One-click generator for the Gundam cockpit prototype scene.
    /// Run this AFTER the project has finished resolving the XR packages added
    /// to Packages/manifest.json (open the project once, let Package Manager
    /// finish importing, then use the menu below).
    ///
    /// Menu: Gundam > Build Cockpit Scene
    /// </summary>
    public static class GundamCockpitSetup
    {
        const string ScenePath = "Assets/Scenes/GundamCockpit.unity";

        // Imported Gundam FBX (see PlaceExternalGundam below). If this path
        // ever changes (re-import under a new name, etc.) update it here -
        // this is the single source of truth Build Cockpit Scene reads from.
        const string GundamModelPath = "Assets/Models/Gundam/Mobile Suit Gundam-52dddca0b3f82f4705b1562457f679a8.fbx";

        // Base color texture for ExternalGundam (per request - "건담 색깔
        // 다운해둠"), applied in PlaceExternalGundam below. Before this, the
        // model only had its single flat gray default material.
        const string GundamBaseColorTexturePath = "Assets/Models/Gundam/Mobile Suit Gundam-baseColor.png";

        // Enemy Zaku model + its own base color texture (per request -
        // "자쿠 모델링이랑... 자쿠는 적으로 만들거야 자쿠를 적으로 배치해줘").
        // See PlaceZakuEnemy below.
        const string ZakuModelPath = "Assets/Models/Zaku/Green Zaku Mobile Suit-aca4bc150af72115520493e85c7e2457.fbx";
        const string ZakuTexturePath = "Assets/Models/Zaku/ZakuTexture.png";

        // Shared "how tall a mobile suit should read as" convention, reused
        // for the enemy Zaku so it reads at the same scale as the player's
        // own Gundam rather than needing its own separately-tuned number.
        const float MobileSuitTargetHeight = 18f;
        // ZakuEnemy's starting Z (ExternalGundam stands at Z=20 -> 80m apart).
        const float ZakuSpawnZ = 100f;

        // An unnamed layer index used ONLY to hide ExternalGundam's own body
        // from the player's own (XR) camera - see PlaceExternalGundam (sets
        // the layer) and CreateXROrigin (excludes it from viewCamera's
        // cullingMask). Doesn't need a name in Tag Manager to work, and 30 is
        // one of the two layers Unity itself never assigns to anything by
        // default, so it's very unlikely to collide with anything else in
        // this project.
        const int GundamBodyLayer = 30;

        // The reverse of GundamBodyLayer: the whole CockpitInterior (seat, dome,
        // frame, displays, joysticks, OrbitHUD...) goes on this unnamed layer and
        // is excluded from HeadCam only - per "콕핏트가 내가 건담 안에서 보이거든
        // 밖에 있는 콕피트가 이거 안보이게해야함". The cockpit physically sits at
        // MobileSuitRoot (the origin), ~20m from ExternalGundam's head, so HeadCam
        // - which renders the exterior view on Cockpit_Dome - was filming the
        // cockpit itself as an object floating outside. The pilot's own XR camera
        // still renders this layer (its mask only drops GundamBodyLayer). The
        // joysticks' grab colliders are NOT moved (XRI hand grab only scans
        // Default) - see SetLayerRecursivelyExceptColliders.
        const int CockpitInteriorLayer = 29;

        // SpaceDustField (the endless star/dust field) lives on this unnamed layer,
        // which only the pilot's own XR camera skips - it's sealed inside the
        // opaque Cockpit_Dome, so this changes nothing it would otherwise show, but
        // dust drifting through the cockpit's position can't float INSIDE the
        // cockpit. HeadCam (the exterior view on the dome) still renders it.
        const int SpaceBackdropLayer = 28;

        // XR Hands package's official "HandVisualizer" sample (Window >
        // Package Manager > XR Hands > Samples > HandVisualizer) - imported
        // once already at this path. Drives REAL rigged/skinned hand
        // meshes from live XRHandSubsystem joint data (not a 21-joint
        // sphere debug rig, not a controller model) - see
        // BuildHandVisualizer below. "AndroidXR" variants are the ones
        // that match this project's target device (Galaxy XR); the plain
        // ones are wired in too as a harmless fallback for any other
        // runtime the same build might run on.
        const string HandVisSampleRoot = "Assets/Samples/XR Hands/1.6.3/HandVisualizer/";
        const string HandVisAndroidLeftMeshPath = HandVisSampleRoot + "Models/LeftHandAndroidXR.fbx";
        const string HandVisAndroidRightMeshPath = HandVisSampleRoot + "Models/RightHandAndroidXR.fbx";
        const string HandVisFallbackLeftMeshPath = HandVisSampleRoot + "Models/LeftHand.fbx";
        const string HandVisFallbackRightMeshPath = HandVisSampleRoot + "Models/RightHand.fbx";
        const string HandVisMaterialPath = HandVisSampleRoot + "Materials/HandsDefaultMaterial.mat";
        const string HandVisJointPrefabPath = HandVisSampleRoot + "Prefabs/Joint.prefab";
        const string HandVisVelocityPrefabPath = HandVisSampleRoot + "Prefabs/VelocityPrefab.prefab";

        [MenuItem("Gundam/Build Cockpit Scene")]
        public static void BuildCockpitScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateLighting();

            GameObject suitRoot = new GameObject("MobileSuitRoot");
            ShipMovementController ship = suitRoot.AddComponent<ShipMovementController>();

            // Shared vitals data for the 3-display Cockpit HUD (FrontDisplay's
            // status badge + LeftDisplay's gauges both read the same
            // CockpitHUDManager.Vitals instance - see that class). No
            // dependencies of its own, so it's created early and wired into
            // BuildSystemCheckDisplay/CockpitHUD below as they're built.
            CockpitHUDManager hudManager = suitRoot.AddComponent<CockpitHUDManager>();

            GameObject interior = new GameObject("CockpitInterior");
            interior.transform.SetParent(suitRoot.transform, false);

            // ---------------------------------------------------------------
            // Materials — dark mecha interior body with glowing instrument
            // panels/buttons/grips. Kept as shared materials (cheap, and easy
            // to retint later from one place).
            // ---------------------------------------------------------------
            Material hullMat = MakeMat(new Color(0.045f, 0.045f, 0.05f));           // main dark armor/frame
            Material hullAccentMat = MakeMat(new Color(0.11f, 0.11f, 0.13f));       // panel plates (dash, walls)
            Material seatMat = MakeMat(new Color(0.07f, 0.07f, 0.085f));
            Material seatAccentMat = MakeMat(new Color(0.035f, 0.035f, 0.045f));
            Material gearMat = MakeMat(new Color(0.35f, 0.35f, 0.38f));             // metallic collars/gears
            Material displayMat = MakeEmissiveMat(new Color(0.02f, 0.05f, 0.07f), new Color(0f, 0.65f, 0.8f));
            Material buttonGreenMat = MakeEmissiveMat(new Color(0.02f, 0.05f, 0.03f), new Color(0.2f, 1f, 0.35f));
            Material buttonRedMat = MakeEmissiveMat(new Color(0.05f, 0.01f, 0.01f), new Color(1f, 0.15f, 0.1f));
            Material leftAccentMat = MakeEmissiveMat(new Color(0.04f, 0.09f, 0.18f), new Color(0.15f, 0.55f, 1f));   // steering (blue)
            Material rightAccentMat = MakeEmissiveMat(new Color(0.18f, 0.03f, 0.03f), new Color(1f, 0.2f, 0.15f));   // weapon (red)
            Material targetMat = MakeEmissiveMat(new Color(0.3f, 0.05f, 0.02f), new Color(1f, 0.35f, 0.05f));
            Material starMat = MakeEmissiveMat(Color.black, Color.white);
            Material hudLineMat = MakeEmissiveMat(new Color(0.05f, 0.08f, 0.1f), new Color(0.6f, 0.85f, 1f));   // OrbitHUD ring ticks
            Material hudGateMat = MakeEmissiveMat(new Color(0.12f, 0.09f, 0.01f), new Color(1f, 0.82f, 0.15f)); // OrbitHUD gate markers

            // --- Seat (bucket seat with headrest + side bolsters, on a pedestal) ---
            BuildSeat(interior.transform, seatMat, seatAccentMat, hullMat);

            // The old physical dashboard console (gauges + button grid) has
            // also been removed - its informational role is now entirely
            // taken over by the three System Check screens below ("데시보드
            // 역할이 아까 만든 디스플레이 3개가 하는거야").

            // --- External Gundam model - placed BEFORE the enclosure/dashboard
            //     below so its head-cam RenderTexture (if the FBX is already
            //     imported) can be piped straight onto both the
            //     SystemCheckDisplay screen and the Cockpit_Dome. Standing out
            //     in space in front of the cockpit, per request ("방금 만든
            //     Gundam FBX 모델을 현재
            //     gundam game 프로젝트에 추가해줘... 콕핏 앞쪽 가상 공간에
            //     배치한다"). NOT Transform-parented under suitRoot/
            //     MobileSuitRoot (see PlaceExternalGundam's own comment on why -
            //     it still doesn't inherit the suit's rotation), but per report
            //     ("LeftJoystick을 움직여도 실제 Gundam이 움직이는 것이 화면에서
            //     보이지 않는다") it's now kept in sync with MobileSuitRoot's
            //     POSITION via ExternalGundamFollower below - HeadCam (parented
            //     deep inside this model's own Head bone) is what actually
            //     generates the Cockpit_Dome/aux-screen exterior feed, so without
            //     this the player's own movement was invisible in that feed even
            //     though MobileSuitRoot itself was moving correctly. ---
            GundamPlacementResult gundamResult = PlaceExternalGundam();
            RenderTexture gundamHeadCamTex = gundamResult != null ? gundamResult.headCamTex : null;
            RenderTexture gundamHeadCam360CubeTex = gundamResult != null ? gundamResult.headCam360CubemapTex : null;

            if (gundamResult != null && gundamResult.instance != null)
            {
                ExternalGundamFollower follower = gundamResult.instance.AddComponent<ExternalGundamFollower>();
                follower.target = suitRoot.transform;
            }

            // --- Enemy Zaku - per request ("자쿠를 적으로 배치해줘") ---
            // Passed the player's Gundam so its ZakuCombatAI knows what to face/circle.
            PlaceZakuEnemy(gundamResult != null && gundamResult.instance != null ? gundamResult.instance.transform : null);

            // --- Full 360-degree solid enclosure (floor + an inward-facing
            //     curved dome) so the real room/Skybox is never visible in
            //     any direction, even as the real XR camera height varies
            //     with headset tracking. Always kept in sync with this
            //     generator so "Build Cockpit Scene" never regresses it. ---
            // The old structural frame (side pillars/rails/braces/wall
            // panels) has been removed per request ("옆에 벽이랑 기둥은
            // 없애도 되고") - it was largely redundant now that the dome
            // encloses the space, and it was blocking the view of it.
            //
            // Moved to run AFTER PlaceExternalGundam (was before it) so the
            // live head-cam RenderTexture it creates already exists here and
            // can be painted directly onto the dome - see
            // BuildCockpitEnclosure for why, per request ("콕피트에서 밖에가
            // 보이면안됨 콕피트에 외관은 전부 디스플레이어야해").
            BuildCockpitEnclosure(interior.transform, hullAccentMat, gundamHeadCamTex, gundamHeadCam360CubeTex);

            // --- Front display: "SYSTEM CHECK" style dashboard - one large
            //     center screen (radial tick dial + pilot/weapon/ammo
            //     readout) flanked by two smaller aux screens, replacing the
            //     old plain curved windshield + corner HUD per request. The
            //     left aux screen now shows a live camera feed from the
            //     Gundam's head (see gundamHeadCamTex above), per request
            //     ("건담에 머리에 시선이 내 콕핏 화면에 나와야해"). ---
            BuildSystemCheckDisplay(interior.transform, displayMat, hullMat, gundamHeadCamTex, hudManager,
                out Text telemetryText, out Text speedText, out Text statusText, out Text vulcanAmmoText,
                out CockpitStatusHUD statusHUD, out CockpitWeaponHUD weaponHUD);

            // --- Orbit/HUD ring: a big floating compass-style reticle overlaying
            //     the live head-cam view now shown on the dome itself (per request,
            //     with a reference photo of an anime cockpit HUD ring + trajectory
            //     "gate" markers: "콕핏에 외관에 구형이 있어서 그 구형에 밖에가 보이면서
            //     저런 표식으로 궤도를 알려주는 시스템이 있어야해"). ---
            Transform orbitHud = BuildOrbitHUD(interior.transform, hudLineMat, hudGateMat);

            // --- Hand trackers (placed under the XR Origin once it is created below) ---
            HandJointTracker leftHand = CreateHandTracker("LeftHandTracker", Handedness.Left);
            HandJointTracker rightHand = CreateHandTracker("RightHandTracker", Handedness.Right);

            // --- Joysticks: visually distinct left (steer/blue) vs right (weapon/red) ---
            // Each stick only accepts ITS OWN hand tracker (the other hand's
            // slot is passed as null) - LeftJoystick can only be grabbed by
            // the left hand, RightJoystick only by the right hand. Per
            // request: "LeftJoystick: 왼손으로 잡음... RightJoystick: 오른손으로
            // 잡음". RightJoystick previously also accepted the left hand
            // tracker (a leftover from before this hand-exclusivity rule
            // existed) - fixed here so grabbing is exclusive per stick.
            //
            // Position moved from the seat armrests up into the SystemCheckDisplay
            // cluster's own area - per request ("조종장치에 위치가 디스플레이
            // 3개 사이에 그쯤에 있어야할거같아"). The 3 screens sit at:
            // center (0, 0.92, 0.4), left (-0.3536, 0.92, 0.2713), right
            // (0.3536, 0.92, 0.2713) - see BuildSystemCheckDisplay/
            // CreatePolarScreen. (+-0.22, 0.82, 0.18) sits roughly in the
            // gap among all three (closer to center than the old armrest
            // spot, slightly below/in front of the display height/depth)
            // rather than tucked under the seat's side bolsters.
            //
            // Note: this is noticeably closer to the display panels than the
            // old armrest position was (which was deliberately kept ~0.35 -
            // its "grab+frame radius" - away from the aux screens to avoid
            // visual overlap). I can't check in the Editor whether the grip
            // now visually clips into a display frame from this angle - if
            // it looks like it's poking into a screen when you check, tell
            // me and I'll pull it back a bit.
            //
            // Per report ("조종기를 조금 위쪽으로 올린다"): Y raised a modest
            // 0.05m (0.82 -> 0.87), X/Z left exactly as they were. ---
            JoystickLever leftStick = CreateJoystick("LeftJoystick", interior.transform,
                new Vector3(-0.22f, 0.87f, 0.18f), false, leftAccentMat, buttonGreenMat,
                leftHand, null);
            JoystickLever rightStick = CreateJoystick("RightJoystick", interior.transform,
                new Vector3(0.22f, 0.87f, 0.18f), true, rightAccentMat, buttonRedMat,
                null, rightHand);

            ship.leftStick = leftStick;

            // --- Separate T-shaped VERTICAL lever (rise/descend), left hand,
            //     at seated shoulder height - per "새로운 수직 이동 T자 레버" /
            //     "어깨 높이를 기준으로 배치". See CreateVerticalTLever and
            //     VerticalTLever.cs. LeftJoystick/RightJoystick are untouched. ---
            VerticalTLever verticalLever = CreateVerticalTLever(interior.transform, leftHand, leftStick, leftAccentMat, buttonGreenMat);

            // Lever -> actual rise/descend (per "레버를 앞으로 올리면 건담이 위로
            // 날아야 하는데"): a separate component on the same MobileSuitRoot
            // that only adds vertical motion - ShipMovementController untouched.
            VerticalThrustController verticalThrust = suitRoot.AddComponent<VerticalThrustController>();
            verticalThrust.lever = verticalLever;
            verticalThrust.maxClimbSpeed = ship.maxMoveSpeed; // same top speed as horizontal

            // --- Gun turret (mounted on the suit, aimed by the right stick) ---
            // Per report ("눈앞에있는 막대기로 만들어친 총열같이 생긴놈을
            // 지우고"): the turret's own visible meshes (GunHousing/Barrel -
            // two plain cylinders sitting 2.4m in front of the suit root,
            // right in the pilot's forward view) were confirmed as this
            // "stick-shaped barrel" and removed below. GunPivot/MuzzlePoint
            // stay as plain empty Transforms and WeaponAimFireController stays
            // fully wired exactly as before - per the same report, its own
            // aim/fire feature is NOT being removed, only the mesh that was
            // visually poking into view. (hullMat/gearMat, only ever used to
            // color those two removed meshes, are now unused by this block -
            // left as-is, still valid Material params used elsewhere.)
            GameObject turretRoot = new GameObject("GunTurret");
            turretRoot.transform.SetParent(suitRoot.transform, false);
            turretRoot.transform.localPosition = new Vector3(0, 1.0f, 2.4f);

            GameObject gunPivot = new GameObject("GunPivot");
            gunPivot.transform.SetParent(turretRoot.transform, false);

            GameObject muzzle = new GameObject("MuzzlePoint");
            muzzle.transform.SetParent(gunPivot.transform, false);
            muzzle.transform.localPosition = new Vector3(0, 0, 0.95f);

            WeaponAimFireController weapon = suitRoot.AddComponent<WeaponAimFireController>();
            weapon.rightStick = rightStick;
            weapon.gunPivot = gunPivot.transform;
            weapon.muzzlePoint = muzzle.transform;

            CockpitHUD hud = suitRoot.AddComponent<CockpitHUD>();
            hud.leftStick = leftStick;
            hud.rightStick = rightStick;
            hud.infoText = telemetryText;
            // Per the 3-display Cockpit HUD request, the weapon readout moved
            // to RightDisplay's CockpitWeaponHUD (driven by HeadVulcanController,
            // wired further below once that component exists) - the old
            // turret's weapon/weaponNameText/ammoText fields are left unset
            // (CockpitHUD.Update() already no-ops when they're null), not
            // deleted, so WeaponAimFireController itself is completely
            // untouched and still fully functional if ever wired up again.
            hud.ship = ship;
            hud.vertical = verticalThrust;
            hud.hudManager = hudManager;
            hud.speedText = speedText;
            hud.statusText = statusText;
            // Per report ("내 앞에 디스플레이에 표시가 안됨"): duplicates the
            // Head Vulcan ammo readout directly on FrontDisplay too (headVulcan
            // itself is wired further below, once it exists) - RightDisplay's
            // CockpitWeaponHUD keeps working exactly as before, untouched.
            hud.vulcanAmmoText = vulcanAmmoText;

            // --- XR Origin (camera + hand tracking space) ---
            // Y is 0, not a seat-height offset: the Starter Assets XR Origin's
            // Tracking Origin Mode is "Not Specified", which real OpenXR
            // runtimes resolve to Floor tracking - meaning the runtime camera
            // Y is ALREADY the player's real head height above their real
            // floor. Adding an extra authored offset here would stack on top
            // of that and put the camera far above the seat (this previously
            // caused a "camera too high" complaint). With Y=0, the cockpit's
            // local floor (y=0) lines up with the player's real floor, and
            // Floor tracking naturally places their eyes near the seat's
            // headrest height when they're actually sitting.
            GameObject xrOrigin = CreateXROrigin(suitRoot.transform, new Vector3(0, 0f, -0.15f));
            leftHand.transform.SetParent(xrOrigin.transform, false);
            rightHand.transform.SetParent(xrOrigin.transform, false);

            // --- Real XR hand visuals (Galaxy XR hand tracking -> visible
            //     rigged hand meshes, no controller model, no debug joint
            //     spheres) - per request. See BuildHandVisualizer below. ---
            BuildHandVisualizer(xrOrigin, leftHand, rightHand);

            // --- Real XR Interaction Toolkit hand grab, wired onto the joysticks
            //     now that xrOrigin (and its own "Left Hand"/"Right Hand" Near-Far
            //     Interactors, from the imported "Hands Interaction Demo" sample)
            //     actually exists - per request ("XR Interaction Toolkit의 실제
            //     Hand Interactor/Direct Interactor를 사용... Grab 판정이 제대로
            //     되도록"). Has to happen here, not inside CreateJoystick above,
            //     for the same reason the head-turn wiring above does: the rig
            //     doesn't exist yet when the joysticks are built. See
            //     AttachHandInteractable below for what this actually adds. ---
            WireJoystickHandInteractors(xrOrigin, leftStick, rightStick);
            WireVerticalLeverInteractor(xrOrigin, verticalLever);

            // Wire the pilot's own view camera into the Gundam's head-turn
            // tracking, now that CreateXROrigin has actually created it - per
            // request ("내가 머리를 돌리면 건담 머리도 돌아야해"). Has to
            // happen here (not inside PlaceExternalGundam, which runs earlier)
            // because the XR rig/camera doesn't exist yet at that point.
            if (gundamResult != null && gundamResult.headCam360 != null)
            {
                Camera playerViewCamera = xrOrigin.GetComponentInChildren<Camera>(true);
                if (playerViewCamera != null)
                {
                    gundamResult.headCam360.playerCamera = playerViewCamera.transform;
                }
                else
                {
                    Debug.LogWarning("[Gundam] Could not find the XR rig's camera to drive the Gundam's head-turn tracking.");
                }

                // --- RightJoystick -> external view (per request: "오른손으로
                //     RightJoystick을 잡고 움직이면 '건담 콕핏에서 바라보는 외부
                //     화면'이 움직여야 한다"). See CockpitViewController.cs.
                //
                //     The previous attempt attached HeadCamManualLook to HeadCam,
                //     which only rotated HeadCam - that turns FrontDisplay's flat
                //     inset, but Cockpit_Dome samples a cubemap made by
                //     Camera.RenderToCubemap, which always renders world-axis-
                //     aligned faces and IGNORES the camera's rotation, so the
                //     dome (the main outside view) never moved. It is no longer
                //     attached (the file is left in place, unused) - using both
                //     would double-rotate HeadCam.
                //
                //     CockpitViewController rotates HeadCam's own localRotation
                //     AND passes the same rotation to Cockpit_Dome's shader
                //     (_ViewRotQ), so both outputs turn together. It lives on its
                //     own GameObject so it's easy to find in the Hierarchy; it
                //     never writes to its own transform, MobileSuitRoot, the XR
                //     Origin or Main Camera. WeaponAimFireController still reads
                //     the same RightJoystick, unchanged. ---
                GameObject viewCtrlGo = new GameObject("CockpitViewController");
                viewCtrlGo.transform.SetParent(suitRoot.transform, false);
                CockpitViewController viewController = viewCtrlGo.AddComponent<CockpitViewController>();
                viewController.rightJoystick = rightStick;
                viewController.viewCamera = gundamResult.headCam360.headCam;

                // Per report ("움직이는거를 콕핏에서의 시선을 기준으로 해야할거
                // 같아"): the left stick's movement now follows the view's
                // yaw - see ShipMovementController.Update().
                ship.viewController = viewController;

                Transform domeTransform = interior.transform.Find("Cockpit_Dome");
                viewController.domeRenderer = domeTransform != null ? domeTransform.GetComponent<Renderer>() : null;

                // Per request ("OrbitHUD_Tick_3 이거 만들어 둔게 적을 포착하면 적
                // 사이즈로 모여서 줄어들어야해 원형으로 적을 타겟하는거야"): the
                // OrbitHUD ring becomes a lock-on reticle - see
                // OrbitHUDTargetLock.cs. Wired here because it needs the pilot's
                // camera, the view controller and the dome, which all exist by now.
                if (orbitHud != null)
                {
                    OrbitHUDTargetLock ringLock = orbitHud.gameObject.AddComponent<OrbitHUDTargetLock>();
                    ringLock.pilotCamera = playerViewCamera;
                    ringLock.viewController = viewController;
                    ringLock.dome = domeTransform;
                    ringLock.ringRadius = 1.5f; // BuildOrbitHUD's ringRadius
                    ringLock.lockedMaterial = MakeEmissiveMat(new Color(0.15f, 0.02f, 0.02f), new Color(1f, 0.2f, 0.15f));
                }

                Debug.Log("[Gundam] CockpitViewController wired: RightJoystick -> '" +
                    (viewController.viewCamera != null ? viewController.viewCamera.name : "null") + "' camera + " +
                    (viewController.domeRenderer != null ? "Cockpit_Dome" : "NO dome renderer found") + ".");
                if (viewController.domeRenderer == null)
                {
                    Debug.LogWarning("[Gundam] CockpitViewController: could not find 'Cockpit_Dome' under CockpitInterior - only the FrontDisplay HEAD CAM inset will turn.");
                }
            }

            // --- Head Vulcan: aims wherever the pilot is actually looking
            //     (HMD/Main Camera forward), fires on a right-thumb bend - per
            //     request ("오른손은 조준 장치를 잡아서 조준하는 것이 아니라
            //     '내가 바라보는 방향'으로 건담 머리의 헤드발칸을 조준하고,
            //     오른손 엄지를 구부리는 동작으로 발사한다"). Fully independent
            //     of RightJoystick/WeaponAimFireController (that system drives
            //     the separate GunTurret built above, not the Gundam's own
            //     head - confirmed by reading its own wiring just above, so
            //     there is nothing to disconnect there).
            //
            //     Muzzles are two new Transforms placed just in front of the
            //     SAME "Head" bone PlaceExternalGundam already found for the
            //     head-cam (re-located here the same way, via FindDeepChild -
            //     PlaceExternalGundam doesn't return it directly), using the
            //     exact same world-position-then-SetParent(head, true)
            //     technique already used for HeadCam above (avoids having to
            //     guess the head bone's own local scale). Their own facing
            //     direction is irrelevant to aiming - HeadVulcanController
            //     always fires along mainCamera.transform.forward, never the
            //     muzzle's forward - so only their position matters here. ---
            if (gundamResult != null && gundamResult.instance != null)
            {
                Transform vulcanHead = FindDeepChild(gundamResult.instance.transform, "Head");
                if (vulcanHead != null)
                {
                    SkinnedMeshRenderer vulcanMeshRenderer = gundamResult.instance.GetComponentInChildren<SkinnedMeshRenderer>();
                    Bounds vulcanBounds = vulcanMeshRenderer != null ? vulcanMeshRenderer.bounds : new Bounds(vulcanHead.position, Vector3.one);

                    // Same 92%-of-height eye estimate PlaceExternalGundam's own
                    // HeadCam placement uses, so both sit at a consistent,
                    // already-reasoned-through height on the model.
                    float vulcanEyeHeight = vulcanBounds.min.y + vulcanBounds.size.y * 0.92f;
                    float vulcanForwardClearance = vulcanBounds.size.y * 0.05f;
                    float vulcanSideOffset = vulcanBounds.size.x * 0.28f;

                    GameObject muzzleL = new GameObject("HeadVulcanMuzzle_L");
                    muzzleL.transform.position = new Vector3(vulcanBounds.center.x - vulcanSideOffset, vulcanEyeHeight, vulcanBounds.center.z + vulcanForwardClearance);
                    muzzleL.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                    muzzleL.transform.SetParent(vulcanHead, true);

                    GameObject muzzleR = new GameObject("HeadVulcanMuzzle_R");
                    muzzleR.transform.position = new Vector3(vulcanBounds.center.x + vulcanSideOffset, vulcanEyeHeight, vulcanBounds.center.z + vulcanForwardClearance);
                    muzzleR.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                    muzzleR.transform.SetParent(vulcanHead, true);

                    Camera vulcanPlayerCamera = xrOrigin.GetComponentInChildren<Camera>(true);

                    HeadVulcanController headVulcan = suitRoot.AddComponent<HeadVulcanController>();
                    headVulcan.mainCamera = vulcanPlayerCamera;
                    headVulcan.muzzleLeft = muzzleL.transform;
                    headVulcan.muzzleRight = muzzleR.transform;
                    // Per report ("해드발칸이 발사가 되는지 안보임"): bright
                    // emissive tracer material for the placeholder bullet -
                    // see HeadVulcanController.SpawnBullet's own comment on
                    // why the old default-material 0.04-scale sphere was
                    // nearly invisible at the muzzle's ~20m distance from the
                    // pilot's own camera.
                    headVulcan.bulletMat = MakeEmissiveMat(new Color(0.2f, 0.08f, 0.01f), new Color(1f, 0.55f, 0.15f));

                    // Same report: duplicates the ammo readout onto
                    // FrontDisplay (hud.vulcanAmmoText was already wired
                    // earlier, but headVulcan itself doesn't exist until now).
                    hud.headVulcan = headVulcan;

                    // Per report ("총알을 발사하면 내가 볼수있어야함"): aim at
                    // what the pilot actually sees on the RightJoystick-turnable
                    // Cockpit_Dome view, with both muzzles converging on it -
                    // see HeadVulcanController.Fire(). CockpitViewController
                    // was created earlier (its own GameObject under suitRoot).
                    headVulcan.viewController = suitRoot.GetComponentInChildren<CockpitViewController>(true);

                    // Per request ("엄지 부분에 버튼이 눌렸을때 발칸이 나가게하자"):
                    // the RightJoystick's thumb button is the fire trigger now.
                    headVulcan.fireButtons = rightStick.GetComponent<JoystickFingerButtons>();
                    if (headVulcan.viewController == null)
                    {
                        Debug.LogWarning("[Gundam] Head Vulcan: no CockpitViewController found - shots fall back to plain mainCamera.forward (won't follow the RightJoystick view).");
                    }

                    // Reuses the SAME XRHandTrackingEvents component already
                    // sitting on RightHandTracker (created in CreateHandTracker
                    // above) - adds a second listener to it, does not create a
                    // new hand-tracking system and never touches Left hand data.
                    XRHandTrackingEvents rightHandEvents = rightHand.GetComponent<XRHandTrackingEvents>();
                    headVulcan.SubscribeToRightHand(rightHandEvents);

                    // RightDisplay's WEAPON screen (see BuildWeaponScreenUI,
                    // called from BuildSystemCheckDisplay earlier) reads Head
                    // Vulcan's ammo/state - wired here since headVulcan itself
                    // doesn't exist until this point in the method.
                    if (weaponHUD != null) weaponHUD.headVulcan = headVulcan;

                    if (vulcanPlayerCamera == null)
                    {
                        Debug.LogWarning("[Gundam] Head Vulcan: could not find the XR rig's camera - firing will do nothing until mainCamera is assigned.");
                    }
                    if (rightHandEvents == null)
                    {
                        Debug.LogWarning("[Gundam] Head Vulcan: RightHandTracker has no XRHandTrackingEvents component - thumb-bend firing will never trigger.");
                    }
                }
                else
                {
                    Debug.LogWarning("[Gundam] Could not find a 'Head' bone under ExternalGundam - skipping Head Vulcan muzzle placement.");
                }
            }

            // --- Cockpit Calibration: lets each player set their own comfortable
            //     joystick reach before combat starts (per request - "사람마다
            //     팔 길이와 앉는 위치가 다르기 때문에... 자신의 몸에 맞게 왼쪽/
            //     오른쪽 조종간 위치를 설정할 수 있어야 한다"). Repositions each
            //     joystick's whole Mount (leftStick.transform/rightStick.transform
            //     - never JoystickLever's own internal pivot/handle/grab math,
            //     which is left completely untouched) so it physically appears
            //     wherever the player's hand naturally rests. Runs fresh every
            //     app start (no PlayerPrefs persistence - see CalibrationManager.cs
            //     for why), and per the latest request ("확인을 누르면 게임이
            //     시작되는거고") ends with an explicit CONFIRM button press rather
            //     than auto-completing the instant the right joystick is released.
            //     See CalibrationManager.cs for the full flow - this is just the
            //     wiring. Left/Right hand exclusivity mirrors JoystickLever's own
            //     (leftHand only ever drives leftStick's calibration, rightHand
            //     only ever drives rightStick's). ---
            CalibrationManager calibManager = suitRoot.AddComponent<CalibrationManager>();
            calibManager.leftStick = leftStick;
            calibManager.rightStick = rightStick;
            calibManager.leftHandTracker = leftHand;
            calibManager.rightHandTracker = rightHand;

            // Per report ("LeftJoystick/RightJoystick 조종간 자체가 화면에 보이지
            // 않는다... CalibrationManager를 추가한 이후부터 발생") - the prompt
            // panel used to be parented to the XR rig's camera (head-locked), which
            // is what caused the joysticks to visually disappear. It's now mounted
            // to the cockpit interior instead, so no camera lookup is needed here.
            //
            // Per follow-up report ("새로 이상한 디스플레이 만든거 지우고") - the
            // very next version of this panel added a physical CreatePolarScreen
            // frame+screen prop (like another SystemCheckDisplay panel), which read
            // as an out-of-place extra console screen. BuildCalibrationPromptUI is
            // now just floating text with no physical prop/frame at all - see its
            // doc comment.
            calibManager.promptText = BuildCalibrationPromptUI(interior.transform);

            // Per request ("확인을 누르면 게임이 시작되는거고") - a physical
            // "press to confirm" button, shown only after both joysticks have
            // been placed (see CalibrationManager's WaitingForConfirm state).
            // Same grip-ball visual language as the joysticks' own GripBall.
            calibManager.confirmButton = BuildCalibrationConfirmButton(interior.transform, buttonGreenMat);

            // --- Targets to shoot at — pushed out to the sides/above-below so they
            //     sit in the open space around the front display, not stacked
            //     directly behind it. Kept in world space, not parented to the suit. ---
            GameObject targetsRoot = new GameObject("Targets");
            System.Random rnd = new System.Random(1234);
            for (int i = 0; i < 6; i++)
            {
                float side = (i % 2 == 0) ? -1f : 1f;
                float x = side * (float)(3.5 + rnd.NextDouble() * 9.0);   // |x| in [3.5, 12.5] — outside the display's cone
                float y = (float)(rnd.NextDouble() * 6.0 - 1.0);          // spread above/below eye height too
                float z = 12f + i * 6f;
                GameObject t = CreateCube($"Target_{i}", targetsRoot.transform, new Vector3(x, y, z), Vector3.one * 0.8f, targetMat);
                t.AddComponent<HitTarget>();
            }

            // --- Simple starfield for a sense of motion ---
            GameObject starsRoot = new GameObject("Starfield");
            for (int i = 0; i < 60; i++)
            {
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                Vector3 pos = dir * UnityEngine.Random.Range(25f, 70f);
                GameObject star = CreateCube($"Star_{i}", starsRoot.transform, pos, Vector3.one * 0.25f, starMat);
                Collider col = star.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);
            }

            // --- Endless star/space-dust field around HeadCam - per "배경에 별들이
            //     더 있어야할거같아 움직이는게 안느겨져서" (see SpaceDustField). The
            //     Starfield above is kept; this adds near parallax dust that streams
            //     past as the suit moves, plus a dense far star sky. ---
            GameObject dustRoot = new GameObject("SpaceDust");
            dustRoot.layer = SpaceBackdropLayer;
            SpaceDustField dust = dustRoot.AddComponent<SpaceDustField>();
            dust.material = starMat;
            if (gundamResult != null && gundamResult.headCam360 != null && gundamResult.headCam360.headCam != null)
            {
                dust.viewer = gundamResult.headCam360.headCam.transform;
            }

            // --- Distant space wreckage (VARCO 3D models in Assets/Models/Debris) -
            //     per "우주에 건물을 넣을려고 하는데 우주에 잔해같은거 거슬리지 않게"
            //     (see PlaceSpaceDebris / SpaceDebrisField). ---
            PlaceSpaceDebris(dust.viewer);

            // --- BEAM SABER mode (WEAPON display touch -> right hand leaves
            //     RightJoystick -> hand-held control stick drives the real Gundam
            //     right arm + saber). See SetupBeamSaberMode. ---
            SetupBeamSaberMode(suitRoot, interior, gundamResult, rightStick, leftHand, rightHand, xrOrigin);

            // --- Shrink the 3-display cluster to half size and lift it above the
            //     hands on the sticks - per "앞에 디스플레이를 더 작게 ... 조종기랑
            //     너무 겹쳐서 누를수가 없어" (option: 절반 크기 + 조종기 위로).
            //     Done last so everything built on/under the displays (HUD texts,
            //     head-cam inset, WEAPON touch buttons) scales with them. ---
            ShrinkSystemCheckDisplay(interior.transform);

            // Hide the cockpit from HeadCam's exterior view (see CockpitInteriorLayer).
            // Done last so every object built under CockpitInterior above is included.
            // Only CockpitInterior - the XR Origin (hands etc.) keeps its own layers.
            // FIX (per "갑자기 조종관이 안잡힘"): the joysticks' grab colliders
            // (grip balls, finger buttons, trigger - colliders under an XR
            // interactable) stay on Default - the hands' XRI Near-Far interactor only looks for
            // grab colliders on the Default layer (its near caster's physics mask
            // is Default only), so moving them to CockpitInteriorLayer made the
            // sticks ungrabbable. The XR rig's own settings are left untouched.
            SetLayerRecursivelyExceptColliders(interior, CockpitInteriorLayer);

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);

            Selection.activeGameObject = suitRoot;
            Debug.Log("[Gundam] Cockpit prototype scene (visual overhaul) built and saved at " + ScenePath +
                       ". Open Project Settings > XR Plug-in Management to enable OpenXR + Hand Tracking if you haven't yet.");
        }

        // Where the head-cam feed is saved as a real asset (needs to be a
        // persisted .renderTexture asset, not a plain "new RenderTexture(...)"
        // - a runtime-only RenderTexture doesn't survive a scene save/reload,
        // it would show up as a missing reference next time the project is
        // opened). Re-run-safe: GetOrCreateHeadCamRenderTexture reuses this
        // asset if Build Cockpit Scene is run again instead of recreating it.
        const string HeadCamRenderTexturePath = "Assets/Models/Gundam/GundamHeadCam.renderTexture";

        // Was 512 - per request ("건담시야로 보는 밖이 화질이 깨짐"): 512x512
        // looked acceptable on the small aux screen it was originally built
        // for, but the same texture is now ALSO stretched across the whole
        // Cockpit_Dome (see BuildCockpitEnclosure), a much bigger surface -
        // at that scale 512x512 reads as blocky/blurry up close. 1024 is a
        // reasonable middle ground for a mobile XR headset (this camera
        // renders every frame, so resolution isn't free) - tell me if it's
        // still not sharp enough (I can go higher) or if it costs too much
        // frame rate (I'll pull it back down).
        const int HeadCamResolution = 1024;

        // Persisted cubemap asset for the dome's 360-degree live feed - see
        // GetOrCreateHeadCam360CubemapRenderTexture, MakeCubemapScreenMat, and
        // GundamHeadCam360.cs's "cubemap" field. Needs to be a real saved asset
        // for the same reason HeadCamRenderTexturePath above is (survives scene
        // save/reload without going missing), and separate from the flat
        // headCamTex asset since this one is a Cube-dimension RenderTexture, not
        // a plain 2D one.
        const string HeadCam360CubemapRenderTexturePath = "Assets/Models/Gundam/GundamHeadCam360.renderTexture";

        // Per-face resolution. Was implicitly 256 (GundamHeadCam360's old
        // transient-only default) back when this cubemap only ever fed the
        // Skybox, which the solid Cockpit_Dome always fully occludes anyway (so
        // it was never actually visible). Now that Cockpit_Dome itself samples
        // this cubemap directly (see MakeCubemapScreenMat), it's worth a bit
        // more resolution - 512 is a reasonable middle ground for a mobile XR
        // headset (6 faces re-rendered every frame isn't free); tell me if it
        // still isn't sharp enough or if it costs too much frame rate.
        const int HeadCam360CubemapResolution = 512;

        // ---------------------------------------------------------------
        // External Gundam model — instantiates the imported FBX (see
        // GundamModelPath above) and stands it in world space out in front
        // of the cockpit, for an initial "does it show up correctly" test
        // placement. Per request, this only gets the model displaying
        // normally for now — no animation, AI, or interaction logic yet.
        //
        // The FBX already has a proper 22-bone biped skeleton + single skinned
        // mesh (Root -> Hips -> Spine/Spine1/Spine2 -> {Neck->Head,
        // LeftShoulder/RightShoulder chains, arms/hands}, plus separate
        // LeftUpLeg/RightUpLeg leg chains from Hips) - per request ("FBX
        // 내부에 실제 본/메시 구조가 이미 있다면 임의로 다시 만들지 말고 기존
        // 구조를 최대한 유지한다") that internal hierarchy is left completely
        // untouched; this just renames/positions the single instantiated
        // root object rather than rebuilding any Body/Head/Arms/Legs
        // grouping around it.
        //
        // Also attaches a camera to the rig's own "Head" bone and feeds it to
        // a RenderTexture, so the Gundam's point of view can be shown on one
        // of the cockpit's screens - per request ("건담에 머리에 시선이 내
        // 콕핏 화면에 나와야해"). Returns that RenderTexture (or null if the
        // FBX isn't imported yet, or no "Head" bone was found) so the caller
        // can wire it into BuildSystemCheckDisplay.
        // ---------------------------------------------------------------
        /// <summary>Everything BuildCockpitScene needs back from PlaceExternalGundam:
        /// the aux-screen RenderTexture, and (once CreateXROrigin has created the XR
        /// rig further down) the GundamHeadCam360 component to wire the player's
        /// camera into for head-tracking. A plain class (not a struct) so it can be
        /// returned as null when the model/Head bone weren't found.</summary>
        class GundamPlacementResult
        {
            public RenderTexture headCamTex;
            public GundamHeadCam360 headCam360;
            // Added for the Cockpit_Dome 360-cubemap fix (see MakeCubemapScreenMat) -
            // the SAME persisted cubemap asset GundamHeadCam360 renders into every
            // frame, so BuildCockpitEnclosure can sample it directly on the dome
            // instead of the old flat 2D headCamTex stretched over the whole sphere.
            public RenderTexture headCam360CubemapTex;
            // Added per report ("LeftJoystick을 움직여도 실제 Gundam이 움직이는
            // 것이 화면에서 보이지 않는다"): the root GameObject ("ExternalGundam")
            // that HeadCam (the camera actually feeding the dome/aux-screen exterior
            // view) is parented under - see PlaceExternalGundam's own comment on why
            // it was originally kept fixed in world space. BuildCockpitScene uses
            // this to attach ExternalGundamFollower once suitRoot exists, so that
            // camera moves together with the player instead of staying frozen.
            public GameObject instance;
        }

        static GundamPlacementResult PlaceExternalGundam()
        {
            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(GundamModelPath);
            if (fbxAsset == null)
            {
                // Expected the FIRST time this runs right after the FBX file is
                // copied in: Unity only turns a raw file on disk into a loadable
                // asset once its own import pipeline has run (Assets > Refresh,
                // or reopening the project). Doesn't fail the rest of the scene
                // build - just skips the model (and the head-cam feed) and logs why.
                Debug.LogWarning("[Gundam] Could not load the Gundam FBX at '" + GundamModelPath +
                    "'. If you just added this file, do Assets > Refresh (or reopen the project) " +
                    "so Unity imports it, then re-run Gundam > Build Cockpit Scene.");
                return null;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
            instance.name = "ExternalGundam";
            instance.transform.SetParent(null); // explicit: world space, not under MobileSuitRoot

            // Raw FBX bounding box (via assimp info, since I can't open the
            // Unity Editor myself to read the actual post-import numbers):
            // min (-0.222, -0.156, 0.000), max (0.222, 0.156, 0.985) - Z is by
            // far the tallest axis (0.985) with min Z exactly 0, so this reads
            // as a Z-up export with the pivot at the feet. Unity's FBX importer
            // converts to Y-up on import using the file's own declared up-axis,
            // so this SHOULD come in standing upright already with feet at
            // local Y=0 - but I can't visually confirm that without you
            // checking in the Editor. If it's lying down or rotated, tell me
            // and I'll add a corrective rotation here.
            //
            // Scale: target a canonical mecha height of ~18m (RX-78-2-scale
            // Gundam) from that raw 0.985-unit height.
            //
            // FIX per report ("건담이랑 자쿠에 사이즈가 같아야해 건담이 너무
            // 큰거같아"): that hardcoded raw height was wrong for how Unity
            // actually imports this FBX - measured in the built scene, the
            // Gundam came out 21.0 m tall while ZakuEnemy (scaled from its own
            // measured bounds) is exactly 18.0 m. Now the Gundam is scaled the
            // SAME way as the Zaku: from its own rendered bounds at scale 1, to
            // the shared MobileSuitTargetHeight - so both always match. The old
            // raw-height value is only a fallback if the mesh can't be measured.
            const float rawHeight = 0.985469f;
            float targetHeight = MobileSuitTargetHeight;
            float scale = targetHeight / rawHeight;

            // Standing distance: clearly outside the Cockpit_Dome (its front
            // edge sits around local Z ~= 3.6-3.9 - see BuildCockpitEnclosure/
            // BuildOrbitHUD) and well short of the existing Targets (Z 12-42),
            // so it's an unmistakable, easy-to-spot test placement straight
            // ahead of the pilot without overlapping the cockpit or camera.
            instance.transform.position = new Vector3(0f, 0f, 20f);
            instance.transform.rotation = Quaternion.identity;
            //
            // CORRECTION per follow-up ("지금 크기가 머리하나는 차이 나는데?"):
            // the renderer-bounds approach above was itself the bug. A
            // SkinnedMeshRenderer's bounds are a loose, padded box - checked in
            // the Editor, at scale 1 the Gundam's box is 1.150 tall and the
            // Zaku's 1.253, while their ACTUAL vertices span 0.9855 and 0.9804.
            // The padding differs per model, so "18 m by bounds" left the
            // Gundam's real body at 15.4 m and the Zaku's at 14.1 m. Both are
            // now scaled by their real vertex height (MeasureMeshHeight) to
            // MobileSuitTargetHeight, so both bodies really are 18 m.
            instance.transform.localScale = Vector3.one;
            float measuredHeight = MeasureMeshHeight(instance);
            if (measuredHeight > 0.0001f)
            {
                scale = targetHeight / measuredHeight;
            }
            else
            {
                Debug.LogWarning("[Gundam] Could not measure ExternalGundam's mesh - falling back to the raw-height scale.");
            }
            instance.transform.localScale = Vector3.one * scale;

            // Hide this body from the pilot's own DIRECT first-person view -
            // see GundamBodyLayer above and CreateXROrigin below (excludes
            // this layer from the main XR view camera's cullingMask), so
            // ExternalGundam (a separate floating object, not part of the
            // player's own suit) never appears to the pilot as some other
            // robot floating in front of them.
            //
            // The head-cam below now DELIBERATELY does NOT exclude this layer
            // (see its cullingMask comment) - per the latest request ("건담에
            // 머리는 안보이는데 몸통은 보였으면 좋겠어"), the pilot wants to
            // see their own torso/shoulders in that self-view feed, just not
            // the head.
            SetLayerRecursively(instance, GundamBodyLayer);

            // Base color texture (per request - "건담 색깔 다운해둠") - was
            // previously just a flat gray default material since no texture
            // had been provided yet.
            Texture2D gundamBaseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(GundamBaseColorTexturePath);
            ApplyBaseColorTexture(instance, gundamBaseColor, "ExternalGundam");

            Debug.Log("[Gundam] Placed ExternalGundam at " + instance.transform.position +
                " with scale " + scale.ToString("0.00") + "x (targeting ~" + targetHeight + "m tall). " +
                "Single skinned mesh, 22-bone biped rig" +
                (gundamBaseColor != null ? ", base color texture applied." : ", no texture found (still flat gray)."));

            // --- Head camera: find the rig's own "Head" bone (already part of
            //     its real skeleton - not something invented here) and attach
            //     a small camera to it, feeding a RenderTexture that
            //     BuildSystemCheckDisplay puts on one of the cockpit screens. ---
            Transform head = FindDeepChild(instance.transform, "Head");
            if (head == null)
            {
                Debug.LogWarning("[Gundam] Could not find a 'Head' bone under ExternalGundam - skipping the " +
                    "head-cam feed. (Expected 'Root/Hips/Spine/Spine1/Spine2/Neck/Head' per the rig's own hierarchy.)");
                return null;
            }

            RenderTexture headCamTex = GetOrCreateHeadCamRenderTexture();

            // Positioned from the mesh's ACTUAL rendered bounds, not the Head
            // bone's raw local pivot. Fix for a reported bug: placing the
            // camera exactly at the bone's local origin produced a hazy,
            // uniform gray feed (screenshot showed the OrbitHUD ring against
            // flat fog instead of clean stars) - the bone's pivot sits INSIDE
            // the head geometry (common for auto-rigged bones, which are
            // often placed at a body part's center rather than its surface),
            // so the camera was rendering the model's own inner surface (the
            // single flat gray "DefaultMaterial") at point-blank range.
            //
            // Follow-up request: that "above the head" placement looked too
            // high (like a drone shot, not a pilot's eyes), so this now aims
            // for roughly eye level instead. Eye level sits INSIDE the head's
            // vertical range, so simply lowering Y to a proportional height
            // (while keeping X/Z at the raw geometric center of the ENTIRE
            // mesh) would risk landing back inside the head/neck volume and
            // reintroducing the same gray-fog bug - the old fix only worked
            // by trivially going above ALL geometry, where X/Z didn't matter.
            // To avoid that, the camera is also nudged forward along +Z (the
            // same "facing forward" direction already assumed for the look
            // rotation below), which should move it from the center of the
            // head mass toward the face's front surface. The forward nudge
            // is sized as a fraction of the model's total height (not its
            // bounding depth, which can be inflated by outstretched arms or
            // a held weapon) so it scales sensibly with the model's size.
            // Both the 92%-of-height eye estimate and the 4%-of-height
            // forward nudge are estimates I can't visually verify myself -
            // if the feed goes hazy/gray again (camera still embedded) or
            // still looks off, tell me which and I'll adjust the fractions.
            SkinnedMeshRenderer meshRenderer = instance.GetComponentInChildren<SkinnedMeshRenderer>();
            Bounds meshBounds = meshRenderer != null ? meshRenderer.bounds : new Bounds(head.position, Vector3.one);

            float eyeHeight = meshBounds.min.y + meshBounds.size.y * 0.92f;
            float forwardClearance = meshBounds.size.y * 0.04f;

            GameObject camGo = new GameObject("HeadCam");
            camGo.transform.position = new Vector3(meshBounds.center.x, eyeHeight, meshBounds.center.z + forwardClearance);
            // Faces the same world +Z "forward" everything else in this scene
            // uses (Targets/OrbitHUD are both built along +Z) - a reasonable
            // default since ExternalGundam's own actual facing direction
            // isn't confirmed; tell me if the feed looks like it's facing the
            // wrong way and I'll rotate this.
            camGo.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            // Parented to the Head bone (worldPositionStays: true, so the
            // position/rotation just set are kept) purely so this camera
            // keeps following the bone's position AND its head-turn-tracking
            // rotation (see GundamHeadCam360) as the pilot looks around.
            camGo.transform.SetParent(head, true);

            Camera headCam = camGo.AddComponent<Camera>();
            headCam.targetTexture = headCamTex;
            headCam.fieldOfView = 60f;
            headCam.nearClipPlane = 0.05f;
            headCam.clearFlags = CameraClearFlags.SolidColor;
            headCam.backgroundColor = new Color(0.01f, 0.01f, 0.025f, 1f); // same deep-space color as the main view

            // Excludes GundamBodyLayer here too, per follow-up request ("그냥
            // 콕핏안에서는 내건담에 모습이 보이면안됨" - nothing of the pilot's
            // own Gundam should be visible anywhere inside the cockpit, full
            // stop). An earlier request wanted the torso visible in this
            // specific self-view feed while only the head stayed hidden (via
            // camera position/FOV alone, with headCam left at its default
            // "sees everything" mask) - that's now superseded. headCam
            // matches the main player-view camera exactly, so ExternalGundam's
            // whole body is invisible both in the pilot's direct first-person
            // view AND in this self-view feed (and by extension the 360
            // skybox/Cockpit_Dome background it also drives, further below).
            headCam.cullingMask &= ~(1 << GundamBodyLayer);
            // ...and never the cockpit interior either (see CockpitInteriorLayer).
            headCam.cullingMask &= ~(1 << CockpitInteriorLayer);
            // Far enough for the distant wreckage (up to ~900m) and far stars.
            headCam.farClipPlane = Mathf.Max(headCam.farClipPlane, 2000f);

            Debug.Log("[Gundam] Head camera attached to the '" + head.name + "' bone, feeding RenderTexture at " +
                HeadCamRenderTexturePath + ".");

            // --- 360-degree cockpit background: the SAME head camera also
            //     feeds a live cubemap Skybox, so wherever the pilot looks
            //     around the cockpit's dark enclosure, they see what the
            //     Gundam's head sees in every direction - per request
            //     ("콕핏에 검정부분에서 건담에 머리부분에서 보이는거 처럼
            //     해야해 360도로"). CreateXROrigin below switches the view
            //     camera to CameraClearFlags.Skybox once RenderSettings.skybox
            //     is set here (falls back to the old flat SolidColor
            //     background if this shader isn't found for some reason). ---
            Shader skyboxShader = Shader.Find("Skybox/Cubemap");
            GundamHeadCam360 cam360 = null;
            RenderTexture headCam360CubemapTex = null;
            if (skyboxShader != null)
            {
                Material skyboxMat = new Material(skyboxShader);
                skyboxMat.name = "GundamHeadCam360_Skybox";
                RenderSettings.skybox = skyboxMat;

                // Persisted asset (not a transient runtime-only cubemap) so
                // BuildCockpitEnclosure below can wire the SAME texture into
                // Cockpit_Dome's own material - see GetOrCreateHeadCam360CubemapRenderTexture
                // and MakeCubemapScreenMat. Per request ("밖에 자쿠가 보이지 않아"):
                // this is the actual fix for that - see the comment on
                // MakeCubemapScreenMat for why the old flat-image approach missed it.
                headCam360CubemapTex = GetOrCreateHeadCam360CubemapRenderTexture();

                cam360 = camGo.AddComponent<GundamHeadCam360>();
                cam360.headCam = headCam;
                cam360.skyboxMaterial = skyboxMat;
                cam360.head = head;
                cam360.cubemap = headCam360CubemapTex;
                // cam360.playerCamera is wired up by BuildCockpitScene right after
                // CreateXROrigin runs (that happens after this method returns) -
                // per request ("내가 머리를 돌리면 건담 머리도 돌아야해") so the
                // Gundam's head turns to match the pilot's own look direction.

                Debug.Log("[Gundam] 360-degree head-cam skybox wired up - the cockpit's surrounding view should now live-feed from the Gundam's head.");
            }
            else
            {
                Debug.LogWarning("[Gundam] Could not find the built-in 'Skybox/Cubemap' shader - skipping the " +
                    "360 background (the flat head-cam screen on the left aux display still works).");
            }

            return new GundamPlacementResult { headCamTex = headCamTex, headCam360 = cam360, headCam360CubemapTex = headCam360CubemapTex, instance = instance };
        }

        // ---------------------------------------------------------------
        // Enemy Zaku - per request ("자쿠 모델링이랑 건담 색깔 다운해둠 자쿠는
        // 적으로 만들거야 자쿠를 적으로 배치해줘"). Placed out in world space
        // beyond ExternalGundam, facing back toward it - just placement +
        // an EnemyMarker tag for now, no AI/combat behavior yet (that's a
        // separate, later step per the request - "지금은... 먼저 완성해라").
        // Same defensive "not imported yet" handling as PlaceExternalGundam
        // above (never fails the rest of the scene build).
        // ---------------------------------------------------------------
        // ---------------------------------------------------------------
        // BEAM SABER mode - per "빔샤벨 모드" + "전용 컨트롤 스틱". Builds:
        //   * WEAPON display touch buttons (BEAM RIFLE / BEAM SABER) on the
        //     existing Weapon_Canvas (SysCheck_Right) + WeaponTouchPanel
        //   * WeaponModeController (switches who owns the right hand)
        //   * BeamSaberControlStick - the short hand-held stick (BEAM SABER only)
        //   * GundamRightArm_View - the real Gundam mesh's right-arm triangles,
        //     skinned to the same real bones, on a visible layer (hidden until
        //     BEAM SABER) + BeamSaber (the downloaded hilt model + a beam blade)
        //     attached to the real RightHand bone
        //   * BeamSaberArmController - stick -> 2-bone IK on the real arm bones
        // Nothing existing is re-created or rewired except RightJoystick's two
        // runtime references WeaponModeController swaps (and restores).
        // ---------------------------------------------------------------
        const string BeamSaberHiltPath = "Assets/Models/BeamSaber/BeamSaberHilt.fbx";
        const string BeamSaberHiltTexturePath = "Assets/Models/BeamSaber/BeamSaberHilt-baseColor.png";
        const string GundamRightArmMeshPath = "Assets/Models/Gundam/GundamRightArm_View.asset";
        const float BeamSaberHiltLength = 1.8f;   // m, Gundam scale
        const float BeamSaberBladeLength = 9f;    // m
        const float BeamSaberBladeRadius = 0.28f; // m

        static void SetupBeamSaberMode(GameObject suitRoot, GameObject interior, GundamPlacementResult gundamResult,
            JoystickLever rightStick, HandJointTracker leftHand, HandJointTracker rightHand, GameObject xrOrigin)
        {
            // --- Mode controller ---
            WeaponModeController modes = suitRoot.AddComponent<WeaponModeController>();
            modes.rightStick = rightStick;
            modes.rightStickInteractable = rightStick != null && rightStick.handle != null
                ? rightStick.handle.GetComponent<XRSimpleInteractable>() : null;

            Transform rightNearFar = null;
            if (xrOrigin != null)
            {
                Transform rh = FindDeepChild(xrOrigin.transform, "Right Hand");
                rightNearFar = rh != null ? FindDeepChild(rh, "Near-Far Interactor") : null;
            }

            // --- Hand-held control stick (cockpit space, hidden until BEAM SABER) ---
            Material stickMat = MakeEmissiveMat(new Color(0.12f, 0.12f, 0.14f), new Color(0.9f, 0.2f, 0.55f) * 0.35f);
            GameObject stickRoot = new GameObject("BeamSaberControlStick");
            stickRoot.transform.SetParent(interior.transform, false);
            BeamSaberControlStick stick = stickRoot.AddComponent<BeamSaberControlStick>();
            stickRoot.transform.localPosition = stick.defaultLocalPosition;
            // 14 cm long, 3.4 cm thick grip (Unity cylinder = 2 units tall).
            GameObject grip = CreateCylinder("BeamSaberControlStick_Grip", stickRoot.transform, Vector3.zero,
                new Vector3(0.034f, 0.07f, 0.034f), stickMat);
            GameObject capTop = CreateSphere("BeamSaberControlStick_TopCap", stickRoot.transform, new Vector3(0f, 0.072f, 0f),
                new Vector3(0.038f, 0.02f, 0.038f), MakeEmissiveMat(new Color(0.5f, 0.1f, 0.3f), new Color(1f, 0.3f, 0.7f)));
            StripCollider(capTop);
            XRSimpleInteractable stickInteractable = grip.AddComponent<XRSimpleInteractable>();
            Collider gripCol = grip.GetComponent<Collider>();
            if (gripCol != null)
            {
                SerializedObject so = new SerializedObject(stickInteractable);
                SerializedProperty cols = so.FindProperty("m_Colliders");
                if (cols != null)
                {
                    cols.ClearArray();
                    cols.InsertArrayElementAtIndex(0);
                    cols.GetArrayElementAtIndex(0).objectReferenceValue = gripCol;
                    so.ApplyModifiedProperties();
                }
            }
            stick.interactable = stickInteractable;
            stick.hand = rightHand;
            stick.onlyAllowedInteractor = rightNearFar;
            stick.halfLength = 0.07f;
            if (rightNearFar == null) stickInteractable.enabled = false; // no XRI rig: right-hand grip fallback only
            modes.saberStick = stick;

            // --- Real Gundam right arm + saber ---
            GameObject gundam = gundamResult != null ? gundamResult.instance : null;
            if (gundam != null)
            {
                Transform upper = FindDeepChild(gundam.transform, "RightArm");
                Transform fore = FindDeepChild(gundam.transform, "RightForeArm");
                Transform handBone = FindDeepChild(gundam.transform, "RightHand");
                Transform shoulder = FindDeepChild(gundam.transform, "RightShoulder");
                if (upper != null && fore != null && handBone != null)
                {
                    SkinnedMeshRenderer armView = BuildGundamRightArmView(gundam,
                        new[] { "RightShoulder", "RightArm", "RightForeArm", "RightHand" });
                    Transform saber = BuildBeamSaber(handBone);

                    BeamSaberArmController arm = gundam.AddComponent<BeamSaberArmController>();
                    arm.upperArm = upper;
                    arm.foreArm = fore;
                    arm.handBone = handBone;
                    arm.armView = armView;
                    arm.saber = saber;
                    arm.stick = stick;
                    arm.cockpitSpace = interior.transform;
                    arm.viewController = suitRoot.GetComponentInChildren<CockpitViewController>(true);
                    Camera cam = xrOrigin != null ? xrOrigin.GetComponentInChildren<Camera>(true) : null;
                    if (cam != null) arm.pilotHead = cam.transform;
                    modes.saberArm = arm;
                    Debug.Log("[Gundam] BEAM SABER: arm bones " + upper.name + " > " + fore.name + " > " + handBone.name +
                        (shoulder != null ? " (shoulder " + shoulder.name + ")" : "") + ", saber on " + handBone.name + ".");
                }
                else
                {
                    Debug.LogWarning("[Gundam] BEAM SABER: RightArm/RightForeArm/RightHand bones not found - arm control disabled.");
                }
            }

            // --- WEAPON display touch buttons ---
            Transform weaponCanvas = interior.transform.Find("SystemCheckDisplay/SysCheck_Right/Weapon_Canvas");
            if (weaponCanvas == null)
            {
                Debug.LogWarning("[Gundam] BEAM SABER: Weapon_Canvas not found - no touch buttons.");
                return;
            }
            Text current = CreateUIText("CurrentWeapon", weaponCanvas, new Vector2(0, -50), new Vector2(340, 18), 14,
                TextAnchor.MiddleCenter, new Color(0.6f, 0.9f, 1f), "SELECT: BEAM RIFLE");
            WeaponTouchPanel.TouchButton rifleBtn = BuildTouchButton(weaponCanvas, "Btn_BeamRifle", "BEAM RIFLE",
                new Vector2(0, -95), WeaponModeController.Mode.BeamRifle);
            WeaponTouchPanel.TouchButton saberBtn = BuildTouchButton(weaponCanvas, "Btn_BeamSaber", "BEAM SABER",
                new Vector2(0, -183), WeaponModeController.Mode.BeamSaber);

            WeaponTouchPanel panel = weaponCanvas.gameObject.AddComponent<WeaponTouchPanel>();
            panel.weapons = modes;
            panel.leftHand = leftHand;
            panel.rightHand = rightHand;
            panel.buttons = new[] { rifleBtn, saberBtn };
            panel.currentText = current;
        }

        // Display cluster size/placement - per "조종기 사이 공간에 들어올 사이즈로"
        // (option "절반 크기 + 조종기 위로"). The cluster (built at full size by
        // BuildSystemCheckDisplay) is scaled about the cockpit origin and then
        // offset so the side screens' bottom edge sits DisplayBottomAboveGrip above
        // the joystick grip balls' top - hands on the sticks pass underneath.
        const float DisplayClusterScale = 0.5f;
        const float DisplayBottomAboveGrip = 0.08f; // clear of the knuckles on the grip
        const float DisplayCenterScreenZ = 0.40f;   // keep the center screen where it was reachable

        static void ShrinkSystemCheckDisplay(Transform interior)
        {
            Transform root = interior.Find("SystemCheckDisplay");
            if (root == null) return;
            float s = DisplayClusterScale;

            // Measure (at full size) the side screens' bottom and the center screen depth.
            Transform center = root.Find("SysCheck_Center");
            float sideBottom = float.MaxValue;
            foreach (string n in new[] { "SysCheck_Left", "SysCheck_Right" })
            {
                Transform t = root.Find(n);
                if (t == null) continue;
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                    sideBottom = Mathf.Min(sideBottom, interior.InverseTransformPoint(r.bounds.min).y);
            }
            if (sideBottom == float.MaxValue) sideBottom = 0.79f;

            // Top of the joystick grips (highest renderer of either stick).
            float gripTop = 0.94f;
            foreach (string n in new[] { "LeftJoystick_Mount", "RightJoystick_Mount" })
            {
                Transform t = interior.Find(n);
                if (t == null) continue;
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                    gripTop = Mathf.Max(gripTop, interior.InverseTransformPoint(r.bounds.max).y);
            }

            float centerZ = center != null ? center.localPosition.z : 0.48f;
            Vector3 offset = new Vector3(0f,
                gripTop + DisplayBottomAboveGrip - s * sideBottom,
                DisplayCenterScreenZ - s * centerZ);

            root.localScale = Vector3.one * s;
            root.localPosition = offset;

            // The two tick ladders flanking the screens are separate objects - move/scale them the same way.
            foreach (string n in new[] { "SysCheck_TickLadder_L", "SysCheck_TickLadder_R" })
            {
                Transform t = interior.Find(n);
                if (t == null) continue;
                t.localPosition = offset + t.localPosition * s;
                t.localScale = t.localScale * s;
            }
            Debug.Log("[Gundam] SystemCheckDisplay scaled x" + s + ", offset " + offset.ToString("F3") +
                " (side screens now start " + DisplayBottomAboveGrip + "m above the grips at y " + gripTop.ToString("F2") + ").");
        }

        static WeaponTouchPanel.TouchButton BuildTouchButton(Transform canvas, string name, string label, Vector2 pos,
            WeaponModeController.Mode mode)
        {
            Vector2 size = new Vector2(340, 72); // taller so it stays finger-sized after the display cluster is halved
            Image bg = CreateUIImage(name, canvas, pos, size, new Color(0.08f, 0.12f, 0.2f, 0.95f));
            Text t = CreateUIText(name + "_Label", bg.transform, Vector2.zero, size, 24, TextAnchor.MiddleCenter,
                new Color(0.75f, 0.85f, 1f), label);
            return new WeaponTouchPanel.TouchButton { mode = mode, rect = bg.rectTransform, background = bg, label = t };
        }

        /// <summary>A second SkinnedMeshRenderer on the Gundam that draws ONLY the triangles
        /// of the real Gundam mesh whose vertices are all mainly weighted to the given bones,
        /// skinned to the same real bone array - so the real right arm can be shown while the
        /// rest of the (pilot's own) body stays hidden. Mesh cached as an asset.</summary>
        static SkinnedMeshRenderer BuildGundamRightArmView(GameObject gundam, string[] boneNames)
        {
            SkinnedMeshRenderer src = gundam.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(r => r.sharedMesh != null && r.gameObject.name != "GundamRightArm_View");
            if (src == null) return null;
            UnityEngine.Mesh m = src.sharedMesh;
            Transform[] bones = src.bones;
            var keep = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && boneNames.Contains(bones[i].name)) keep.Add(i);

            BoneWeight[] bw = m.boneWeights;
            int[] tris = m.triangles;
            Vector3[] verts = m.vertices;
            Vector3[] norms = m.normals;
            Vector4[] tans = m.tangents;
            Vector2[] uv = m.uv;
            int[] remap = new int[verts.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var nv = new System.Collections.Generic.List<Vector3>();
            var nn = new System.Collections.Generic.List<Vector3>();
            var nt = new System.Collections.Generic.List<Vector4>();
            var nu = new System.Collections.Generic.List<Vector2>();
            var nw = new System.Collections.Generic.List<BoneWeight>();
            var ntri = new System.Collections.Generic.List<int>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (!keep.Contains(bw[a].boneIndex0) || !keep.Contains(bw[b].boneIndex0) || !keep.Contains(bw[c].boneIndex0)) continue;
                foreach (int v in new[] { a, b, c })
                {
                    if (remap[v] < 0)
                    {
                        remap[v] = nv.Count;
                        nv.Add(verts[v]);
                        if (norms.Length == verts.Length) nn.Add(norms[v]);
                        if (tans.Length == verts.Length) nt.Add(tans[v]);
                        if (uv.Length == verts.Length) nu.Add(uv[v]);
                        nw.Add(bw[v]);
                    }
                    ntri.Add(remap[v]);
                }
            }

            UnityEngine.Mesh arm = new UnityEngine.Mesh { name = "GundamRightArm_View" };
            if (nv.Count > 65000) arm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            arm.SetVertices(nv);
            if (nn.Count == nv.Count) arm.SetNormals(nn);
            if (nt.Count == nv.Count) arm.SetTangents(nt);
            if (nu.Count == nv.Count) arm.SetUVs(0, nu);
            arm.boneWeights = nw.ToArray();
            arm.bindposes = m.bindposes;
            arm.SetTriangles(ntri, 0);
            arm.RecalculateBounds();

            UnityEngine.Mesh existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(GundamRightArmMeshPath);
            if (existing != null) AssetDatabase.DeleteAsset(GundamRightArmMeshPath);
            AssetDatabase.CreateAsset(arm, GundamRightArmMeshPath);

            GameObject go = new GameObject("GundamRightArm_View");
            go.transform.SetParent(src.transform.parent, false);
            go.transform.localPosition = src.transform.localPosition;
            go.transform.localRotation = src.transform.localRotation;
            go.transform.localScale = src.transform.localScale;
            go.layer = 0; // visible to HeadCam (the body itself stays on GundamBodyLayer)
            SkinnedMeshRenderer smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = arm;
            smr.bones = bones;
            smr.rootBone = src.rootBone;
            smr.sharedMaterials = src.sharedMaterials;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.enabled = false; // shown only in BEAM SABER mode
            Debug.Log("[Gundam] GundamRightArm_View: " + nv.Count + " verts / " + (ntri.Count / 3) + " tris from bones " + string.Join(", ", boneNames));
            return smr;
        }

        /// <summary>The downloaded beam saber hilt (with its own base-color texture) plus a
        /// glowing beam blade, parented to the Gundam's RightHand bone. The root's +Y is the
        /// blade direction; BeamSaberArmController sets its world pose every frame.</summary>
        static Transform BuildBeamSaber(Transform handBone)
        {
            GameObject root = new GameObject("BeamSaber");
            root.transform.SetParent(handBone, false);
            float ls = handBone.lossyScale.x > 0.0001f ? 1f / handBone.lossyScale.x : 1f;
            root.transform.localScale = Vector3.one * ls; // 1 unit = 1 m in world
            root.layer = 0;

            float hiltLen = BeamSaberHiltLength;
            GameObject hiltAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BeamSaberHiltPath);
            if (hiltAsset != null)
            {
                GameObject hilt = (GameObject)PrefabUtility.InstantiatePrefab(hiltAsset);
                hilt.name = "BeamSaber_Hilt";
                hilt.transform.SetParent(root.transform, false);
                // The hilt model's long axis is its local Z - turn it onto +Y (blade axis).
                hilt.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                hilt.transform.localPosition = Vector3.zero;
                hilt.transform.localScale = Vector3.one;
                Renderer[] rs = hilt.GetComponentsInChildren<Renderer>(true);
                if (rs.Length > 0)
                {
                    Bounds b = rs[0].bounds;
                    foreach (Renderer r in rs) b.Encapsulate(r.bounds);
                    float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    float s = longest > 0.0001f ? hiltLen / longest : 1f;
                    hilt.transform.localScale = Vector3.one * (s / Mathf.Max(0.0001f, root.transform.lossyScale.x));
                    Vector3 off = root.transform.InverseTransformPoint(b.center) * s;
                    hilt.transform.localPosition = -off; // center the hilt on the grip point
                }
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(BeamSaberHiltTexturePath);
                Material hiltMat = MakeMat(Color.white);
                hiltMat.name = "BeamSaberHilt";
                if (tex != null) hiltMat.mainTexture = tex;
                foreach (Renderer r in rs)
                {
                    Material[] mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++) mats[i] = hiltMat;
                    r.sharedMaterials = mats;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                SetLayerRecursively(hilt, 0);
                foreach (Collider c in hilt.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            }
            else
            {
                Debug.LogWarning("[Gundam] Beam saber hilt model not found at " + BeamSaberHiltPath + " - using a plain grip.");
                GameObject plain = CreateCylinder("BeamSaber_Hilt", root.transform, Vector3.zero,
                    new Vector3(0.35f, hiltLen * 0.5f, 0.35f), MakeMat(new Color(0.8f, 0.8f, 0.82f)));
                StripCollider(plain);
                plain.layer = 0;
            }

            // Beam blade: bright emissive core + slightly wider glow shell.
            Material core = MakeEmissiveMat(new Color(1f, 0.85f, 0.95f), new Color(4f, 2.2f, 3.2f));
            Material glow = MakeEmissiveMat(new Color(1f, 0.2f, 0.6f), new Color(3f, 0.4f, 1.6f));
            float bladeY = hiltLen * 0.5f + BeamSaberBladeLength * 0.5f;
            GameObject blade = CreateCylinder("BeamSaber_Blade", root.transform, new Vector3(0f, bladeY, 0f),
                new Vector3(BeamSaberBladeRadius * 2f, BeamSaberBladeLength * 0.5f, BeamSaberBladeRadius * 2f), glow);
            StripCollider(blade);
            blade.layer = 0;
            GameObject coreGo = CreateCylinder("BeamSaber_Core", root.transform, new Vector3(0f, bladeY, 0f),
                new Vector3(BeamSaberBladeRadius, BeamSaberBladeLength * 0.5f + 0.05f, BeamSaberBladeRadius), core);
            StripCollider(coreGo);
            coreGo.layer = 0;
            GameObject tip = CreateSphere("BeamSaber_Tip", root.transform, new Vector3(0f, hiltLen * 0.5f + BeamSaberBladeLength, 0f),
                Vector3.one * BeamSaberBladeRadius * 2f, glow);
            StripCollider(tip);
            tip.layer = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            root.SetActive(false); // shown only in BEAM SABER mode
            return root.transform;
        }

        // ---------------------------------------------------------------
        // Distant space wreckage - per "우주에 건물을 넣을려고 하는데 우주에
        // 잔해같은거 거슬리지 않게" (placement option "멀리 배경으로"). Uses every
        // model in DebrisModelFolder (the VARCO 3D generated colony / station /
        // battleship pieces), placed in a ring 450-750m out around the combat area
        // so they never block the fight, scaled to 140-300m, slightly dimmed so
        // they read as background, no colliders/shadows, on SpaceBackdropLayer.
        // SpaceDebrisField keeps them drifting past slowly and tumbling.
        // Missing folder/models = skipped with a warning (never fails the build).
        // ---------------------------------------------------------------
        const string DebrisModelFolder = "Assets/Models/Debris";

        static void PlaceSpaceDebris(Transform viewer)
        {
            if (!AssetDatabase.IsValidFolder(DebrisModelFolder))
            {
                Debug.LogWarning("[Gundam] No '" + DebrisModelFolder + "' folder - skipping space wreckage.");
                return;
            }
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { DebrisModelFolder });
            GameObject[] models = guids
                .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null)
                .OrderBy(m => m.name)
                .ToArray();
            if (models.Length == 0)
            {
                Debug.LogWarning("[Gundam] No models in '" + DebrisModelFolder + "' - skipping space wreckage.");
                return;
            }

            // (azimuth deg, elevation deg, distance m, size m) around the combat
            // area's center. Azimuth 0 = +Z (straight ahead at start).
            Vector4[] spots =
            {
                new Vector4(35f, 12f, 560f, 260f),
                new Vector4(105f, -16f, 650f, 180f),
                new Vector4(160f, 9f, 500f, 150f),
                new Vector4(215f, 24f, 720f, 300f),
                new Vector4(275f, -9f, 580f, 200f),
                new Vector4(330f, -26f, 620f, 160f),
            };
            Vector3 center = new Vector3(0f, 10f, 40f);

            GameObject root = new GameObject("SpaceDebris");
            root.layer = SpaceBackdropLayer;
            System.Random rnd = new System.Random(4242);

            for (int i = 0; i < spots.Length; i++)
            {
                Vector4 s = spots[i];
                GameObject model = models[i % models.Length];
                Quaternion dirRot = Quaternion.Euler(-s.y, s.x, 0f);

                // Pivot at the piece's visual center, so it tumbles in place.
                GameObject pivot = new GameObject("Debris_" + i + "_" + model.name);
                pivot.layer = SpaceBackdropLayer;
                pivot.transform.SetParent(root.transform, false);
                pivot.transform.position = center + dirRot * Vector3.forward * s.z;
                pivot.transform.rotation = Quaternion.Euler((float)rnd.NextDouble() * 360f, (float)rnd.NextDouble() * 360f, (float)rnd.NextDouble() * 360f);

                GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                inst.transform.SetParent(pivot.transform, false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one;
                SetLayerRecursively(inst, SpaceBackdropLayer);
                foreach (Collider c in inst.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);

                Renderer[] rends = inst.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0) continue;
                Bounds b = rends[0].bounds;
                for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);
                float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                float scale = maxDim > 0.0001f ? s.w / maxDim : 1f;
                inst.transform.localScale = Vector3.one * scale;
                // Re-center: move the model so its bounds center sits on the pivot.
                Vector3 centerOffset = pivot.transform.InverseTransformPoint(b.center) * scale;
                inst.transform.localPosition = -centerOffset;

                foreach (Renderer r in rends)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    Material[] mats = r.sharedMaterials;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] == null) continue;
                        // Own copy (never edit the imported/shared material), dimmed
                        // so the wrecks sit back in the scene instead of popping.
                        Material dim = new Material(mats[m]) { name = mats[m].name + " (Debris dim)" };
                        if (dim.HasProperty("_BaseColor")) dim.SetColor("_BaseColor", dim.GetColor("_BaseColor") * 0.6f);
                        else if (dim.HasProperty("_Color")) dim.SetColor("_Color", dim.GetColor("_Color") * 0.6f);
                        mats[m] = dim;
                    }
                    r.sharedMaterials = mats;
                }
            }

            SpaceDebrisField field = root.AddComponent<SpaceDebrisField>();
            field.viewer = viewer;
            Debug.Log("[Gundam] Placed " + spots.Length + " distant wreck pieces from " + models.Length + " model(s) in " + DebrisModelFolder + ".");
        }

        static void PlaceZakuEnemy(Transform playerGundam)
        {
            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ZakuModelPath);
            if (fbxAsset == null)
            {
                Debug.LogWarning("[Gundam] Could not load the Zaku FBX at '" + ZakuModelPath +
                    "'. If you just added this file, do Assets > Refresh (or reopen the project) " +
                    "so Unity imports it, then re-run Gundam > Build Cockpit Scene.");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
            instance.name = "ZakuEnemy";
            instance.transform.SetParent(null); // world space, same as ExternalGundam - not part of the player's own suit

            // Beyond ExternalGundam (which stands at Z=20) so the two read
            // as separate mobile suits facing off in open space, not
            // overlapping. Facing back toward the player (180 degrees from
            // ExternalGundam/the cockpit's own forward) rather than facing
            // away.
            // Moved from Z=40 (only 20m from ExternalGundam) to Z=100 - per
            // "시작할떄 조금 거리가 있어야할거같아": starts 80m away, then
            // ZakuCombatAI closes to its ~60m engagement distance.
            instance.transform.position = new Vector3(0f, 0f, ZakuSpawnZ);
            instance.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            // Scaled from the model's OWN actual imported bounds (whatever
            // raw scale this particular FBX ships at) to the same ~18m
            // mobile-suit-height convention ExternalGundam uses, rather than
            // a hardcoded number guessed without opening the file.
            // (Measured from the real vertices, not the padded renderer bounds -
            // see the matching note in PlaceExternalGundam: "지금 크기가 머리하나는
            // 차이 나는데?".)
            instance.transform.localScale = Vector3.one;
            float zakuMeasuredHeight = MeasureMeshHeight(instance);
            if (zakuMeasuredHeight > 0.0001f)
            {
                float scale = MobileSuitTargetHeight / zakuMeasuredHeight;
                instance.transform.localScale = Vector3.one * scale;
            }
            else
            {
                Debug.LogWarning("[Gundam] Could not measure ZakuEnemy's mesh bounds to auto-scale it to ~" +
                    MobileSuitTargetHeight + "m - it may be the wrong size. Adjust its Scale by hand if so.");
            }

            Texture2D zakuTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(ZakuTexturePath);
            ApplyBaseColorTexture(instance, zakuTexture, "ZakuEnemy");

            // Enemy tag (see Gundam.Cockpit.EnemyMarker - OrbitHUDTargetLock uses it).
            instance.AddComponent<EnemyMarker>();

            // --- Combat: hitbox, health, maneuvering - per request ("자쿠에게
            // 움직임을 주고 체력을 1000으로 설정해줘 그리고 해드 발칸에 데미지는
            // 1이야"). Runtime behavior lives in EnemyHealth / ZakuCombatAI /
            // HeadVulcanBullet; this only adds and wires them. ---
            // Hitbox: an upright capsule over the real body (root-local units -
            // the root's own scale brings it to ~18m). Without a collider the
            // Head Vulcan's rounds had nothing to hit.
            float localHeight = zakuMeasuredHeight > 0.0001f ? zakuMeasuredHeight : 1f;
            CapsuleCollider hitbox = instance.AddComponent<CapsuleCollider>();
            hitbox.direction = 1; // Y
            hitbox.height = localHeight;
            hitbox.radius = localHeight * 0.22f;
            hitbox.center = new Vector3(0f, localHeight * 0.5f, 0f);

            EnemyHealth health = instance.AddComponent<EnemyHealth>();
            health.maxHealth = 1000;
            health.respawnDelay = 5f;
            health.effectMaterial = MakeEmissiveMat(new Color(0.35f, 0.12f, 0.02f), new Color(1f, 0.5f, 0.1f));
            health.barHeightAboveRoot = MobileSuitTargetHeight + 2f;
            health.faceTowards = playerGundam;

            ZakuCombatAI ai = instance.AddComponent<ZakuCombatAI>();
            ai.target = playerGundam;
            ai.preferredDistance = 60f;

            Debug.Log("[Gundam] Placed ZakuEnemy at " + instance.transform.position +
                " (facing the player's Gundam)" +
                (zakuTexture != null ? ", base color texture applied." : ", no texture found (flat default material)."));
        }

        // Applies a base color/albedo texture to every material used by
        // every renderer under root - shader-agnostic (uses Material.mainTexture,
        // which Unity resolves to whichever property a given shader tags as
        // its main texture, e.g. URP Lit's _BaseMap) rather than assuming a
        // specific shader/property name. Shared by both ExternalGundam and
        // ZakuEnemy above.
        static void ApplyBaseColorTexture(GameObject root, Texture2D texture, string label)
        {
            if (texture == null)
            {
                Debug.LogWarning("[Gundam] No base color texture found for " + label + " - it will keep its default/flat material.");
                return;
            }

            // Bug fix: this used to mutate renderer.sharedMaterials[i] in
            // place. Both ExternalGundam and ZakuEnemy came in with no real
            // material of their own (both FBX imports fell back to the same
            // shared default/no-texture material), so mutating that shared
            // asset for one model bled its texture onto every OTHER model
            // still pointing at the same material - reported as "자쿠
            // 색깔이 건담한테도 들어갔는데" (Zaku's texture ended up on the
            // Gundam too). Fix: give each model its OWN material instance
            // (a copy) before touching its texture, so setting one model's
            // texture can never affect another model that happened to share
            // the same starting material.
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;

                    Material uniqueMat = new Material(materials[i]);
                    uniqueMat.name = materials[i].name + " (" + label + ")";
                    uniqueMat.mainTexture = texture;
                    materials[i] = uniqueMat;
                }
                renderer.sharedMaterials = materials;
            }
        }

        /// <summary>Recursively searches a transform and all its descendants for a child
        /// with the given name (case-insensitive) - used to find the "Head" bone inside
        /// the Gundam FBX's own skeleton without assuming its exact depth/path.</summary>
        static Transform FindDeepChild(Transform root, string name)
        {
            if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeepChild(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Sets a GameObject and every descendant to the given layer - used to put
        /// ExternalGundam's whole hierarchy (mesh + every bone) on GundamBodyLayer in one go.</summary>
        /// <summary>Like SetLayerRecursively, but a GameObject whose Collider belongs to
        /// an XR interactable (the joystick handles' grip balls / finger buttons /
        /// trigger) keeps its current layer so the hands can still grab it (see the
        /// CockpitInteriorLayer call site for why).</summary>
        static void SetLayerRecursivelyExceptColliders(GameObject go, int layer)
        {
            bool grabCollider = go.GetComponent<Collider>() != null
                && go.GetComponentInParent<XRBaseInteractable>(true) != null;
            if (!grabCollider) go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
            {
                SetLayerRecursivelyExceptColliders(go.transform.GetChild(i).gameObject, layer);
            }
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
            {
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
            }
        }

        /// <summary>Loads the head-cam RenderTexture asset if Build Cockpit Scene already
        /// created one on a previous run, otherwise creates and saves a fresh one. Must be
        /// a real saved asset (not just "new RenderTexture(...)") so the Camera/RawImage
        /// references to it survive a scene save and project reopen.</summary>
        static RenderTexture GetOrCreateHeadCamRenderTexture()
        {
            RenderTexture existing = AssetDatabase.LoadAssetAtPath<RenderTexture>(HeadCamRenderTexturePath);
            if (existing != null)
            {
                // Upgrade an already-existing asset (from an earlier, lower-res
                // run of this generator) in place rather than leaving it stale -
                // changing HeadCamResolution above wouldn't otherwise do
                // anything once this .renderTexture asset already exists on
                // disk, since this method would just keep returning it as-is.
                // Resizing the SAME asset (instead of deleting/recreating it)
                // keeps its GUID, so the aux screen's RawImage, the dome's
                // material, and headCam.targetTexture all keep pointing at it
                // correctly with nothing left dangling.
                if (existing.width != HeadCamResolution || existing.height != HeadCamResolution)
                {
                    existing.Release();
                    existing.width = HeadCamResolution;
                    existing.height = HeadCamResolution;
                    existing.Create();
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[Gundam] Upgraded the existing GundamHeadCam RenderTexture to " + HeadCamResolution + "x" +
                        HeadCamResolution + " (was lower-res) for a sharper feed on the dome/aux screen.");
                }
                return existing;
            }

            RenderTexture rt = new RenderTexture(HeadCamResolution, HeadCamResolution, 16);
            rt.name = "GundamHeadCam";
            AssetDatabase.CreateAsset(rt, HeadCamRenderTexturePath);
            return rt;
        }

        /// <summary>Same idea as GetOrCreateHeadCamRenderTexture above, but for the
        /// Cube-dimension RenderTexture that feeds both RenderSettings.skybox and (new)
        /// Cockpit_Dome's own 360-degree material - see MakeCubemapScreenMat and
        /// GundamHeadCam360.cs's "cubemap" field. Re-run-safe: reuses/upgrades the same
        /// saved asset in place (keeping its GUID) rather than recreating it, same
        /// reasoning as the flat texture's version.</summary>
        static RenderTexture GetOrCreateHeadCam360CubemapRenderTexture()
        {
            RenderTexture existing = AssetDatabase.LoadAssetAtPath<RenderTexture>(HeadCam360CubemapRenderTexturePath);
            if (existing != null)
            {
                if (existing.width != HeadCam360CubemapResolution ||
                    existing.dimension != UnityEngine.Rendering.TextureDimension.Cube)
                {
                    existing.Release();
                    existing.width = HeadCam360CubemapResolution;
                    existing.height = HeadCam360CubemapResolution;
                    existing.dimension = UnityEngine.Rendering.TextureDimension.Cube;
                    existing.Create();
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[Gundam] Upgraded the existing GundamHeadCam360 cubemap RenderTexture to " +
                        HeadCam360CubemapResolution + "x" + HeadCam360CubemapResolution + " per face (Cube dimension).");
                }
                return existing;
            }

            RenderTexture rt = new RenderTexture(HeadCam360CubemapResolution, HeadCam360CubemapResolution, 16);
            rt.dimension = UnityEngine.Rendering.TextureDimension.Cube;
            rt.name = "GundamHeadCam360Cubemap";
            AssetDatabase.CreateAsset(rt, HeadCam360CubemapRenderTexturePath);
            return rt;
        }

        static void CreateLighting()
        {
            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // Soft cool fill light inside the cockpit only (short range) so the
            // dark interior still reads as "dark cabin + glowing panels" instead
            // of pitch black, without brightening the exterior/targets/starfield.
            GameObject fillGo = new GameObject("Cockpit_FillLight");
            Light fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.color = new Color(0.55f, 0.75f, 1f);
            fill.intensity = 0.6f;
            fill.range = 3.5f;
            fillGo.transform.position = new Vector3(0f, 1.6f, 0.6f);
        }

        // ---------------------------------------------------------------
        // Seat
        // ---------------------------------------------------------------
        static void BuildSeat(Transform interior, Material seatMat, Material seatAccentMat, Material hullMat)
        {
            CreateCylinder("Seat_Base", interior, new Vector3(0, 0.18f, -0.25f), new Vector3(0.28f, 0.18f, 0.28f), hullMat);
            CreateCube("Seat_Pan", interior, new Vector3(0, 0.42f, -0.25f), new Vector3(0.55f, 0.12f, 0.55f), seatMat);

            GameObject back = CreateCube("Seat_Back", interior, new Vector3(0, 0.85f, -0.5f), new Vector3(0.55f, 0.75f, 0.12f), seatMat);
            back.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);

            GameObject headrest = CreateCube("Seat_Headrest", interior, new Vector3(0, 1.28f, -0.52f), new Vector3(0.4f, 0.22f, 0.12f), seatMat);
            headrest.transform.localRotation = Quaternion.Euler(-6f, 0f, 0f);

            GameObject bolsterL = CreateCube("Seat_Bolster_L", interior, new Vector3(-0.32f, 0.5f, -0.28f), new Vector3(0.1f, 0.2f, 0.55f), seatAccentMat);
            bolsterL.transform.localRotation = Quaternion.Euler(0f, 0f, -12f);
            GameObject bolsterR = CreateCube("Seat_Bolster_R", interior, new Vector3(0.32f, 0.5f, -0.28f), new Vector3(0.1f, 0.2f, 0.55f), seatAccentMat);
            bolsterR.transform.localRotation = Quaternion.Euler(0f, 0f, 12f);
        }

        // ---------------------------------------------------------------
        // Full 360-degree enclosure — a physical floor to stand/sit on,
        // plus a single continuous curved "screen" dome that wraps every
        // other direction (ceiling, rear, both sides, and the space above
        // the seat's front too) as one seamless round surface instead of
        // flat panels — "공간이 360도 전부 둥그런 화면이어야해". The dome is
        // an ellipsoid (a Sphere primitive scaled non-uniformly), rendered
        // inward-facing via Cull Front + Unlit so it's visible from inside
        // and guarantees no real-world Skybox/passthrough is ever visible
        // through a seam or gap the way separate flat walls could leave.
        //
        // Margins are generous on purpose: the Starter Assets XR Origin
        // this rig is built from has m_RequestedTrackingOriginMode = "Not
        // Specified", which most OpenXR runtimes resolve to real Floor
        // tracking. That means the actual runtime camera Y is this
        // transform's authored XR Origin offset (0) PLUS the player's REAL
        // head height above their REAL floor - which can reach roughly
        // 1.6-2.0m even sitting, more standing. A tight dome can end up
        // with the camera embedded inside/above it, which looks like a
        // solid black screen. Keep the dome well clear of that (same
        // extents the old flat ceiling/walls used, which were already
        // verified generous enough).
        // ---------------------------------------------------------------
        static void BuildCockpitEnclosure(Transform interior, Material hullAccentMat, RenderTexture liveHeadCamTex, RenderTexture liveHeadCam360CubeTex)
        {
            // Cockpit_Floor is still built (kept as a GameObject so it's one
            // flag-flip to bring back) but its Renderer is switched OFF, same
            // treatment as Cockpit_Dome below - per request ("콕피드 아래
            // 바닥은 없어야하고... 건담에 머리에서 바라보고 있는 모습이
            // 나와야해"): now that the whole surrounding view is meant to be
            // what the Gundam's own head-cam sees, a flat little floor slab
            // fixed to the cockpit's local space doesn't belong in that
            // picture - the pilot should see the Gundam's actual point of
            // view (the 360 head-cam skybox), not a room floor under their
            // feet. The seat/dashboard/joysticks the pilot actually touches
            // stay visible as before; only this floor prop is hidden.
            GameObject floor = CreateCube("Cockpit_Floor", interior, new Vector3(0, -0.05f, 0.5f), new Vector3(4.0f, 0.1f, 5.4f), hullAccentMat);
            Collider floorCol = floor.GetComponent<Collider>();
            if (floorCol != null) UnityEngine.Object.DestroyImmediate(floorCol);
            Renderer floorRenderer = floor.GetComponent<Renderer>();
            if (floorRenderer != null) floorRenderer.enabled = false;

            // Cockpit_Dome: EARLIER this was made invisible (Renderer off) so
            // the real Starfield/skybox behind it would show through, per an
            // earlier request to actually see "outside". Latest request
            // reverses that ("콕피트에서 밖에가 보이면안됨 콕피트에 외관은
            // 전부 디스플레이어야해 그래야 로봇안에 있는거지 로봇에 시점으로만
            // 세상을 보는거야") - seeing a raw gap straight through to the
            // background skybox reads as "floating in open space", not
            // "sitting inside a sealed robot cockpit watching a screen". So
            // the dome's Renderer is back ON (solid, no holes - the pilot can
            // never see straight past it to anything else) and its material
            // now shows the SAME live head-cam feed already piped onto the
            // SysCheck_Left aux screen (liveHeadCamTex - see
            // PlaceExternalGundam), via a plain URP Unlit material (same
            // family MakeShellMat/every other material here already uses, so
            // it's guaranteed to render correctly - unlike the raw legacy
            // "Skybox/Cubemap" shader RenderSettings.skybox uses, which would
            // very likely show as a broken/pink "unsupported shader" if
            // applied directly to a regular mesh under URP).
            //
            // UPDATE per report ("밖에 자쿠가 보이지 않아"): the caveat below
            // (flat 2D image stretched over a sphere) turned out to actually
            // hide distant objects like ZakuEnemy, not just look a bit
            // distorted - anything outside headCam's own narrow 60-degree
            // forward cone at the instant of capture simply wasn't in that
            // flat image at all, so no amount of UV-stretching could recover
            // it. Fixed properly now: MakeCubemapScreenMat samples the SAME
            // full 360-degree cubemap already being rendered every frame for
            // RenderSettings.skybox (see GundamHeadCam360.cs), by real world
            // direction, so every direction shows its own correct content -
            // no stretching, and nothing missing regardless of where headCam
            // happened to be facing. Falls back to the old flat-image
            // material (MakeUnlitScreenMat), and finally to the plain static
            // shell (MakeShellMat), if the cubemap or its shader aren't
            // available for some reason - the pilot is never left with a
            // hole to the void either way.
            Vector3 domeCenter = new Vector3(0, 1.75f, 0.5f);
            Vector3 domeScale = new Vector3(4.6f, 4.2f, 6.2f);
            Material domeMat = MakeCubemapScreenMat(liveHeadCam360CubeTex);
            if (domeMat == null && liveHeadCam360CubeTex != null)
            {
                Debug.LogWarning("[Gundam] Could not find the custom 'Custom/CockpitDomeCubemap' shader (expected at " +
                    "Assets/Shaders/CockpitDomeCubemap.shader) - falling back to the old flat 2D head-cam image " +
                    "stretched over the dome. That version WILL make distant objects (e.g. ZakuEnemy) hard or " +
                    "impossible to make out. Make sure the shader file exists, then re-run Build Cockpit Scene.");
            }
            if (domeMat == null)
            {
                domeMat = liveHeadCamTex != null ? MakeUnlitScreenMat(liveHeadCamTex) : MakeShellMat();
            }
            GameObject dome = CreateSphere("Cockpit_Dome", interior, domeCenter, domeScale, domeMat);
            Collider domeCol = dome.GetComponent<Collider>();
            if (domeCol != null) UnityEngine.Object.DestroyImmediate(domeCol);
            if (liveHeadCamTex == null && liveHeadCam360CubeTex == null)
            {
                Debug.LogWarning("[Gundam] Cockpit_Dome has no live head-cam feed yet (Gundam FBX/head-cam not " +
                    "wired up) - using a static opaque shell for now. Re-run Build Cockpit Scene after the FBX is " +
                    "imported to get the live view on the dome itself.");
            }

            // Canopy frame: thin structural ribs over the now-solid dome, so
            // it still reads as a rounded canopy (with visible frame
            // structure) rather than a plain sphere - per request (with a
            // reference photo of a rounded glass canopy around the pilot):
            // "이런식으로 콕핏이 구형으로 보여야해 그안에 있을때 구형안에 있는
            // 느낌". These trace the SAME sphere as Cockpit_Dome (great-circle
            // meridian bows + one equatorial ring, built from short beam
            // segments since Unity has no curved primitive) - like a real
            // canopy's frame bars over its glass/screen panes.
            // Per "콕핏 태두리선을 더얇게하고 아주조금 빛나게해줘": a faint cool glow
            // (was an almost-black 0.08/0.1/0.14 emission).
            Material canopyFrameMat = MakeEmissiveMat(new Color(0.05f, 0.06f, 0.08f), new Color(0.18f, 0.3f, 0.42f));
            BuildCanopyFrame(interior, domeCenter, domeScale * 0.5f, canopyFrameMat);
        }

        /// <summary>Thin structural ribs (great-circle meridian bows + one equatorial
        /// ring) tracing the Cockpit_Dome's sphere, so its round shape/curvature is
        /// visible even though the dome mesh itself stays invisible. Built from short
        /// CreateBeam segments since Unity has no native curved-line primitive.</summary>
        static void BuildCanopyFrame(Transform interior, Vector3 domeCenterLocal, Vector3 domeRadii, Material frameMat)
        {
            GameObject frameRoot = new GameObject("Canopy_Frame");
            frameRoot.transform.SetParent(interior, false);

            const int ribCount = 6;       // meridian bows, 30 degrees apart (each covers both sides of the sphere)
            // More, shorter segments + pulled slightly INSIDE the dome - per "선이 끈어진거
            // 없이": the old 20/24-segment chords had their ends exactly ON the opaque dome
            // surface, so the dome hid the beam near every joint and the lines looked dashed.
            const int meridianSegments = 48;
            const int equatorSegments = 48;
            const float beamThickness = 0.012f; // was 0.025 - thinner per "태두리선을 더얇게"
            const float insetFromDome = 0.02f;   // keeps every segment fully in front of the dome
            float minRadius = Mathf.Max(0.1f, Mathf.Min(domeRadii.x, Mathf.Min(domeRadii.y, domeRadii.z)));
            float inset = 1f - insetFromDome / minRadius;

            for (int r = 0; r < ribCount; r++)
            {
                float theta = r * (180f / ribCount) * Mathf.Deg2Rad;
                Vector3 prevPoint = Vector3.zero;
                for (int s = 0; s <= meridianSegments; s++)
                {
                    float phi = s * (360f / meridianSegments) * Mathf.Deg2Rad;
                    Vector3 unit = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                    Vector3 point = domeCenterLocal + Vector3.Scale(unit, domeRadii) * inset;

                    if (s > 0)
                    {
                        StripCollider(CreateBeam($"Canopy_Rib_{r}_{s}", frameRoot.transform, prevPoint, point, beamThickness, frameMat));
                    }
                    prevPoint = point;
                }
            }

            // One equatorial ring (a horizontal band at the dome's own center
            // height) tying the meridian bows together, like a canopy's side rail.
            Vector3 prevLat = Vector3.zero;
            for (int s = 0; s <= equatorSegments; s++)
            {
                float phi = s * (360f / equatorSegments) * Mathf.Deg2Rad;
                Vector3 unit = new Vector3(Mathf.Cos(phi), 0f, Mathf.Sin(phi));
                Vector3 point = domeCenterLocal + Vector3.Scale(unit, domeRadii) * inset;

                if (s > 0)
                {
                    StripCollider(CreateBeam($"Canopy_EquatorRib_{s}", frameRoot.transform, prevLat, point, beamThickness, frameMat));
                }
                prevLat = point;
            }
        }

        /// <summary>
        /// Cull-Front + Unlit fallback shell material. Cull Front is required
        /// because the camera sits INSIDE this cube (unlike every other wall
        /// above, which the camera views from outside), so only the mesh's
        /// back faces should render. Unlit (not Lit) matters too: those
        /// visible back faces keep their outward-pointing normals, so a Lit
        /// shader would compute lighting as if every face pointed away from
        /// every light in the cockpit and render solid black. Unlit ignores
        /// normals/lighting entirely and just shows the flat base color.
        /// </summary>
        static Material MakeShellMat()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            Material m = new Material(shader != null ? shader : Shader.Find("Unlit/Color"));
            m.color = new Color(0.05f, 0.055f, 0.07f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 1f); // 1 = Front
            return m;
        }

        /// <summary>Same URP Unlit + Cull-Front shell material as MakeShellMat, but with
        /// a live RenderTexture (e.g. the Gundam's head-cam feed) as its base texture -
        /// used to make Cockpit_Dome read as "a display showing the feed" rather than a
        /// flat static color, per request ("콕피트에 외관은 전부 디스플레이어야해").
        /// Deliberately the standard 2D Unlit shader (not the "Skybox/Cubemap" shader
        /// RenderSettings.skybox uses) - that legacy shader isn't written for URP's
        /// pipeline tags and would very likely render as a broken/pink "unsupported
        /// shader" on a regular mesh, so this trades a perfectly seamless 360 wrap for a
        /// guaranteed-to-render result.</summary>
        static Material MakeUnlitScreenMat(RenderTexture tex)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            Material m = new Material(shader != null ? shader : Shader.Find("Unlit/Texture"));
            m.name = "Cockpit_Dome_LiveFeed";
            m.mainTexture = tex;
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 1f); // 1 = Front - visible from inside the dome
            return m;
        }

        /// <summary>Proper 360-degree version of MakeUnlitScreenMat above - samples the
        /// SAME live cubemap already being rendered every frame for RenderSettings.skybox
        /// (see GundamHeadCam360.cs) using a small custom URP shader
        /// (Assets/Shaders/CockpitDomeCubemap.shader), instead of stretching one flat 2D
        /// image over the whole sphere. Per report ("밖에 자쿠가 보이지 않아") - the flat-
        /// image approach only ever captured whatever was directly in headCam's own
        /// 60-degree forward cone at the moment of capture, so anything outside that cone
        /// (ZakuEnemy included, most of the time) was simply never in the image at all,
        /// no matter how it got stretched around the sphere. A cubemap sampled by the true
        /// world-space direction from the dome's surface (equivalent to "from roughly the
        /// pilot's own head", since the dome is centered close to them) shows the CORRECT
        /// content in every direction with no stretching - exactly like a normal skybox,
        /// just rendered onto a regular mesh instead of used as the camera's background
        /// (which would never actually be seen, since the dome fully encloses the camera).
        /// Returns null (letting the caller fall back) if the cubemap texture or the
        /// custom shader aren't available - e.g. the shader file was deleted, or an older
        /// scene was built before this fix and hasn't been rebuilt yet.</summary>
        static Material MakeCubemapScreenMat(RenderTexture cubemapTex)
        {
            if (cubemapTex == null) return null;
            Shader shader = Shader.Find("Custom/CockpitDomeCubemap");
            if (shader == null) return null;

            Material m = new Material(shader);
            m.name = "Cockpit_Dome_LiveFeed360";
            if (m.HasProperty("_CubeTex")) m.SetTexture("_CubeTex", cubemapTex);
            return m;
        }

        // ---------------------------------------------------------------
        // Front display — "SYSTEM CHECK / BOOT CONFIGURATION" style mecha
        // dashboard: one large center screen (radial tick dial + pilot/
        // weapon/ammo readout) flanked by two smaller aux screens, a red
        // accent bar underneath, and vertical tick-meter ladders at the far
        // edges. Replaces the old plain curved-windshield + corner-HUD
        // display entirely, per request ("지금 콕핏에 있는거 다지우고 이런거
        // 만들어" against the reference screenshot). Everything here is
        // built from primitives + procedural uGUI (no imported art), so it
        // approximates the reference's look rather than reproducing it
        // pixel-for-pixel.
        //
        // Mounted low and close, tilted up toward the pilot's face - a
        // console sitting just above the thighs (like a real cockpit
        // instrument panel), not a far-off eye-level windshield. Per
        // request: "내가 앉은 자리에 허벅지쯤 위에 있는거야 이게".
        // ---------------------------------------------------------------
        static void BuildSystemCheckDisplay(Transform interior, Material screenMat, Material frameMat, RenderTexture headCamTex, CockpitHUDManager hudManager,
            out Text telemetryText, out Text speedText, out Text statusText, out Text vulcanAmmoText, out CockpitStatusHUD statusHUD, out CockpitWeaponHUD weaponHUD)
        {
            GameObject rootGo = new GameObject("SystemCheckDisplay");
            rootGo.transform.SetParent(interior, false);

            // Per report ("화면이 너무 가까이 붙어 보인다"): pulled back a modest
            // 0.08m from the player (0.55 -> 0.63) - same orientation (tiltX,
            // per-screen angleDeg) and the same relative Center/Left/Right
            // angular layout, just a bit further along that same direction so
            // the screens don't feel like they're pressed up against the face.
            const float radius = 0.63f;   // was 0.55f - still within arm's reach
            const float eyeZ = -0.15f;
            const float baseY = 0.92f;    // low, just above the seated pilot's thighs
            const float tiltX = 36f;      // tilted up so the pilot looking down sees it face-on

            Transform centerScreen = CreatePolarScreen(rootGo.transform, "SysCheck_Center", 0f, radius, eyeZ, baseY, tiltX, 0.42f, 0.42f, frameMat, screenMat);
            Transform leftScreen = CreatePolarScreen(rootGo.transform, "SysCheck_Left", -40f, radius, eyeZ, baseY, tiltX, 0.2f, 0.24f, frameMat, screenMat);
            Transform rightScreen = CreatePolarScreen(rootGo.transform, "SysCheck_Right", 40f, radius, eyeZ, baseY, tiltX, 0.2f, 0.24f, frameMat, screenMat);

            // Red accent bar under the center screen.
            Material redAccentMat = MakeEmissiveMat(new Color(0.1f, 0.01f, 0.01f), new Color(1f, 0.15f, 0.1f));
            CreatePolarScreen(rootGo.transform, "SysCheck_AccentBar", 0f, radius - 0.005f, eyeZ, baseY - 0.24f, tiltX, 0.36f, 0.015f, frameMat, redAccentMat);

            // Vertical tick-meter ladders just outside the aux screens (world-space, purely decorative).
            BuildTickLadder("SysCheck_TickLadder_L", interior, new Vector3(-0.52f, baseY, eyeZ + radius * 0.7f), frameMat);
            BuildTickLadder("SysCheck_TickLadder_R", interior, new Vector3(0.52f, baseY, eyeZ + radius * 0.7f), frameMat);

            // --- Center screen UI (FrontDisplay - main tactical HUD): title, radial tick
            //     dial, SPEED/STATUS readout (replaces the old WeaponName/AmmoReadout -
            //     that readout now lives on SysCheck_Right's own WEAPON screen instead,
            //     see BuildWeaponScreenUI), a center reticle, and the relocated head-cam
            //     inset (per request: "기존 HeadCam 기능은 삭제하지 말고 FrontDisplay
            //     중앙 전술 HUD의 한쪽 구석에 작은 인셋 영상으로 옮겨줘"). ---
            Canvas centerCanvas = CreateWorldCanvas("SysCheck_Canvas", centerScreen, new Vector2(760, 760), 0.00054f);
            CreateUIImage("Backing", centerCanvas.transform, Vector2.zero, new Vector2(760, 760), new Color(0.02f, 0.03f, 0.08f, 0.75f));
            CreateUIText("Title", centerCanvas.transform, new Vector2(0, 320), new Vector2(600, 50), 30, TextAnchor.MiddleCenter, new Color(0.75f, 0.85f, 1f), "SYSTEM CHECK");
            CreateUIText("Subtitle", centerCanvas.transform, new Vector2(0, 280), new Vector2(600, 26), 15, TextAnchor.MiddleCenter, new Color(0.55f, 0.7f, 0.95f), "- BOOT CONFIGURATION -");
            BuildTickRing(centerCanvas.transform, new Vector2(0, 10), 230f, 48);
            CreateUIText("PilotLabel", centerCanvas.transform, new Vector2(0, 130), new Vector2(500, 24), 14, TextAnchor.MiddleCenter, new Color(0.6f, 0.75f, 0.95f), "PILOT: ---");
            speedText = CreateUIText("SpeedReadout", centerCanvas.transform, new Vector2(0, 20), new Vector2(500, 40), 26, TextAnchor.MiddleCenter, new Color(0.85f, 0.95f, 1f), "SPEED 0.0 m/s");
            statusText = CreateUIText("StatusReadout", centerCanvas.transform, new Vector2(0, -40), new Vector2(500, 30), 20, TextAnchor.MiddleCenter, new Color(0.3f, 1f, 0.4f), "STATUS: NORMAL");
            // Per report ("해드발칸이 몇발 남았는지 내앞에 디스플래이에 표시가
            // 안됨"): RightDisplay's WEAPON screen (CockpitWeaponHUD, see
            // BuildWeaponScreenUI below) already shows Head Vulcan's ammo, but
            // it sits 40 degrees off to the side - this duplicates a compact
            // version directly on FrontDisplay/SysCheck_Center, the screen
            // straight ahead of the pilot, so it's visible without looking
            // away while aiming with the head. Placed just under StatusReadout
            // (y=-40) and well clear of the tick ring (BuildTickRing above,
            // radius 230 centered at y=10, so its bottom edge is ~y=-220).
            // Two lines now (ammo/state + right-thumb bend readout, see
            // CockpitHUD) - taller box, centered a bit lower to match.
            vulcanAmmoText = CreateUIText("VulcanAmmoReadout", centerCanvas.transform, new Vector2(0, -92), new Vector2(600, 52), 16, TextAnchor.MiddleCenter, new Color(1f, 0.7f, 0.3f), "HEAD VULCAN AMMO 60/60 [READY]\nTHUMB --");
            telemetryText = CreateUIText("Telemetry", centerCanvas.transform, new Vector2(0, -300), new Vector2(700, 70), 13, TextAnchor.UpperCenter, new Color(0.45f, 0.9f, 1f), "");

            BuildReticle(centerCanvas.transform);

            // Head-cam inset - only if the FBX/head-cam RenderTexture pipeline is
            // available (same guard the old full-screen version used); the pipeline
            // itself (GundamHeadCam360/PlaceExternalGundam's camera + RenderTexture,
            // and BuildHeadCamScreenUI below) is completely untouched, this just adds
            // a second, smaller RawImage consumer of the same texture.
            if (headCamTex != null)
            {
                BuildHeadCamInsetUI(centerCanvas.transform, headCamTex);
            }

            // --- Left screen (SysCheck_Left) is now the GUNDAM STATUS screen -
            //     the head-cam feed that used to live here has been relocated to
            //     the inset above; its own creation/rendering pipeline is untouched. ---
            statusHUD = BuildStatusScreenUI(leftScreen, hudManager);

            // --- Right screen (SysCheck_Right) is now the WEAPON screen. ---
            weaponHUD = BuildWeaponScreenUI(rightScreen);
        }

        /// <summary>Center-screen crosshair reticle for FrontDisplay's tactical HUD - two
        /// thin crossed bars centered on the same point BuildTickRing's dial surrounds.
        /// Purely visual, no live data (per request: "간단한 중앙 조준점").</summary>
        static void BuildReticle(Transform canvasParent)
        {
            CreateUIImage("Reticle_H", canvasParent, new Vector2(0, 10), new Vector2(50, 3), new Color(0.6f, 1f, 0.7f, 0.85f));
            CreateUIImage("Reticle_V", canvasParent, new Vector2(0, 10), new Vector2(3, 50), new Color(0.6f, 1f, 0.7f, 0.85f));
        }

        /// <summary>Small corner inset on FrontDisplay showing the Gundam's head-cam feed -
        /// relocated here per request from its old full-screen slot on SysCheck_Left. Reuses
        /// the SAME RenderTexture that GundamHeadCam360/PlaceExternalGundam's head camera
        /// already renders into every frame - no new camera or RenderTexture is created here,
        /// this is just another RawImage sampling that existing texture, anchored to the
        /// canvas's own top-right corner instead of filling a whole screen.</summary>
        static void BuildHeadCamInsetUI(Transform canvasParent, RenderTexture tex)
        {
            GameObject frameGo = new GameObject("HeadCamInset");
            RectTransform frameRect = frameGo.AddComponent<RectTransform>();
            frameGo.transform.SetParent(canvasParent, false);
            frameRect.anchorMin = frameRect.anchorMax = frameRect.pivot = new Vector2(1f, 1f);
            frameRect.anchoredPosition = new Vector2(-20f, -20f);
            frameRect.sizeDelta = new Vector2(200f, 200f);

            Image backing = frameGo.AddComponent<Image>();
            backing.color = new Color(0.02f, 0.03f, 0.08f, 0.85f);

            GameObject labelGo = new GameObject("Label");
            RectTransform labelRect = labelGo.AddComponent<RectTransform>();
            labelGo.transform.SetParent(frameGo.transform, false);
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.pivot = new Vector2(0.5f, 1f);
            labelRect.anchoredPosition = new Vector2(0f, -4f);
            labelRect.sizeDelta = new Vector2(0f, 18f);
            Text label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 11;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = new Color(0.6f, 0.75f, 0.95f);
            label.text = "HEAD CAM";

            GameObject feedGo = new GameObject("Feed");
            RectTransform feedRect = feedGo.AddComponent<RectTransform>();
            feedGo.transform.SetParent(frameGo.transform, false);
            feedRect.anchorMin = Vector2.zero;
            feedRect.anchorMax = Vector2.one;
            feedRect.pivot = new Vector2(0.5f, 0.5f);
            feedRect.anchoredPosition = new Vector2(0f, -10f);
            feedRect.sizeDelta = new Vector2(-16f, -34f);

            RawImage feed = feedGo.AddComponent<RawImage>();
            feed.texture = tex;
        }

        /// <summary>One gauge row (background bar + colored fill bar + overlaid label/value
        /// text) for SysCheck_Left's 9-row STATUS screen. Returns the fill Image + Text pair
        /// that CockpitStatusHUD reads every frame via its own GaugeRefs - this method only
        /// builds the UI, CockpitStatusHUD (added by BuildStatusScreenUI) does the rendering.</summary>
        static CockpitStatusHUD.GaugeRefs BuildGaugeRow(Transform canvasParent, float y, string label)
        {
            const float barWidth = 300f;
            const float barHeight = 22f;

            CreateUIImage($"{label}_Bg", canvasParent, new Vector2(0, y), new Vector2(barWidth, barHeight), new Color(0.08f, 0.1f, 0.16f, 0.9f));

            GameObject fillGo = new GameObject($"{label}_Fill");
            RectTransform fillRect = fillGo.AddComponent<RectTransform>();
            fillGo.transform.SetParent(canvasParent, false);
            fillRect.anchorMin = new Vector2(0.5f, 0.5f);
            fillRect.anchorMax = new Vector2(0.5f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(-barWidth * 0.5f, y);
            fillRect.sizeDelta = new Vector2(barWidth, barHeight);

            Image fill = fillGo.AddComponent<Image>();
            fill.color = new Color(0.3f, 1f, 0.4f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 1f;

            Text text = CreateUIText($"{label}_Text", canvasParent, new Vector2(0, y), new Vector2(barWidth, barHeight), 13,
                TextAnchor.MiddleCenter, Color.white, $"{label} 100%");

            return new CockpitStatusHUD.GaugeRefs { fill = fill, text = text };
        }

        /// <summary>SysCheck_Left - GUNDAM STATUS screen. Replaces the old head-cam feed that
        /// used to live here (moved to a small inset on SysCheck_Center instead - see
        /// BuildHeadCamInsetUI) with 9 gauge rows: HP, SHIELD, ENERGY, then HEAD/BODY/
        /// LEFT ARM/RIGHT ARM/LEFT LEG/RIGHT LEG. Builds the canvas + gauge rows and wires
        /// them into a new CockpitStatusHUD component, which does the actual per-frame
        /// rendering from the shared CockpitHUDManager.Vitals passed in.</summary>
        static CockpitStatusHUD BuildStatusScreenUI(Transform screen, CockpitHUDManager hudManager)
        {
            Canvas canvas = CreateWorldCanvas("Status_Canvas", screen, new Vector2(380, 440), 0.00047f);
            CreateUIImage("Backing", canvas.transform, Vector2.zero, new Vector2(380, 440), new Color(0.02f, 0.03f, 0.08f, 0.7f));
            CreateUIText("Label", canvas.transform, new Vector2(0, 195), new Vector2(340, 26), 16, TextAnchor.MiddleCenter, new Color(0.6f, 0.75f, 0.95f), "GUNDAM STATUS");

            float y = 155f;
            const float step = 34f;
            CockpitStatusHUD.GaugeRefs hpRow = BuildGaugeRow(canvas.transform, y, "HP"); y -= step;
            CockpitStatusHUD.GaugeRefs shieldRow = BuildGaugeRow(canvas.transform, y, "SHIELD"); y -= step;
            CockpitStatusHUD.GaugeRefs energyRow = BuildGaugeRow(canvas.transform, y, "ENERGY"); y -= step;
            CockpitStatusHUD.GaugeRefs headRow = BuildGaugeRow(canvas.transform, y, "HEAD"); y -= step;
            CockpitStatusHUD.GaugeRefs bodyRow = BuildGaugeRow(canvas.transform, y, "BODY"); y -= step;
            CockpitStatusHUD.GaugeRefs lArmRow = BuildGaugeRow(canvas.transform, y, "L ARM"); y -= step;
            CockpitStatusHUD.GaugeRefs rArmRow = BuildGaugeRow(canvas.transform, y, "R ARM"); y -= step;
            CockpitStatusHUD.GaugeRefs lLegRow = BuildGaugeRow(canvas.transform, y, "L LEG"); y -= step;
            CockpitStatusHUD.GaugeRefs rLegRow = BuildGaugeRow(canvas.transform, y, "R LEG");

            GameObject hudGo = new GameObject("StatusHUD");
            hudGo.transform.SetParent(screen, false);
            CockpitStatusHUD hud = hudGo.AddComponent<CockpitStatusHUD>();
            hud.hudManager = hudManager;
            hud.hpGauge = hpRow;
            hud.shieldGauge = shieldRow;
            hud.energyGauge = energyRow;
            hud.headGauge = headRow;
            hud.bodyGauge = bodyRow;
            hud.leftArmGauge = lArmRow;
            hud.rightArmGauge = rArmRow;
            hud.leftLegGauge = lLegRow;
            hud.rightLegGauge = rLegRow;
            return hud;
        }

        /// <summary>SysCheck_Right - WEAPON screen. Shows current weapon name, ammo count,
        /// an ammo gauge, and READY/EMPTY/RELOADING, wired to the real HeadVulcanController
        /// via a new CockpitWeaponHUD component. headVulcan itself is left null here and
        /// wired later, once HeadVulcanController is created (see the existing Head Vulcan
        /// wiring block, which now also sets weaponHUD.headVulcan).</summary>
        static CockpitWeaponHUD BuildWeaponScreenUI(Transform screen)
        {
            Canvas canvas = CreateWorldCanvas("Weapon_Canvas", screen, new Vector2(380, 440), 0.00047f);
            CreateUIImage("Backing", canvas.transform, Vector2.zero, new Vector2(380, 440), new Color(0.02f, 0.03f, 0.08f, 0.7f));
            CreateUIText("Label", canvas.transform, new Vector2(0, 195), new Vector2(340, 26), 16, TextAnchor.MiddleCenter, new Color(0.6f, 0.75f, 0.95f), "WEAPON");

            Text nameText = CreateUIText("WeaponName", canvas.transform, new Vector2(0, 130), new Vector2(340, 34), 22, TextAnchor.MiddleCenter, new Color(0.85f, 0.95f, 1f), "HEAD VULCAN");
            Text ammoText = CreateUIText("AmmoReadout", canvas.transform, new Vector2(0, 80), new Vector2(340, 26), 18, TextAnchor.MiddleCenter, new Color(1f, 0.7f, 0.3f), "AMMO 060/060");

            CreateUIImage("AmmoGauge_Bg", canvas.transform, new Vector2(0, 30), new Vector2(300, 22), new Color(0.08f, 0.1f, 0.16f, 0.9f));
            GameObject fillGo = new GameObject("AmmoGauge_Fill");
            RectTransform fillRect = fillGo.AddComponent<RectTransform>();
            fillGo.transform.SetParent(canvas.transform, false);
            fillRect.anchorMin = new Vector2(0.5f, 0.5f);
            fillRect.anchorMax = new Vector2(0.5f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(-150f, 30f);
            fillRect.sizeDelta = new Vector2(300f, 22f);
            Image ammoFill = fillGo.AddComponent<Image>();
            ammoFill.color = new Color(0.3f, 1f, 0.4f);
            ammoFill.type = Image.Type.Filled;
            ammoFill.fillMethod = Image.FillMethod.Horizontal;
            ammoFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            ammoFill.fillAmount = 1f;

            Text stateText = CreateUIText("StateReadout", canvas.transform, new Vector2(0, -20), new Vector2(340, 34), 24, TextAnchor.MiddleCenter, new Color(0.3f, 1f, 0.4f), "READY");

            GameObject hudGo = new GameObject("WeaponHUD");
            hudGo.transform.SetParent(screen, false);
            CockpitWeaponHUD hud = hudGo.AddComponent<CockpitWeaponHUD>();
            hud.nameText = nameText;
            hud.ammoText = ammoText;
            hud.ammoFillImage = ammoFill;
            hud.stateText = stateText;
            return hud;
        }

        /// <summary>Left aux screen content when the Gundam's head camera feed is available:
        /// a label plus a live RawImage of the head-cam RenderTexture (see PlaceExternalGundam).
        /// No longer called by BuildSystemCheckDisplay (the head-cam feed now lives in a small
        /// inset on the center screen instead - see BuildHeadCamInsetUI), but kept exactly as-is
        /// per instruction not to delete the existing head-cam UI/rendering functionality.</summary>
        static void BuildHeadCamScreenUI(Transform screen, RenderTexture tex)
        {
            Canvas canvas = CreateWorldCanvas("HeadCam_Canvas", screen, new Vector2(380, 440), 0.00047f);
            CreateUIImage("Backing", canvas.transform, Vector2.zero, new Vector2(380, 440), new Color(0.02f, 0.03f, 0.08f, 0.7f));
            CreateUIText("Label", canvas.transform, new Vector2(0, 190), new Vector2(300, 30), 14, TextAnchor.MiddleCenter, new Color(0.6f, 0.75f, 0.95f), "HEAD CAM");

            GameObject feedGo = new GameObject("Feed");
            RectTransform feedRect = feedGo.AddComponent<RectTransform>();
            feedGo.transform.SetParent(canvas.transform, false);
            feedRect.anchorMin = feedRect.anchorMax = new Vector2(0.5f, 0.5f);
            feedRect.pivot = new Vector2(0.5f, 0.5f);
            feedRect.anchoredPosition = new Vector2(0, -20);
            feedRect.sizeDelta = new Vector2(340, 340);

            RawImage feed = feedGo.AddComponent<RawImage>();
            feed.texture = tex;
        }

        /// <summary>A flat display panel (with a slightly larger backing frame) positioned on
        /// the polar arc around the pilot's eye point, tilted to face them. Returns the
        /// inner "screen" transform (the emissive face) to anchor a Canvas or further children on.</summary>
        static Transform CreatePolarScreen(Transform parent, string name, float angleDeg, float radius, float eyeZ, float baseY,
            float tiltX, float width, float height, Material frameMat, Material screenMat)
        {
            float angleRad = angleDeg * Mathf.Deg2Rad;
            float x = radius * Mathf.Sin(angleRad);
            float z = eyeZ + radius * Mathf.Cos(angleRad);

            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(x, baseY, z);
            root.transform.localRotation = Quaternion.Euler(tiltX, angleDeg, 0f);

            CreateCube(name + "_Frame", root.transform, Vector3.zero, new Vector3(width + 0.06f, height + 0.06f, 0.03f), frameMat);
            GameObject screen = CreateCube(name + "_Screen", root.transform, new Vector3(0, 0, -0.02f), new Vector3(width, height, 0.02f), screenMat);
            return screen.transform;
        }

        /// <summary>A column of short tick marks (like a fuel/pressure ladder gauge) bolted
        /// to the frame at the far edge of the cockpit - purely decorative, no live data.</summary>
        static void BuildTickLadder(string name, Transform parent, Vector3 centerPos, Material mat)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = centerPos;

            const int count = 14;
            const float spacing = 0.09f;
            float startY = -(count - 1) * spacing * 0.5f;
            for (int i = 0; i < count; i++)
            {
                float len = (i % 3 == 0) ? 0.09f : 0.05f;
                CreateCube($"{name}_Tick_{i}", root.transform, new Vector3(0, startY + i * spacing, 0), new Vector3(len, 0.012f, 0.012f), mat);
            }
        }

        /// <summary>A World Space Canvas anchored just in front of a display panel's screen face.
        ///
        /// FIX for the long-standing "디스플레이가 파랗기만 해 / 글자가 전혀 안 보임" and
        /// "디스플레이 3개 있는곳에 발칸 총알 개수가 안나옴" reports - root cause confirmed
        /// by inspecting the built GundamCockpit scene in the Editor: every caller passes
        /// the SCALED screen cube from CreatePolarScreen (e.g. 0.42 x 0.42 x 0.02) as the
        /// anchor, and this used to parent the canvas directly UNDER that cube at local
        /// z = -0.015. In the cube's scaled local space that is only 0.3 mm from the
        /// cube's center, while its front face is 10 mm away - so every canvas sat
        /// INSIDE the opaque screen cube and was completely hidden (solid blue screen,
        /// zero text). It also inherited the cube's non-uniform scale, squashing the
        /// side screens' canvases to 3.6 cm wide.
        ///
        /// Now the canvas is parented to the cube's own (unscaled) parent - the panel
        /// root CreatePolarScreen builds - and placed 2 mm in front of the cube's real
        /// front face, with the cube's rotation. worldScale was always sized for an
        /// unscaled parent (760 x 0.00054 = 0.41 m on the 0.42 m center screen, 380 x
        /// 0.00047 = 0.18 m on the 0.20 m side screens), so the UI now fills each screen
        /// as originally intended. Falls back to the old behavior if the anchor somehow
        /// has no parent.</summary>
        static Canvas CreateWorldCanvas(string name, Transform anchor, Vector2 sizeDelta, float worldScale)
        {
            GameObject canvasGo = new GameObject(name);
            RectTransform canvasRect = canvasGo.AddComponent<RectTransform>();
            Transform panelRoot = anchor.parent;
            if (panelRoot != null)
            {
                canvasGo.transform.SetParent(panelRoot, false);
                float frontFaceOffset = anchor.localScale.z * 0.5f + 0.002f;
                canvasGo.transform.localPosition = anchor.localPosition + anchor.localRotation * new Vector3(0f, 0f, -frontFaceOffset);
                canvasGo.transform.localRotation = anchor.localRotation;
            }
            else
            {
                canvasGo.transform.SetParent(anchor, false);
                canvasGo.transform.localPosition = new Vector3(0, 0, -0.015f);
                canvasGo.transform.localRotation = Quaternion.identity;
            }
            canvasGo.transform.localScale = Vector3.one * worldScale;
            canvasRect.sizeDelta = sizeDelta;

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<CanvasScaler>();
            return canvas;
        }

        /// <summary>Cockpit Calibration's step-by-step prompt - plain WORLD-FIXED
        /// floating text mounted to the cockpit interior, with NO physical panel,
        /// frame, or screen prop of any kind.
        ///
        /// This went through two earlier versions, both reported as bugs:
        ///   1. A HEAD-LOCKED Canvas parented directly to the player's view camera -
        ///      being opaque and dead-center, it followed the player's gaze
        ///      everywhere, including straight down at the joysticks whenever they
        ///      leaned in to grab one, visually covering them ("LeftJoystick/
        ///      RightJoystick 조종간 자체가 화면에 보이지 않는다").
        ///   2. A world-fixed version that used CreatePolarScreen to build a
        ///      physical frame+screen prop, the same way BuildSystemCheckDisplay
        ///      builds its dashboard screens - this fixed the occlusion, but read as
        ///      an odd extra console screen that didn't belong there ("새로 이상한
        ///      디스플레이 만든거 지우고").
        /// This version drops the physical prop entirely: just a small floating
        /// Text with an Outline component for contrast (no backing Image, no frame,
        /// no screen cube), mounted high and slightly back (baseY/z below) so it
        /// stays out of the way of the joysticks and the SystemCheckDisplay cluster
        /// alike, and reads as an on-screen prompt rather than a piece of hardware.
        ///
        /// Hidden automatically by CalibrationManager once calibration completes -
        /// see that script.</summary>
        static Text BuildCalibrationPromptUI(Transform interior)
        {
            GameObject canvasGo = new GameObject("CalibrationPrompt_Canvas");
            canvasGo.transform.SetParent(interior, false);
            canvasGo.transform.localPosition = new Vector3(0f, 1.25f, 0.32f); // above the joysticks (y=0.87) and the display cluster (y=0.92); z between the joysticks (0.18) and the displays (~0.48)
            canvasGo.transform.localRotation = Quaternion.identity; // same no-yaw convention CreateWorldCanvas/CreatePolarScreen(angleDeg:0) already rely on to face the pilot
            canvasGo.transform.localScale = Vector3.one * 0.0009f;

            RectTransform canvasRect = canvasGo.AddComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(560, 160);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<CanvasScaler>();

            Text prompt = CreateUIText("PromptText", canvasGo.transform, Vector2.zero, new Vector2(540, 140), 26,
                TextAnchor.MiddleCenter, new Color(0.85f, 0.95f, 1f), "COCKPIT CALIBRATION");
            Outline outline = prompt.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(2f, -2f);

            return prompt;
        }

        /// <summary>Physical "press to confirm" button used to end Cockpit
        /// Calibration and start the game, per request ("확인을 누르면 게임이
        /// 시작되는거고"). A simple touch-sensitive sphere - same grip-ball visual
        /// language as the joysticks' own GripBall - rather than a full XR
        /// Interaction Toolkit interactable: CalibrationManager's
        /// IsHandPressingConfirm treats either hand's palm coming within
        /// confirmPressRadius of this root Transform's position as a "press", using
        /// only HandJointTracker's existing public PalmPosition (no changes to
        /// hand-tracking code).
        ///
        /// Returns the ROOT of a small object group (button sphere + a "확인"
        /// label floating just above it) rather than the sphere itself, so
        /// CalibrationManager can show/hide both together with one
        /// GameObject.SetActive call while still reading the root's position as
        /// the button's location (the sphere sits at the root's local origin).
        ///
        /// Starts inactive - GundamCockpitSetup only builds it, CalibrationManager
        /// is what shows it (only during its WaitingForConfirm state, once both
        /// joysticks have been placed) and hides it again once pressed.</summary>
        static Transform BuildCalibrationConfirmButton(Transform interior, Material mat)
        {
            GameObject root = new GameObject("CalibrationConfirmButton");
            root.transform.SetParent(interior, false);
            root.transform.localPosition = new Vector3(0f, 1.0f, 0.28f); // centered between the two joysticks (x=+-0.22, y=0.87, z=0.18), just above/forward of them - an easy, unambiguous reach for either hand

            CreateSphere("CalibrationConfirmButton_Ball", root.transform, Vector3.zero, new Vector3(0.07f, 0.07f, 0.07f), mat);

            GameObject canvasGo = new GameObject("CalibrationConfirmButton_Label");
            canvasGo.transform.SetParent(root.transform, false);
            canvasGo.transform.localPosition = new Vector3(0f, 0.09f, 0f); // just above the 0.07m ball
            canvasGo.transform.localRotation = Quaternion.identity;
            canvasGo.transform.localScale = Vector3.one * 0.0007f;

            RectTransform canvasRect = canvasGo.AddComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(220, 90);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<CanvasScaler>();

            Text label = CreateUIText("Text", canvasGo.transform, Vector2.zero, new Vector2(200, 70), 30,
                TextAnchor.MiddleCenter, new Color(0.85f, 1f, 0.85f), "확인");
            Outline labelOutline = label.gameObject.AddComponent<Outline>();
            labelOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            labelOutline.effectDistance = new Vector2(2f, -2f);

            root.SetActive(false); // shown only by CalibrationManager, during its WaitingForConfirm state
            return root.transform;
        }

        /// <summary>A ring of radial tick marks (like the reference's circular dial),
        /// with every 6th tick drawn longer/brighter as a "major" mark.</summary>
        static void BuildTickRing(Transform parent, Vector2 center, float radius, int count)
        {
            GameObject ringGo = new GameObject("TickRing");
            RectTransform ringRect = ringGo.AddComponent<RectTransform>();
            ringGo.transform.SetParent(parent, false);
            ringRect.anchorMin = ringRect.anchorMax = new Vector2(0.5f, 0.5f);
            ringRect.pivot = new Vector2(0.5f, 0.5f);
            ringRect.anchoredPosition = center;
            ringRect.sizeDelta = Vector2.zero;

            for (int i = 0; i < count; i++)
            {
                float angle = (360f / count) * i;
                float rad = angle * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
                bool major = (i % 6 == 0);

                GameObject tick = new GameObject($"Tick_{i}");
                RectTransform tickRect = tick.AddComponent<RectTransform>();
                tick.transform.SetParent(ringGo.transform, false);
                tickRect.anchorMin = tickRect.anchorMax = new Vector2(0.5f, 0.5f);
                tickRect.pivot = new Vector2(0.5f, 0f);
                tickRect.sizeDelta = new Vector2(major ? 5f : 3f, major ? 30f : 16f);
                tickRect.anchoredPosition = dir * radius;
                tickRect.localRotation = Quaternion.Euler(0f, 0f, -angle);

                Image img = tick.AddComponent<Image>();
                img.color = major ? new Color(0.75f, 0.9f, 1f, 0.95f) : new Color(0.45f, 0.65f, 0.9f, 0.7f);
            }
        }

        // ---------------------------------------------------------------
        // Orbit/HUD ring — a big floating compass-style reticle made of
        // real 3D tick marks (the same "ring of ticks" idea as BuildTickRing
        // above, per the same reference style), but built in world space
        // instead of on a flat UI Canvas so it floats directly in the
        // pilot's forward view, overlaying the outside seen through the
        // transparent Cockpit_Dome, rather than being confined to a small
        // console monitor. A few ticks are drawn as brighter yellow "gate"
        // markers (paired angled bars) standing in for the reference's
        // trajectory/waypoint marks. Static/decorative for now - no live
        // orbit or trajectory data is computed, same as the console's dial.
        // ---------------------------------------------------------------
        static Transform BuildOrbitHUD(Transform interior, Material lineMat, Material gateMat)
        {
            GameObject hudRoot = new GameObject("OrbitHUD");
            hudRoot.transform.SetParent(interior, false);

            // Centered on the forward view: well above/past the low
            // SystemCheckDisplay console (baseY 0.92, radius 0.55) and well
            // inside the enclosing Cockpit_Dome (half-extents in the
            // several-meter range), so this reads as a big HUD ring
            // floating over the outside view rather than overlapping
            // either of those.
            const float centerY = 1.45f;
            const float centerZ = 2.4f;
            const float ringRadius = 1.5f;
            hudRoot.transform.localPosition = new Vector3(0f, centerY, centerZ);

            const int tickCount = 60;   // every 6 degrees
            const int gateEvery = 15;   // 4 gate markers spaced evenly around the ring

            for (int i = 0; i < tickCount; i++)
            {
                float angle = i * (360f / tickCount);
                float rad = angle * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(rad), Mathf.Cos(rad), 0f);

                if (i % gateEvery == 0)
                {
                    BuildOrbitGateMark(hudRoot.transform, dir * ringRadius, angle, gateMat);
                }
                else
                {
                    bool major = (i % 5 == 0);
                    GameObject tick = CreateCube($"OrbitHUD_Tick_{i}", hudRoot.transform, dir * ringRadius,
                        new Vector3(0.02f, major ? 0.16f : 0.09f, 0.02f), lineMat);
                    tick.transform.localRotation = Quaternion.Euler(0f, 0f, -angle);
                    StripCollider(tick);
                }
            }

            return hudRoot.transform;
        }

        /// <summary>A paired-diagonal-bar "gate" marker on the OrbitHUD ring, standing in for
        /// a trajectory/waypoint tick (the reference image's brighter angled double marks).</summary>
        static void BuildOrbitGateMark(Transform parent, Vector3 pos, float angle, Material mat)
        {
            GameObject gate = new GameObject("OrbitHUD_Gate");
            gate.transform.SetParent(parent, false);
            gate.transform.localPosition = pos;
            gate.transform.localRotation = Quaternion.Euler(0f, 0f, -angle);

            GameObject barA = CreateCube("Bar_A", gate.transform, new Vector3(-0.035f, 0f, 0f), new Vector3(0.022f, 0.22f, 0.022f), mat);
            barA.transform.localRotation = Quaternion.Euler(0f, 0f, 25f);
            StripCollider(barA);

            GameObject barB = CreateCube("Bar_B", gate.transform, new Vector3(0.035f, 0f, 0f), new Vector3(0.022f, 0.22f, 0.022f), mat);
            barB.transform.localRotation = Quaternion.Euler(0f, 0f, 25f);
            StripCollider(barB);
        }

        /// <summary>World-space height (m) of a model's ACTUAL vertices, at its current
        /// transform. SkinnedMeshRenderer.bounds is a loose padded box whose padding
        /// differs per model, so it can't be used to make two models the same size -
        /// this bakes each skinned mesh (BakeMesh output already carries the world
        /// scale) and places the vertices with the renderer's position+rotation only
        /// (verified in the Editor: linear with the root's localScale, e.g. Gundam
        /// 0.9855 at x1 -> 9.855 at x10). Also includes plain MeshFilters.
        /// Returns 0 if nothing measurable is found.</summary>
        static float MeasureMeshHeight(GameObject root)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                UnityEngine.Mesh baked = new UnityEngine.Mesh();
                smr.BakeMesh(baked, false);
                // BakeMesh already applies the renderer's world scale, so only
                // position+rotation are applied here (localToWorldMatrix would
                // count the scale twice whenever lossyScale != 1).
                Matrix4x4 l2w = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (Vector3 v in baked.vertices)
                {
                    float y = l2w.MultiplyPoint3x4(v).y;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                UnityEngine.Object.DestroyImmediate(baked);
            }
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 l2w = mf.transform.localToWorldMatrix;
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    float y = l2w.MultiplyPoint3x4(v).y;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxY > minY ? maxY - minY : 0f;
        }

        static void StripCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
        }

        /// <summary>Simple schematic-style filler for the two smaller flanking screens - a
        /// label plus a few thin bars, standing in for the reference's wireframe diagrams
        /// without needing imported art. Static, no live data.</summary>
        static void BuildAuxScreenUI(Transform screen, string label)
        {
            Canvas canvas = CreateWorldCanvas(label + "_Canvas", screen, new Vector2(380, 440), 0.00047f);
            CreateUIImage("Backing", canvas.transform, Vector2.zero, new Vector2(380, 440), new Color(0.02f, 0.03f, 0.08f, 0.7f));
            CreateUIText("Label", canvas.transform, new Vector2(0, 190), new Vector2(300, 30), 14, TextAnchor.MiddleCenter, new Color(0.6f, 0.75f, 0.95f), label);

            for (int i = 0; i < 6; i++)
            {
                float y = 120f - i * 40f;
                float w = 260f - (i % 3) * 60f;
                CreateUIImage($"Line_{i}", canvas.transform, new Vector2(0, y), new Vector2(w, 6f), new Color(0.35f, 0.55f, 0.85f, 0.5f));
            }
        }

        static Text CreateUIText(string name, Transform parent, Vector2 anchoredPos, Vector2 size, int fontSize,
            TextAnchor align, Color color, string content)
        {
            GameObject go = new GameObject(name);
            RectTransform rect = go.AddComponent<RectTransform>();
            go.transform.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = color;
            text.text = content;
            return text;
        }

        static Image CreateUIImage(string name, Transform parent, Vector2 anchoredPos, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name);
            RectTransform rect = go.AddComponent<RectTransform>();
            go.transform.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            Image img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        static HandJointTracker CreateHandTracker(string name, Handedness handedness)
        {
            GameObject go = new GameObject(name);
            XRHandTrackingEvents events = go.AddComponent<XRHandTrackingEvents>();
            HandJointTracker tracker = go.AddComponent<HandJointTracker>();
            tracker.handedness = handedness;
            events.handedness = handedness;
            return tracker;
        }

        // ---------------------------------------------------------------
        // Real XR hand visuals - per request ("Galaxy XR 실제 손 추적 데이터를
        // 이용한 '보이는 XR 손'", "21개 관절을 구형 오브젝트로 표시하는 디버그
        // 방식은 사용하지 마", "컨트롤러 모델이 게임 안에 나타나는 것도 원하지
        // 않는다"). Uses the XR Hands package's OWN official "HandVisualizer"
        // sample component rather than anything custom-built here - it
        // already does exactly this: drives a real rigged/skinned hand MESH
        // (SkinnedMeshRenderer + XRHandSkeletonDriver bone-by-bone) from
        // live XRHandSubsystem joint data, shows it only while that hand is
        // actually tracked, and hides it the instant tracking is lost (see
        // HandVisualizer.cs's OnTrackingAcquired/OnTrackingLost). Nothing
        // about this needs a controller - it reads XRHandSubsystem directly,
        // completely separate from any XRController/interaction-profile
        // input path.
        //
        // IMPORTANT (found by directly inspecting the imported prefab's own
        // source file, after "hands still don't show" was reported again):
        // the "XR Origin Hands (XR Rig)" prefab from the "Hands Interaction
        // Demo" sample - the one CreateXROrigin actually instantiates -
        // ALREADY ships with its own "Hand Visualizer" child object, and it's
        // already fully wired: drawMeshes=true, m_AndroidXRLeftHandMesh/
        // m_AndroidXRRightHandMesh already pointing at real rigged meshes.
        // Adding a SECOND HandVisualizer here (the old behavior) just
        // duplicated it - two independent hand renderers, parented in two
        // different places - which was never going to fix "hands don't show"
        // and could only add confusion (or a second, differently-positioned
        // set of hand meshes). So this now only builds one from scratch as a
        // fallback, if the instantiated rig does NOT already have one (e.g.
        // the minimal placeholder rig CreateXROrigin falls back to when the
        // sample isn't imported yet).
        //
        // debugDrawJoints is left OFF - only the mesh (drawMeshes) is shown.
        // The component still wants non-null debugDrawPrefab/velocityPrefab
        // references even with those features off (it's part of its fixed
        // internal wiring), but with debugDrawJoints=false and
        // velocityType=None those objects are created disabled and never
        // rendered - no spheres ever appear.
        // ---------------------------------------------------------------
        static void BuildHandVisualizer(GameObject xrOrigin, HandJointTracker leftHandTracker, HandJointTracker rightHandTracker)
        {
            HandVisualizer existingVisualizer = xrOrigin.GetComponentInChildren<HandVisualizer>(true);
            if (existingVisualizer != null)
            {
                Debug.Log("[Gundam] Using the XR rig's own built-in Hand Visualizer ('" + existingVisualizer.gameObject.name +
                    "') - it already ships with real Android XR hand meshes wired up (drawMeshes=true), so no duplicate " +
                    "was added. If hands still don't render on-device, the problem is upstream of this scene (hand-tracking " +
                    "data not actually reaching the app - see the log line right after this one, and check the headset's " +
                    "OWN system settings for a hands-vs-controllers input toggle, not just this project's OpenXR settings).");
            }
            else
            {
                GameObject androidLeft = AssetDatabase.LoadAssetAtPath<GameObject>(HandVisAndroidLeftMeshPath);
                GameObject androidRight = AssetDatabase.LoadAssetAtPath<GameObject>(HandVisAndroidRightMeshPath);
                GameObject fallbackLeft = AssetDatabase.LoadAssetAtPath<GameObject>(HandVisFallbackLeftMeshPath);
                GameObject fallbackRight = AssetDatabase.LoadAssetAtPath<GameObject>(HandVisFallbackRightMeshPath);
                Material handMat = AssetDatabase.LoadAssetAtPath<Material>(HandVisMaterialPath);
                GameObject jointDebugPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandVisJointPrefabPath);
                GameObject velocityPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandVisVelocityPrefabPath);

                if (androidLeft == null || androidRight == null || jointDebugPrefab == null || velocityPrefab == null)
                {
                    Debug.LogWarning("[Gundam] Could not load the XR Hands 'HandVisualizer' sample assets under '" + HandVisSampleRoot +
                        "' - real hand meshes will NOT be set up this run. In the Editor: Window > Package Manager > XR Hands > " +
                        "Samples tab > import 'HandVisualizer', then re-run Gundam > Build Cockpit Scene.");
                }
                else
                {
                    GameObject handVisGo = new GameObject("HandVisualizer");
                    // Directly under the XR Origin, same as leftHandTracker/rightHandTracker
                    // above - joint poses from XRHandSubsystem are reported in the XR
                    // Origin's own local tracking space.
                    handVisGo.transform.SetParent(xrOrigin.transform, false);
                    handVisGo.transform.localPosition = Vector3.zero;
                    handVisGo.transform.localRotation = Quaternion.identity;

                    HandVisualizer handVisualizer = handVisGo.AddComponent<HandVisualizer>();

                    // HandVisualizer's mesh/prefab/material fields are private
                    // [SerializeField]s with no public setters, so they're assigned
                    // through SerializedObject here (standard way to configure a
                    // third-party component's inspector-only fields from an editor
                    // script) rather than reflection or a custom subclass.
                    SerializedObject so = new SerializedObject(handVisualizer);
                    so.FindProperty("m_AndroidXRLeftHandMesh").objectReferenceValue = androidLeft;
                    so.FindProperty("m_AndroidXRRightHandMesh").objectReferenceValue = androidRight;
                    if (fallbackLeft != null) so.FindProperty("m_MetaQuestLeftHandMesh").objectReferenceValue = fallbackLeft;
                    if (fallbackRight != null) so.FindProperty("m_MetaQuestRightHandMesh").objectReferenceValue = fallbackRight;
                    if (handMat != null) so.FindProperty("m_HandMeshMaterial").objectReferenceValue = handMat;
                    so.FindProperty("m_DebugDrawPrefab").objectReferenceValue = jointDebugPrefab;
                    so.FindProperty("m_VelocityPrefab").objectReferenceValue = velocityPrefab;
                    so.ApplyModifiedProperties();

                    handVisualizer.drawMeshes = true;
                    handVisualizer.debugDrawJoints = false; // no visible joint spheres - real hand mesh only
                    handVisualizer.velocityType = HandVisualizer.VelocityType.None;
                }
            }

            // Minimal on-device confirmation logging (per request: "손 추적이
            // 정상적으로 들어오는지 확인할 수 있는 최소한의 로그") - attached to
            // the XR Origin itself (not the HandVisualizer, which may not
            // exist as a new object anymore - see above) so it's always
            // created regardless of which branch above ran. Runtime behavior
            // lives in its own script file, not here (this file only builds
            // scene structure). This reads OUR OWN HandJointTracker instances
            // (used by JoystickLever's grab logic), which is a completely
            // separate path from whichever HandVisualizer renders the mesh -
            // so this logger's ACQUIRED/LOST lines are the ones that tell you
            // whether the joysticks should be grabbable, independent of
            // whether the hand mesh itself is visible.
            HandTrackingStatusLogger logger = xrOrigin.AddComponent<HandTrackingStatusLogger>();
            logger.leftHandTracker = leftHandTracker;
            logger.rightHandTracker = rightHandTracker;

            // One-shot boot diagnostic - per request ("XRHandSubsystem 존재
            // 여부 / running 여부 / left/right tracked 여부... 매 프레임 로그를
            // 찍지는 마라"): logs exactly once (a few seconds after start, to
            // give OpenXR time to spin up), straight from the runtime
            // XRHandSubsystem itself rather than from any of this project's
            // own scripts - see HandSubsystemBootDiagnostics.cs for what each
            // outcome means. This is the log to check first after a device
            // run: if it says the subsystem isn't running or isn't tracking
            // either hand, that's outside this project (the headset/OS not
            // handing hand data to the app), no matter how this scene or its
            // scripts are wired.
            xrOrigin.AddComponent<HandSubsystemBootDiagnostics>();

            // Defensive - per request ("컨트롤러 모델이 게임 안에 나타나는 것도
            // 원하지 않는다"): the imported "XR Origin Hands (XR Rig)" sample
            // was checked and does NOT contain any controller mesh (only
            // hand pinch/poke/aim logic), so this is normally a no-op. It
            // guards against one appearing if the rig prefab or its
            // packages/samples are ever re-imported or changed later.
            DisableAnyControllerVisuals(xrOrigin);
        }

        static void DisableAnyControllerVisuals(GameObject root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.gameObject.name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    renderer.gameObject.SetActive(false);
                    Debug.Log("[Gundam] Disabled a controller-model visual found under the XR rig: " + renderer.gameObject.name);
                }
            }
        }

        // ---------------------------------------------------------------
        // Real XR Interaction Toolkit hand grab for the joysticks - per request
        // ("XR Interaction Toolkit의 실제 Hand Interactor/Direct Interactor를
        // 사용... 현재 프로젝트의 XR Hands 구조와 충돌하지 않는다면"). Investigated
        // first (per that request's own instructions) by inspecting the actual
        // imported "XR Origin Hands (XR Rig)" prefab
        // (Assets/Samples/XR Interaction Toolkit/3.3.2/Hands Interaction Demo/
        // Prefabs/XR Origin Hands (XR Rig).prefab) directly: it already contains
        // a "Left Hand" and a "Right Hand" GameObject, each with its own real,
        // officially-supported "Near-Far Interactor" child - reading actual
        // pinch/grip selection straight from Galaxy XR's hand tracking through
        // XRI's own tested logic (the SAME hand-tracking data source
        // HandJointTracker already reads, just via a different, more "official"
        // path). This does NOT conflict with this project's existing XR Hands
        // structure - it doesn't touch hand tracking, the Hand Visualizer, or any
        // OpenXR/XR Hands setting; it only ADDS a plain, non-physical
        // XRSimpleInteractable onto each joystick's handle (see
        // AttachHandInteractable) so those already-existing interactors have
        // something to select, and reports that selection into JoystickLever
        // (which still owns 100% of the actual tilt/twist/return math either way).
        // ---------------------------------------------------------------
        static void WireJoystickHandInteractors(GameObject xrOrigin, JoystickLever leftStick, JoystickLever rightStick)
        {
            Transform leftHandRoot = FindDeepChild(xrOrigin.transform, "Left Hand");
            Transform rightHandRoot = FindDeepChild(xrOrigin.transform, "Right Hand");

            Transform leftInteractor = leftHandRoot != null ? FindDeepChild(leftHandRoot, "Near-Far Interactor") : null;
            Transform rightInteractor = rightHandRoot != null ? FindDeepChild(rightHandRoot, "Near-Far Interactor") : null;

            if (leftHandRoot == null || rightHandRoot == null || leftInteractor == null || rightInteractor == null)
            {
                Debug.LogWarning("[Gundam] Could not find this rig's own 'Left Hand'/'Right Hand' > 'Near-Far Interactor' " +
                    "hierarchy (from the imported 'Hands Interaction Demo' sample) - the real XR Interaction Toolkit grab " +
                    "path won't be wired up this run. The existing proximity+grip/pinch fallback in JoystickLever will " +
                    "still work on its own. Make sure the 'Hands Interaction Demo' sample is imported and this rig's " +
                    "prefab still has that hierarchy, then re-run Build Cockpit Scene.");
                return;
            }

            AttachHandInteractable(leftStick, leftInteractor);
            AttachHandInteractable(rightStick, rightInteractor);

            Debug.Log("[Gundam] Wired LeftJoystick/RightJoystick to this rig's own real Near-Far Interactors " +
                "(left hand only grabs LeftJoystick, right hand only RightJoystick).");
        }

        /// <summary>Adds a plain XRSimpleInteractable (no automatic movement - JoystickLever
        /// keeps full control of the actual tilt/twist/return math) plus a
        /// HandExclusiveGrabAdapter onto the given stick's handle, restricted to only ever
        /// being selected by 'allowedInteractor'. Colliders are wired explicitly (rather
        /// than relying on XRSimpleInteractable's own auto-populate-from-children timing)
        /// to the same GripBall/finger-button spheres CreateJoystick already parents under
        /// the handle - no separate grab collider needed.</summary>
        static void AttachHandInteractable(JoystickLever lever, Transform allowedInteractor)
        {
            if (lever == null || lever.handle == null) return;

            XRSimpleInteractable interactable = lever.handle.gameObject.AddComponent<XRSimpleInteractable>();

            Collider[] colliders = lever.handle.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 0)
            {
                SerializedObject so = new SerializedObject(interactable);
                SerializedProperty collidersProp = so.FindProperty("m_Colliders");
                if (collidersProp != null)
                {
                    collidersProp.ClearArray();
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        collidersProp.InsertArrayElementAtIndex(i);
                        collidersProp.GetArrayElementAtIndex(i).objectReferenceValue = colliders[i];
                    }
                    so.ApplyModifiedProperties();
                }
            }
            else
            {
                Debug.LogWarning("[Gundam] " + lever.gameObject.name + "'s handle has no Colliders under it for the " +
                    "real XR Interaction Toolkit interactable to use - it won't be selectable that way (the proximity+" +
                    "grip/pinch fallback in JoystickLever is unaffected).");
            }

            HandExclusiveGrabAdapter adapter = lever.handle.gameObject.AddComponent<HandExclusiveGrabAdapter>();
            adapter.joystick = lever;
            adapter.onlyAllowedInteractor = allowedInteractor;
        }

        // ---------------------------------------------------------------
        // Control yoke — horizontal side-mounted grip handle reaching in
        // hovering free in space at the seat's armrest - no mechanical
        // housing/rod/collar/console anchoring it anymore. Per request
        // ("조이스틱으로 조종하는게 아니고 손으로 하는거니까 조이스틱 빼고 손으로
        // 잡을수있게해줘"): this was never actually a joystick input-wise -
        // JoystickLever always read a tracked HAND (proximity + pinch, see
        // JoystickLever.cs), never a physical stick/gamepad axis - but it
        // LOOKED like a mechanical joystick (console housing + connecting
        // arm + ratchet collar + indicator light), which read as "조종간"
        // rather than something you just reach out and grab. That static
        // decoration is removed here; the grab ball + finger buttons (the
        // only part the hand ever actually touches) stay exactly where they
        // were, at the same armrest position, so the pilot still reaches to
        // the same spot and grabs/tilts/twists it exactly as before -
        // JoystickLever's grab-and-tilt math (pivotGo/handleGo) is completely
        // untouched by this visual change.
        // ---------------------------------------------------------------
        static JoystickLever CreateJoystick(string name, Transform parent, Vector3 localPos, bool isRight,
            Material accentMat, Material buttonMat,
            HandJointTracker left, HandJointTracker right)
        {
            GameObject baseGo = new GameObject(name + "_Mount");
            baseGo.transform.SetParent(parent, false);
            baseGo.transform.localPosition = localPos;

            // --- Grip (this is what the hand actually grabs and tilts) ---
            GameObject pivotGo = new GameObject(name + "_Pivot");
            pivotGo.transform.SetParent(baseGo.transform, false);
            pivotGo.transform.localPosition = new Vector3(0f, 0.02f, 0.05f);

            GameObject handleGo = new GameObject(name + "_Handle");
            handleGo.transform.SetParent(pivotGo.transform, false);
            handleGo.transform.localPosition = Vector3.zero;

            // The visible grip ball and its buttons are all CHILDREN of
            // handleGo - JoystickLever overwrites handleGo's own
            // localRotation every frame for tilt, and children ride along
            // with that rigidly, so the whole grip tilts as one piece.
            //
            // Grip changed from a horizontal capsule bar to a sphere sized
            // to be wrapped by a whole fist (per request: "구형으로 만들어서
            // 손으로 움켜쥐고 사진과 같이 잡고", matching the attached reference
            // photos of a clenched fist around a round grip ball).
            CreateSphere(name + "_GripBall", handleGo.transform, Vector3.zero,
                new Vector3(0.10f, 0.10f, 0.10f), accentMat);

            // --- Finger buttons ON THE GRIP BALL - per request ("네모난 판이
            //     생김 저거지워 그리고 구체 조종기 유지해 구체 조종기를 잡았을때
            //     손가락 끝마디에 버튼이 있는거라고"): no finger plate; the ball
            //     is gripped with a fist as before, and each button sits on the
            //     ball's surface where that finger's last segment (끝마디) lands
            //     when the fist is closed around it. Default layout = a hand
            //     wrapping the ball from the pilot's side: palm on the back/top,
            //     the four fingertips side by side across the FRONT just below
            //     the equator (index on the thumb side, pinky lowest), thumb pad
            //     around the inner side. Mirrored per hand (the right hand's
            //     thumb side is -X, the left hand's +X). JoystickFingerButtons
            //     (added below) then moves each one, on the ball surface, to
            //     exactly under the pilot's REAL tracked fingertip segment a
            //     moment after each grab, and handles pressing (fingertip pushed
            //     in toward the ball); the RightJoystick's thumb button fires the
            //     Head Vulcan. ---
            float handX = isRight ? 1f : -1f;
            const float ballRadius = 0.05f;
            GameObject thumbButton = CreateSphere(name + "_ThumbButton", handleGo.transform,
                new Vector3(-0.85f * handX, 0.25f, 0.46f).normalized * ballRadius, new Vector3(0.022f, 0.022f, 0.022f), buttonMat);
            GameObject indexButton = CreateSphere(name + "_IndexButton", handleGo.transform,
                new Vector3(-0.40f * handX, -0.15f, 0.904f).normalized * ballRadius, new Vector3(0.016f, 0.016f, 0.016f), buttonMat);
            GameObject middleButton = CreateSphere(name + "_MiddleButton", handleGo.transform,
                new Vector3(-0.13f * handX, -0.20f, 0.971f).normalized * ballRadius, new Vector3(0.016f, 0.016f, 0.016f), buttonMat);
            GameObject ringButton = CreateSphere(name + "_RingButton", handleGo.transform,
                new Vector3(0.13f * handX, -0.22f, 0.967f).normalized * ballRadius, new Vector3(0.016f, 0.016f, 0.016f), buttonMat);
            GameObject pinkyButton = CreateSphere(name + "_PinkyButton", handleGo.transform,
                new Vector3(0.36f * handX, -0.30f, 0.883f).normalized * ballRadius, new Vector3(0.016f, 0.016f, 0.016f), buttonMat);

            if (isRight)
            {
                CreateCube(name + "_Trigger", handleGo.transform, new Vector3(0f, -0.035f, 0.045f),
                    new Vector3(0.03f, 0.018f, 0.035f), accentMat);
            }

            // GripPoint - per report ("손과 조종간 손잡이가 정확히 붙어야 한다"):
            // a separate child marking the exact point JoystickLever aligns to
            // the hand's palm/grip on grab (see JoystickLever.cs's Update),
            // rather than the whole Stick Transform being copied onto the hand.
            // Placed at the GripBall's own center (handleGo's local origin,
            // same spot _GripBall above is created at) since that's where a
            // wrapped fist naturally centers on this ball-shaped grip.
            GameObject gripPointGo = new GameObject(name + "_GripPoint");
            gripPointGo.transform.SetParent(handleGo.transform, false);
            gripPointGo.transform.localPosition = Vector3.zero;

            JoystickLever lever = baseGo.AddComponent<JoystickLever>();
            lever.pivot = pivotGo.transform;
            lever.handle = handleGo.transform;
            lever.gripPoint = gripPointGo.transform;
            lever.leftHandTracker = left;
            lever.rightHandTracker = right;

            // Fingertip-fitted, pressable buttons - see JoystickFingerButtons.cs.
            JoystickFingerButtons fingerButtons = baseGo.AddComponent<JoystickFingerButtons>();
            fingerButtons.lever = lever;
            fingerButtons.hand = isRight ? right : left;
            fingerButtons.handle = handleGo.transform;
            fingerButtons.buttons = new Transform[]
            {
                thumbButton.transform, indexButton.transform, middleButton.transform,
                ringButton.transform, pinkyButton.transform,
            };
            fingerButtons.gripRadius = ballRadius; // GripBall above is 0.10 across
            fingerButtons.pressedMaterial = MakeEmissiveMat(new Color(0.9f, 0.95f, 0.9f), new Color(1f, 1f, 0.6f));

            return lever;
        }

        // ---------------------------------------------------------------
        // T-shaped vertical lever - per "새로운 수직 이동 T자 레버" and "플레이어가
        // 콕핏 좌석에 정상적으로 앉았을 때의 어깨 높이를 기준으로 배치". Position is
        // derived from the seat actually built by BuildSeat (not a fixed world Y):
        //   seat surface   = Seat_Pan top
        //   shoulder Y     = seat surface + SeatedShoulderAboveSeat (adult seated
        //                    shoulder height, ~0.52-0.64 m; 0.58 used)
        //   shoulder Z     = Seat_Back's front face at that height + shoulder depth
        //   shoulder X     = +-SeatedShoulderHalfWidth (left side here)
        // The grip sits just below shoulder height, a relaxed bent-elbow reach
        // forward and slightly outboard of the left shoulder, so the forearm stays
        // level and the wrist straight, and it's clear of LeftJoystick (lower and
        // further in front) and the left display sightline. VerticalTLever then
        // fine-tunes the height from the pilot's real eye height at runtime.
        // Left hand only (movement hand); refused while LeftJoystick is held.
        // ---------------------------------------------------------------
        const float SeatedShoulderAboveSeat = 0.58f;
        const float SeatedShoulderHalfWidth = 0.19f;
        const float ShoulderDepthFromBackrest = 0.08f;
        const float TLeverReachForward = 0.34f;   // grip in front of the shoulder
        const float TLeverOutboard = 0.14f;       // grip outboard of the shoulder
        const float TLeverBelowShoulder = 0.02f;
        const float TLeverStemLength = 0.16f;

        static VerticalTLever CreateVerticalTLever(Transform interior, HandJointTracker leftHand, JoystickLever leftStick,
            Material accentMat, Material gripMat)
        {
            // --- Seated shoulder point, from the real seat objects ---
            Transform pan = interior.Find("Seat_Pan");
            Transform back = interior.Find("Seat_Back");
            float seatTop = pan != null ? pan.localPosition.y + pan.localScale.y * 0.5f : 0.48f;
            float shoulderY = seatTop + SeatedShoulderAboveSeat;
            float backFrontZ = -0.44f;
            if (back != null)
            {
                // Point on the backrest's front face at shoulder height (the back is tilted).
                float localY = back.localScale.y > 0.0001f ? (shoulderY - back.localPosition.y) / back.localScale.y : 0f;
                Vector3 frontLocal = back.localRotation * Vector3.Scale(new Vector3(0f, localY, 0.5f), back.localScale);
                backFrontZ = back.localPosition.z + frontLocal.z;
            }
            float shoulderZ = backFrontZ + ShoulderDepthFromBackrest;
            Vector3 leftShoulder = new Vector3(-SeatedShoulderHalfWidth, shoulderY, shoulderZ);
            Vector3 gripPos = leftShoulder + new Vector3(-TLeverOutboard, -TLeverBelowShoulder, TLeverReachForward);

            // --- Mount (origin = grip center) ---
            GameObject mount = new GameObject("VerticalTLever");
            mount.transform.SetParent(interior, false);
            mount.transform.localPosition = gripPos;
            mount.transform.localRotation = Quaternion.identity;

            GameObject baseGo = CreateCube("VerticalTLever_Base", mount.transform, new Vector3(0f, -TLeverStemLength, 0f),
                new Vector3(0.05f, 0.03f, 0.09f), accentMat);
            StripCollider(baseGo);

            GameObject stem = CreateCylinder("VerticalTLever_Stem", mount.transform, new Vector3(0f, -TLeverStemLength * 0.5f, 0f),
                new Vector3(0.018f, TLeverStemLength * 0.5f, 0.018f), accentMat);
            StripCollider(stem);

            GameObject handleGo = new GameObject("VerticalTLever_Handle");
            handleGo.transform.SetParent(mount.transform, false);
            handleGo.transform.localPosition = Vector3.zero;

            // Horizontal T bar: a capsule lying along X (capsule long axis = local Y,
            // rotated 90 about Z), 14 cm long, 3.5 cm thick - a full overhand grip.
            GameObject bar = CreateCapsule("VerticalTLever_Crossbar", handleGo.transform, Vector3.zero,
                new Vector3(0.035f, 0.07f, 0.035f), gripMat);
            bar.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

            // Small hub where the stem meets the bar (visual only).
            GameObject hub = CreateSphere("VerticalTLever_Hub", handleGo.transform, new Vector3(0f, -0.012f, 0f),
                new Vector3(0.03f, 0.03f, 0.03f), accentMat);
            StripCollider(hub);

            // Real XRI selection target (wired to the left Near-Far Interactor
            // in WireVerticalLeverInteractor once the rig exists).
            XRSimpleInteractable interactable = handleGo.AddComponent<XRSimpleInteractable>();
            Collider barCol = bar.GetComponent<Collider>();
            if (barCol != null)
            {
                SerializedObject so = new SerializedObject(interactable);
                SerializedProperty cols = so.FindProperty("m_Colliders");
                if (cols != null)
                {
                    cols.ClearArray();
                    cols.InsertArrayElementAtIndex(0);
                    cols.GetArrayElementAtIndex(0).objectReferenceValue = barCol;
                    so.ApplyModifiedProperties();
                }
            }

            VerticalTLever lever = mount.AddComponent<VerticalTLever>();
            lever.handle = handleGo.transform;
            lever.stem = stem.transform;
            lever.stemBase = baseGo.transform;
            lever.crossbar = bar.transform;
            lever.hand = leftHand;
            lever.blockWhileGrabbed = leftStick != null ? new[] { leftStick } : new JoystickLever[0];
            lever.travel = 0.035f; // 7 cm total
            lever.gripAboveShoulder = -TLeverBelowShoulder;

            Debug.Log("[Gundam] VerticalTLever at cockpit-local " + gripPos.ToString("F3") +
                " (seat top " + seatTop.ToString("F2") + ", seated left shoulder " + leftShoulder.ToString("F3") + ").");
            return lever;
        }

        /// <summary>Restricts the T lever's XRI selection to the LEFT hand's own
        /// Near-Far Interactor (same lookup WireJoystickHandInteractors uses) and
        /// hands it the pilot's head camera for the shoulder-height adaptation.</summary>
        static void WireVerticalLeverInteractor(GameObject xrOrigin, VerticalTLever lever)
        {
            if (lever == null || xrOrigin == null) return;
            Camera cam = xrOrigin.GetComponentInChildren<Camera>(true);
            if (cam != null) lever.pilotHead = cam.transform;

            Transform leftHandRoot = FindDeepChild(xrOrigin.transform, "Left Hand");
            Transform leftInteractor = leftHandRoot != null ? FindDeepChild(leftHandRoot, "Near-Far Interactor") : null;
            if (leftInteractor == null)
            {
                // No XRI hand rig: disable the XRI path so no other interactor can
                // ever select it; the left-hand grip/pinch fallback still works.
                XRSimpleInteractable xi = lever.handle != null ? lever.handle.GetComponent<XRSimpleInteractable>() : null;
                if (xi != null) xi.enabled = false;
                Debug.LogWarning("[Gundam] VerticalTLever: left 'Near-Far Interactor' not found - using the left-hand grip fallback only.");
                return;
            }
            lever.onlyAllowedInteractor = leftInteractor;
        }

        static GameObject CreateXROrigin(Transform parent, Vector3 localPos)
        {
            GameObject prefab = null;
            try
            {
                prefab = TryImportSampleAndFindXROriginPrefab();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Gundam] XR Origin sample import failed, will create a minimal placeholder instead: " + e);
            }

            GameObject instance;
            if (prefab != null)
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = "XR Origin (Gundam Cockpit)";
                Debug.Log("[Gundam] Instantiated XR rig from imported sample: " + AssetDatabase.GetAssetPath(prefab));
            }
            else
            {
                instance = new GameObject("XR_ORIGIN_PLACEHOLDER_REPLACE_ME");
                GameObject camGo = new GameObject("Main Camera");
                camGo.transform.SetParent(instance.transform, false);
                camGo.tag = "MainCamera";
                Camera cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.05f;
                Debug.LogWarning("[Gundam] Could not auto-import the XR Interaction Toolkit 'Hands Interaction Demo' sample. " +
                    "Open Package Manager > XR Interaction Toolkit > Samples, import 'Hands Interaction Demo', " +
                    "then drag the 'XR Origin Hands (XR Rig)' prefab into the scene, place it where " +
                    "'XR_ORIGIN_PLACEHOLDER_REPLACE_ME' is (under MobileSuitRoot), move the " +
                    "LeftHandTracker/RightHandTracker children onto it, and delete the placeholder.");
            }

            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = localPos;

            // Widen the view camera's field of view - Unity's default (60
            // degrees) reads noticeably narrower/more "zoomed in" than
            // natural human vision, which made the cockpit feel more
            // enclosed than intended ("내 시점이 더 넓어야 할 것 같아").
            // On a real OpenXR headset the true per-eye FOV ultimately
            // comes from the hardware lenses/runtime and this value isn't
            // guaranteed to change that, but it fixes the Editor/simulator
            // preview and any runtime path that does honor
            // Camera.fieldOfView, and is harmless either way. Searches the
            // whole instantiated hierarchy so it works whichever XR rig
            // prefab was actually found (or the placeholder fallback).
            Camera viewCamera = instance.GetComponentInChildren<Camera>(true);
            if (viewCamera != null)
            {
                viewCamera.fieldOfView = 100f;
                viewCamera.nearClipPlane = Mathf.Min(viewCamera.nearClipPlane, 0.05f);

                // Background: if PlaceExternalGundam successfully wired up the
                // 360 head-cam skybox (RenderSettings.skybox), use THAT - per
                // request ("콕핏에 검정부분에서 건담에 머리부분에서 보이는거
                // 처럼 해야해 360도로") the pilot should see what the Gundam's
                // head sees in every direction. This mostly matters just
                // outside Cockpit_Dome's own coverage now (the dome itself is
                // solid/opaque again and shows the same live feed directly -
                // see BuildCockpitEnclosure), e.g. if the real headset height
                // ever puts the camera right at the dome's edge. Otherwise
                // (FBX/Head bone not found yet) fall back to the original
                // plain deep-space Solid Color.
                if (RenderSettings.skybox != null)
                {
                    viewCamera.clearFlags = CameraClearFlags.Skybox;
                }
                else
                {
                    viewCamera.clearFlags = CameraClearFlags.SolidColor;
                    viewCamera.backgroundColor = new Color(0.01f, 0.01f, 0.025f, 1f);
                }

                // Don't let the pilot directly see ExternalGundam's own body
                // through the dome - per request ("건담이 콕핏 안에서 보이면
                // 안돼 왜냐면 내가 저 안에 타고 있는 설정이니까"): the pilot
                // IS this Gundam, so its exterior must not float in the
                // player's own first-person view. Its body is on
                // GundamBodyLayer (see PlaceExternalGundam) - exclude just
                // that one layer, everything else the player's camera
                // already sees (Starfield/Targets/OrbitHUD/cockpit) is
                // untouched. The head-cam (also in PlaceExternalGundam) keeps
                // its default "see everything" mask, so that's how the
                // Gundam's own viewpoint still reaches the cockpit screen.
                viewCamera.cullingMask &= ~(1 << GundamBodyLayer);
                // ...and the space-dust field (see SpaceBackdropLayer).
                viewCamera.cullingMask &= ~(1 << SpaceBackdropLayer);
            }
            else
            {
                Debug.LogWarning("[Gundam] Could not find a Camera under the instantiated XR rig to widen its field of view.");
            }

            // Force the rig to ignore the player's REAL room floor height.
            // The Starter Assets/Hands sample rig ships with
            // RequestedTrackingOriginMode = "Not Specified", which most
            // OpenXR runtimes (including on-device Android XR) resolve to
            // real Floor tracking - meaning the runtime camera Y becomes
            // the player's actual head height above their actual real
            // floor (roughly 1.6-2.0m). That can place the camera outside
            // the cockpit dome entirely depending on the player's real
            // height/room, which reads as a solid black screen that never
            // changes no matter which way the player looks (the classic
            // symptom reported: "검정화면만 나와... 360도가 다 바뀌어야해" - the
            // camera isn't inside the 360 dome at all, so there's nothing
            // for it to see in any direction). This is a fixed seated
            // cockpit, not a room-scale experience, so the real room floor
            // must never affect the camera's height - "Device" mode uses
            // only the headset's own relative pose and ignores real floor
            // height, guaranteeing the camera stays exactly where this
            // script places it (inside the dome) regardless of the
            // player's real-world height or floor.
            XROrigin xrOriginComponent = instance.GetComponentInChildren<XROrigin>(true);
            if (xrOriginComponent != null)
            {
                xrOriginComponent.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
            }
            else
            {
                Debug.LogWarning("[Gundam] Could not find an XROrigin component under the instantiated XR rig to force Device tracking mode.");
            }

            return instance;
        }

        static GameObject TryImportSampleAndFindXROriginPrefab()
        {
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.unity.xr.interaction.toolkit");
            if (info == null)
            {
                Debug.LogWarning("[Gundam] com.unity.xr.interaction.toolkit is not resolved yet. " +
                    "Open Window > Package Manager once to let packages finish installing, then re-run this menu item.");
                return null;
            }

            var samples = UnityEditor.PackageManager.UI.Sample.FindByPackage(info.name, info.version);
            bool importedAny = false;
            foreach (var sample in samples)
            {
                // Import exactly the two samples XR Interaction Toolkit's own
                // package.json declares as needed for hand tracking:
                //   - "Hands Interaction Demo"  (has the XR Origin Hands rig)
                //   - "Starter Assets"          (a documented dependency of the
                //                                 sample above — input actions/
                //                                 presets it relies on)
                // "AR Starter Assets" is deliberately excluded: it also depends
                // on "Starter Assets", but additionally pulls in
                // com.unity.xr.arfoundation, which this project does not want
                // (see README) and which was the source of the
                // "XRSimulation-Session" duplicate-ID warning. Matching must be
                // an exact name check, not a substring/Contains check — e.g.
                // "AR Starter Assets" also contains the substring
                // "Starter Assets", so a Contains("Starter Assets") test would
                // wrongly match it too.
                bool isNeededSample =
                    sample.displayName.Equals("Hands Interaction Demo", StringComparison.OrdinalIgnoreCase) ||
                    sample.displayName.Equals("Starter Assets", StringComparison.OrdinalIgnoreCase);

                if (!isNeededSample) continue;

                if (sample.isImported)
                {
                    importedAny = true;
                    continue;
                }

                sample.Import();
                importedAny = true;
            }

            AssetDatabase.Refresh();
            if (!importedAny) return null;

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Samples" });
            string best = null;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.IndexOf("XR Origin", StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (best == null || path.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = path;
                }
            }

            return best == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(best);
        }

        // Always makes this scene BUILD INDEX 0 - not just "present somewhere
        // in the list". The old version only appended if missing, which on a
        // fresh project left Assets/Scenes/SampleScene.unity (added to Build
        // Settings by the default template) at index 0 and GundamCockpit at
        // index 1 - so a build launched straight into the empty default
        // scene (a bare Camera + Light, no XR session, no cockpit at all),
        // which on-device reads as "그냥 유니티 공간만 보임" (nothing but
        // Unity's default background - no error, no cockpit, nothing).
        // Removing any existing entry for this path first and re-inserting
        // at position 0 guarantees this scene is what actually launches,
        // regardless of what else Build Settings already contains.
        static void AddSceneToBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            scenes.RemoveAll(s => s.path == path);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static Material MakeMat(Color c)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Material m = new Material(shader != null ? shader : Shader.Find("Standard"));
            m.color = c;
            return m;
        }

        static Material MakeEmissiveMat(Color baseColor, Color emission)
        {
            Material m = MakeMat(baseColor);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return m;
        }

        static GameObject CreatePrimitiveGo(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            Renderer rend = go.GetComponent<Renderer>();
            if (rend != null) rend.sharedMaterial = mat;
            return go;
        }

        static GameObject CreateCube(string n, Transform p, Vector3 pos, Vector3 scale, Material m) =>
            CreatePrimitiveGo(PrimitiveType.Cube, n, p, pos, scale, m);

        static GameObject CreateCylinder(string n, Transform p, Vector3 pos, Vector3 scale, Material m) =>
            CreatePrimitiveGo(PrimitiveType.Cylinder, n, p, pos, scale, m);

        static GameObject CreateCapsule(string n, Transform p, Vector3 pos, Vector3 scale, Material m) =>
            CreatePrimitiveGo(PrimitiveType.Capsule, n, p, pos, scale, m);

        static GameObject CreateSphere(string n, Transform p, Vector3 pos, Vector3 scale, Material m) =>
            CreatePrimitiveGo(PrimitiveType.Sphere, n, p, pos, scale, m);

        /// <summary>Thin box stretched/rotated to connect two points — used for frame beams.</summary>
        static GameObject CreateBeam(string name, Transform parent, Vector3 a, Vector3 b, float thickness, Material mat)
        {
            Vector3 mid = (a + b) * 0.5f;
            Vector3 dir = b - a;
            float len = dir.magnitude;
            // +thickness so neighbouring segments overlap at the joints (no hairline gaps).
            GameObject go = CreateCube(name, parent, mid, new Vector3(thickness, thickness, Mathf.Max(len + thickness, 0.001f)), mat);
            if (len > 0.0001f) go.transform.localRotation = Quaternion.LookRotation(dir.normalized);
            return go;
        }
    }
}
