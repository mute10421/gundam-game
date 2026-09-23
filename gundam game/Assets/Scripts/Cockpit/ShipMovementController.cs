using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Reads the left joystick and moves/turns the mobile suit (this transform's
    /// GameObject - MobileSuitRoot, the root that the cockpit interior and the XR
    /// Origin are parented under, so the whole cockpit + player move together).
    ///
    /// Controls (per CockpitHUD's own pre-existing "THROTTLE {l.y}   TURN {l.x}"
    /// readout label, which was always the intended mapping):
    ///   - Push/pull the stick forward/back (tiltInput.y) = move forward/back.
    ///   - Push the stick left/right (tiltInput.x) = turn (yaw) left/right.
    ///   The stick is a push/pull/slide flight-stick (see JoystickLever) - it
    ///   doesn't rotate or twist in place - so yaw is driven by the stick's
    ///   LEFT/RIGHT POSITION. There is no separate sideways strafe: the stick's
    ///   X axis is dedicated to turning (pushing the stick right both strafing
    ///   AND turning at once would fight each other), and turning is itself a
    ///   valid way to change the suit's direction of travel per request
    ///   ("왼쪽으로 밀기 -> Gundam 왼쪽 이동/원하는 방향 전환").
    ///
    /// Per report ("LeftJoystick을 움직여도 Gundam/MobileSuit가 실제로 이동하지
    /// 않는다") this class was re-audited end to end: leftStick is assigned in
    /// GundamCockpitSetup.cs (ship.leftStick = leftStick, right after
    /// CreateJoystick("LeftJoystick", ...)), tiltInput's X/Y meaning matches
    /// JoystickLever's current position-based output (see that class - X = left/
    /// right offset / lateralRange, Y = forward/back offset / depthRange, both
    /// already clamped -1..1), maxMoveSpeed/maxTurnSpeed are non-zero (8 m/s,
    /// 60 deg/s), and Update() does write transform.position every frame it
    /// runs. No dead code or leftover rotation-era field was found - the only
    /// thing actually missing against the request was the dead zone added
    /// below, plus this being written up clearly for verification.
    ///
    /// IMPORTANT, per an earlier report ("왼손으로 LeftJoystick을 잡고 돌리면
    /// XR Origin / Main Camera의 머리 시야까지 같이 돌아간다"): this used to turn
    /// the mobile suit by calling transform.Rotate(...) directly on THIS
    /// GameObject - but the XR Origin (and so Main Camera, and the whole cockpit
    /// interior) is parented right under this same transform (see
    /// GundamCockpitSetup.cs's BuildCockpitScene: CreateXROrigin(suitRoot.transform,
    /// ...) and BuildCockpitEnclosure/BuildSeat etc. under 'interior', also a
    /// child of this root). Rotating this transform necessarily rotated
    /// everything parented under it too, including the pilot's own view - not
    /// just the suit's facing direction. In VR, any view rotation that isn't
    /// driven by the player's own real head movement is a well-known, strong
    /// motion-sickness trigger, and the fix must guarantee Main Camera only ever
    /// turns from real HMD tracking.
    ///
    /// Fixed by tracking the suit's heading as a plain float (HeadingYaw) used
    /// purely for movement DIRECTION math - this GameObject's actual
    /// transform.rotation is still NEVER written to by this script, so nothing
    /// parented under it (XR Origin/Main Camera, the cockpit interior, the hand
    /// trackers) ever rotates because of the stick. transform.position is still
    /// updated normally below - never Main Camera's local pose directly - so
    /// translation still correctly carries the cockpit + player along with the
    /// suit through the same existing parent-root structure.
    /// </summary>
    public class ShipMovementController : MonoBehaviour
    {
        public JoystickLever leftStick;

        public float maxMoveSpeed = 8f;
        public float maxTurnSpeed = 60f; // degrees/sec

        [Tooltip("Stick input below this magnitude (per axis) is treated as 0, so the suit doesn't drift/creep from tiny hand jitter while the stick is resting near center.")]
        [Range(0f, 0.3f)] public float inputDeadZone = 0.08f;

        public float CurrentSpeed { get; private set; }

        /// <summary>The mobile suit's own current heading (yaw, in world-space
        /// degrees), controlled by the left stick's LEFT/RIGHT POSITION (not a
        /// wrist twist - the stick is push/pull/slide only, see JoystickLever).
        /// This is NOT this GameObject's transform.rotation - see the class doc
        /// comment above for exactly why - it's tracked here purely as a
        /// direction to move in. Exposed publicly in case some other script (a
        /// compass readout, a future exterior/third-person view of the suit,
        /// etc.) ever needs to know which way the suit is "facing" without
        /// relying on a transform.rotation that deliberately never changes.</summary>
        public float HeadingYaw { get; private set; }

        void Update()
        {
            if (leftStick == null) return;

            Vector2 raw = leftStick.tiltInput;
            Vector2 t = new Vector2(ApplyDeadZone(raw.x), ApplyDeadZone(raw.y));

            // Turn (yaw) - the stick's left/right POSITION, since it no longer
            // rotates/twists in place. Still only ever accumulated into this
            // float, never applied to transform.rotation.
            HeadingYaw += t.x * maxTurnSpeed * Time.deltaTime;

            Quaternion heading = Quaternion.Euler(0f, HeadingYaw, 0f);
            Vector3 headingForward = heading * Vector3.forward;

            // Forward/back push/pull = forward/back thrust, measured against the
            // tracked heading above instead of transform.forward (same result
            // when nothing else ever rotates this transform, which is guaranteed
            // - see above). This is what actually moves MobileSuitRoot (and so
            // the whole cockpit + XR Origin + player riding along under it).
            CurrentSpeed = t.y * maxMoveSpeed;
            transform.position += headingForward * (CurrentSpeed * Time.deltaTime);
        }

        /// <summary>Zeroes out small stick input near center (so hand jitter while
        /// resting on the stick doesn't creep the suit), then rescales whatever is
        /// left back out to the full -1..1 range so there's no dead "notch" at the
        /// dead-zone boundary - full deflection still reads as exactly 1.</summary>
        float ApplyDeadZone(float v)
        {
            float mag = Mathf.Abs(v);
            if (mag < inputDeadZone) return 0f;
            float rescaled = (mag - inputDeadZone) / (1f - inputDeadZone);
            return Mathf.Sign(v) * Mathf.Clamp01(rescaled);
        }
    }
}
