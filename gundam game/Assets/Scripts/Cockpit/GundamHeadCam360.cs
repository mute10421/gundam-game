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
    ///
    /// NOTE: an earlier attempt briefly added a RightJoystick-driven "manual
    /// look" offset directly to this class (on top of the head-tracking
    /// below). That has been reverted - per a follow-up, more precise
    /// request, the right stick now rotates the HeadCam Camera itself
    /// instead (see HeadCamManualLook.cs, wired onto HeadCam's own
    /// GameObject in GundamCockpitSetup.cs), so this class is back to doing
    /// exactly what its class name says: pure head-tracking + the 360
    /// cubemap feed, nothing else.
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

        [Header("Optimization (per \"랙이 조금 거림 ... 최적화좀해줘\")")]
        [Tooltip("Only re-render the cube faces the pilot can actually see (plus the HEAD CAM inset's view) every frame; the hidden ones are refreshed in rotation.")]
        public bool faceCulling = true;
        [Tooltip("The pilot's XR camera (Main Camera). Found from playerCamera if empty.")]
        public Camera viewerCamera;
        [Tooltip("RightJoystick view rotation (turns what the dome samples). Found automatically if empty.")]
        public CockpitViewController viewController;
        [Tooltip("Extra degrees around the pilot's field of view that are kept live (head turns faster than a frame never show a stale face).")]
        public float cullMarginDeg = 18f;
        [Tooltip("Assumed minimum half field of view (deg) of the headset, in case the XR camera reports a narrow one.")]
        public float minHalfFovDeg = 55f;
        [Tooltip("A hidden face is refreshed every this many frames (one face at a time).")]
        public int hiddenFaceInterval = 2;
        [Tooltip("HEAD CAM inset on the center display. With the Custom/HeadCamInsetCube material it samples this cubemap, so HeadCam no longer renders the whole scene a second time into its flat texture.")]
        public UnityEngine.UI.RawImage insetImage;

        RenderTexture _cubemap;
        Material _insetMat;
        bool _firstFrame = true;
        int _rr;
        static readonly int CubeTexId = Shader.PropertyToID("_CubeTex");
        static readonly int CamRotId = Shader.PropertyToID("_CamRotQ");
        static readonly int TanHalfFovId = Shader.PropertyToID("_TanHalfFov");
        static readonly int AspectId = Shader.PropertyToID("_Aspect");
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

            if (viewerCamera == null && playerCamera != null) viewerCamera = playerCamera.GetComponent<Camera>();
            if (viewController == null) viewController = FindFirstObjectByType<CockpitViewController>();
            if (insetImage != null && insetImage.material != null && insetImage.material.HasProperty(CubeTexId))
            {
                _insetMat = new Material(insetImage.material);
                _insetMat.SetTexture(CubeTexId, _cubemap);
                insetImage.material = _insetMat;
                insetImage.texture = null;
                // The inset now comes from the cubemap: stop HeadCam's own automatic
                // full-scene render into its flat texture (RenderToCubemap below
                // works with the camera component disabled).
                headCam.enabled = false;
            }

            if (head != null)
            {
                _startHeadRotation = head.rotation;
            }
        }

        /// <summary>Follow another head bone (GUNDAM / ZAKU selection) - its current
        /// pose becomes the rest the pilot's head turns are added to.</summary>
        public void SetHead(Transform newHead)
        {
            head = newHead;
            if (head != null) _startHeadRotation = head.rotation;
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

            // Re-renders the cube faces from the head camera's current position
            // every frame, so the pilot's surrounding view stays live as the
            // Gundam (and whatever it's looking at) moves. Faces outside the
            // pilot's view (and the inset's) are only refreshed in rotation.
            int mask = 63;
            if (faceCulling && !_firstFrame && viewerCamera != null)
            {
                mask = VisibleFaceMask();
                if (hiddenFaceInterval <= 1 || Time.frameCount % hiddenFaceInterval == 0)
                {
                    for (int k = 0; k < 6; k++)
                    {
                        _rr = (_rr + 1) % 6;
                        if ((mask & (1 << _rr)) == 0) { mask |= 1 << _rr; break; }
                    }
                }
            }
            _firstFrame = false;
            headCam.RenderToCubemap(_cubemap, mask);

            if (_insetMat != null)
            {
                Quaternion q = headCam.transform.rotation;
                _insetMat.SetVector(CamRotId, new Vector4(q.x, q.y, q.z, q.w));
                _insetMat.SetFloat(TanHalfFovId, Mathf.Tan(headCam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                _insetMat.SetFloat(AspectId, 1f);
            }
        }

        /// <summary>Cube faces (bit = CubemapFace index) the pilot can see through
        /// the dome - its view directions rotated the same way the dome shader
        /// rotates them (_ViewRotQ) - plus the faces the HEAD CAM inset shows.</summary>
        int VisibleFaceMask()
        {
            int mask = 0;
            Quaternion look = viewController != null ? viewController.LookRotation : Quaternion.identity;
            float fov = viewerCamera.fieldOfView * 0.5f;
            float halfV = Mathf.Min(80f, Mathf.Max(fov, minHalfFovDeg) + cullMarginDeg);
            float halfHraw = Mathf.Atan(Mathf.Tan(fov * Mathf.Deg2Rad) * Mathf.Max(0.5f, viewerCamera.aspect)) * Mathf.Rad2Deg;
            float halfH = Mathf.Min(80f, Mathf.Max(halfHraw, minHalfFovDeg) + cullMarginDeg);
            mask |= FrustumFaces(look * viewerCamera.transform.rotation, Mathf.Tan(halfH * Mathf.Deg2Rad), Mathf.Tan(halfV * Mathf.Deg2Rad), 8);
            if (_insetMat != null || headCam.enabled)
            {
                float t = Mathf.Tan(Mathf.Min(80f, headCam.fieldOfView * 0.5f + 6f) * Mathf.Deg2Rad);
                mask |= FrustumFaces(headCam.transform.rotation, t, t, 4);
            }
            return mask;
        }

        static int FrustumFaces(Quaternion rot, float tx, float ty, int n)
        {
            int mask = 0;
            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; j <= n; j++)
                {
                    Vector3 d = rot * new Vector3(tx * (2f * i / n - 1f), ty * (2f * j / n - 1f), 1f);
                    mask |= 1 << FaceOf(d);
                }
            }
            return mask;
        }

        /// <summary>CubemapFace index of a direction: +X 0, -X 1, +Y 2, -Y 3, +Z 4, -Z 5.</summary>
        static int FaceOf(Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            if (ax >= ay && ax >= az) return d.x >= 0f ? 0 : 1;
            if (ay >= az) return d.y >= 0f ? 2 : 3;
            return d.z >= 0f ? 4 : 5;
        }
    }
}
