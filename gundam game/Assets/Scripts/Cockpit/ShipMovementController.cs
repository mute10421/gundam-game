using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Reads the left joystick and moves/turns the mobile suit (this transform's
    /// GameObject - MobileSuitRoot, the root that the cockpit interior and the XR
    /// Origin are parented under, so the whole cockpit + player move together).
    ///
    /// Controls:
    ///   - Push/pull the stick forward/back (tiltInput.y) = move forward/back
    ///     along the suit's current heading.
    ///   - Push the stick left/right (tiltInput.x) = strafe/slide the suit
    ///     sideways. (UPDATED: it no longer also turns an invisible heading -
    ///     see the "앞으로 밀면 옆으로 가고" fix in Update(); the history below
    ///     is kept for context.) Per report ("옆으로가 잘 안감. 옆으로
    ///     해도 앞으로 하고나서 옆으로 해야 옆으로감. 그냥 옆으로 해도 갈 수
    ///     있게 해야함"): a pure left/right push used to ONLY accumulate
    ///     HeadingYaw (a turn-in-place with zero translation), so nothing
    ///     visibly moved until forward/back thrust was also applied - the suit
    ///     only appeared to "go sideways" once it was already moving forward
    ///     and then curved into the new heading. The strafe term below is
    ///     ADDED on top of (not instead of) that existing turn behavior, so
    ///     the stick's left/right position now moves the suit immediately by
    ///     itself, with no forward input required.
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

        /// <summary>Overall planar movement speed (always >= 0) - the magnitude of
        /// the suit's actual combined forward/back + strafe velocity this frame, so
        /// a HUD speedometer reads correctly whether the suit is moving straight,
        /// strafing sideways, or both at once (previously this only reflected
        /// forward/back thrust and read as 0 while strafing).</summary>
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

            // Per report ("왼손 조종기로 어느정도 조종을하면 조종이 제대로
            // 안됨 앞으로 밀면 옆으로 가고 옆으로 밀면 앞으로가고... 처음에만
            // 멀정하고"): this used to ALSO accumulate HeadingYaw from t.x
            // (HeadingYaw += t.x * maxTurnSpeed * dt) and then measure
            // forward/strafe against that rotated heading. But nothing the
            // pilot can SEE ever rotates with HeadingYaw - this transform's
            // rotation is deliberately never written (see the class doc
            // comment - VR motion sickness), so the cockpit, the dome and the
            // pilot's view all stay facing the same fixed direction forever.
            // Every left/right push silently turned the invisible movement
            // basis a bit more (60 deg/s), so after ~1.5s of sideways input
            // it was 90 deg off: "forward" on the stick moved the suit
            // sideways on screen and vice versa - fine only at the very
            // start, before any heading had accumulated. Exactly the report.
            //
            // Fix: movement is now measured against this transform's own
            // fixed forward/right (the same direction the cockpit and the
            // pilot's view face), so stick forward = screen forward and stick
            // sideways = screen sideways, always, no matter how long it's
            // been steered. The stick's left/right no longer turns anything.
            // (Looking around is the RightJoystick's job now - see
            // HeadCamManualLook - completely separate from this.) HeadingYaw
            // is kept as a property so CockpitHUD's HEADING readout still
            // compiles; it simply stays at 0 now. maxTurnSpeed is kept too
            // (unused) so no serialized field disappears from the Inspector.
            Vector3 suitForward = transform.forward;
            Vector3 suitRight = transform.right;

            // Diagonal input is clamped to length 1 first so pushing the stick
            // to a corner doesn't move faster than pushing it straight in one
            // axis.
            Vector2 moveInput = t.sqrMagnitude > 1f ? t.normalized : t;
            Vector3 planarVelocity = (suitForward * moveInput.y + suitRight * moveInput.x) * maxMoveSpeed;
            planarVelocity.y = 0f;

            CurrentSpeed = planarVelocity.magnitude;
            transform.position += planarVelocity * Time.deltaTime;

            // (The temporary "[ShipMove]" Debug.Log diagnostic that lived here
            // for the earlier "옆으로 안감" report has been removed - that
            // report is resolved, and this one's root cause is found above.)
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
