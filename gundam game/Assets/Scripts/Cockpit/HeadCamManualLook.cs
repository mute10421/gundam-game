using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Rotates ONLY the Camera this component sits on (HeadCam - the viewpoint
    /// camera that feeds Cockpit_Dome's 360 skybox and FrontDisplay's flat
    /// head-cam inset, see PlaceExternalGundam in GundamCockpitSetup.cs) using
    /// RightJoystick's tilt, per request:
    ///
    ///   "실제로 회전시킬 대상은 콕핏 외부를 보여주는 시점용 Camera이다...
    ///    그 Camera를 정확히 찾아서 그 Camera의 회전만 RightJoystick 입력으로
    ///    제어한다."
    ///
    /// HeadCam is parented under the Gundam's own "Head" bone (worldPositionStays:
    /// true - see PlaceExternalGundam), and that Head bone's own rotation is
    /// separately, continuously driven by GundamHeadCam360 to track the
    /// pilot's real HMD look direction. This component NEVER touches that
    /// parent bone - it only ever writes its OWN transform.localRotation,
    /// which Unity composes on top of whatever the parent's world rotation
    /// is that frame, so real head-tracking and this manual look add
    /// together rather than fighting each other. Script execution order
    /// between this and GundamHeadCam360 does not matter for correctness -
    /// each only ever writes its own transform, and both settle before the
    /// frame renders.
    ///
    /// The camera's local rotation at the moment this first runs (whatever
    /// PlaceExternalGundam originally set it to, to face world +Z) is
    /// captured once as a reference and this component's own yaw/pitch is
    /// applied ON TOP of that reference - same reasoning as
    /// GundamHeadCam360's own head-tracking delta: the camera's rest local
    /// rotation relative to its parent bone isn't guaranteed to be identity,
    /// so overwriting it outright (instead of composing on top of it) could
    /// silently point the camera in the wrong direction at center-stick.
    ///
    /// Explicitly never touches: MobileSuitRoot's position/rotation, the XR
    /// Origin/Main Camera (the pilot's own real HMD view/tracking), the Head
    /// bone's own rotation (see above), LeftJoystick (never read here),
    /// ShipMovementController, JoystickLever's own grab logic, or
    /// ExternalGundamFollower (that only moves ExternalGundam's position to
    /// follow MobileSuitRoot - unrelated, untouched).
    /// </summary>
    public class HeadCamManualLook : MonoBehaviour
    {
        [Tooltip("RightJoystick only. LeftJoystick's input is never read here.")]
        public JoystickLever rightStick;

        [Tooltip("Degrees/sec this Camera turns at full stick tilt.")]
        public float lookSpeed = 60f;

        [Tooltip("Pitch is clamped to +/- this many degrees so the view can never flip upside down. Yaw is free/unclamped (full 360-degree turning).")]
        public float maxPitch = 60f;

        float _yaw;
        float _pitch;

        Quaternion _startLocalRotation;
        bool _haveStart;

        void LateUpdate()
        {
            if (rightStick == null) return;

            if (!_haveStart)
            {
                _startLocalRotation = transform.localRotation;
                _haveStart = true;
            }

            Vector2 t = rightStick.tiltInput;

            // Yaw: free/unclamped - full 360-degree turning, per request
            // ("Yaw는 360도 자유롭게 회전할 수 있게 한다"). Quaternion.Euler
            // normalizes any accumulated degree value correctly on its own -
            // same unclamped-accumulator pattern this project's
            // ShipMovementController already uses for HeadingYaw.
            // tiltInput.x > 0 turns right, per request's own worked example
            // ("tiltInput.x = +1 -> 오른쪽으로 회전").
            _yaw += t.x * lookSpeed * Time.deltaTime;

            // Pitch: clamped so the view can't flip upside down, per request
            // ("pitch = -60 ~ +60도"). Sign convention matches this project's
            // existing WeaponAimFireController (the old cockpit-turret aim,
            // also RightJoystick-driven): t.y > 0 (stick pushed forward)
            // looks UP, matching the request's own worked example
            // ("tiltInput.y = +1 -> 위쪽으로 회전").
            _pitch = Mathf.Clamp(_pitch - t.y * lookSpeed * Time.deltaTime, -maxPitch, maxPitch);

            transform.localRotation = _startLocalRotation * Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }
}
