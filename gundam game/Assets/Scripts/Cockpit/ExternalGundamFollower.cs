using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Makes ExternalGundam (the big visible mecha body that HeadCam - see
    /// GundamCockpitSetup.PlaceExternalGundam - is parented deep inside, via
    /// its "Head" bone) translate together with the player's MobileSuitRoot.
    ///
    /// Per report ("LeftJoystick을 움직여도 실제 Gundam이 움직이는 것이 화면에서
    /// 보이지 않는다"): ExternalGundam was deliberately instantiated in WORLD
    /// space (SetParent(null)) at a fixed position, independent of
    /// MobileSuitRoot, so it wouldn't inherit the suit's own facing/rotation
    /// like the cockpit interior does (see PlaceExternalGundam's own comment).
    /// That also meant it never TRANSLATED either - and HeadCam (this object's
    /// own descendant) is the actual camera that renders the Cockpit_Dome /
    /// SysCheck_Left aux-screen exterior view (both the flat feed and the live
    /// 360 skybox, see GundamHeadCam360), so a joystick push that correctly
    /// moved MobileSuitRoot (see ShipMovementController - untouched by this
    /// fix) still produced zero visible change in either of those, because the
    /// camera generating that view simply never moved.
    ///
    /// This is the minimal fix: it captures ExternalGundam's ORIGINAL relative
    /// offset from MobileSuitRoot once (so it still starts out exactly where
    /// PlaceExternalGundam placed it), then every frame re-applies that same
    /// offset on top of MobileSuitRoot's CURRENT position - so ExternalGundam
    /// (and everything parented under it, including the Head bone and HeadCam)
    /// now translates rigidly along with the player's own movement.
    ///
    /// Position only - never rotation (UPDATE: except the body yaw, see BODY TURN below), and nothing here writes to
    /// MobileSuitRoot, JoystickLever, or any camera/HMD transform.
    /// ExternalGundam's own rotation stays exactly as PlaceExternalGundam set
    /// it (Quaternion.identity); GundamHeadCam360 separately still owns turning
    /// the Head BONE's rotation to track the pilot's own look direction, and
    /// this script never touches that.
    ///
    /// BODY TURN (faceViewDirection, per "건담이 내가 바라보는 방향으로 항상 몸이
    /// 돌아야함"): the body now also YAWS to face the direction the pilot is
    /// looking - CockpitViewController.Yaw, i.e. what the cockpit's front shows
    /// (RightJoystick look, or the locked target in BEAM SABER). Yaw only, never
    /// pitch/roll. It turns about a vertical axis through turnPivot (the Head
    /// bone), so HeadCam - which sits in the head and renders the pilot's whole
    /// 360 view - stays exactly where it is while the body swings around under it:
    /// no view shift, only the body (and the BEAM SABER arm) turns. Still nothing
    /// here writes to MobileSuitRoot, the XR rig, the cockpit, or any camera.
    /// Runs early in LateUpdate (order -60) so the arm IK (BeamSaberArmController,
    /// -50) and the head/360 camera (GundamHeadCam360) see this frame's body pose.
    ///
    /// LateUpdate (not Update) so this applies AFTER ShipMovementController has
    /// already moved MobileSuitRoot for the frame - no one-frame lag between
    /// the suit moving and the exterior view following it.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public class ExternalGundamFollower : MonoBehaviour
    {
        [Tooltip("MobileSuitRoot - the player's own suit/cockpit root this object should translate together with.")]
        public Transform target;

        [Header("Body turn (face the view direction)")]
        public bool faceViewDirection = true;
        [Tooltip("Its Yaw is the direction the body turns to face.")]
        public CockpitViewController viewController;
        [Tooltip("The body turns about a vertical axis through this (the Head bone), so HeadCam doesn't move.")]
        public Transform turnPivot;
        [Tooltip("How quickly the body catches up with the view (higher = tighter).")]
        public float turnSharpness = 10f;
        [Tooltip("Max body turn speed (deg/s).")]
        public float maxTurnSpeed = 360f;

        Vector3 _offset;
        bool _haveOffset;
        Quaternion _startRotation;
        Vector3 _pivotLocal;   // pivot relative to this transform, in its start orientation
        float _bodyYaw;

        void LateUpdate()
        {
            if (target == null) return;

            if (!_haveOffset)
            {
                // Captured on first use (not at scene-build time in the editor
                // script) so this is robust to whichever order components end
                // up initializing in - it's just "wherever I am relative to the
                // target right now" the very first time this runs, which is
                // still PlaceExternalGundam's original placement since nothing
                // moves either object before Play starts.
                _offset = transform.position - target.position;
                _startRotation = transform.rotation;
                _pivotLocal = turnPivot != null
                    ? Quaternion.Inverse(_startRotation) * (turnPivot.position - transform.position)
                    : Vector3.zero;
                _bodyYaw = 0f;
                _haveOffset = true;
            }

            Vector3 basePos = target.position + _offset;

            if (!faceViewDirection || viewController == null)
            {
                transform.position = basePos;
                return;
            }

            float dt = Time.deltaTime;
            float wantYaw = viewController.Yaw;
            float k = 1f - Mathf.Exp(-Mathf.Max(0f, turnSharpness) * dt);
            float step = Mathf.DeltaAngle(_bodyYaw, wantYaw) * k;
            float maxStep = maxTurnSpeed * dt;
            _bodyYaw += Mathf.Clamp(step, -maxStep, maxStep);

            Quaternion rot = Quaternion.AngleAxis(_bodyYaw, Vector3.up) * _startRotation;
            Vector3 pivotWorld = basePos + _startRotation * _pivotLocal; // where the pivot is when not turned
            transform.rotation = rot;
            transform.position = pivotWorld - rot * _pivotLocal;
        }
    }
}
