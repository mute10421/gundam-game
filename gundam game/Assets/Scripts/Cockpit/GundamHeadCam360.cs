using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Feeds a Camera's live view into a cubemap RenderTexture every frame and
    /// assigns that cubemap to a Skybox material, so the cockpit's whole
    /// surrounding background becomes a live 360-degree feed from the
    /// Gundam's own head camera - per request: "콕핏에 검정부분에서 건담에
    /// 머리부분에서 보이는거 처럼 해야해 360도로" (the cockpit's dark
    /// enclosure should look like what the Gundam's head sees, all around).
    ///
    /// This does NOT replace the existing flat head-cam screen on the
    /// SystemCheckDisplay's left aux panel (that keeps working exactly as
    /// before, off the same headCam) - it adds the same feed as the
    /// player's actual background via Unity's Skybox system, which is
    /// what CreateXROrigin switches the view camera's clearFlags to
    /// (Skybox instead of a flat SolidColor) once this is wired up.
    ///
    /// headCam and skyboxMaterial are both created once in
    /// GundamCockpitSetup.cs (PlaceExternalGundam) and assigned here -
    /// this script only contains the per-frame runtime behavior (the
    /// generator script only builds scene structure once, it can't run
    /// logic every frame).
    ///
    /// Also turns the Gundam's own head bone (head) to follow the pilot's
    /// real head/look direction (playerCamera - the XR rig's camera) - per
    /// request ("내가 머리를 돌리면 건담 머리도 돌아야해"): when the pilot
    /// looks around, the Gundam's head (and so the head-cam's view direction
    /// feeding the 360 skybox) turns the same way. playerCamera is assigned
    /// by GundamCockpitSetup.cs right after it creates the XR rig (that
    /// happens AFTER this component is created, so it's wired up in a
    /// second pass rather than at construction time).
    /// </summary>
    public class GundamHeadCam360 : MonoBehaviour
    {
        public Camera headCam;
        public Material skyboxMaterial;
        public Transform head;
        public Transform playerCamera;

        // Added per report ("건담시야로 보는 밖이 화질이 깨짐" root-caused further,
        // then "밖에 자쿠가 보이지 않아"): GundamCockpitSetup.cs can now create a
        // PERSISTED cubemap RenderTexture asset (same reason headCamTex is a saved
        // asset, not a plain "new RenderTexture(...)") and hand it in here, so the
        // SAME cubemap this script renders into every frame can also be assigned
        // directly to Cockpit_Dome's own material (see MakeCubemapScreenMat in
        // GundamCockpitSetup.cs) - giving the dome a true, undistorted 360-degree
        // view (sampled by real world direction) instead of one flat 2D image
        // stretched over the whole sphere, which was hiding/smearing anything
        // outside headCam's own narrow forward cone (ZakuEnemy included). If this
        // is left unassigned (e.g. an older scene that hasn't been rebuilt), a
        // transient runtime-only cubemap is created instead, same as before - the
        // Skybox background still works, it just won't be what the dome itself
        // samples.
        [Tooltip("Pre-created persisted cubemap RenderTexture (from GundamCockpitSetup.GetOrCreateHeadCam360CubemapRenderTexture) to render into. Leave null to fall back to a transient runtime-only cubemap sized by 'resolution' below.")]
        public RenderTexture cubemap;

        [Tooltip("Cubemap face resolution, only used when 'cubemap' above isn't already assigned. Lower = cheaper/faster, higher = sharper picture.")]
        public int resolution = 256;

        RenderTexture _cubemap;
        Quaternion _startHeadRotation;
        Quaternion _startCameraRotation;
        bool _haveCameraStart;

        void Start()
        {
            if (headCam == null || skyboxMaterial == null)
            {
                Debug.LogWarning("[Gundam] GundamHeadCam360 is missing headCam or skyboxMaterial - the 360 feed won't update.");
                return;
            }

            if (cubemap != null)
            {
                _cubemap = cubemap;
                if (!_cubemap.IsCreated())
                {
                    _cubemap.Create();
                }
            }
            else
            {
                _cubemap = new RenderTexture(resolution, resolution, 16);
                _cubemap.dimension = UnityEngine.Rendering.TextureDimension.Cube;
                _cubemap.name = "GundamHeadCam360_Cubemap";
                _cubemap.Create();
            }

            skyboxMaterial.SetTexture("_Tex", _cubemap);

            if (head != null)
            {
                _startHeadRotation = head.rotation;
            }
        }

        void LateUpdate()
        {
            // Turn the Gundam's head to match how much the pilot's own head
            // has turned since this started - captured as a DELTA from a
            // starting reference (same idea as JoystickLever's wrist-twist
            // reference capture) rather than copying playerCamera.rotation
            // directly onto head.rotation, since the head bone's own rest
            // orientation (baked into the rig) isn't guaranteed to line up
            // with the camera's axes - this way the head just turns FROM its
            // own natural rest pose, by however much the pilot has looked
            // around, instead of snapping to a possibly-wrong "forward".
            if (head != null && playerCamera != null)
            {
                if (!_haveCameraStart)
                {
                    _startCameraRotation = playerCamera.rotation;
                    _haveCameraStart = true;
                }

                Quaternion cameraDelta = playerCamera.rotation * Quaternion.Inverse(_startCameraRotation);
                head.rotation = cameraDelta * _startHeadRotation;
            }

            if (headCam == null || _cubemap == null) return;

            // Re-renders all 6 directions from the head camera's current
            // position/rotation into the cubemap every frame, so the
            // pilot's surrounding view stays live as the Gundam (and
            // whatever it's looking at) moves.
            headCam.RenderToCubemap(_cubemap);
        }
    }
}
