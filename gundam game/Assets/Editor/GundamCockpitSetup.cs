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
        const float ShipTopSpeed = 40f; // m/s at full LeftJoystick push (was 8)
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
            // Per "건담에 스피드를 더 빠르게 해줘 어느정도 미는정도에 따라서": top speed
            // 8 -> 40 m/s. ShipMovementController itself is unchanged - it already
            // scales speed linearly with how far LeftJoystick is pushed (after its
            // dead zone), so a light push is still slow and a full push is 40 m/s.
            ship.maxMoveSpeed = ShipTopSpeed;

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
            // Player hit points (per "건담에 체력은 3000이고") - on the Gundam body the
            // Zakus shoot at, feeding the cockpit's shared vitals (HP gauges).
            ColonyStructure colony = BuildColony();
            PlayerHealth playerHealth = null;
            if (gundamResult != null && gundamResult.instance != null)
            {
                playerHealth = gundamResult.instance.AddComponent<PlayerHealth>();
                playerHealth.maxHealth = 3000;
                playerHealth.hudManager = hudManager;
                playerHealth.capsuleTop = MobileSuitTargetHeight - 1f;
                hudManager.Vitals.MaxHP = 3000f;
                hudManager.Vitals.HP = 3000f;

                // Per "건담이 상대를 통과하지않게 충돌을 넣어": the Gundam body stops at
                // (and slides around) enemy suits instead of passing through them.
                SuitCollision suitCollision = suitRoot.AddComponent<SuitCollision>();
                suitCollision.body = gundamResult.instance.transform;
                suitCollision.bodyHeight = MobileSuitTargetHeight;
                suitCollision.colony = colony;
            }
            PlaceZakuEnemy(gundamResult != null && gundamResult.instance != null ? gundamResult.instance.transform : null, colony);

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
            // Colony gravity: climbing on the T lever cancels the fall (SuitCollision).
            SuitCollision gravityCollision = suitRoot.GetComponent<SuitCollision>();
            if (gravityCollision != null) gravityCollision.thrust = verticalThrust;

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

                // Per "건담이 내가 바라보는 방향으로 항상 몸이 돌아야함": the Gundam body
                // turns (yaw) to face the view direction, pivoting about its head so
                // HeadCam - and so the pilot's view - never shifts. See
                // ExternalGundamFollower.
                ExternalGundamFollower bodyTurn = gundamResult.instance != null
                    ? gundamResult.instance.GetComponent<ExternalGundamFollower>() : null;
                if (bodyTurn != null)
                {
                    bodyTurn.viewController = viewController;
                    bodyTurn.turnPivot = gundamResult.headCam360.head;
                    bodyTurn.faceViewDirection = true;
                }

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

            // --- SORTIE POINT selection (per "처음에 스폰 위치를 지정해야할듯 어디서
            //     시작할지 우주 콜로니 외관 콜로니 안 이런식으로", option "게임 시작 시
            //     선택 화면"): after calibration, a touch panel picks where the suit
            //     starts. See SpawnSelectPanel. ---
            BuildSpawnSelectPanel(interior.transform, suitRoot.transform,
                gundamResult != null && gundamResult.instance != null ? gundamResult.instance.transform : null,
                colony, calibManager, leftHand, rightHand, new Behaviour[] { ship, verticalThrust });

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
            // Far stars go in the Background queue without depth writes so they never
            // draw in front of the kilometres-big colony; near dust hides inside it.
            Material farStarMat = MakeEmissiveMat(Color.black, Color.white);
            if (farStarMat.HasProperty("_ZWrite")) farStarMat.SetFloat("_ZWrite", 0f);
            farStarMat.renderQueue = 1000;
            dust.farMaterial = farStarMat;
            dust.colony = colony;
            if (gundamResult != null && gundamResult.headCam360 != null && gundamResult.headCam360.headCam != null)
            {
                dust.viewer = gundamResult.headCam360.headCam.transform;
            }
            // Colony air (fog/ambient) and exterior/interior culling follow HeadCam.
            if (colony != null)
            {
                ColonyAtmosphere atmosphere = colony.GetComponent<ColonyAtmosphere>();
                if (atmosphere != null) atmosphere.viewer = dust.viewer;
            }

            // --- Distant space wreckage (VARCO 3D models in Assets/Models/Debris) -
            //     per "우주에 건물을 넣을려고 하는데 우주에 잔해같은거 거슬리지 않게"
            //     (see PlaceSpaceDebris / SpaceDebrisField). ---
            PlaceSpaceDebris(dust.viewer, colony);

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

            // --- COCKPIT STATION - per "콕핏이 건담을 따라다니니까 콕핏에서 자꾸 밖에
            //     사물이 들어와서 시야를 가려 콕핏을 졸라 멀리 배치해 건담을 안따라
            //     다니게해". The cockpit (interior + the XR rig the pilot sits in +
            //     its fill light) used to be a child of MobileSuitRoot, so it flew
            //     along with the Gundam - into streets, buildings and wreckage, which
            //     the pilot's own camera then saw INSIDE the cabin. It now sits in a
            //     fixed, empty spot far outside the battlefield and never moves; the
            //     pilot sees the world only through the dome (HeadCam in the Gundam's
            //     head, unchanged). MobileSuitRoot itself still moves exactly as
            //     before (ShipMovementController untouched) and the Gundam still
            //     follows it; nothing reads the cockpit's world position (only its
            //     directions), so aiming, the view, the sticks and the touch screens
            //     work the same. The XR rig is only re-parented - none of its
            //     settings change. ---
            GameObject station = new GameObject("CockpitStation");
            station.transform.SetPositionAndRotation(suitRoot.transform.position + CockpitStationOffset, suitRoot.transform.rotation);
            GameObject fillLight = GameObject.Find("Cockpit_FillLight");
            foreach (Transform t in new[] { interior.transform, xrOrigin != null ? xrOrigin.transform : null, fillLight != null ? fillLight.transform : null })
            {
                if (t == null) continue;
                Vector3 lp = suitRoot.transform.InverseTransformPoint(t.position);
                Quaternion lr = Quaternion.Inverse(suitRoot.transform.rotation) * t.rotation;
                t.SetParent(station.transform, false);
                t.localPosition = lp;
                t.localRotation = lr;
            }
            Debug.Log("[Gundam] Cockpit moved to a fixed CockpitStation at " + station.transform.position +
                " - it no longer follows the Gundam (the world is seen only through the dome).");

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);

            Selection.activeGameObject = suitRoot;
            Debug.Log("[Gundam] Cockpit prototype scene (visual overhaul) built and saved at " + ScenePath +
                       ". Open Project Settings > XR Plug-in Management to enable OpenXR + Hand Tracking if you haven't yet.");
        }

        // Fixed spot for the cockpit (see COCKPIT STATION above): ~9 km from the
        // battlefield - well past the pilot camera's 1 km far clip in every
        // direction from the colony, wreckage and start area - yet close enough to
        // the world origin that XR tracking keeps sub-millimetre float precision
        // (much farther, e.g. 60 km, would make the cabin and hands visibly jitter).
        static readonly Vector3 CockpitStationOffset = new Vector3(0f, -4000f, -8000f);

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
            // Far enough for the distant wreckage (up to ~900m), far stars and the
            // whole space colony (6.4 km across, far end 13.5 km away at the start).
            headCam.farClipPlane = Mathf.Max(headCam.farClipPlane, 16000f);

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
        const float BeamSaberHiltLength = 2.4f;   // m, Gundam scale - long enough to stick out of both sides of the fist (~1.5 m)
        const float BeamSaberBladeLength = 9f;    // m
        const float BeamSaberBladeRadius = 0.28f; // m
        // Where the hilt passes through the Gundam's (closed) right fist, in RightHand-bone
        // space, metres, unscaled - measured from the model's fist mesh: x toward the palm/
        // finger channel, y = mid-fist, z = along the fingers from the wrist. The grip axis is
        // the bone's +Y (index/thumb side), so the blade comes out over the thumb like a real
        // grip ("건담 손에 빔샤벨을 쥐고있어야").
        static readonly Vector3 BeamSaberGripInHand = new Vector3(-0.24f, 0.13f, 0.80f);

        static void SetupBeamSaberMode(GameObject suitRoot, GameObject interior, GundamPlacementResult gundamResult,
            JoystickLever rightStick, HandJointTracker leftHand, HandJointTracker rightHand, GameObject xrOrigin)
        {
            // --- Mode controller ---
            WeaponModeController modes = suitRoot.AddComponent<WeaponModeController>();
            modes.rightStick = rightStick;
            modes.rightStickInteractable = rightStick != null && rightStick.handle != null
                ? rightStick.handle.GetComponent<XRSimpleInteractable>() : null;

            // RightJoystick itself drives the saber in BEAM SABER mode (per "빔샤벨 조종기를
            // 빼고 오른쪽 조종관으로 밀고 당기기는 찌르기 옆으로 당기면 옆으로 휘둘러"):
            // push/pull = thrust, sideways = horizontal swing. The separate hand-held
            // BeamSaberControlStick is no longer built. In that mode the stick's usual
            // consumers (aim, view turning) are detached; its movement code is untouched.
            modes.rightStickDrivesSaber = true;
            modes.aim = suitRoot.GetComponent<WeaponAimFireController>();
            modes.viewController = suitRoot.GetComponentInChildren<CockpitViewController>(true);
            // Head Vulcan is off in BEAM RIFLE (the right thumb fires the rifle there);
            // start in HEAD VULCAN mode = the old default behaviour.
            modes.vulcan = suitRoot.GetComponent<HeadVulcanController>();
            modes.startMode = WeaponModeController.Mode.HeadVulcan;

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
                    // Arm only (per "일단 건담 팔만 보여야"): RightShoulder is no longer
                    // included - its skin carried the chest/collar armor right next to
                    // the head camera.
                    SkinnedMeshRenderer armView = BuildGundamRightArmView(gundam,
                        new[] { "RightArm", "RightForeArm", "RightHand" });
                    Transform saber = BuildBeamSaber(handBone);

                    BeamSaberArmController arm = gundam.AddComponent<BeamSaberArmController>();
                    arm.upperArm = upper;
                    arm.foreArm = fore;
                    arm.handBone = handBone;
                    arm.armView = armView;
                    arm.saber = saber;
                    arm.rightStick = rightStick; // stick deflection -> saber poses (thrust / swing)
                    arm.cockpitSpace = interior.transform;
                    arm.viewController = suitRoot.GetComponentInChildren<CockpitViewController>(true);
                    Camera cam = xrOrigin != null ? xrOrigin.GetComponentInChildren<Camera>(true) : null;
                    if (cam != null) arm.pilotHead = cam.transform;
                    modes.saberArm = arm;

                    // --- BEAM RIFLE (both hands) ---
                    SetupBeamRifle(gundam, interior, suitRoot, modes, armView, rightStick, upper, fore, handBone);
                    Debug.Log("[Gundam] BEAM SABER: arm bones " + upper.name + " > " + fore.name + " > " + handBone.name +
                        (shoulder != null ? " (shoulder " + shoulder.name + ")" : "") + ", saber on " + handBone.name + ".");
                }
                else
                {
                    Debug.LogWarning("[Gundam] BEAM SABER: RightArm/RightForeArm/RightHand bones not found - arm control disabled.");
                }
            }

            // --- BEAM SABER view assist: the right hand can't turn the view with
            //     RightJoystick in this mode, so the view follows the locked-on
            //     enemy (per "락온된 상대에게 시선이 고정되서 따라가게하자"). ---
            SaberLookAssist lookAssist = suitRoot.AddComponent<SaberLookAssist>();
            lookAssist.modes = modes;
            lookAssist.viewController = suitRoot.GetComponentInChildren<CockpitViewController>(true);
            lookAssist.targetLock = interior.GetComponentInChildren<OrbitHUDTargetLock>(true);
            lookAssist.followOnlyLocked = true;
            // Per "빔샤벨 상태에서 아무도 락온 안되어있을때는 화면조작 락온이 되면 그때
            // 공격기능으로": the lock decides whether RightJoystick turns the view or
            // swings the saber; per "건물뒤에 보이지 않는 적은 락온되면 안됨" the ring
            // checks line of sight against the colony.
            modes.targetLock = lookAssist.targetLock;
            if (lookAssist.targetLock != null) lookAssist.targetLock.colony = UnityEngine.Object.FindFirstObjectByType<ColonyStructure>();

            // --- WEAPON display touch buttons ---
            Transform weaponCanvas = interior.transform.Find("SystemCheckDisplay/SysCheck_Right/Weapon_Canvas");
            if (weaponCanvas == null)
            {
                Debug.LogWarning("[Gundam] BEAM SABER: Weapon_Canvas not found - no touch buttons.");
                return;
            }
            Text current = CreateUIText("CurrentWeapon", weaponCanvas, new Vector2(0, -52), new Vector2(340, 18), 14,
                TextAnchor.MiddleCenter, new Color(0.6f, 0.9f, 1f), "SELECT: HEAD VULCAN");
            // Three weapons side by side (per "무기에 빔라이플을 추가", HEAD VULCAN button
            // added): 118 x 118 canvas units each (~2.8 cm square on the half-size display).
            Vector2 btnSize = new Vector2(118, 118);
            WeaponTouchPanel.TouchButton vulcanBtn = BuildTouchButton(weaponCanvas, "Btn_HeadVulcan", "HEAD\nVULCAN",
                new Vector2(-126, -138), WeaponModeController.Mode.HeadVulcan, btnSize, 22);
            WeaponTouchPanel.TouchButton rifleBtn = BuildTouchButton(weaponCanvas, "Btn_BeamRifle", "BEAM\nRIFLE",
                new Vector2(0, -138), WeaponModeController.Mode.BeamRifle, btnSize, 22);
            WeaponTouchPanel.TouchButton saberBtn = BuildTouchButton(weaponCanvas, "Btn_BeamSaber", "BEAM\nSABER",
                new Vector2(126, -138), WeaponModeController.Mode.BeamSaber, btnSize, 22);

            WeaponTouchPanel panel = weaponCanvas.gameObject.AddComponent<WeaponTouchPanel>();
            panel.weapons = modes;
            panel.leftHand = leftHand;
            panel.rightHand = rightHand;
            panel.buttons = new[] { vulcanBtn, rifleBtn, saberBtn };
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

        /// <summary>The SORTIE POINT panel: floating (no screen prop, like the
        /// calibration prompt) where that prompt was, with four big touch buttons.</summary>
        static SpawnSelectPanel BuildSpawnSelectPanel(Transform interior, Transform suitRoot, Transform body, ColonyStructure colony,
            CalibrationManager calibration, HandJointTracker leftHand, HandJointTracker rightHand, Behaviour[] pause)
        {
            GameObject canvasGo = new GameObject("SpawnSelect_Canvas");
            canvasGo.transform.SetParent(interior, false);
            canvasGo.transform.localPosition = new Vector3(0f, 1.2f, 0.34f);
            canvasGo.transform.localRotation = Quaternion.identity;
            canvasGo.transform.localScale = Vector3.one * 0.0009f; // 1 unit = 0.9 mm
            RectTransform canvasRect = canvasGo.AddComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(520, 360);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<CanvasScaler>();

            CreateUIImage("Backing", canvasGo.transform, Vector2.zero, new Vector2(520, 360), new Color(0.02f, 0.03f, 0.08f, 0.8f));
            Text title = CreateUIText("Title", canvasGo.transform, new Vector2(0, 148), new Vector2(500, 40), 26, TextAnchor.MiddleCenter,
                new Color(0.85f, 0.95f, 1f), "SORTIE POINT  /  출격 위치 선택");
            string[] labels = { "SPACE\n우주", "COLONY EXTERIOR\n콜로니 외관", "COLONY ENTRANCE\n콜로니 안 (입구)", "DOWNTOWN\n콜로니 안 (도심)" };
            Vector2[] pos = { new Vector2(-122, 55), new Vector2(122, 55), new Vector2(-122, -95), new Vector2(122, -95) };
            SpawnSelectPanel.TouchButton[] buttons = new SpawnSelectPanel.TouchButton[4];
            Vector2 size = new Vector2(226, 130); // ~20 x 12 cm
            for (int i = 0; i < 4; i++)
            {
                Image bg = CreateUIImage("Btn_Spawn_" + (SpawnSelectPanel.Point)i, canvasGo.transform, pos[i], size, new Color(0.08f, 0.12f, 0.2f, 0.95f));
                Text t = CreateUIText("Label", bg.transform, Vector2.zero, size, 22, TextAnchor.MiddleCenter, new Color(0.8f, 0.9f, 1f), labels[i]);
                buttons[i] = new SpawnSelectPanel.TouchButton { point = (SpawnSelectPanel.Point)i, rect = bg.rectTransform, background = bg, label = t };
            }

            SpawnSelectPanel panel = suitRoot.gameObject.AddComponent<SpawnSelectPanel>();
            panel.suitRoot = suitRoot;
            panel.body = body;
            panel.colony = colony;
            panel.calibration = calibration;
            panel.pauseUntilChosen = pause;
            panel.leftHand = leftHand;
            panel.rightHand = rightHand;
            panel.buttons = buttons;
            panel.panelRoot = canvasGo;
            panel.titleText = title;
            canvasGo.SetActive(false);
            return panel;
        }

        static WeaponTouchPanel.TouchButton BuildTouchButton(Transform canvas, string name, string label, Vector2 pos,
            WeaponModeController.Mode mode)
        {
            // taller so it stays finger-sized after the display cluster is halved
            return BuildTouchButton(canvas, name, label, pos, mode, new Vector2(340, 72), 24);
        }

        static WeaponTouchPanel.TouchButton BuildTouchButton(Transform canvas, string name, string label, Vector2 pos,
            WeaponModeController.Mode mode, Vector2 size, int fontSize)
        {
            Image bg = CreateUIImage(name, canvas, pos, size, new Color(0.08f, 0.12f, 0.2f, 0.95f));
            Text t = CreateUIText(name + "_Label", bg.transform, Vector2.zero, size, fontSize, TextAnchor.MiddleCenter,
                new Color(0.75f, 0.85f, 1f), label);
            return new WeaponTouchPanel.TouchButton { mode = mode, rect = bg.rectTransform, background = bg, label = t };
        }

        /// <summary>A second SkinnedMeshRenderer on the Gundam that draws ONLY the triangles
        /// of the real Gundam mesh whose vertices are all mainly weighted to the given bones,
        /// skinned to the same real bone array - so the real right arm can be shown while the
        /// rest of the (pilot's own) body stays hidden. Mesh cached as an asset.</summary>
        const float ArmViewMinBoneWeight = 0.6f;

        static SkinnedMeshRenderer BuildGundamRightArmView(GameObject gundam, string[] boneNames)
        {
            return BuildGundamArmView(gundam, boneNames, "GundamRightArm_View", GundamRightArmMeshPath);
        }

        static SkinnedMeshRenderer BuildGundamArmView(GameObject gundam, string[] boneNames, string viewName, string meshPath)
        {
            SkinnedMeshRenderer src = gundam.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(r => r.sharedMesh != null && !r.gameObject.name.EndsWith("_View"));
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
            // Keep a triangle when, on average over its 3 vertices, at least
            // ArmViewMinBoneWeight of the skinning comes from the given bones. (The
            // old "every vertex's main bone" test left ragged holes where the arm
            // meets the shoulder and let chest pieces in via RightShoulder.)
            System.Func<BoneWeight, float> armW = w =>
                (keep.Contains(w.boneIndex0) ? w.weight0 : 0f) + (keep.Contains(w.boneIndex1) ? w.weight1 : 0f) +
                (keep.Contains(w.boneIndex2) ? w.weight2 : 0f) + (keep.Contains(w.boneIndex3) ? w.weight3 : 0f);
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if ((armW(bw[a]) + armW(bw[b]) + armW(bw[c])) / 3f < ArmViewMinBoneWeight) continue;
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

            UnityEngine.Mesh arm = new UnityEngine.Mesh { name = viewName };
            if (nv.Count > 65000) arm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            arm.SetVertices(nv);
            if (nn.Count == nv.Count) arm.SetNormals(nn);
            if (nt.Count == nv.Count) arm.SetTangents(nt);
            if (nu.Count == nv.Count) arm.SetUVs(0, nu);
            arm.boneWeights = nw.ToArray();
            arm.bindposes = m.bindposes;
            arm.SetTriangles(ntri, 0);
            arm.RecalculateBounds();

            UnityEngine.Mesh existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath);
            if (existing != null) AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(arm, meshPath);

            GameObject go = new GameObject(viewName);
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
            Debug.Log("[Gundam] " + viewName + ": " + nv.Count + " verts / " + (ntri.Count / 3) + " tris from bones " + string.Join(", ", boneNames));
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
            // Seated in the fist: root origin = grip point inside the fist, root +Y (blade)
            // = the fist's grip axis (bone +Y), root +Z = along the fingers (bone +Z).
            // BeamSaberArmController keeps this hand->saber relation and turns the HAND
            // so the saber points where the control stick points.
            root.transform.localPosition = BeamSaberGripInHand * ls;
            root.transform.localRotation = Quaternion.identity;
            root.layer = 0;

            float hiltLen = BeamSaberHiltLength;
            GameObject hiltAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BeamSaberHiltPath);
            if (hiltAsset != null)
            {
                GameObject hilt = (GameObject)PrefabUtility.InstantiatePrefab(hiltAsset);
                hilt.name = "BeamSaber_Hilt";
                hilt.transform.SetParent(root.transform, false);
                hilt.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                hilt.transform.localPosition = Vector3.zero;
                hilt.transform.localScale = Vector3.one;
                Renderer[] rs = hilt.GetComponentsInChildren<Renderer>(true);
                // The model's long axis is NOT on any of its local axes (it's tilted ~30deg),
                // which made the beam come out of the hilt at an angle ("손잡이에서 레이저가
                // 이상하게 나오는데"). Find the real long axis from the vertices (principal
                // axis) and turn it exactly onto +Y, emitter end up.
                Vector3 axis;
                if (HiltPrincipalAxis(hilt, root.transform, out axis))
                {
                    // Emitter = the end with the dark round emitter face; with the -90deg X
                    // turn above that end points to -Z (checked on renders of the model).
                    if (Vector3.Dot(axis, Vector3.back) < 0f) axis = -axis;
                    hilt.transform.localRotation = Quaternion.FromToRotation(axis, Vector3.up) * hilt.transform.localRotation;
                }
                if (rs.Length > 0)
                {
                    // Length along +Y and cross-section center, now that it's straight.
                    Vector3 mn, mx;
                    HiltExtents(hilt, root.transform, out mn, out mx);
                    float len = mx.y - mn.y;
                    float s = len > 0.0001f ? hiltLen / len : 1f;
                    hilt.transform.localScale = Vector3.one * s;
                    Vector3 mid = (mn + mx) * 0.5f;
                    hilt.transform.localPosition = -mid * s; // hilt centered on the grip point, on the axis
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

            // Hits: 200 damage per contact (per "빔샤벨은 데미지 일단은 200") - see BeamSaberBlade.
            BeamSaberBlade hits = root.AddComponent<BeamSaberBlade>();
            hits.damage = 200;
            hits.bladeStart = hiltLen * 0.5f;
            hits.bladeLength = BeamSaberBladeLength;
            hits.hitRadius = BeamSaberBladeRadius + 0.3f;
            hits.sparkMaterial = glow;

            root.SetActive(false); // shown only in BEAM SABER mode
            return root.transform;
        }

        /// <summary>Hilt vertices (subsampled) in 'space' coordinates.</summary>
        static System.Collections.Generic.List<Vector3> HiltPoints(GameObject hilt, Transform space)
        {
            var pts = new System.Collections.Generic.List<Vector3>();
            foreach (MeshFilter mf in hilt.GetComponentsInChildren<MeshFilter>(true))
            {
                UnityEngine.Mesh m = mf.sharedMesh;
                if (m == null) continue;
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 30000);
                for (int i = 0; i < v.Length; i += step)
                    pts.Add(space.InverseTransformPoint(mf.transform.TransformPoint(v[i])));
            }
            return pts;
        }

        /// <summary>Long (principal) axis of the hilt model in 'space' coordinates.</summary>
        static bool HiltPrincipalAxis(GameObject hilt, Transform space, out Vector3 axis)
        {
            axis = Vector3.up;
            var pts = HiltPoints(hilt, space);
            if (pts.Count < 10) return false;
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in pts) mean += p;
            mean /= pts.Count;
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Vector3 p in pts)
            {
                Vector3 d = p - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            // Power iteration: dominant eigenvector of the covariance.
            Vector3 a = new Vector3(0.3f, 0.5f, 0.8f).normalized;
            for (int it = 0; it < 64; it++)
            {
                Vector3 n = new Vector3(xx * a.x + xy * a.y + xz * a.z,
                                        xy * a.x + yy * a.y + yz * a.z,
                                        xz * a.x + yz * a.y + zz * a.z);
                if (n.sqrMagnitude < 1e-20f) return false;
                a = n.normalized;
            }
            axis = a;
            return true;
        }

        // ---------------------------------------------------------------
        // BEAM RIFLE - per "다운로드에 빔라이플을 다운해두었거든 무기에 빔라이플을
        // 추가하고 ... 빔라이플을 양손으로 잡는거야 ... 락온한 상대를 쏠거야 쏘는거는
        // 오른손 엄지", "빔 데미지는 500". The downloaded model (copied to
        // BeamRifleModelPath with its base-color texture) is held in both hands by
        // BeamRifleController; the left arm gets its own arm-only view.
        //
        // Model space (the FBX prefab root, measured from its vertices): the
        // rifle stands along Y with the MUZZLE at -Y, its top toward +Z (pistol
        // grip / magazine hang toward -Z). Points below are in that space and are
        // turned/scaled into the rifle root (+Z muzzle, +Y top, metres).
        // ---------------------------------------------------------------
        const string BeamRifleModelPath = "Assets/Models/BeamRifle/BeamRifle.fbx";
        const string BeamRifleTexturePath = "Assets/Models/BeamRifle/BeamRifle-baseColor.png";
        const string GundamLeftArmMeshPath = "Assets/Models/Gundam/GundamLeftArm_View.asset";
        const float BeamRifleScale = 5.5f;  // model is ~1 unit long -> 5.5 m rifle
        static readonly Vector3 RifleModelStock = new Vector3(0f, 0.495f, 0.07f);
        static readonly Vector3 RifleModelMuzzle = new Vector3(0f, -0.50f, 0.06f);
        static readonly Vector3 RifleModelGrip = new Vector3(0f, 0.199f, -0.130f);          // pistol grip middle
        static readonly Vector3 RifleModelGripOutward = new Vector3(0f, 0.709f, -0.706f);   // toward the grip's bottom
        static readonly Vector3 RifleModelForeGrip = new Vector3(0f, -0.15f, 0.04f);        // fore-end, left hand

        /// <summary>Model (prefab) space -> rifle root space: prefab -Y -> +Z (muzzle), prefab +Z -> +Y (top).</summary>
        static Quaternion RifleModelToRoot => Quaternion.Inverse(Quaternion.LookRotation(Vector3.down, Vector3.forward));

        static void SetupBeamRifle(GameObject gundam, GameObject interior, GameObject suitRoot, WeaponModeController modes,
            SkinnedMeshRenderer saberArmView, JoystickLever rightStick, Transform rUpper, Transform rFore, Transform rHand)
        {
            Transform lUpper = FindDeepChild(gundam.transform, "LeftArm");
            Transform lFore = FindDeepChild(gundam.transform, "LeftForeArm");
            Transform lHand = FindDeepChild(gundam.transform, "LeftHand");
            if (lUpper == null || lFore == null || lHand == null)
            {
                Debug.LogWarning("[Gundam] BEAM RIFLE: LeftArm/LeftForeArm/LeftHand bones not found - rifle disabled.");
                return;
            }

            Transform rifle = BuildBeamRifle(gundam.transform);
            if (rifle == null) return;

            SkinnedMeshRenderer leftView = BuildGundamArmView(gundam, new[] { "LeftArm", "LeftForeArm", "LeftHand" },
                "GundamLeftArm_View", GundamLeftArmMeshPath);
            // Separate right-arm renderer (same arm-only mesh) so the saber's own
            // show/hide of its arm view never hides the arm while the rifle is out.
            SkinnedMeshRenderer rightView = null;
            if (saberArmView != null)
            {
                GameObject rv = new GameObject("GundamRightArm_RifleView");
                rv.transform.SetParent(saberArmView.transform.parent, false);
                rv.transform.localPosition = saberArmView.transform.localPosition;
                rv.transform.localRotation = saberArmView.transform.localRotation;
                rv.transform.localScale = saberArmView.transform.localScale;
                rv.layer = 0;
                rightView = rv.AddComponent<SkinnedMeshRenderer>();
                rightView.sharedMesh = saberArmView.sharedMesh;
                rightView.bones = saberArmView.bones;
                rightView.rootBone = saberArmView.rootBone;
                rightView.sharedMaterials = saberArmView.sharedMaterials;
                rightView.updateWhenOffscreen = true;
                rightView.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rightView.enabled = false;
            }

            BeamRifleController rc = gundam.AddComponent<BeamRifleController>();
            rc.rightUpperArm = rUpper; rc.rightForeArm = rFore; rc.rightHand = rHand;
            rc.leftUpperArm = lUpper; rc.leftForeArm = lFore; rc.leftHand = lHand;
            rc.rifle = rifle;
            rc.rightArmView = rightView;
            rc.leftArmView = leftView;

            Quaternion q = RifleModelToRoot;
            float S = BeamRifleScale;
            rc.stockPoint = q * RifleModelStock * S;
            rc.muzzlePoint = q * RifleModelMuzzle * S;
            rc.rightGripPoint = q * RifleModelGrip * S;
            Vector3 thumb = (q * -RifleModelGripOutward).normalized;                    // toward the rifle body
            rc.rightGripThumb = thumb;
            rc.rightGripFingers = Vector3.ProjectOnPlane(Vector3.forward, thumb).normalized; // knuckles toward the muzzle
            rc.leftGripPoint = q * RifleModelForeGrip * S;
            rc.leftGripThumb = Vector3.forward;  // thumb along the barrel
            rc.leftGripFingers = Vector3.right;  // fingers wrap across under the fore-end (palm up)
            rc.rightFistGrip = BeamSaberGripInHand;

            rc.viewController = suitRoot.GetComponentInChildren<CockpitViewController>(true);
            rc.cockpitSpace = interior.transform;
            rc.targetLock = interior.GetComponentInChildren<OrbitHUDTargetLock>(true);
            rc.fireButtons = rightStick != null ? rightStick.GetComponent<JoystickFingerButtons>() : null;
            rc.damage = 500;
            rc.maxShots = 15;
            rc.rechargeTime = 5f; // per "재장전 5초로 변경하자"
            rc.shotGlowMaterial = MakeEmissiveMat(new Color(1f, 0.3f, 0.65f), new Color(3.2f, 0.7f, 1.8f));
            rc.shotCoreMaterial = MakeEmissiveMat(new Color(1f, 0.92f, 0.97f), new Color(4f, 3f, 3.6f));
            rc.weaponHUD = interior.GetComponentInChildren<CockpitWeaponHUD>(true);

            modes.rifle = rc;
            Debug.Log("[Gundam] BEAM RIFLE: rifle " + (BeamRifleScale).ToString("F1") + " m, both hands (" + rHand.name + " grip, " + lHand.name +
                " fore-end), fire = right thumb button" + (rc.fireButtons != null ? "" : " (NOT FOUND)") + ", lock = " +
                (rc.targetLock != null ? "OrbitHUD" : "none") + ".");
        }

        static Transform BuildBeamRifle(Transform gundamRoot)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(BeamRifleModelPath);
            if (asset == null)
            {
                Debug.LogWarning("[Gundam] BEAM RIFLE: model not found at " + BeamRifleModelPath + " - rifle disabled.");
                return null;
            }
            GameObject root = new GameObject("BeamRifle");
            root.transform.SetParent(gundamRoot, false);
            float ls = gundamRoot.lossyScale.x > 0.0001f ? 1f / gundamRoot.lossyScale.x : 1f;
            root.transform.localScale = Vector3.one * ls; // 1 unit = 1 m
            root.layer = 0;

            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            model.name = "BeamRifle_Model";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = RifleModelToRoot;
            model.transform.localScale = Vector3.one * BeamRifleScale;

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(BeamRifleTexturePath);
            Material mat = MakeMat(Color.white);
            mat.name = "BeamRifle";
            if (tex != null) mat.mainTexture = tex;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            SetLayerRecursively(root, 0);
            root.SetActive(false); // shown only in BEAM RIFLE mode
            return root.transform;
        }

        static void HiltExtents(GameObject hilt, Transform space, out Vector3 min, out Vector3 max)
        {
            min = Vector3.one * float.MaxValue;
            max = Vector3.one * float.MinValue;
            foreach (Vector3 p in HiltPoints(hilt, space)) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            if (min.x > max.x) { min = Vector3.zero; max = Vector3.zero; }
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

        static void PlaceSpaceDebris(Transform viewer, ColonyStructure colony = null)
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
                new Vector4(75f, 12f, 560f, 260f),   // (was 35 deg - moved off the colony, straight ahead)
                new Vector4(105f, -16f, 650f, 180f),
                new Vector4(160f, 9f, 500f, 150f),
                new Vector4(215f, 24f, 720f, 300f),
                new Vector4(275f, -9f, 580f, 200f),
                new Vector4(300f, -26f, 620f, 160f), // (was 330 deg)
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
            field.colony = colony; // wreckage hidden while inside the colony
            Debug.Log("[Gundam] Placed " + spots.Length + " distant wreck pieces from " + models.Length + " model(s) in " + DebrisModelFolder + ".");
        }

        // ---------------------------------------------------------------
        // Space colony battlefield - per "우주공간에 거대한 구조물이 있으면 좋을거같아
        // 건담이 들어갈수있는 그래서 거기를 전장으로 사용하게", "콜로니를 엄청 크게
        // 만들어서 콜로니 안에 도시를", "조금 멀리있어도되 내가 가서 싸우면되니까",
        // and then "더 커야하고 더 자세하고 더 콜로니 같아야하고 콜로니 안에서는
        // 중력이 있을거야 진짜 엄청커야해 도시가 진짜크게 복잡한도시고 그래서
        // 도시에서 싸우는느낌이 있게" (collision: "부딪히게").
        //
        // A full-size Island-3 (O'Neill) colony like the ones in Gundam: 6.4 km
        // across and 12 km long, lying along +Z with its near end cap 1.5 km
        // ahead of the start. Outside: panelled hull with frame ribs and light
        // rings, three giant mirrors hinged at the far end, a docking spindle on
        // the near cap - and a huge war-damage BREACH torn in the near cap at
        // ground level, which is the way in. Inside: the hull alternates three
        // land strips and three glowing window strips; the bottom land strip is a
        // dense downtown (4 km x 2 km: a boulevard with an elevated highway down
        // the middle, cross highways, hundreds of setback towers / podium towers
        // / slabs with rooftop gear, parks and plazas on curb-high block plates)
        // fading into suburbs and farmland toward the far end, and the land curves
        // up on both sides like a real colony. The two land strips overhead are
        // city-light textures. Gravity inside (SuitCollision) pulls down onto it.
        //
        // Everything is procedural, low-poly and merged per material and per
        // 1 km chunk (the head camera renders the scene six times a frame for
        // the 360 view). Collision lives in ColonyStructure.
        // ---------------------------------------------------------------
        const float ColonyRadius = 3200f;
        const float ColonyLength = 12000f;
        const float ColonyNearZ = 1500f;          // near end cap (the Gundam starts at z = 20)
        const float ColonyBreachHalfWidth = 330f;  // breach in the near cap: |x| < this ...
        const float ColonyBreachTop = 430f;        // ... and y < this (ground at the breach is y = 0)
        const float ColonyDowntownDepth = 4600f;   // downtown runs this far in from the near cap
        const float ColonyDowntownHalf = 1005f;
        const float ColonyLandHalf = 1550f;        // bottom land strip (60 deg of hull) half width
        const string ColonyFolder = "Assets/Models/Colony";
        const float BuildingTile = 28f;            // m per facade-texture tile (8 floors of 3.5 m)

        class MeshAcc
        {
            public readonly System.Collections.Generic.List<Vector3> v = new System.Collections.Generic.List<Vector3>();
            public readonly System.Collections.Generic.List<Vector3> n = new System.Collections.Generic.List<Vector3>();
            public readonly System.Collections.Generic.List<Vector2> uv = new System.Collections.Generic.List<Vector2>();
            public readonly System.Collections.Generic.List<int> t = new System.Collections.Generic.List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                n.Add(normal); n.Add(normal); n.Add(normal); n.Add(normal);
                uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
                // Unity's front face of triangle (a,b,c) is along Cross(b-a, c-a); the
                // diagonals give the same sign and also work for fan (degenerate) quads.
                if (Vector3.Dot(Vector3.Cross(c - a, d - b), normal) >= 0f)
                {
                    t.Add(i); t.Add(i + 1); t.Add(i + 2);
                    t.Add(i); t.Add(i + 2); t.Add(i + 3);
                }
                else
                {
                    t.Add(i); t.Add(i + 2); t.Add(i + 1);
                    t.Add(i); t.Add(i + 3); t.Add(i + 2);
                }
            }

            public GameObject Build(string name, Transform parent, Material mat)
            {
                if (v.Count == 0) return null;
                UnityEngine.Mesh m = new UnityEngine.Mesh { name = name };
                if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
                m.RecalculateBounds();
                if (!s_colonyPreview)
                {
                    string path = ColonyFolder + "/" + name + ".asset";
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(m, path);
                }
                GameObject go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                MeshRenderer r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go;
            }
        }

        /// <summary>Per-material, per-1km-chunk mesh accumulators.</summary>
        class ChunkedAcc
        {
            readonly System.Collections.Generic.Dictionary<long, MeshAcc> _d = new System.Collections.Generic.Dictionary<long, MeshAcc>();
            public MeshAcc Get(int mat, float z)
            {
                long key = ((long)mat << 32) | (uint)Mathf.FloorToInt(z / 1000f);
                if (!_d.TryGetValue(key, out MeshAcc a)) { a = new MeshAcc(); _d[key] = a; }
                return a;
            }
            public void BuildAll(string prefix, Transform parent, Material[] mats)
            {
                foreach (var kv in _d)
                {
                    int mat = (int)(kv.Key >> 32);
                    int chunk = (int)(kv.Key & 0xffffffff);
                    kv.Value.Build(prefix + "_m" + mat + "_c" + chunk, parent, mats[mat]);
                }
            }
        }

        /// <summary>Cylinder band between angles a0..a1 (deg, 0 = +X, 90 = up) at 'radius'
        /// around the axis through axis0 (along +Z), from z0 to z1 (relative to axis0.z).</summary>
        static void AddCylinderBand(MeshAcc acc, Vector3 axis0, float radius, float a0, float a1, float z0, float z1,
            int segs, bool inward, float uvScale, int zSegs = 1)
        {
            for (int zs = 0; zs < zSegs; zs++)
            {
                float za = Mathf.Lerp(z0, z1, zs / (float)zSegs), zb = Mathf.Lerp(z0, z1, (zs + 1) / (float)zSegs);
                for (int i = 0; i < segs; i++)
                {
                    float t0 = Mathf.Lerp(a0, a1, i / (float)segs) * Mathf.Deg2Rad;
                    float t1 = Mathf.Lerp(a0, a1, (i + 1) / (float)segs) * Mathf.Deg2Rad;
                    Vector3 d0 = new Vector3(Mathf.Cos(t0), Mathf.Sin(t0), 0f);
                    Vector3 d1 = new Vector3(Mathf.Cos(t1), Mathf.Sin(t1), 0f);
                    Vector3 p00 = axis0 + d0 * radius + Vector3.forward * za, p01 = axis0 + d0 * radius + Vector3.forward * zb;
                    Vector3 p10 = axis0 + d1 * radius + Vector3.forward * za, p11 = axis0 + d1 * radius + Vector3.forward * zb;
                    Vector3 nrm = ((d0 + d1) * 0.5f).normalized * (inward ? -1f : 1f);
                    float u0 = radius * t0 / uvScale, u1 = radius * t1 / uvScale;
                    acc.Quad(p00, p10, p11, p01, nrm, new Vector2(u0, za / uvScale), new Vector2(u1, za / uvScale),
                        new Vector2(u1, zb / uvScale), new Vector2(u0, zb / uvScale));
                }
            }
        }

        /// <summary>Flat ring (annulus) in the plane z = zPlane (relative to axis0), facing +/-Z.</summary>
        static void AddAnnulus(MeshAcc acc, Vector3 axis0, float rIn, float rOut, float zPlane, int segs, float facing)
        {
            Vector3 nrm = Vector3.forward * facing;
            for (int i = 0; i < segs; i++)
            {
                float t0 = i / (float)segs * Mathf.PI * 2f, t1 = (i + 1) / (float)segs * Mathf.PI * 2f;
                Vector3 d0 = new Vector3(Mathf.Cos(t0), Mathf.Sin(t0), 0f), d1 = new Vector3(Mathf.Cos(t1), Mathf.Sin(t1), 0f);
                Vector3 c = axis0 + Vector3.forward * zPlane;
                Vector3 a = c + d0 * rIn, b = c + d1 * rIn, e = c + d1 * rOut, f = c + d0 * rOut;
                acc.Quad(a, b, e, f, nrm, new Vector2(a.x, a.y) / 200f, new Vector2(b.x, b.y) / 200f, new Vector2(e.x, e.y) / 200f, new Vector2(f.x, f.y) / 200f);
            }
        }

        /// <summary>End cap as a polar grid (so a breach can be cut out of it): tiles for
        /// which skip(x, y) is true are left out. Both faces.</summary>
        static void AddCapGrid(MeshAcc acc, Vector3 axis0, float radius, float zPlane, int rings, int sectors,
            System.Func<float, float, bool> skip)
        {
            Vector3 c = axis0 + Vector3.forward * zPlane;
            int prevSecs = 8;
            for (int ri = 0; ri < rings; ri++)
            {
                float r0 = radius * ri / rings, r1 = radius * (ri + 1) / rings;
                int secs = Mathf.Max(8, Mathf.RoundToInt(sectors * (ri + 1) / (float)rings));
                // Rings have different sector counts, so their straight edges don't
                // meet exactly - pull each ring's inner edge inside the previous
                // ring's outer chords (hairline cracks let space show through).
                if (ri > 0) r0 = Mathf.Max(0f, r0 * Mathf.Cos(Mathf.PI / prevSecs) - 1.5f);
                prevSecs = secs;
                for (int si = 0; si < secs; si++)
                {
                    float t0 = si / (float)secs * Mathf.PI * 2f, t1 = (si + 1) / (float)secs * Mathf.PI * 2f;
                    Vector3 d0 = new Vector3(Mathf.Cos(t0), Mathf.Sin(t0), 0f), d1 = new Vector3(Mathf.Cos(t1), Mathf.Sin(t1), 0f);
                    Vector3 a = c + d0 * r0, b = c + d1 * r0, e = c + d1 * r1, f = c + d0 * r1;
                    Vector3 mid = (a + b + e + f) * 0.25f;
                    if (skip != null && skip(mid.x, mid.y)) continue;
                    Vector2 ua = new Vector2(a.x, a.y) / 200f, ub = new Vector2(b.x, b.y) / 200f, ue = new Vector2(e.x, e.y) / 200f, uf = new Vector2(f.x, f.y) / 200f;
                    acc.Quad(a, b, e, f, Vector3.back, ua, ub, ue, uf);
                    acc.Quad(a, b, e, f, Vector3.forward, ua, ub, ue, uf);
                }
            }
        }

        /// <summary>Axis-aligned box (no bottom), facade UVs scaled by size; roof mapped to a dark texel.</summary>
        static void AddBuildingBox(MeshAcc acc, Vector3 mn, Vector3 mx, Vector2 uvOffset)
        {
            float T = BuildingTile;
            Vector2 roof = new Vector2(0.004f, 0.004f);
            float w = mx.x - mn.x, d = mx.z - mn.z, h = mx.y - mn.y;
            Vector2 o = uvOffset;
            acc.Quad(new Vector3(mx.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mx.z), Vector3.forward,
                o, o + new Vector2(w / T, 0f), o + new Vector2(w / T, h / T), o + new Vector2(0f, h / T));
            acc.Quad(new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mn.z), Vector3.back,
                o, o + new Vector2(w / T, 0f), o + new Vector2(w / T, h / T), o + new Vector2(0f, h / T));
            acc.Quad(new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mn.z), Vector3.right,
                o, o + new Vector2(d / T, 0f), o + new Vector2(d / T, h / T), o + new Vector2(0f, h / T));
            acc.Quad(new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mn.z), new Vector3(mn.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mx.z), Vector3.left,
                o, o + new Vector2(d / T, 0f), o + new Vector2(d / T, h / T), o + new Vector2(0f, h / T));
            acc.Quad(new Vector3(mn.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mn.x, mx.y, mx.z), Vector3.up,
                roof, roof, roof, roof);
        }

        /// <summary>Plain box with all 6 faces and tiny UVs (solid-colour materials).</summary>
        static void AddSolidBox(MeshAcc acc, Vector3 mn, Vector3 mx)
        {
            Vector2 z = Vector2.zero;
            acc.Quad(new Vector3(mx.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mx.z), Vector3.forward, z, z, z, z);
            acc.Quad(new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mn.z), Vector3.back, z, z, z, z);
            acc.Quad(new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mn.z), Vector3.right, z, z, z, z);
            acc.Quad(new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mn.z), new Vector3(mn.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mx.z), Vector3.left, z, z, z, z);
            acc.Quad(new Vector3(mn.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mn.x, mx.y, mx.z), Vector3.up, z, z, z, z);
            acc.Quad(new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mn.x, mn.y, mn.z), Vector3.down, z, z, z, z);
        }

        /// <summary>Four-sided tree (pyramid on a short trunk block).</summary>
        static void AddTree(MeshAcc acc, Vector3 basePos, float h, float r)
        {
            Vector3 top = basePos + Vector3.up * h;
            Vector3 b0 = basePos + new Vector3(-r, h * 0.25f, -r), b1 = basePos + new Vector3(r, h * 0.25f, -r);
            Vector3 b2 = basePos + new Vector3(r, h * 0.25f, r), b3 = basePos + new Vector3(-r, h * 0.25f, r);
            Vector2 z = Vector2.zero;
            acc.Quad(b0, b1, top, top, Vector3.back, z, z, z, z);
            acc.Quad(b1, b2, top, top, Vector3.right, z, z, z, z);
            acc.Quad(b2, b3, top, top, Vector3.forward, z, z, z, z);
            acc.Quad(b3, b0, top, top, Vector3.left, z, z, z, z);
        }

        /// <summary>Generated texture, saved as a PNG asset (repeat wrap).</summary>
        /// <summary>Preview build (BuildColonyPreview): meshes/textures stay in memory, no asset writes.</summary>
        static bool s_colonyPreview;

        /// <summary>Builds ONLY the space colony into the active scene with nothing
        /// written to the project (meshes and textures stay in memory) - for looking
        /// at it in a scratch scene. The real build is Gundam > Build Cockpit Scene.</summary>
        public static ColonyStructure BuildColonyPreview()
        {
            s_colonyPreview = true;
            try { return BuildColony(); }
            finally { s_colonyPreview = false; }
        }

        static Texture2D MakeColonyTexture(string name, int size, System.Func<int, int, System.Random, Color> pixel, int seed)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            System.Random rnd = new System.Random(seed);
            Color[] px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Color c = pixel(x, y, rnd);
                    c.a = 1f;
                    px[y * size + x] = c;
                }
            tex.SetPixels(px);
            if (s_colonyPreview)
            {
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.anisoLevel = 4;
                tex.Apply(true);
                return tex;
            }
            tex.Apply();
            System.IO.Directory.CreateDirectory(ColonyFolder);
            string path = ColonyFolder + "/" + name + ".png";
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti != null)
            {
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.filterMode = FilterMode.Bilinear;
                ti.maxTextureSize = size;
                ti.mipmapEnabled = true;
                ti.anisoLevel = 4;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Facade: an 8 x 8 grid of window cells, 'lit' of them lit.</summary>
        static Texture2D MakeFacadeTexture(string name, Color wall, Color frame, Color lit1, Color lit2, Color unlit, float lit,
            int winX0, int winX1, int winY0, int winY1, int seed)
        {
            const int N = 128, cell = 16;
            bool[,] on = new bool[8, 8];
            Color[,] col = new Color[8, 8];
            System.Random r0 = new System.Random(seed * 7 + 1);
            for (int cy = 0; cy < 8; cy++)
                for (int cx = 0; cx < 8; cx++)
                {
                    on[cx, cy] = r0.NextDouble() < lit;
                    float b = 0.5f + (float)r0.NextDouble() * 0.5f;
                    col[cx, cy] = (r0.NextDouble() < 0.7 ? lit1 : lit2) * b;
                }
            return MakeColonyTexture(name, N, (x, y, rnd) =>
            {
                int cx = x / cell, cy = y / cell, lx = x % cell, ly = y % cell;
                if (x < 2 && y < 2) return new Color(0.05f, 0.05f, 0.06f); // dark texel for roofs
                if (ly == 0 || ly == cell - 1) return frame;                  // floor slab line
                if (lx >= winX0 && lx < winX1 && ly >= winY0 && ly < winY1) return on[cx, cy] ? col[cx, cy] : unlit;
                return wall;
            }, seed);
        }

        static Material MakeTexturedMat(Color tint, Texture2D tex, float emission, float smoothness = 0.3f)
        {
            Material m = MakeMat(tint);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (tex != null)
            {
                m.mainTexture = tex;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                if (emission > 0f)
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetTexture("_EmissionMap", tex);
                    m.SetColor("_EmissionColor", Color.white * emission);
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
            }
            return m;
        }

        // ----- Colony v3 detail helpers (per "디테일을 더 살려줘 지금은 너무 비현실적이야") -----

        static float Hash01(int x, int y, int s)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + s * 982451653;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        struct FacadeStyle
        {
            public Color wall, wall2, frame, glassTop, glassBottom;
            public int wx0, wx1, wy0, wy1;
            public float lit, grime;
            public bool brick, balcony;
        }

        /// <summary>0 office concrete, 1 glass curtain wall, 2 residential, 3 brick, 4 dark modern.</summary>
        static FacadeStyle FacadeStyleFor(int style)
        {
            switch (style)
            {
                case 1: return new FacadeStyle { wall = new Color(0.33f, 0.36f, 0.4f), wall2 = new Color(0.28f, 0.3f, 0.33f), frame = new Color(0.62f, 0.65f, 0.68f),
                    glassTop = new Color(0.62f, 0.74f, 0.86f), glassBottom = new Color(0.16f, 0.24f, 0.33f), wx0 = 1, wx1 = 31, wy0 = 5, wy1 = 31, lit = 0.12f, grime = 0.05f };
                case 2: return new FacadeStyle { wall = new Color(0.8f, 0.75f, 0.66f), wall2 = new Color(0.66f, 0.62f, 0.56f), frame = new Color(0.9f, 0.9f, 0.88f),
                    glassTop = new Color(0.5f, 0.58f, 0.66f), glassBottom = new Color(0.14f, 0.16f, 0.2f), wx0 = 8, wx1 = 24, wy0 = 10, wy1 = 27, lit = 0.2f, grime = 0.18f, balcony = true };
                case 3: return new FacadeStyle { wall = new Color(0.56f, 0.32f, 0.24f), wall2 = new Color(0.62f, 0.6f, 0.56f), frame = new Color(0.86f, 0.84f, 0.8f),
                    glassTop = new Color(0.46f, 0.54f, 0.62f), glassBottom = new Color(0.12f, 0.13f, 0.16f), wx0 = 9, wx1 = 23, wy0 = 9, wy1 = 27, lit = 0.18f, grime = 0.22f, brick = true };
                case 4: return new FacadeStyle { wall = new Color(0.12f, 0.13f, 0.15f), wall2 = new Color(0.2f, 0.21f, 0.23f), frame = new Color(0.78f, 0.8f, 0.82f),
                    glassTop = new Color(0.4f, 0.48f, 0.58f), glassBottom = new Color(0.06f, 0.08f, 0.11f), wx0 = 2, wx1 = 30, wy0 = 6, wy1 = 30, lit = 0.1f, grime = 0.04f };
                default: return new FacadeStyle { wall = new Color(0.64f, 0.64f, 0.62f), wall2 = new Color(0.5f, 0.5f, 0.49f), frame = new Color(0.3f, 0.31f, 0.33f),
                    glassTop = new Color(0.55f, 0.65f, 0.76f), glassBottom = new Color(0.12f, 0.16f, 0.22f), wx0 = 3, wx1 = 29, wy0 = 9, wy1 = 28, lit = 0.16f, grime = 0.2f };
            }
        }

        /// <summary>Facade texture (8 floors x 8 bays, 256 px = 28 m) with sky
        /// reflections in the glass, frames, mullions, blinds, slab bands, vertical
        /// grime streaks and a few lit rooms - plus a matching emission map that
        /// only lights those rooms (the walls themselves never glow).</summary>
        static void MakeFacade(string name, int style, int seed, out Texture2D baseTex, out Texture2D emisTex)
        {
            FacadeStyle st = FacadeStyleFor(style);
            const int N = 256, cell = 32;
            bool[,] on = new bool[8, 8];
            float[,] blind = new float[8, 8];
            Color[,] litCol = new Color[8, 8];
            System.Random r0 = new System.Random(seed * 7 + 1);
            for (int cy = 0; cy < 8; cy++)
                for (int cx = 0; cx < 8; cx++)
                {
                    on[cx, cy] = r0.NextDouble() < st.lit;
                    blind[cx, cy] = r0.NextDouble() < 0.35 ? 0.25f + 0.5f * (float)r0.NextDouble() : 0f;
                    float b = 0.6f + 0.4f * (float)r0.NextDouble();
                    litCol[cx, cy] = (r0.NextDouble() < 0.75 ? new Color(1f, 0.86f, 0.62f) : new Color(0.82f, 0.9f, 1f)) * b;
                }
            baseTex = MakeColonyTexture(name, N, (x, y, rnd) =>
            {
                if (x < 3 && y < 3) return new Color(0.2f, 0.2f, 0.21f); // roof texel
                int cx = x / cell, cy = y / cell, lx = x % cell, ly = y % cell;
                float grime = st.grime * Mathf.Clamp01(Mathf.PerlinNoise(x * 0.11f + seed, y * 0.012f) * 1.4f - 0.3f);
                float n = ((float)rnd.NextDouble() - 0.5f) * 0.035f;
                bool inWin = lx >= st.wx0 && lx < st.wx1 && ly >= st.wy0 && ly < st.wy1;
                if (inWin)
                {
                    bool edge = lx == st.wx0 || lx == st.wx1 - 1 || ly == st.wy0 || ly == st.wy1 - 1;
                    bool mullion = (st.wx1 - st.wx0) > 16 && (lx == (st.wx0 + st.wx1) / 2);
                    if (edge || mullion) return st.frame * (1f - grime * 0.6f);
                    float v = (ly - st.wy0) / (float)(st.wy1 - st.wy0);
                    if (on[cx, cy]) return litCol[cx, cy] * 0.9f;
                    float bl = blind[cx, cy];
                    if (bl > 0f && v > 1f - bl) return new Color(0.74f, 0.71f, 0.62f) * (0.92f + n);
                    Color g = Color.Lerp(st.glassBottom, st.glassTop, v * v);
                    float band = ((x + y * 0.7f + seed * 13) % 90f) / 90f;
                    float streak = Mathf.Max(0f, 1f - Mathf.Abs(band - 0.5f) * 9f) * 0.1f;
                    return g + new Color(streak + n, streak + n, streak + n);
                }
                Color w = ly < 4 ? st.wall2 : st.wall;
                if (st.balcony && ly >= st.wy0 - 5 && ly < st.wy0 && lx >= st.wx0 - 4 && lx < st.wx1 + 4) w = ly == st.wy0 - 5 ? st.frame : st.wall2 * 0.85f;
                if (st.brick && ly >= 4)
                {
                    int by = y / 4, off = (by % 2) * 4;
                    bool mortar = y % 4 == 0 || (x + off) % 8 == 0;
                    w = mortar ? st.wall2 : st.wall * (0.86f + 0.26f * Hash01((x + off) / 8, by, seed));
                }
                return w * (1f - grime) + new Color(n, n, n);
            }, seed);
            emisTex = MakeColonyTexture(name + "_Emission", N, (x, y, rnd) =>
            {
                int cx = x / cell, cy = y / cell, lx = x % cell, ly = y % cell;
                bool inWin = lx > st.wx0 && lx < st.wx1 - 1 && ly > st.wy0 && ly < st.wy1 - 1;
                return inWin && on[cx, cy] ? litCol[cx, cy] : Color.black;
            }, seed + 1);
        }

        /// <summary>Street-level shop fronts (one 256 px tile = 28 m x one band): lit
        /// display windows between pillars, a colored sign strip above each shop.</summary>
        static void MakeStorefront(out Texture2D baseTex, out Texture2D emisTex)
        {
            Color[] signs = { new Color(0.55f, 0.2f, 0.16f), new Color(0.18f, 0.3f, 0.5f), new Color(0.7f, 0.6f, 0.35f),
                              new Color(0.22f, 0.4f, 0.28f), new Color(0.85f, 0.84f, 0.8f), new Color(0.3f, 0.3f, 0.32f) };
            System.Func<int, int, bool, Color> px = (x, y, emis) =>
            {
                int shop = x / 64, lx = x % 64;
                Color sign = signs[(shop * 7 + 3) % signs.Length];
                bool pillar = lx < 5;
                if (pillar) return emis ? Color.black : new Color(0.55f, 0.54f, 0.52f);
                if (y > 200) return emis ? Color.black : new Color(0.5f, 0.5f, 0.49f);         // fascia above
                if (y > 178) return emis ? sign * 0.35f : sign;                                  // sign band
                if (y > 162) return emis ? Color.black : new Color(0.3f, 0.3f, 0.32f);         // awning edge
                bool door = lx > 26 && lx < 40 && y < 120;
                if (door) return emis ? new Color(0.5f, 0.45f, 0.35f) : new Color(0.25f, 0.24f, 0.22f);
                if (y < 14) return emis ? Color.black : new Color(0.45f, 0.45f, 0.44f);        // plinth
                float v = y / 162f;
                Color inside = Color.Lerp(new Color(0.95f, 0.85f, 0.65f), new Color(0.75f, 0.8f, 0.85f), v);
                return emis ? inside * 0.75f : inside * 0.8f;
            };
            baseTex = MakeColonyTexture("Colony_Storefront", 256, (x, y, r) => px(x, y, false), 31);
            emisTex = MakeColonyTexture("Colony_Storefront_Emission", 256, (x, y, r) => px(x, y, true), 32);
        }

        static Material MakeEmissionMapMat(Texture2D baseTex, Texture2D emisTex, float emission, float smoothness, float metallic = 0f)
        {
            Material m = MakeTexturedMat(Color.white, baseTex, 0f, smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (emisTex != null)
            {
                m.EnableKeyword("_EMISSION");
                m.SetTexture("_EmissionMap", emisTex);
                m.SetColor("_EmissionColor", Color.white * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            return m;
        }

        /// <summary>Box band (4 sides, no top/bottom), u = length / uTile, v 0..1 over its height.</summary>
        static void AddBandBox(MeshAcc acc, Vector3 mn, Vector3 mx, float uTile, float u0)
        {
            float w = (mx.x - mn.x) / uTile, d = (mx.z - mn.z) / uTile;
            acc.Quad(new Vector3(mx.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mx.z), Vector3.forward,
                new Vector2(u0, 0f), new Vector2(u0 + w, 0f), new Vector2(u0 + w, 1f), new Vector2(u0, 1f));
            acc.Quad(new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mn.z), Vector3.back,
                new Vector2(u0, 0f), new Vector2(u0 + w, 0f), new Vector2(u0 + w, 1f), new Vector2(u0, 1f));
            acc.Quad(new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mn.z), Vector3.right,
                new Vector2(u0, 0f), new Vector2(u0 + d, 0f), new Vector2(u0 + d, 1f), new Vector2(u0, 1f));
            acc.Quad(new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mn.z), new Vector3(mn.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mx.z), Vector3.left,
                new Vector2(u0, 0f), new Vector2(u0 + d, 0f), new Vector2(u0 + d, 1f), new Vector2(u0, 1f));
        }

        /// <summary>Box with every UV at one point (for picking a color from a palette texture).</summary>
        static void AddColorBox(MeshAcc acc, Vector3 mn, Vector3 mx, Vector2 uv)
        {
            int start = acc.uv.Count;
            AddSolidBox(acc, mn, mx);
            for (int i = start; i < acc.uv.Count; i++) acc.uv[i] = uv;
        }

        /// <summary>Oriented box: center, half extents along (right, up, forward).</summary>
        static void AddOrientedBox(MeshAcc acc, Vector3 c, Vector3 right, Vector3 up, Vector3 fwd, Vector3 half, Vector2 uv)
        {
            Vector3 r = right.normalized * half.x, u = up.normalized * half.y, f = fwd.normalized * half.z;
            Vector3[] p =
            {
                c - r - u - f, c + r - u - f, c + r + u - f, c - r + u - f,
                c - r - u + f, c + r - u + f, c + r + u + f, c - r + u + f,
            };
            acc.Quad(p[0], p[1], p[2], p[3], -f.normalized, uv, uv, uv, uv);
            acc.Quad(p[5], p[4], p[7], p[6], f.normalized, uv, uv, uv, uv);
            acc.Quad(p[1], p[5], p[6], p[2], r.normalized, uv, uv, uv, uv);
            acc.Quad(p[4], p[0], p[3], p[7], -r.normalized, uv, uv, uv, uv);
            acc.Quad(p[3], p[2], p[6], p[7], u.normalized, uv, uv, uv, uv);
            acc.Quad(p[4], p[5], p[1], p[0], -u.normalized, uv, uv, uv, uv);
        }

        /// <summary>A painted mark lying on the curved land (x0..x1, z0..z1).</summary>
        static void AddGroundPaint(MeshAcc acc, Vector3 axis0, float x0, float x1, float z0, float z1)
        {
            AddCylinderBand(acc, axis0, ColonyRadius - 0.15f, LandAngle(x0), LandAngle(x1), z0 - axis0.z, z1 - axis0.z, 1, true, 30f);
        }

        const string ColonyPropFolder = "Assets/Models/ColonyProps";
        const int ColonyDetailLayer = 27; // street-level detail, distance-culled by ColonyAtmosphere

        /// <summary>
        /// VARCO models from Assets/Models/ColonyProps (per "파공 주변 잔해나 항구
        /// 크레인 같은 외관 소품"): wreckage around the breach (always shown), port
        /// modules around the near docking spindle, cranes on the near end cap and
        /// antenna towers on the hull (exterior, hidden from inside), and rubble of
        /// collapsed buildings just inside the breach (collidable). Missing models
        /// are skipped. Returns how many were placed.
        /// </summary>
        static int PlaceColonyVarcoProps(Transform root, Transform exterior, Transform breachRoot, Vector3 axis0,
            System.Action<Vector3, Vector3> collide)
        {
            GameObject wreck = LoadColonyProp("BreachWreck"), crane = LoadColonyProp("DockingCrane"), port = LoadColonyProp("PortModule");
            GameObject antenna = LoadColonyProp("HullAntenna"), rubble = LoadColonyProp("Rubble");
            System.Random r = new System.Random(515);
            int n = 0;
            float zN = axis0.z, R = ColonyRadius;
            Bounds b;

            // Wreckage blown out around the breach rim, jutting from the cap.
            if (wreck != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    float ang = Mathf.PI * (0.06f + 0.88f * i / 5f);
                    Vector3 p = new Vector3(Mathf.Cos(ang) * (ColonyBreachHalfWidth + 55f), Mathf.Max(25f, Mathf.Sin(ang) * (ColonyBreachTop + 45f)), zN - 8f);
                    Vector3 outDir = new Vector3(p.x, p.y - 120f, 0f).normalized;
                    Vector3 up = (-Vector3.forward * 0.7f + outDir * 0.7f + Random01Vec(r) * 0.3f).normalized;
                    b = PlaceColonyProp(wreck, breachRoot, p, up, (float)r.NextDouble() * 360f, 80f + (float)r.NextDouble() * 60f, "BreachWreck_" + i, true);
                    if (b.size != Vector3.zero) n++;
                }
            }
            // Rubble of collapsed buildings just inside the breach.
            if (rubble != null)
            {
                Transform inner = new GameObject("InteriorProps").transform;
                inner.SetParent(root, false);
                float[] xs = { -200f, 175f, -300f, 290f };
                float[] zs = { 140f, 220f, 330f, 120f };
                for (int i = 0; i < xs.Length; i++)
                {
                    Vector3 p = new Vector3(xs[i], ColonyGroundY(xs[i]) - 1.5f, zN + zs[i]);
                    b = PlaceColonyProp(rubble, inner, p, Vector3.up, (float)r.NextDouble() * 360f, 70f + (float)r.NextDouble() * 30f, "Rubble_" + i, true);
                    if (b.size == Vector3.zero) continue;
                    collide(b.min, b.max);
                    n++;
                }
            }
            // Port modules around the near docking spindle (radius 280, 900 m long).
            if (port != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    float ang = (i * 60f + 30f) * Mathf.Deg2Rad;
                    Vector3 d = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                    Vector3 p = axis0 + d * 281f + Vector3.forward * (-280f - (i % 2) * 330f);
                    b = PlaceColonyProp(port, exterior, p, d, 0f, 260f, "PortModule_" + i);
                    if (b.size != Vector3.zero) n++;
                }
            }
            // Cranes standing out of the near end cap's outer face.
            if (crane != null)
            {
                float[] angs = { 20f, 70f, 110f, 160f, 200f, 340f };
                for (int i = 0; i < angs.Length; i++)
                {
                    float ang = angs[i] * Mathf.Deg2Rad;
                    float rr = 700f + (i % 3) * 420f;
                    Vector3 p = axis0 + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * rr + Vector3.forward * -3f;
                    b = PlaceColonyProp(crane, exterior, p, -Vector3.forward, angs[i] + 90f, 460f, "DockingCrane_" + i);
                    if (b.size != Vector3.zero) n++;
                }
            }
            // Antenna / sensor towers on the outer hull's land strips.
            if (antenna != null)
            {
                float[] angs = { 30f, 150f, 270f, 12f, 138f, 255f };
                float[] zs = { 1200f, 3600f, 6000f, 8400f, 10200f, 4800f };
                for (int i = 0; i < angs.Length; i++)
                {
                    float ang = angs[i] * Mathf.Deg2Rad;
                    Vector3 d = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                    Vector3 p = axis0 + d * (R + 2f) + Vector3.forward * zs[i];
                    b = PlaceColonyProp(antenna, exterior, p, d, (float)r.NextDouble() * 360f, 300f, "HullAntenna_" + i);
                    if (b.size != Vector3.zero) n++;
                }
            }
            return n;
        }

        static GameObject LoadColonyProp(string name)
        {
            if (!AssetDatabase.IsValidFolder(ColonyPropFolder)) return null;
            foreach (string g in AssetDatabase.FindAssets("t:Model", new[] { ColonyPropFolder }))
            {
                GameObject m = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g));
                if (m != null && m.name == name) return m;
            }
            return null;
        }

        /// <summary>Places a VARCO prop: its largest dimension = size, its base on
        /// 'basePoint', its up axis along 'up', turned 'yaw' about it. Returns the
        /// world bounds (for collision), or an empty Bounds if the model is missing.</summary>
        static Bounds PlaceColonyProp(GameObject model, Transform parent, Vector3 basePoint, Vector3 up, float yaw, float size, string label, bool sinkBase = false)
        {
            if (model == null) return new Bounds();
            GameObject holder = new GameObject(label);
            holder.transform.SetParent(parent, false);
            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.transform.SetParent(holder.transform, false);
            inst.transform.localPosition = Vector3.zero;
            foreach (Collider col in inst.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(col);
            Renderer[] rends = inst.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) { UnityEngine.Object.DestroyImmediate(holder); return new Bounds(); }
            Bounds b = rends[0].bounds;
            for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float s = maxDim > 1e-4f ? size / maxDim : 1f;
            // Model base (bottom center, model space at holder identity) to the origin.
            Vector3 baseLocal = new Vector3(b.center.x, b.min.y + (sinkBase ? b.size.y * 0.08f : 0f), b.center.z);
            inst.transform.localPosition = -baseLocal;
            holder.transform.localScale = Vector3.one * s;
            holder.transform.rotation = Quaternion.FromToRotation(Vector3.up, up.normalized) * Quaternion.Euler(0f, yaw, 0f);
            holder.transform.position = basePoint;
            foreach (Renderer r in rends)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            b = rends[0].bounds;
            for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);
            return b;
        }

        static Vector3 Random01Vec(System.Random r) =>
            new Vector3((float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f) * 2f;

        /// <summary>Simple gable roof (two slopes + two gable ends) over a box top, ridge along the longer side.</summary>
        static void AddGableRoof(MeshAcc acc, Vector3 mn, Vector3 mx, float h, Vector2 uv)
        {
            float y0 = mx.y, y1 = mx.y + h;
            bool alongZ = (mx.z - mn.z) >= (mx.x - mn.x);
            float o = 0.6f; // eaves overhang
            if (alongZ)
            {
                float cx = (mn.x + mx.x) * 0.5f;
                Vector3 a = new Vector3(mn.x - o, y0, mn.z - o), b = new Vector3(mn.x - o, y0, mx.z + o), c = new Vector3(cx, y1, mx.z + o), d = new Vector3(cx, y1, mn.z - o);
                Vector3 e = new Vector3(mx.x + o, y0, mn.z - o), f = new Vector3(mx.x + o, y0, mx.z + o);
                acc.Quad(a, b, c, d, new Vector3(-h, cx - mn.x, 0f).normalized, uv, uv, uv, uv);
                acc.Quad(f, e, d, c, new Vector3(h, cx - mn.x, 0f).normalized, uv, uv, uv, uv);
                acc.Quad(a, e, d, d, Vector3.back, uv, uv, uv, uv);
                acc.Quad(f, b, c, c, Vector3.forward, uv, uv, uv, uv);
            }
            else
            {
                float cz = (mn.z + mx.z) * 0.5f;
                Vector3 a = new Vector3(mn.x - o, y0, mn.z - o), b = new Vector3(mx.x + o, y0, mn.z - o), c = new Vector3(mx.x + o, y1, cz), d = new Vector3(mn.x - o, y1, cz);
                Vector3 e = new Vector3(mn.x - o, y0, mx.z + o), f = new Vector3(mx.x + o, y0, mx.z + o);
                acc.Quad(a, b, c, d, new Vector3(0f, cz - mn.z, -h).normalized, uv, uv, uv, uv);
                acc.Quad(f, e, d, c, new Vector3(0f, cz - mn.z, h).normalized, uv, uv, uv, uv);
                acc.Quad(e, a, d, d, Vector3.left, uv, uv, uv, uv);
                acc.Quad(b, f, c, c, Vector3.right, uv, uv, uv, uv);
            }
        }

        /// <summary>AddTree with every UV at one palette point.</summary>
        static void AddTreeUV(MeshAcc acc, Vector3 basePos, float h, float r, Vector2 uv)
        {
            int start = acc.uv.Count;
            AddTree(acc, basePos, h, r);
            for (int i = start; i < acc.uv.Count; i++) acc.uv[i] = uv;
        }

        const string ColonyBuildingFolder = "Assets/Models/ColonyBuildings";
        const int ColonyLandmarkUsesPerModel = 2;

        /// <summary>VARCO building models for the colony city (empty if none).</summary>
        static GameObject[] LoadColonyBuildingModels()
        {
            if (!AssetDatabase.IsValidFolder(ColonyBuildingFolder)) return new GameObject[0];
            return AssetDatabase.FindAssets("t:Model", new[] { ColonyBuildingFolder })
                .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null)
                .OrderBy(m => m.name)
                .ToArray();
        }

        /// <summary>Angle (deg) of the bottom land at x (270 = straight down from the axis).</summary>
        static float LandAngle(float x) => 270f + Mathf.Asin(Mathf.Clamp(x / ColonyRadius, -1f, 1f)) * Mathf.Rad2Deg;

        static float ColonyGroundY(float x) => ColonyRadius - Mathf.Sqrt(Mathf.Max(0f, ColonyRadius * ColonyRadius - x * x));

        static ColonyStructure BuildColony()
        {
            if (!s_colonyPreview)
            {
                System.IO.Directory.CreateDirectory(ColonyFolder);
                AssetDatabase.Refresh();
                // Previously generated colony meshes (chunk counts change between builds).
                foreach (string g in AssetDatabase.FindAssets("t:Mesh", new[] { ColonyFolder }))
                    AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(g));
            }
            GameObject root = new GameObject("SpaceColony");
            Vector3 axis0 = new Vector3(0f, ColonyRadius, ColonyNearZ); // near cap, on the axis; ground at x=0 is y=0
            float R = ColonyRadius, L = ColonyLength, zN = ColonyNearZ, zF = ColonyNearZ + ColonyLength;
            System.Random rnd = new System.Random(80085);

            // --- Textures (v3, per "디테일을 더 살려줘 지금은 너무 비현실적이야": 256 px,
            //     weathering, sky reflections, and emission maps that light only the
            //     lit rooms / shops - the walls themselves no longer glow) ---
            string[] facNames = { "Facade_Office", "Facade_Glass", "Facade_Residential", "Facade_Brick", "Facade_DarkModern" };
            Texture2D[] facBase = new Texture2D[5], facEmis = new Texture2D[5];
            for (int i = 0; i < 5; i++) MakeFacade(facNames[i], i, 11 + i, out facBase[i], out facEmis[i]);
            MakeStorefront(out Texture2D shopBase, out Texture2D shopEmis);
            Texture2D groundTex = MakeColonyTexture("Colony_Asphalt", 256, (x, y, r) =>
            {
                float blot = Mathf.PerlinNoise(x * 0.03f, y * 0.03f) * 0.05f + Mathf.PerlinNoise(x * 0.15f + 7f, y * 0.15f) * 0.03f;
                float n = 0.12f + blot + (float)r.NextDouble() * 0.035f;
                float crack = Mathf.Abs(Mathf.PerlinNoise(x * 0.045f + 3f, y * 0.045f + 9f) - 0.5f);
                if (crack < 0.012f) n *= 0.6f;
                return new Color(n, n, n * 1.04f);
            }, 21);
            Texture2D paverTex = MakeColonyTexture("Colony_Pavers", 128, (x, y, r) =>
            {
                bool joint = x % 32 == 0 || y % 16 == 0;
                float n = 0.55f + (float)r.NextDouble() * 0.05f + (Hash01(x / 32, y / 16, 5) - 0.5f) * 0.08f;
                if (joint) n *= 0.7f;
                return new Color(n, n * 0.98f, n * 0.94f);
            }, 25);
            Texture2D grassTex = MakeColonyTexture("Colony_Grass", 128, (x, y, r) =>
            {
                float p = Mathf.PerlinNoise(x * 0.08f, y * 0.08f);
                float n = (float)r.NextDouble() * 0.06f;
                return new Color(0.16f + p * 0.08f + n, 0.3f + p * 0.12f + n, 0.12f + n * 0.5f);
            }, 26);
            // 4 x 4 color palette for cars, roofs, poles and trees.
            Color[] pal =
            {
                new Color(0.85f, 0.85f, 0.83f), new Color(0.55f, 0.57f, 0.6f), new Color(0.08f, 0.08f, 0.09f), new Color(0.6f, 0.08f, 0.07f),
                new Color(0.12f, 0.22f, 0.45f), new Color(0.9f, 0.7f, 0.1f), new Color(0.12f, 0.25f, 0.16f), new Color(0.33f, 0.35f, 0.22f),
                new Color(0.42f, 0.16f, 0.12f), new Color(0.24f, 0.26f, 0.3f), new Color(0.62f, 0.3f, 0.18f), new Color(0.04f, 0.035f, 0.03f),
                new Color(0.2f, 0.42f, 0.7f), new Color(0.3f, 0.2f, 0.12f), new Color(0.1f, 0.24f, 0.09f), new Color(0.2f, 0.36f, 0.14f),
            };
            Texture2D palTex = MakeColonyTexture("Colony_Palette", 64, (x, y, r) => pal[(y / 16) * 4 + (x / 16)], 27);
            System.Func<int, Vector2> Pal = i => new Vector2(((i % 4) + 0.5f) / 4f, ((i / 4) + 0.5f) / 4f);
            // Overhead land seen from below: a daytime city / farmland pattern (the
            // air-haze from ColonyAtmosphere does the rest).
            Texture2D cityLightsTex = MakeColonyTexture("Colony_UpperLandDay", 256, (x, y, r) =>
            {
                int bx = x / 32, by = y / 32, lx = x % 32, ly = y % 32;
                bool mainRoad = x % 128 < 3 || y % 128 < 3;
                bool road = lx < 2 || ly < 2;
                if (mainRoad) return new Color(0.5f, 0.5f, 0.5f);
                if (road) return new Color(0.4f, 0.4f, 0.41f);
                float kind = Hash01(bx, by, 3);
                if (kind < 0.18f) return new Color(0.2f, 0.34f, 0.16f) * (0.9f + 0.2f * (float)r.NextDouble());  // park
                if (kind < 0.22f) return new Color(0.2f, 0.32f, 0.42f);                                            // pond
                // rooftops: small random squares
                float h = Hash01(x / 6, y / 6, 4);
                float g = 0.42f + h * 0.28f;
                return new Color(g, g * 0.98f, g * 0.94f);
            }, 22);
            // Window strips: sky-blue glass panes with soft clouds between heavy
            // structural frames and thin mullions.
            Texture2D glassTex = MakeColonyTexture("Colony_WindowSky", 256, (x, y, r) =>
            {
                bool frame = x < 6 || y < 6;
                bool mull = x % 64 < 2 || y % 64 < 2;
                if (frame) return new Color(0.3f, 0.32f, 0.35f);
                if (mull) return new Color(0.45f, 0.48f, 0.52f);
                float c = Mathf.Clamp01(Mathf.PerlinNoise(x * 0.018f, y * 0.03f) * 1.6f - 0.55f);
                Color sky = Color.Lerp(new Color(0.48f, 0.66f, 0.9f), new Color(0.72f, 0.84f, 0.98f), y / 256f);
                return Color.Lerp(sky, new Color(0.95f, 0.96f, 0.98f), c * 0.7f);
            }, 23);
            // Outer hull: panels of mixed sizes, seams, hatches, grime.
            Texture2D hullTex = MakeColonyTexture("Colony_HullPanels", 256, (x, y, r) =>
            {
                int big = (x / 64) * 5 + (y / 32) * 3;
                bool split = Hash01(x / 64, y / 32, 8) > 0.5f;
                bool seam = x % 64 == 0 || y % 32 == 0 || (split && x % 32 == 0) || (!split && y % 16 == 0);
                float n = 0.44f + (big % 7) * 0.013f + ((float)r.NextDouble() - 0.5f) * 0.03f;
                float grime = Mathf.Clamp01(Mathf.PerlinNoise(x * 0.02f, y * 0.05f) - 0.35f) * 0.25f;
                bool hatch = Hash01(x / 64, y / 32, 9) > 0.85f && x % 64 > 16 && x % 64 < 48 && y % 32 > 8 && y % 32 < 24;
                Color c = new Color(n, n + 0.01f, n + 0.02f) * (1f - grime);
                if (hatch) c *= (x % 64 == 17 || x % 64 == 47 || y % 32 == 9 || y % 32 == 23) ? 0.55f : 0.85f;
                bool rivet = (x % 64 == 3 || x % 64 == 61) && y % 8 == 4;
                if (rivet) c *= 0.7f;
                return seam ? new Color(0.2f, 0.21f, 0.23f) : c;
            }, 24);

            // --- Materials ---
            // City materials by index (see ChunkedAcc): 0 office, 1 glass, 2 residential,
            // 3 concrete, 4 paving, 5 grass, 6 roof gear, 7 red warning lights,
            // 8 lamps, 9 brick, 10 dark modern, 11 shop fronts, 12 palette (cars,
            // roofs, poles, trees), 13 road paint.
            int[] facMat = { 0, 1, 2, 9, 10 };
            Material[] cityMats =
            {
                MakeEmissionMapMat(facBase[0], facEmis[0], 1.2f, 0.25f),
                MakeEmissionMapMat(facBase[1], facEmis[1], 1.2f, 0.85f, 0.3f),
                MakeEmissionMapMat(facBase[2], facEmis[2], 1.2f, 0.2f),
                MakeMat(new Color(0.5f, 0.5f, 0.5f)),
                MakeTexturedMat(Color.white, paverTex, 0f, 0.15f),
                MakeTexturedMat(Color.white, grassTex, 0f, 0.05f),
                MakeMat(new Color(0.34f, 0.35f, 0.37f)),
                MakeEmissiveMat(new Color(0.8f, 0.1f, 0.08f), new Color(3f, 0.2f, 0.15f)),
                MakeEmissiveMat(new Color(1f, 0.9f, 0.7f), new Color(2.4f, 2.1f, 1.6f)),
                MakeEmissionMapMat(facBase[3], facEmis[3], 1.2f, 0.12f),
                MakeEmissionMapMat(facBase[4], facEmis[4], 1.2f, 0.8f, 0.4f),
                MakeEmissionMapMat(shopBase, shopEmis, 1.1f, 0.5f),
                MakeTexturedMat(Color.white, palTex, 0f, 0.45f),
                MakeMat(new Color(0.82f, 0.82f, 0.78f)),
            };
            if (cityMats[3].HasProperty("_Smoothness")) cityMats[3].SetFloat("_Smoothness", 0.1f);
            Material groundMat = MakeTexturedMat(Color.white, groundTex, 0f, 0.2f);
            Material upperLand = MakeTexturedMat(Color.white, cityLightsTex, 0.45f, 0.1f);
            Material windowMat = MakeTexturedMat(Color.white, glassTex, 0.95f, 0.9f);
            Material hullMat = MakeTexturedMat(Color.white, hullTex, 0f, 0.35f);
            if (hullMat.HasProperty("_Metallic")) hullMat.SetFloat("_Metallic", 0.35f);
            Material windowOutMat = MakeTexturedMat(new Color(0.3f, 0.38f, 0.48f), glassTex, 0.18f, 0.95f);
            if (windowOutMat.HasProperty("_Metallic")) windowOutMat.SetFloat("_Metallic", 0.6f);
            Material hullLights = MakeEmissiveMat(new Color(1f, 0.95f, 0.8f), new Color(2.6f, 2.4f, 1.9f));
            // Mirrors: dark, almost perfectly reflective panels (they show the stars
            // and the sun's glare, not a glow of their own).
            Material mirrorMat = MakeEmissiveMat(new Color(0.16f, 0.19f, 0.24f), new Color(0.05f, 0.07f, 0.1f));
            if (mirrorMat.HasProperty("_Smoothness")) mirrorMat.SetFloat("_Smoothness", 0.97f);
            if (mirrorMat.HasProperty("_Metallic")) mirrorMat.SetFloat("_Metallic", 1f);
            Material frameMat = MakeMat(new Color(0.36f, 0.37f, 0.39f));
            if (frameMat.HasProperty("_Metallic")) frameMat.SetFloat("_Metallic", 0.5f);

            // Scene fog stays ENABLED (unreachable start distance) so the fog shader
            // variants are kept in the player build; ColonyAtmosphere moves the
            // distances in and out when the camera enters / leaves the colony.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 90000f;
            RenderSettings.fogEndDistance = 100000f;
            RenderSettings.fogColor = new Color(0.64f, 0.72f, 0.84f);

            // --- Groups: the exterior is switched off from inside (ColonyAtmosphere),
            //     the breach wreckage and the caps are always shown. ---
            Transform exterior = new GameObject("Exterior").transform;
            exterior.SetParent(root.transform, false);
            Transform breachRoot = new GameObject("BreachWreckage").transform;
            breachRoot.SetParent(root.transform, false);

            // --- Inner hull: 3 land + 3 window strips ---
            MeshAcc bottomLand = new MeshAcc(), upper = new MeshAcc(), windows = new MeshAcc();
            AddCylinderBand(bottomLand, axis0, R, 240f, 300f, 0f, L, 96, true, 40f, 12);
            AddCylinderBand(upper, axis0, R, 0f, 60f, 0f, L, 32, true, 900f, 12);
            AddCylinderBand(upper, axis0, R, 120f, 180f, 0f, L, 32, true, 900f, 12);
            AddCylinderBand(windows, axis0, R, 300f, 360f, 0f, L, 32, true, 420f, 12);
            AddCylinderBand(windows, axis0, R, 60f, 120f, 0f, L, 32, true, 420f, 12);
            AddCylinderBand(windows, axis0, R, 180f, 240f, 0f, L, 32, true, 420f, 12);

            // --- Outer hull, frame ribs, light rings ---
            MeshAcc hullOut = new MeshAcc(), lightsOut = new MeshAcc(), windowsOut = new MeshAcc(), frames = new MeshAcc();
            foreach (float a in new[] { 240f, 0f, 120f })
            {
                AddCylinderBand(hullOut, axis0, R + 2f, a, a + 60f, 0f, L, 24, false, 120f, 12);          // land strips: panelled hull
                AddCylinderBand(windowsOut, axis0, R + 2f, a + 60f, a + 120f, 0f, L, 24, false, 420f, 12); // window strips: glass seen from outside
            }
            // Frame ribs between the strips: a top band plus two side walls each.
            for (int k = 0; k < 6; k++)
            {
                float a = k * 60f;
                AddCylinderBand(frames, axis0, R + 30f, a - 0.6f, a + 0.6f, 0f, L, 1, false, 300f, 12);
                foreach (float s in new[] { -0.6f, 0.6f })
                {
                    float ar = (a + s) * Mathf.Deg2Rad;
                    Vector3 d = new Vector3(Mathf.Cos(ar), Mathf.Sin(ar), 0f), t = new Vector3(-Mathf.Sin(ar), Mathf.Cos(ar), 0f) * Mathf.Sign(s);
                    for (int zs = 0; zs < 12; zs++)
                    {
                        float za = L * zs / 12f, zb = L * (zs + 1) / 12f;
                        Vector2 zz = Vector2.zero;
                        frames.Quad(axis0 + d * (R + 2f) + Vector3.forward * za, axis0 + d * (R + 30f) + Vector3.forward * za,
                            axis0 + d * (R + 30f) + Vector3.forward * zb, axis0 + d * (R + 2f) + Vector3.forward * zb, t, zz, zz, zz, zz);
                    }
                }
            }
            // Window lattice seen from outside: cross frames every 400 m and
            // lengthwise mullions every 10 deg over the three window strips.
            foreach (float a in new[] { 300f, 60f, 180f })
            {
                for (float z = 200f; z < L; z += 400f)
                {
                    AddCylinderBand(frames, axis0, R + 12f, a, a + 60f, z, z + 14f, 16, false, 300f);
                    AddCylinderBand(frames, axis0, R + 12f, a, a + 60f, z, z + 14f, 16, true, 300f); // underside
                }
                for (float m = 10f; m < 60f; m += 10f)
                    AddCylinderBand(frames, axis0, R + 8f, a + m - 0.15f, a + m + 0.15f, 0f, L, 1, false, 300f, 12);
            }
            for (float z = 500f; z < L; z += 1000f)
                AddCylinderBand(lightsOut, axis0, R + 3f, 0f, 360f, z, z + 18f, 144, false, 300f);
            // Hull greebles on the land strips: radiator panels, conduits, vents.
            for (float z = 120f; z < L - 100f; z += 180f)
            {
                foreach (float a in new[] { 240f, 0f, 120f })
                {
                    for (int g = 0; g < 5; g++)
                    {
                        float ang = (a + 4f + (float)rnd.NextDouble() * 52f) * Mathf.Deg2Rad;
                        Vector3 d = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f), t = new Vector3(-Mathf.Sin(ang), Mathf.Cos(ang), 0f);
                        float hw = 8f + (float)rnd.NextDouble() * 34f, hh = 2f + (float)rnd.NextDouble() * 10f, hd = 10f + (float)rnd.NextDouble() * 60f;
                        Vector3 c = axis0 + d * (R + 2f + hh) + Vector3.forward * (z + (float)rnd.NextDouble() * 120f);
                        AddOrientedBox(hullOut, c, t, d, Vector3.forward, new Vector3(hw, hh, hd), new Vector2(0.3f + 0.4f * g / 5f, 0.4f));
                    }
                }
            }

            // --- Mirrors: hinged at the far end along each window strip, opened 28 deg,
            //     with frame beams on their edges and struts back to the hull ---
            MeshAcc mirrors = new MeshAcc();
            foreach (float wa in new[] { 330f, 90f, 210f })
            {
                float ar = wa * Mathf.Deg2Rad;
                Vector3 nrm = new Vector3(Mathf.Cos(ar), Mathf.Sin(ar), 0f);
                Vector3 tan = new Vector3(-Mathf.Sin(ar), Mathf.Cos(ar), 0f);
                float half = R * Mathf.Sin(30f * Mathf.Deg2Rad) * 0.98f, len = L * 0.92f, open = 28f * Mathf.Deg2Rad;
                Vector3 hinge = axis0 + nrm * (R + 40f) + Vector3.forward * L;
                Vector3 dir = (-Vector3.forward * Mathf.Cos(open) + nrm * Mathf.Sin(open)).normalized;
                Vector3 a0 = hinge - tan * half, a1 = hinge + tan * half, b1 = a1 + dir * len, b0 = a0 + dir * len;
                Vector3 face = Vector3.Cross(tan, dir).normalized;
                Vector2 zz = Vector2.zero, fuv = new Vector2(0.3f, 0.3f);
                mirrors.Quad(a0, a1, b1, b0, face, zz, zz, zz, zz);
                mirrors.Quad(a0, a1, b1, b0, -face, zz, zz, zz, zz);
                // Edge beams.
                AddOrientedBox(frames, (a0 + b0) * 0.5f, tan, face, dir, new Vector3(18f, 14f, len * 0.5f + 18f), fuv);
                AddOrientedBox(frames, (a1 + b1) * 0.5f, tan, face, dir, new Vector3(18f, 14f, len * 0.5f + 18f), fuv);
                AddOrientedBox(frames, (b0 + b1) * 0.5f, dir, face, tan, new Vector3(18f, 14f, half + 18f), fuv);
                AddOrientedBox(frames, (a0 + a1) * 0.5f, dir, face, tan, new Vector3(22f, 22f, half + 18f), fuv); // hinge beam
                // Cross ribs on the back side.
                for (int k = 1; k < 6; k++)
                {
                    Vector3 c = (a0 + a1) * 0.5f + dir * (len * k / 6f) - face * 10f;
                    AddOrientedBox(frames, c, dir, face, tan, new Vector3(8f, 6f, half), fuv);
                }
                // Struts from the hull to the mirror edges.
                foreach (float f in new[] { 0.45f, 0.8f })
                {
                    foreach (float sd in new[] { -1f, 1f })
                    {
                        Vector3 mp = hinge + tan * (half * sd) + dir * (len * f);
                        Vector3 hp = axis0 + nrm * (R + 30f) + tan * (half * 0.9f * sd) + Vector3.forward * (L + (mp.z - hinge.z) * 0.55f);
                        Vector3 axis = mp - hp;
                        Vector3 side = Vector3.Cross(axis, nrm).normalized;
                        AddOrientedBox(frames, (mp + hp) * 0.5f, side, Vector3.Cross(side, axis).normalized, axis, new Vector3(9f, 9f, axis.magnitude * 0.5f), fuv);
                    }
                }
                AddCylinderBand(frames, axis0, R + 36f, wa - 30.5f, wa - 29.5f, L - 60f, L, 1, false, 300f);
                AddCylinderBand(frames, axis0, R + 36f, wa + 29.5f, wa + 30.5f, L - 60f, L, 1, false, 300f);
            }

            // --- End caps: near one with the breach, docking spindles on both ---
            MeshAcc caps = new MeshAcc(), breachAcc = new MeshAcc();
            System.Func<float, float, bool> breach = (x, y) =>
            {
                float jag = (Mathf.PerlinNoise(x * 0.012f, y * 0.012f) - 0.5f) * 140f;
                return Mathf.Abs(x) < ColonyBreachHalfWidth + jag && y < ColonyBreachTop + jag * 0.8f;
            };
            AddCapGrid(caps, axis0, R + 2f, 0f, 36, 180, breach);
            AddCapGrid(caps, axis0, R + 2f, L, 16, 96, null);
            AddCylinderBand(hullOut, axis0, 280f, 0f, 360f, -900f, 0f, 48, false, 200f, 3);          // near spindle
            AddAnnulus(hullOut, axis0, 0f, 280f, -900f, 48, -1f);
            AddCylinderBand(hullOut, axis0, 360f, 0f, 360f, L, L + 700f, 48, false, 200f, 3);        // far spindle
            AddAnnulus(hullOut, axis0, 0f, 360f, L + 700f, 48, 1f);
            AddAnnulus(lightsOut, axis0, 200f, 230f, -901f, 48, -1f);
            for (int k = 0; k < 3; k++)
            {
                AddCylinderBand(lightsOut, axis0, 282f, 0f, 360f, -800f + k * 280f, -780f + k * 280f, 48, false, 200f);
                AddCylinderBand(lightsOut, axis0, 362f, 0f, 360f, L + 150f + k * 200f, L + 165f + k * 200f, 48, false, 200f);
            }
            AddAnnulus(lightsOut, axis0, 900f, 930f, -1f, 128, -1f);
            AddAnnulus(lightsOut, axis0, 1800f, 1830f, -1f, 160, -1f);
            AddAnnulus(lightsOut, axis0, 1200f, 1260f, L - 1f, 128, -1f);   // far cap inner glow rings
            AddAnnulus(lightsOut, axis0, 2400f, 2440f, L - 1f, 160, -1f);
            // Radial stiffening spokes on the outside of both caps (none across the breach).
            for (int k = 0; k < 16; k++)
            {
                float angD = k * 22.5f + 11.25f;
                if (angD > 235f && angD < 305f) continue;
                float ang = angD * Mathf.Deg2Rad;
                Vector3 d = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f), t = new Vector3(-Mathf.Sin(ang), Mathf.Cos(ang), 0f);
                float rMid = (300f + R) * 0.5f, hl = (R - 300f) * 0.5f;
                AddOrientedBox(frames, axis0 + d * rMid - Vector3.forward * 14f, t, -Vector3.forward, d, new Vector3(22f, 14f, hl), new Vector2(0.3f, 0.3f));
                AddOrientedBox(frames, axis0 + d * rMid + Vector3.forward * (L + 14f), t, Vector3.forward, d, new Vector3(22f, 14f, hl), new Vector2(0.3f, 0.3f));
            }
            // Torn plates around the breach (outside face).
            for (int k = 0; k < 90; k++)
            {
                float ang = (float)rnd.NextDouble() * Mathf.PI;
                float ex = Mathf.Cos(ang) * (ColonyBreachHalfWidth + 60f + (float)rnd.NextDouble() * 70f);
                float ey = Mathf.Sin(ang) * (ColonyBreachTop + 60f + (float)rnd.NextDouble() * 70f);
                float s = 8f + (float)rnd.NextDouble() * 36f;
                Vector3 c = new Vector3(ex, Mathf.Max(0f, ey), zN - s * 0.3f);
                // Bent outward, tilted at random - blown out from inside.
                Vector3 outDir = new Vector3(ex, ey - 150f, 0f).normalized;
                Vector3 up = (-Vector3.forward * 0.6f + outDir * 0.8f + new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, 0f) * 0.6f).normalized;
                Vector3 side = Vector3.Cross(up, outDir).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                AddOrientedBox(breachAcc, c, side, Vector3.Cross(side, up).normalized, up, new Vector3(s, s * 0.08f + 1.5f, s * 0.7f), new Vector2(0.2f + (float)rnd.NextDouble() * 0.6f, 0.5f));
            }
            // Exposed structural girders sticking into the opening.
            for (int k = 0; k < 26; k++)
            {
                float ang = (0.05f + 0.9f * (float)rnd.NextDouble()) * Mathf.PI;
                Vector3 edge = new Vector3(Mathf.Cos(ang) * (ColonyBreachHalfWidth + 20f), Mathf.Max(10f, Mathf.Sin(ang) * (ColonyBreachTop + 20f)), zN + 2f);
                Vector3 inward = (new Vector3(0f, 160f, zN) - edge);
                inward.z = 0f;
                inward = inward.normalized;
                float l = 30f + (float)rnd.NextDouble() * 70f;
                Vector3 axis = (inward + new Vector3(0f, 0f, ((float)rnd.NextDouble() - 0.5f) * 1.2f) + Random01Vec(rnd) * 0.5f).normalized;
                Vector3 side = Vector3.Cross(axis, Vector3.forward).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                AddOrientedBox(breachAcc, edge + axis * (l * 0.5f), side, Vector3.Cross(axis, side).normalized, axis, new Vector3(2.2f, 2.2f, l * 0.5f), new Vector2(0.25f, 0.25f));
            }

            // --- The city ---
            ChunkedAcc city = new ChunkedAcc();
            var bMin = new System.Collections.Generic.List<Vector3>();
            var bMax = new System.Collections.Generic.List<Vector3>();
            System.Action<Vector3, Vector3> collide = (mn, mx) => { bMin.Add(mn); bMax.Add(mx); };

            float zCity0 = zN + 350f, zDown1 = zN + ColonyDowntownDepth;
            float boulevard = 55f, blockW = 110f, street = 30f, pitch = blockW + street;
            Vector2 core = new Vector2(0f, zN + 2300f);

            // Curved curb-high plate over a block (follows the land).
            System.Action<int, float, float, float, float, float> plate = (mat, x0, x1, z0, z1, lift) =>
            {
                MeshAcc a = city.Get(mat, (z0 + z1) * 0.5f);
                AddCylinderBand(a, axis0, R - lift, LandAngle(x0), LandAngle(x1), z0 - zN, z1 - zN,
                    Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / 40f)), true, 6f);
            };

            // One building on a lot (x0..x1, z0..z1), 'hScale' 0..1 how tall this spot is.
            System.Action<float, float, float, float, float, bool> building = (x0, x1, z0, z1, hScale, suburb) =>
            {
                float w = x1 - x0, d = z1 - z0;
                // Base: sunk into the land at the lower side, top measured from the higher side.
                float gLow = Mathf.Min(ColonyGroundY(x0), ColonyGroundY(x1)) - 3f;
                float gHigh = Mathf.Max(ColonyGroundY(x0), ColonyGroundY(x1));
                int sty = suburb ? (rnd.Next(2) == 0 ? 2 : 3) : rnd.Next(5);
                int fac = facMat[sty];
                Vector3 baseMin = Vector3.zero, baseMax = Vector3.zero;
                Vector2 uvo = new Vector2(rnd.Next(8) / 8f, rnd.Next(8) / 8f);
                float zc = (z0 + z1) * 0.5f;
                float h = suburb ? 8f + (float)rnd.NextDouble() * 20f
                                 : 22f + (float)rnd.NextDouble() * 50f + hScale * (60f + (float)rnd.NextDouble() * 360f);
                int type = suburb ? 0 : rnd.Next(4);
                Vector3 topMin, topMax;
                if (type == 1 && h > 80f)
                {
                    // Setback tower: 3 tiers, each smaller.
                    float yb = gLow, cx = (x0 + x1) * 0.5f, cz = zc, fw = w, fd = d;
                    float[] share = { 0.45f, 0.33f, 0.22f };
                    topMin = topMax = Vector3.zero;
                    for (int tr = 0; tr < 3; tr++)
                    {
                        float top = (tr == 0 ? gHigh : yb) + h * share[tr];
                        Vector3 mn = new Vector3(cx - fw * 0.5f, yb, cz - fd * 0.5f), mx = new Vector3(cx + fw * 0.5f, top, cz + fd * 0.5f);
                        AddBuildingBox(city.Get(fac, zc), mn, mx, uvo);
                        collide(mn, mx);
                        if (tr == 0) { baseMin = mn; baseMax = mx; }
                        else AddSolidBox(city.Get(3, zc), new Vector3(topMin.x - 0.6f, yb - 1.2f, topMin.z - 0.6f), new Vector3(topMax.x + 0.6f, yb + 0.3f, topMax.z + 0.6f)); // setback ledge
                        topMin = mn; topMax = mx;
                        yb = top; fw *= 0.72f; fd *= 0.72f;
                    }
                }
                else if (type == 2 && h > 60f)
                {
                    // Podium + tower.
                    float podTop = gHigh + 12f + (float)rnd.NextDouble() * 14f;
                    Vector3 pmn = new Vector3(x0, gLow, z0), pmx = new Vector3(x1, podTop, z1);
                    AddBuildingBox(city.Get(fac, zc), pmn, pmx, uvo);
                    collide(pmn, pmx);
                    baseMin = pmn; baseMax = pmx;
                    AddSolidBox(city.Get(3, zc), new Vector3(pmn.x - 0.6f, podTop - 1.2f, pmn.z - 0.6f), new Vector3(pmx.x + 0.6f, podTop + 0.3f, pmx.z + 0.6f));
                    float tw = w * (0.45f + (float)rnd.NextDouble() * 0.15f), td = d * (0.45f + (float)rnd.NextDouble() * 0.15f);
                    float tx = x0 + (w - tw) * (float)rnd.NextDouble(), tz = z0 + (d - td) * (float)rnd.NextDouble();
                    Vector3 tmn = new Vector3(tx, podTop, tz), tmx = new Vector3(tx + tw, gHigh + h, tz + td);
                    AddBuildingBox(city.Get(facMat[(sty + 1 + rnd.Next(4)) % 5], zc), tmn, tmx, uvo);
                    collide(tmn, tmx);
                    topMin = tmn; topMax = tmx;
                }
                else if (type == 3 && w > 40f)
                {
                    // Twin slabs.
                    float gap = w * 0.18f, sw = (w - gap) * 0.5f;
                    Vector3 amn = new Vector3(x0, gLow, z0), amx = new Vector3(x0 + sw, gHigh + h, z1);
                    Vector3 bmn = new Vector3(x1 - sw, gLow, z0), bmx = new Vector3(x1, gHigh + h * (0.65f + (float)rnd.NextDouble() * 0.3f), z1);
                    AddBuildingBox(city.Get(fac, zc), amn, amx, uvo);
                    AddBuildingBox(city.Get(fac, zc), bmn, bmx, uvo);
                    collide(amn, amx); collide(bmn, bmx);
                    baseMin = amn; baseMax = amx;
                    AddSolidBox(city.Get(3, zc), new Vector3(bmn.x - 0.6f, bmx.y - 1.2f, bmn.z - 0.6f), new Vector3(bmx.x + 0.6f, bmx.y + 0.3f, bmx.z + 0.6f));
                    topMin = amn; topMax = amx;
                }
                else
                {
                    Vector3 mn = new Vector3(x0, gLow, z0), mx = new Vector3(x1, gHigh + h, z1);
                    AddBuildingBox(city.Get(fac, zc), mn, mx, uvo);
                    collide(mn, mx);
                    baseMin = mn; baseMax = mx;
                    topMin = mn; topMax = mx;
                }

                // Street level: shop fronts with a canopy ledge (downtown), or a
                // pitched roof (suburban houses); a cornice along every roof edge.
                if (!suburb)
                {
                    float bandTop = gHigh + 6.5f;
                    if (baseMax.y > bandTop + 4f)
                    {
                        AddBandBox(city.Get(11, zc), new Vector3(baseMin.x - 0.3f, gLow, baseMin.z - 0.3f), new Vector3(baseMax.x + 0.3f, bandTop, baseMax.z + 0.3f), BuildingTile, uvo.x);
                        AddSolidBox(city.Get(3, zc), new Vector3(baseMin.x - 1.8f, bandTop, baseMin.z - 1.8f), new Vector3(baseMax.x + 1.8f, bandTop + 0.5f, baseMax.z + 1.8f));
                    }
                    AddSolidBox(city.Get(3, zc), new Vector3(topMin.x - 0.7f, topMax.y - 1.4f, topMin.z - 0.7f), new Vector3(topMax.x + 0.7f, topMax.y + 0.5f, topMax.z + 0.7f));
                }
                else
                {
                    AddGableRoof(city.Get(12, zc), topMin, topMax, 3f + (float)rnd.NextDouble() * 3f, Pal(8 + rnd.Next(3)));
                }

                // Rooftop gear, antennas, warning lights.
                float roofY = topMax.y;
                float rw = topMax.x - topMin.x, rd = topMax.z - topMin.z;
                int gear = suburb ? 0 : 1 + rnd.Next(3);
                for (int g = 0; g < gear; g++)
                {
                    float gw = Mathf.Min(rw * 0.4f, 4f + (float)rnd.NextDouble() * 9f), gd = Mathf.Min(rd * 0.4f, 4f + (float)rnd.NextDouble() * 9f);
                    float gx = topMin.x + (rw - gw) * (float)rnd.NextDouble(), gz = topMin.z + (rd - gd) * (float)rnd.NextDouble();
                    AddSolidBox(city.Get(6, zc), new Vector3(gx, roofY, gz), new Vector3(gx + gw, roofY + 2f + (float)rnd.NextDouble() * 5f, gz + gd));
                }
                if (!suburb && roofY - gHigh > 120f && rnd.NextDouble() < 0.45)
                {
                    float ax = (topMin.x + topMax.x) * 0.5f, az = (topMin.z + topMax.z) * 0.5f, ah = 12f + (float)rnd.NextDouble() * 35f;
                    AddSolidBox(city.Get(6, zc), new Vector3(ax - 0.8f, roofY, az - 0.8f), new Vector3(ax + 0.8f, roofY + ah, az + 0.8f));
                    AddSolidBox(city.Get(7, zc), new Vector3(ax - 1.4f, roofY + ah, az - 1.4f), new Vector3(ax + 1.4f, roofY + ah + 2.8f, az + 1.4f));
                }
            };

            // VARCO landmark buildings (per "콜로니 외관이랑 도시에 건물은 varko에서
            // 만들어도되"): models in Assets/Models/ColonyBuildings replace some
            // downtown blocks - towers near the core, apartment clusters and the
            // domed civic hall elsewhere. Each model is used a few times at most
            // (the head camera renders the scene six times a frame).
            int buildings = 0;
            GameObject[] landmarkModels = LoadColonyBuildingModels();
            Transform landmarkRoot = null;
            if (landmarkModels.Length > 0)
            {
                landmarkRoot = new GameObject("VARCO_Buildings").transform;
                landmarkRoot.SetParent(root.transform, false);
            }
            int[] landmarkUses = new int[landmarkModels.Length];
            System.Random lrnd = new System.Random(4711);
            int landmarkCount = 0;
            // Places one model with its footprint inside the lot, standing on the land;
            // returns false if it doesn't suit the spot (or it's used up).
            System.Func<float, float, float, float, float, bool> landmark = (x0, x1, z0, z1, hScale) =>
            {
                int best = -1;
                for (int i = 0; i < landmarkModels.Length; i++)
                {
                    bool tower = landmarkModels[i].name.Contains("Tower");
                    if (tower != (hScale > 0.35f)) continue;
                    if (landmarkUses[i] >= ColonyLandmarkUsesPerModel) continue;
                    if (best < 0 || landmarkUses[i] < landmarkUses[best]) best = i;
                }
                if (best < 0) return false;
                GameObject model = landmarkModels[best];
                landmarkUses[best]++;
                bool isTower = model.name.Contains("Tower");
                bool isApartment = model.name.Contains("Apartment");
                int copies = isApartment ? 2 : 1;
                for (int c = 0; c < copies; c++)
                {
                    float lx0 = x0, lx1 = x1, lz0 = z0, lz1 = z1;
                    if (isApartment)
                    {
                        float xm = (x0 + x1) * 0.5f, zm = (z0 + z1) * 0.5f;
                        if ((c & 1) == 0) lx1 = xm - 4f; else lx0 = xm + 4f;
                    }
                    float targetH = isTower
                        ? (model.name.Contains("Central") ? 360f : model.name.Contains("Twin") ? 300f : 260f) * (0.8f + hScale * 0.3f)
                        : isApartment ? 45f + (float)lrnd.NextDouble() * 20f : 1000f;
                    // Holder carries the (world-axis) scale; the model keeps its import
                    // rotation (FBX root is rotated 270 deg about X) under it, so a
                    // vertical stretch really goes up and not into the depth.
                    GameObject holder = new GameObject(model.name + "_" + landmarkCount + "_" + c);
                    holder.transform.SetParent(landmarkRoot, false);
                    GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                    inst.transform.SetParent(holder.transform, false);
                    inst.transform.localPosition = Vector3.zero;
                    inst.transform.localRotation = Quaternion.Euler(0f, 90f * lrnd.Next(4), 0f) * inst.transform.localRotation;
                    foreach (Collider col in inst.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(col);
                    Renderer[] rends = inst.GetComponentsInChildren<Renderer>(true);
                    if (rends.Length == 0) { UnityEngine.Object.DestroyImmediate(holder); continue; }
                    Bounds b = rends[0].bounds;
                    for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);
                    float fp = Mathf.Max(b.size.x, b.size.z), lot = Mathf.Min(lx1 - lx0, lz1 - lz0);
                    if (fp < 0.0001f || b.size.y < 0.0001f) { UnityEngine.Object.DestroyImmediate(holder); continue; }
                    float sFoot = lot / fp, sHeight = targetH / b.size.y;
                    float sXZ = Mathf.Min(sFoot, sHeight);
                    // Towers limited by the lot may stretch up to 1.5x vertically to keep their height.
                    float sY = isTower ? Mathf.Min(sHeight, sXZ * 1.5f) : sXZ;
                    sY = Mathf.Max(sY, sXZ);
                    Vector3 scl = new Vector3(sXZ, sY, sXZ);
                    holder.transform.localScale = scl;
                    b = rends[0].bounds;
                    for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);
                    float cx = (lx0 + lx1) * 0.5f, cz = (lz0 + lz1) * 0.5f;
                    float gLow = Mathf.Min(ColonyGroundY(lx0), ColonyGroundY(lx1)) - 2f;
                    holder.transform.position += new Vector3(cx - b.center.x, gLow - b.min.y, cz - b.center.z);
                    b = rends[0].bounds;
                    for (int r = 1; r < rends.Length; r++) b.Encapsulate(rends[r].bounds);
                    foreach (Renderer r in rends)
                    {
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                    }
                    collide(b.min, b.max);
                    buildings++;
                }
                landmarkCount++;
                return true;
            };

            // Downtown blocks.
            for (float z0 = zCity0; z0 + blockW <= zDown1; z0 += pitch)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    for (int k = 0; k < 7; k++)
                    {
                        float bx0 = boulevard + k * pitch, bx1 = bx0 + blockW;
                        float x0 = side > 0 ? bx0 : -bx1, x1 = side > 0 ? bx1 : -bx0;
                        float zc = z0 + blockW * 0.5f;
                        float dist = new Vector2(((x0 + x1) * 0.5f - core.x) / 1.3f, zc - core.y).magnitude;
                        float hScale = Mathf.Exp(-(dist / 1100f) * (dist / 1100f));
                        double roll = rnd.NextDouble();
                        if (roll < 0.07)
                        {
                            // Park: grass plate + trees.
                            plate(5, x0, x1, z0, z0 + blockW, 0.9f);
                            for (int tr = 0; tr < 14; tr++)
                            {
                                float tx = x0 + 8f + (float)rnd.NextDouble() * (blockW - 16f), tz = z0 + 8f + (float)rnd.NextDouble() * (blockW - 16f);
                                AddTree(city.Get(5, zc), new Vector3(tx, ColonyGroundY(tx) - 0.5f, tz), 9f + (float)rnd.NextDouble() * 7f, 3f + (float)rnd.NextDouble() * 1.5f);
                            }
                            continue;
                        }
                        plate(4, x0, x1, z0, z0 + blockW, 0.9f);
                        if (roll < 0.1) continue; // open plaza
                        if (roll < 0.22 && landmarkModels.Length > 0 && landmark(x0 + 4f, x1 - 4f, z0 + 4f, z0 + blockW - 4f, hScale)) continue;
                        // Lots: 1, 2 or 4.
                        int split = rnd.Next(3);
                        float m = 6f;
                        if (split == 0) { building(x0 + m, x1 - m, z0 + m, z0 + blockW - m, hScale, false); buildings++; }
                        else if (split == 1)
                        {
                            float xm = (x0 + x1) * 0.5f;
                            building(x0 + m, xm - 4f, z0 + m, z0 + blockW - m, hScale, false);
                            building(xm + 4f, x1 - m, z0 + m, z0 + blockW - m, hScale * 0.8f, false);
                            buildings += 2;
                        }
                        else
                        {
                            float xm = (x0 + x1) * 0.5f, zm = z0 + blockW * 0.5f;
                            building(x0 + m, xm - 4f, z0 + m, zm - 4f, hScale, false);
                            building(xm + 4f, x1 - m, z0 + m, zm - 4f, hScale * 0.7f, false);
                            building(x0 + m, xm - 4f, zm + 4f, z0 + blockW - m, hScale * 0.85f, false);
                            building(xm + 4f, x1 - m, zm + 4f, z0 + blockW - m, hScale * 0.6f, false);
                            buildings += 4;
                        }
                    }
                }
            }

            // Suburbs and farmland: the sides of downtown and everything further in.
            for (float z0 = zCity0; z0 + 120f <= zF - 400f; z0 += 170f)
            {
                for (float xs = -ColonyLandHalf + 20f; xs + 120f <= ColonyLandHalf - 20f; xs += 170f)
                {
                    bool downtownArea = z0 < zDown1 && xs + 120f > -ColonyDowntownHalf && xs < ColonyDowntownHalf;
                    if (downtownArea) continue;
                    double roll = rnd.NextDouble();
                    float zc = z0 + 60f;
                    if (roll < 0.35) { plate(5, xs, xs + 120f, z0, z0 + 120f, 0.6f); continue; }   // fields
                    if (roll < 0.5) continue;
                    plate(4, xs, xs + 120f, z0, z0 + 120f, 0.6f);
                    int houses = 1 + rnd.Next(3);
                    for (int hsI = 0; hsI < houses; hsI++)
                    {
                        float hx = xs + 10f + (float)rnd.NextDouble() * 60f, hz = z0 + 10f + (float)rnd.NextDouble() * 60f;
                        building(hx, hx + 25f + (float)rnd.NextDouble() * 25f, hz, hz + 25f + (float)rnd.NextDouble() * 25f, 0f, true);
                        buildings++;
                    }
                }
            }

            // Elevated highway along the east half of the boulevard (x 17..43, so the
            // centre line stays open to walk/fly down) + two cross highways over
            // east-west streets.
            float deckY = 30f, hwX = 30f;
            for (float z = zCity0 - 100f; z < zDown1 + 200f; z += 300f)
            {
                Vector3 mn = new Vector3(hwX - 13f, deckY, z), mx = new Vector3(hwX + 13f, deckY + 3f, z + 300f);
                AddSolidBox(city.Get(3, z + 150f), mn, mx);
                collide(mn, mx);
                AddSolidBox(city.Get(8, z + 150f), new Vector3(hwX - 0.6f, deckY + 3f, z), new Vector3(hwX + 0.6f, deckY + 3.2f, z + 300f));
                for (float pz = z + 40f; pz < z + 300f; pz += 100f)
                {
                    Vector3 pmn = new Vector3(hwX - 4f, -2f, pz - 4f), pmx = new Vector3(hwX + 4f, deckY, pz + 4f);
                    AddSolidBox(city.Get(3, pz), pmn, pmx);
                    collide(pmn, pmx);
                }
            }
            foreach (float zc in new[] { zN + 1455f, zN + 3135f })   // centres of E-W streets
            {
                // Follows the land's curve in 60 m steps.
                for (float x = -ColonyDowntownHalf; x < ColonyDowntownHalf; x += 60f)
                {
                    float gy = ColonyGroundY(x + 30f);
                    Vector3 mn = new Vector3(x, gy + deckY + 8f, zc - 14f), mx = new Vector3(x + 62f, gy + deckY + 11f, zc + 14f);
                    AddSolidBox(city.Get(3, zc), mn, mx);
                    collide(mn, mx);
                    if (Mathf.Abs(x + 30f) > 40f && ((int)((x + 3000f) / 60f)) % 2 == 0)
                    {
                        Vector3 pmn = new Vector3(x + 26f, gy - 2f, zc - 4f), pmx = new Vector3(x + 34f, gy + deckY + 8f, zc + 4f);
                        AddSolidBox(city.Get(3, zc), pmn, pmx);
                        collide(pmn, pmx);
                    }
                }
            }
            // --- Street-level detail (per "디테일을 더 살려줘"): road paint and
            //     crossings, street lights, trees along the boulevard, and traffic
            //     left standing where it stopped when the fighting started (some of
            //     it burnt out). On its own layer so the head camera only draws it
            //     nearby (ColonyAtmosphere sets the cull distance). ---
            ChunkedAcc detail = new ChunkedAcc();
            System.Random drnd = new System.Random(2024);
            System.Action<float, float, bool> car = (px, pz, alongZ) =>
            {
                float gy = ColonyGroundY(px);
                bool bus = drnd.NextDouble() < 0.08;
                bool burnt = drnd.NextDouble() < 0.15;
                int col = burnt ? 11 : bus ? (drnd.NextDouble() < 0.5 ? 12 : 0) : new[] { 0, 0, 1, 1, 2, 3, 4, 5, 6, 7 }[drnd.Next(10)];
                float len = bus ? 11.5f : 4.4f + (float)drnd.NextDouble() * 0.6f, wid = bus ? 2.5f : 1.8f, hgt = bus ? 3.1f : 1.0f;
                Vector3 c = new Vector3(px, gy + hgt * 0.5f + 0.35f, pz);
                Vector3 fwd = alongZ ? Vector3.forward : Vector3.right;
                // A little askew, like abandoned traffic.
                fwd = Quaternion.Euler(0f, ((float)drnd.NextDouble() - 0.5f) * (burnt ? 50f : 14f), 0f) * fwd;
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                AddOrientedBox(detail.Get(12, pz), c, right, Vector3.up, fwd, new Vector3(wid * 0.5f, hgt * 0.5f, len * 0.5f), Pal(col));
                if (!bus)
                    AddOrientedBox(detail.Get(12, pz), c + Vector3.up * (hgt * 0.5f + 0.35f) - fwd * 0.3f, right, Vector3.up, fwd,
                        new Vector3(wid * 0.44f, 0.35f, len * 0.26f), Pal(burnt ? 11 : 2));
            };
            System.Action<float, float, float> lamp = (px, pz, armDir) =>
            {
                float gy = ColonyGroundY(px);
                AddColorBox(detail.Get(12, pz), new Vector3(px - 0.2f, gy, pz - 0.2f), new Vector3(px + 0.2f, gy + 10f, pz + 0.2f), Pal(1));
                float ax0 = Mathf.Min(px, px + armDir * 3.2f), ax1 = Mathf.Max(px, px + armDir * 3.2f);
                AddColorBox(detail.Get(12, pz), new Vector3(ax0, gy + 9.8f, pz - 0.15f), new Vector3(ax1, gy + 10.1f, pz + 0.15f), Pal(1));
                float lx = px + armDir * 3.2f;
                AddSolidBox(detail.Get(8, pz), new Vector3(lx - 0.7f, gy + 9.55f, pz - 0.35f), new Vector3(lx + 0.7f, gy + 9.85f, pz + 0.35f));
            };

            float[] nsStreets = new float[12];
            for (int k = 0; k < 6; k++) { nsStreets[2 * k] = boulevard + blockW + 15f + k * pitch; nsStreets[2 * k + 1] = -nsStreets[2 * k]; }
            // Boulevard (x -55..55; highway piers at x 26..34).
            float[] westLanes = { -47f, -39f, -31f, -23f, -15f, -7f }, eastLanes = { 7f, 13f, 19f, 43f, 50f };
            for (float z = zCity0; z < zDown1; z += 18f)
            {
                foreach (float lx in new[] { -43f, -27f, -11f, 16f, 46.5f })
                    AddGroundPaint(detail.Get(13, z), axis0, lx - 0.15f, lx + 0.15f, z, z + 4f);
            }
            for (float z = zCity0; z < zDown1; z += 45f)
            {
                AddGroundPaint(detail.Get(13, z), axis0, -0.9f, -0.6f, z, z + 45f);   // double centre line
                AddGroundPaint(detail.Get(13, z), axis0, 0.6f, 0.9f, z, z + 45f);
                lamp(-52.5f, z, 1f);
                lamp(52.5f, z + 22f, -1f);
            }
            for (float z = zCity0 + 6f; z < zDown1; z += 22f)
            {
                foreach (float tx in new[] { -58.5f, 58.5f })
                {
                    float gy = ColonyGroundY(tx) + 0.9f;
                    AddColorBox(detail.Get(12, z), new Vector3(tx - 0.25f, gy, z - 0.25f), new Vector3(tx + 0.25f, gy + 3f, z + 0.25f), Pal(13));
                    AddTreeUV(detail.Get(12, z), new Vector3(tx, gy + 1.5f, z), 7f + (float)drnd.NextDouble() * 3f, 2.8f + (float)drnd.NextDouble(), Pal(drnd.NextDouble() < 0.5 ? 14 : 15));
                }
            }
            foreach (float lx in westLanes) for (float z = zCity0 + (float)drnd.NextDouble() * 30f; z < zDown1; z += 18f + (float)drnd.NextDouble() * 70f) if (drnd.NextDouble() < 0.5) car(lx, z, true);
            foreach (float lx in eastLanes) for (float z = zCity0 + (float)drnd.NextDouble() * 30f; z < zDown1; z += 18f + (float)drnd.NextDouble() * 70f) if (drnd.NextDouble() < 0.5) car(lx, z, true);
            // North-south streets.
            foreach (float sx in nsStreets)
            {
                for (float z = zCity0; z < zDown1; z += 18f)
                    AddGroundPaint(detail.Get(13, z), axis0, sx - 0.15f, sx + 0.15f, z, z + 4f);
                bool west = true;
                for (float z = zCity0 + 10f; z < zDown1; z += 90f)
                {
                    lamp(sx + (west ? -13.5f : 13.5f), z, west ? 1f : -1f);
                    west = !west;
                }
                for (float z = zCity0 + (float)drnd.NextDouble() * 40f; z < zDown1; z += 25f + (float)drnd.NextDouble() * 90f)
                    if (drnd.NextDouble() < 0.45) car(sx + (drnd.NextDouble() < 0.5 ? -4f : 4f), z, true);
            }
            // East-west streets between block rows + zebra crossings on the boulevard.
            for (float z0 = zCity0; z0 + blockW <= zDown1; z0 += pitch)
            {
                float sz = z0 + blockW + 15f; // street centre
                for (float x = -52f; x < 52f; x += 2.6f)
                {
                    AddGroundPaint(detail.Get(13, sz), axis0, x, x + 1.3f, sz - 20f, sz - 16f);
                    AddGroundPaint(detail.Get(13, sz), axis0, x, x + 1.3f, sz + 16f, sz + 20f);
                }
                for (float x = -ColonyDowntownHalf + (float)drnd.NextDouble() * 40f; x < ColonyDowntownHalf; x += 30f + (float)drnd.NextDouble() * 110f)
                {
                    if (Mathf.Abs(x) < 60f) continue;
                    if (drnd.NextDouble() < 0.4) car(x, sz + (drnd.NextDouble() < 0.5 ? -4f : 4f), false);
                }
            }

            // --- Build the meshes ---
            // War damage just inside the breach: concrete and hull fragments strewn
            // over the entrance plaza (the biggest ones block like buildings).
            for (int k = 0; k < 160; k++)
            {
                float fx = ((float)drnd.NextDouble() - 0.5f) * 2f * (ColonyBreachHalfWidth + 150f);
                float fz = zN + 20f + (float)drnd.NextDouble() * 360f * (0.4f + 0.6f * (float)drnd.NextDouble());
                float sz0 = 1.5f + (float)(drnd.NextDouble() * drnd.NextDouble()) * 16f;
                Vector3 up = (Vector3.up + Random01Vec(drnd) * 0.8f).normalized;
                Vector3 side = Vector3.Cross(up, Vector3.forward).normalized;
                Vector3 c = new Vector3(fx, ColonyGroundY(fx) + sz0 * 0.25f, fz);
                Vector3 half = new Vector3(sz0, sz0 * (0.2f + 0.4f * (float)drnd.NextDouble()), sz0 * (0.5f + 0.7f * (float)drnd.NextDouble()));
                if (drnd.NextDouble() < 0.7) AddOrientedBox(city.Get(12, fz), c, side, up, Vector3.Cross(side, up), half, Pal(drnd.NextDouble() < 0.35 ? 11 : drnd.NextDouble() < 0.5 ? 9 : 1)); // scorched concrete
                else AddOrientedBox(breachAcc, c, side, up, Vector3.Cross(side, up), half, new Vector2(0.3f + 0.4f * (float)drnd.NextDouble(), 0.5f));
                if (sz0 > 9f) collide(c - half * 0.7f, c + half * 0.7f);
            }

            // --- VARCO props (per "파공 주변 잔해나 항구 크레인 같은 외관 소품") ---
            int props = PlaceColonyVarcoProps(root.transform, exterior, breachRoot, axis0, collide);

            // --- Build the meshes ---
            bottomLand.Build("Colony_LandStrip", root.transform, groundMat);
            upper.Build("Colony_UpperLand", root.transform, upperLand);
            windows.Build("Colony_Windows", root.transform, windowMat);
            hullOut.Build("Colony_Hull", exterior, hullMat);
            frames.Build("Colony_Frames", exterior, frameMat);
            lightsOut.Build("Colony_Lights", exterior, hullLights);
            mirrors.Build("Colony_Mirrors", exterior, mirrorMat);
            windowsOut.Build("Colony_WindowsOutside", exterior, windowOutMat);
            caps.Build("Colony_Caps", root.transform, hullMat);
            breachAcc.Build("Colony_BreachPlates", breachRoot, hullMat);
            city.BuildAll("Colony_City", root.transform, cityMats);
            Transform detailRoot = new GameObject("StreetDetail").transform;
            detailRoot.SetParent(root.transform, false);
            detail.BuildAll("Colony_Street", detailRoot, cityMats);
            foreach (Transform t in detailRoot.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = ColonyDetailLayer;

            ColonyStructure cs = root.AddComponent<ColonyStructure>();
            cs.nearCapCenter = axis0;
            cs.radius = R;
            cs.length = L;
            cs.breachHalfWidth = ColonyBreachHalfWidth;
            cs.breachTop = ColonyBreachTop;
            cs.landHalfWidth = ColonyLandHalf;
            cs.boxMin = bMin.ToArray();
            cs.boxMax = bMax.ToArray();
            ColonyAtmosphere atmosphere = root.AddComponent<ColonyAtmosphere>();
            atmosphere.colony = cs;
            atmosphere.exteriorRoot = exterior;
            atmosphere.streetDetailRoot = detailRoot;
            atmosphere.detailLayer = ColonyDetailLayer;
            if (!s_colonyPreview) AssetDatabase.SaveAssets();
            Debug.Log("[Gundam] Space colony: " + (2f * R / 1000f) + " km x " + (L / 1000f) + " km, breach at z " + zN +
                ", " + buildings + " buildings incl. " + landmarkCount + " VARCO landmark lots, " + props + " VARCO props (" + bMin.Count + " collision boxes).");
            return cs;
        }

        /// <summary>Zaku spawn points in the downtown streets near the breach.</summary>
        static Vector3[] ColonyZakuSpawns()
        {
            float[] xs = { 0f, -180f, 320f };
            float[] zs = { ColonyNearZ + 600f, ColonyNearZ + 1000f, ColonyNearZ + 1500f };
            Vector3[] p = new Vector3[3];
            for (int i = 0; i < 3; i++) p[i] = new Vector3(xs[i], ColonyGroundY(xs[i]), zs[i]);
            return p;
        }

        // Per "자쿠 ... 3마리정도 나오게 해줘 다른 곳에": three Zakus, each starting in
        // a different direction/distance around the Gundam (which stands at Z=20):
        // straight ahead, front-left, and off to the right - so the pilot has to
        // look around. Each gets its own health/AI; ZakuCombatAI keeps them apart.
        static readonly Vector3[] ZakuSpawnPoints =
        {
            new Vector3(0f, 0f, ZakuSpawnZ),   // ahead, 80 m
            new Vector3(-85f, 0f, 75f),        // front-left, ~100 m
            new Vector3(95f, 0f, -10f),        // right, ~100 m
        };

        static void PlaceZakuEnemy(Transform playerGundam) => PlaceZakuEnemy(playerGundam, null);

        /// <summary>With a colony: the three Zakus guard its city instead (per
        /// "거기를 전장으로 사용하게" - "내가 가서 싸우면되니까").</summary>
        static void PlaceZakuEnemy(Transform playerGundam, ColonyStructure colony)
        {
            Vector3[] spawns = colony != null ? ColonyZakuSpawns() : ZakuSpawnPoints;
            for (int i = 0; i < spawns.Length; i++)
                PlaceZakuEnemy(playerGundam, i == 0 ? "ZakuEnemy" : "ZakuEnemy_" + (i + 1), spawns[i], colony);
        }

        static void PlaceZakuEnemy(Transform playerGundam, string zakuName, Vector3 spawn, ColonyStructure colony = null)
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
            instance.name = zakuName;
            instance.transform.SetParent(null); // world space, same as ExternalGundam - not part of the player's own suit

            // Beyond ExternalGundam (which stands at Z=20) so the two read
            // as separate mobile suits facing off in open space, not
            // overlapping. Facing back toward the player (180 degrees from
            // ExternalGundam/the cockpit's own forward) rather than facing
            // away.
            // Moved from Z=40 (only 20m from ExternalGundam) to Z=100 - per
            // "시작할떄 조금 거리가 있어야할거같아": starts 80m away, then
            // ZakuCombatAI closes to its ~60m engagement distance.
            instance.transform.position = spawn;
            // Facing the player's Gundam from wherever it starts.
            Vector3 face = (playerGundam != null ? playerGundam.position : new Vector3(0f, 0f, 20f)) - spawn;
            face.y = 0f;
            instance.transform.rotation = face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face, Vector3.up) : Quaternion.Euler(0f, 180f, 0f);

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
            ApplyBaseColorTexture(instance, zakuTexture, zakuName);

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
            ai.arena = colony; // stays in the colony city, walks around buildings

            // Machine gun in the right hand (per "자쿠의 머신건 ... 자쿠손에 들려주고
            // 사격을 하게 해줘").
            AddZakuMachineGun(instance, playerGundam);

            Debug.Log("[Gundam] Placed " + zakuName + " at " + instance.transform.position +
                " (facing the player's Gundam)" +
                (zakuTexture != null ? ", base color texture applied." : ", no texture found (flat default material)."));
        }

        // ---------------------------------------------------------------
        // Zaku machine gun - per "자쿠의 머신건을 다운로드 했어 이거를 자쿠손에
        // 들려주고 사격을 하게 해줘 자쿠 머신건에 데미지는 50이야 탄환은 100발이야
        // 재장전속도는 5초야 분당 280발에 속도로 총알이 나가". The downloaded model
        // (copied to ZakuGunModelPath) in its own default orientation already lies
        // with the muzzle toward +Z and the drum magazine on top (+Y); points below
        // are in that frame (model units, ~1 long) and scaled by ZakuGunScale.
        // ---------------------------------------------------------------
        const string ZakuGunModelPath = "Assets/Models/ZakuMachineGun/ZakuMachineGun.fbx";
        const string ZakuGunTexturePath = "Assets/Models/ZakuMachineGun/ZakuMachineGun-baseColor.png";
        const float ZakuGunScale = 8f; // ~8 m gun for the 18 m Zaku (short enough for a two-hand hold)
        static readonly Vector3 ZakuGunModelGrip = new Vector3(0f, -0.113f, -0.170f);
        static readonly Vector3 ZakuGunModelMuzzle = new Vector3(0f, -0.015f, 0.499f);
        static readonly Vector3 ZakuGunModelForeGrip = new Vector3(0f, -0.090f, 0.219f); // knurled grip under the barrel (left hand)
        static readonly Vector3 ZakuGunGripThumb = new Vector3(0f, 1f, 0.45f); // pistol grip raked back
        // Grip point inside the Zaku's closed right fist, in RightHand bone axes,
        // model (unscaled) units - measured from the Zaku's fist mesh.
        static readonly Vector3 ZakuFistGripUnscaled = new Vector3(-0.005f, 0.0045f, 0.048f);

        static void AddZakuMachineGun(GameObject zaku, Transform playerGundam)
        {
            Transform upper = FindDeepChild(zaku.transform, "RightArm");
            Transform fore = FindDeepChild(zaku.transform, "RightForeArm");
            Transform hand = FindDeepChild(zaku.transform, "RightHand");
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(ZakuGunModelPath);
            if (upper == null || fore == null || hand == null || asset == null)
            {
                Debug.LogWarning("[Gundam] " + zaku.name + ": machine gun not added (" +
                    (asset == null ? "model missing at " + ZakuGunModelPath : "right arm bones not found") + ").");
                return;
            }

            // Gun holder: +Z muzzle, +Y top, 1 unit = 1 m. Child of the Zaku so
            // EnemyHealth hides it with the body when destroyed.
            GameObject holder = new GameObject("ZakuMachineGun");
            holder.transform.SetParent(zaku.transform, false);
            float ls = zaku.transform.lossyScale.x > 0.0001f ? 1f / zaku.transform.lossyScale.x : 1f;
            holder.transform.localScale = Vector3.one * ls;
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            model.name = "ZakuMachineGun_Model";
            Quaternion defaultRot = model.transform.localRotation; // the FBX's own orientation (muzzle +Z)
            model.transform.SetParent(holder.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = defaultRot;
            model.transform.localScale = Vector3.one * ZakuGunScale;
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(ZakuGunTexturePath);
            Material mat = MakeMat(Color.white);
            mat.name = "ZakuMachineGun";
            if (tex != null) mat.mainTexture = tex;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            SetLayerRecursively(holder, 0);

            ZakuMachineGun mg = zaku.AddComponent<ZakuMachineGun>();
            mg.upperArm = upper;
            mg.foreArm = fore;
            mg.hand = hand;
            // Two-hand firing stance (per "자쿠가 총쏘는 자세로 자쿠머신건을 잡고 쏘는거야").
            mg.leftUpperArm = FindDeepChild(zaku.transform, "LeftArm");
            mg.leftForeArm = FindDeepChild(zaku.transform, "LeftForeArm");
            mg.leftHand = FindDeepChild(zaku.transform, "LeftHand");
            mg.chest = FindDeepChild(zaku.transform, "Spine2") ?? FindDeepChild(zaku.transform, "Spine1");
            mg.head = FindDeepChild(zaku.transform, "Head");
            mg.supportPoint = ZakuGunModelForeGrip * ZakuGunScale;
            mg.gun = holder.transform;
            mg.gripPoint = ZakuGunModelGrip * ZakuGunScale;
            mg.muzzlePoint = ZakuGunModelMuzzle * ZakuGunScale;
            Vector3 thumb = ZakuGunGripThumb.normalized;
            mg.gripThumb = thumb;
            mg.gripFingers = Vector3.ProjectOnPlane(Vector3.forward, thumb).normalized;
            mg.fistGrip = ZakuFistGripUnscaled * hand.lossyScale.x;
            mg.target = playerGundam != null ? playerGundam.GetComponent<PlayerHealth>() : null;
            mg.aimHeight = MobileSuitTargetHeight * 0.6f;
            mg.damage = 50;
            mg.magazine = 100;
            mg.reloadTime = 5f;
            mg.roundsPerMinute = 280f;
            mg.tracerMaterial = MakeEmissiveMat(new Color(1f, 0.75f, 0.3f), new Color(3f, 1.8f, 0.5f));
            mg.flashMaterial = MakeEmissiveMat(new Color(1f, 0.8f, 0.4f), new Color(3.5f, 2.2f, 0.8f));
            Debug.Log("[Gundam] " + zaku.name + ": machine gun in " + hand.name + " (" + ZakuGunScale + " m, 50 dmg, 100 rds, 280 rpm, 5 s reload)" +
                (mg.target != null ? "." : " - NO PlayerHealth target found."));
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
        // Per "위아래로 이동하는 손잡이가 너무 뒤에 있어 조금 앞으로 옮겨줘 근데 왼손
        // 조종기를 방해하지 않게": 8 cm further forward (0.34 -> 0.42) and 3 cm more
        // outboard (0.14 -> 0.17) - grip now ~(-0.36, 1.04, 0.09): still 9 cm behind,
        // 14 cm outboard of and 15 cm above LeftJoystick's grip ball (-0.22, ~0.89,
        // 0.18), so a hand on either never touches the other.
        const float TLeverReachForward = 0.42f;   // grip in front of the shoulder
        const float TLeverOutboard = 0.17f;       // grip outboard of the shoulder
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
