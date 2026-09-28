using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// RightJoystick -> rotates the cockpit's EXTERNAL view (what the pilot
    /// sees of the outside world), per request:
    ///
    ///   "오른손으로 RightJoystick을 잡고 움직이면 '건담 콕핏에서 바라보는 외부
    ///    화면'이 움직여야 한다... RightJoystick.tiltInput.x -> Yaw,
    ///    RightJoystick.tiltInput.y -> Pitch"
    ///
    /// Rate-based (not position-based): the stick's tilt is a turn SPEED.
    /// Holding it right keeps turning right; letting it return to center stops
    /// the view exactly where it is (it does NOT spring back to the start).
    ///
    /// What the external view actually is (confirmed in code, not guessed):
    ///   - Camera "HeadCam" (created by GundamCockpitSetup.PlaceExternalGundam,
    ///     parented under ExternalGundam's "Head" bone) renders it.
    ///   - It feeds TWO outputs:
    ///       1. Flat RenderTexture Assets/Models/Gundam/GundamHeadCam.renderTexture
    ///          (headCam.targetTexture) -> FrontDisplay's "HEAD CAM" inset RawImage.
    ///       2. Cube RenderTexture Assets/Models/Gundam/GundamHeadCam360.renderTexture
    ///          (GundamHeadCam360 calls headCam.RenderToCubemap every LateUpdate)
    ///          -> Cockpit_Dome's material (shader Custom/CockpitDomeCubemap),
    ///          the big 360-degree view surrounding the pilot.
    ///
    /// Why the previous attempt (HeadCamManualLook) didn't visibly work: it only
    /// rotated HeadCam. That does turn output 1 (the small inset), but
    /// Camera.RenderToCubemap ALWAYS renders world-axis-aligned cube faces and
    /// ignores the camera's own rotation - so output 2, the dome the pilot is
    /// actually surrounded by, never moved. This controller therefore does both:
    ///   - rotates HeadCam's own localRotation (inset turns), and
    ///   - sends the same rotation to the dome material's _ViewRotQ, which the
    ///     shader applies to its cubemap sampling direction (dome turns).
    ///
    /// Never touches: MobileSuitRoot (position/rotation), XR Origin, Main
    /// Camera / HMD tracking, the Head bone (GundamHeadCam360 still drives it
    /// from real head-tracking - HeadCam is its child, so HeadCam's own
    /// localRotation simply composes on top), LeftJoystick (never read),
    /// ShipMovementController, JoystickLever's grab logic, or
    /// WeaponAimFireController (it still reads the same RightJoystick for its
    /// own aim/fire, unchanged - this is an additional reader, not a
    /// replacement).
    /// </summary>
    public class CockpitViewController : MonoBehaviour
    {
        [Header("References (wired by GundamCockpitSetup)")]
        [Tooltip("RightJoystick only. LeftJoystick is never read here.")]
        public JoystickLever rightJoystick;
        [Tooltip("The external-view camera: 'HeadCam' (NOT the XR Main Camera).")]
        public Camera viewCamera;
        [Tooltip("Cockpit_Dome's renderer (Custom/CockpitDomeCubemap shader). Its _ViewRotQ gets the same rotation so the surrounding 360 view turns too.")]
        public Renderer domeRenderer;

        [Header("Speeds (degrees per second at full stick tilt)")]
        public float yawSpeed = 60f;
        public float pitchSpeed = 45f;

        [Header("Limits")]
        [Tooltip("Pitch is clamped to this range so the view can never flip over. Yaw is unlimited (full 360).")]
        public float minPitch = -60f;
        public float maxPitch = 60f;

        [Header("Axis direction (flip if a direction feels reversed on the headset)")]
        public bool invertYaw = false;
        public bool invertPitch = false;

        /// <summary>Current accumulated yaw, degrees (+ = turned right).</summary>
        public float Yaw { get; private set; }
        /// <summary>Current accumulated pitch, degrees (+ = looking up).</summary>
        public float Pitch { get; private set; }

        static readonly int ViewRotQId = Shader.PropertyToID("_ViewRotQ");

        Quaternion _cameraStartLocalRotation;
        bool _haveCameraStart;
        Material _domeMat;

        void Start()
        {
            if (viewCamera != null)
            {
                _cameraStartLocalRotation = viewCamera.transform.localRotation;
                _haveCameraStart = true;
            }
            if (domeRenderer != null)
            {
                // .material (not .sharedMaterial) = a runtime instance, so Play
                // mode never writes the rotation back into the scene's saved
                // material.
                _domeMat = domeRenderer.material;
                if (!_domeMat.HasProperty(ViewRotQId))
                {
                    Debug.LogWarning("[Gundam] CockpitViewController: Cockpit_Dome's material has no _ViewRotQ property " +
                        "(old shader?) - only the FrontDisplay HEAD CAM inset will turn, not the dome.");
                    _domeMat = null;
                }
            }
            if (rightJoystick == null) Debug.LogWarning("[Gundam] CockpitViewController: rightJoystick not assigned - view won't turn.");
            if (viewCamera == null) Debug.LogWarning("[Gundam] CockpitViewController: viewCamera (HeadCam) not assigned.");
            if (domeRenderer == null) Debug.LogWarning("[Gundam] CockpitViewController: domeRenderer (Cockpit_Dome) not assigned.");
        }

        void Update()
        {
            if (rightJoystick == null) return;

            // JoystickLever already applies its own dead zone and eases the
            // handle back to center on release, so center = exactly 0 = no
            // rotation speed = the view holds its current direction.
            Vector2 input = rightJoystick.tiltInput;

            float yawInput = invertYaw ? -input.x : input.x;
            float pitchInput = invertPitch ? -input.y : input.y;

            // tiltInput.x -> Yaw (+1 = turn right, -1 = turn left). Unlimited.
            Yaw += yawInput * yawSpeed * Time.deltaTime;
            if (Yaw > 360f || Yaw < -360f) Yaw %= 360f;

            // tiltInput.y -> Pitch (+1 = look up, -1 = look down). Clamped.
            Pitch = Mathf.Clamp(Pitch + pitchInput * pitchSpeed * Time.deltaTime, minPitch, maxPitch);

            // Unity: negative X rotation = nose up, so Pitch (+ = up) is negated.
            // Euler order = yaw around world up, then pitch around the turned
            // right axis - so the view never rolls.
            Quaternion look = Quaternion.Euler(-Pitch, Yaw, 0f);

            // 1) The external-view Camera itself (HeadCam) - its LOCAL rotation
            //    only, composed on top of its original rest rotation.
            if (viewCamera != null && _haveCameraStart)
            {
                viewCamera.transform.localRotation = _cameraStartLocalRotation * look;
            }

            // 2) The dome's 360 view (see class comment for why this is needed).
            if (_domeMat != null)
            {
                _domeMat.SetVector(ViewRotQId, new Vector4(look.x, look.y, look.z, look.w));
            }
        }
    }
}
