using UnityEngine;
using UnityEngine.UI;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Cockpit Calibration (per request - "사람마다 팔 길이와 앉는 위치가 다르기
    /// 때문에 게임 시작 전에 사용자가 자신의 몸에 맞게 왼쪽/오른쪽 조종간 위치를
    /// 설정할 수 있어야 한다"): a guided step, run every time the app starts, that
    /// lets each player physically carry LeftJoystick/RightJoystick to wherever is
    /// comfortable for their own arm length/seating position, before combat starts.
    ///
    /// REDESIGNED per report ("새로 이상한 디스플레이 만든거 지우고 조종기 위치가
    /// 아래로 내려갔잖아... 앱을 시작하면 조종기 위치를 어디에 둘건지 물어보고 내가
    /// 둔곳에 고정이 되고 시작하는 거야 그래야 팔길이 상관없이 조종하지"):
    ///   - This used to load a saved calibration from PlayerPrefs and, if one
    ///     existed, skip straight to State.Complete WITHOUT ever running the
    ///     guided flow again. That's why the joystick appeared to have "moved
    ///     down and gotten stuck there" - an earlier calibration pass (while this
    ///     drag-to-place mechanic was still new) ended up saving a lower-than-
    ///     intended position, and every run after that silently reloaded and
    ///     reused that same position with no way to redo it in-game.
    ///   - PlayerPrefs persistence is removed entirely. There is no saved data to
    ///     go stale anymore: every single app start runs the same guided
    ///     left-then-right placement flow fresh, so the player always ends up
    ///     exactly where they just placed it this session - "내가 둔곳에 고정이
    ///     되고 시작하는" - regardless of what any previous session did.
    ///   - The joysticks' own scene-authored spawn position
    ///     (GundamCockpitSetup.cs's CreateJoystick calls, untouched by this
    ///     script) is just the starting point before this flow repositions them;
    ///     it no longer matters much since calibration overrides it every run.
    ///
    /// UNCHANGED from the previous version - still not a teleport:
    ///   - The joysticks are visible and normally grabbable from the moment the
    ///     scene starts, exactly as they always are - calibration adds no hiding,
    ///     no disabling, no separate "calibration mode" gate on them.
    ///   - The user grabs a joystick with the correct hand using JoystickLever's
    ///     own, completely UNMODIFIED grab detection (this script only ever
    ///     READS its public isGrabbed property - it never touches
    ///     JoystickLever.cs, and never calls into its grab/pivot/handle math).
    ///   - While isGrabbed stays true, THIS script (not JoystickLever) drags the
    ///     joystick's whole Mount (the GameObject JoystickLever itself lives on)
    ///     to follow that hand - unclamped (aside from a generous safety clamp),
    ///     real physical movement, not a snap/teleport - so it feels like
    ///     actually carrying the stick to a new spot. JoystickLever's own small
    ///     +/-8cm handle tracking keeps running completely unmodified underneath
    ///     this; since it's always computed relative to the pivot's current
    ///     (frame-fresh) position, it simply rides along rather than fighting it.
    ///   - The instant isGrabbed goes back to false (the user lets go), dragging
    ///     stops and wherever the Mount ended up becomes that joystick's base
    ///     position for the rest of this session.
    ///   - LeftJoystick's drag is driven ONLY by leftHandTracker, RightJoystick's
    ///     ONLY by rightHandTracker - mirroring the same hand-exclusivity
    ///     JoystickLever itself already enforces.
    ///
    /// ADDED per follow-up request ("일단 게임을 시작하면 조종기에 위치를 설정하겠다고
    /// 하고 내가 조종기 위치를 손으로 잡어서 움길거야 그리고 확인을 누르면 게임이
    /// 시작되는거고"): placing both joysticks no longer completes calibration by
    /// itself. Once the right joystick is released, a WaitingForConfirm step
    /// shows a physical "확인" button (GundamCockpitSetup.cs's
    /// BuildCalibrationConfirmButton, hidden the rest of the time) and waits for
    /// either hand to press it - see IsHandPressingConfirm - before finishing and
    /// letting the game start.
    ///
    /// FIXED per bug report ("위치를 바꾸려고 조종관을 잡는순간 바닥쪽으로 내려가서
    /// 안올라옴 움겨지지도 않고 그리고 확인 버튼도 안눌림" - grabbing a stick to
    /// reposition it made it drop toward the floor and get stuck there,
    /// unresponsive to further hand movement, and the confirm button never
    /// registered a press either):
    ///   - Root cause: DragMountToHand used to place the Mount at an ABSOLUTE
    ///     position computed directly from the raw hand.PalmPosition every
    ///     single frame ("wherever your palm is right now, put the pivot
    ///     exactly there"), with no memory of where the hand was relative to
    ///     the stick at the moment of the grab. JoystickLever's own,
    ///     completely separate grab math avoids exactly this trap - it only
    ///     ever moves the handle by the hand's DELTA since grab start, never by
    ///     an absolute position - specifically because a hand-tracking
    ///     sample's absolute position (here, the palm joint) doesn't line up
    ///     with wherever on the object you actually gripped it, and can carry
    ///     its own small constant bias besides. DragMountToHand was reading the
    ///     very same HandJointTracker.PalmPosition JoystickLever's own fallback
    ///     path reads, but - unlike JoystickLever - was using it as an absolute
    ///     target every frame, so any such offset showed up as an instant,
    ///     large jump the moment you grabbed, and (once the generous
    ///     maxOffsetFromDefault safety clamp kicked in to contain that jump) as
    ///     the Mount appearing stuck at the same clamped spot afterward, since
    ///     every subsequent frame kept re-computing a position that still
    ///     exceeded the clamp by roughly the same amount. IsHandPressingConfirm
    ///     had the same class of problem: it compared hand.PalmPosition
    ///     directly against a fixed world point, so the same absolute
    ///     inaccuracy that made dragging jump also kept the palm from ever
    ///     reading as "close enough" to the confirm button.
    ///   - Fix: DragMountToHand now works exactly like JoystickLever's own grab
    ///     math - it captures the hand's local position (within the Mount's
    ///     parent) and the Mount's own local position once, on the very first
    ///     frame a grab is
    ///     detected (no movement that frame), then on every later frame while
    ///     still held, moves the Mount by (hand's current local position minus
    ///     that captured starting local position), added to the Mount's
    ///     starting local position. Any constant offset/bias in PalmPosition's
    ///     absolute value cancels out perfectly in that subtraction, so only
    ///     genuine hand movement ever drives the drag, and the stick starts
    ///     moving from wherever it already was rather than snapping anywhere.
    ///     IsHandPressingConfirm no longer depends on PalmPosition's absolute
    ///     accuracy at all - it now reads the same GripAmount/PinchAmount
    ///     gesture signal JoystickLever's own grab detection already relies on
    ///     successfully (see CanGrab/StillGrabbing in JoystickLever.cs,
    ///     untouched), so "make a fist or pinch with either hand" while the
    ///     confirm step is showing is what confirms - the physical button
    ///     object is kept purely as a visual target telling the player where
    ///     to reach, no longer as a position hit-test.
    ///
    /// FLOW (every app start, no exceptions):
    ///   "지금부터 조종간 위치를 설정합니다 / 왼손으로 왼쪽 조종간을 잡아 원하는
    ///   위치로 옮긴 뒤 놓으세요" -> waits for a real grab+drag+release on
    ///   LeftJoystick -> same prompt for RightJoystick -> "확인 버튼을 눌러
    ///   시작하세요", confirm button appears -> waits for either hand to press it
    ///   -> hides the prompt and the button. Nothing is written to disk, so
    ///   nothing can get stuck from a previous session.
    ///
    /// ShipMovementController and WeaponAimFireController are not referenced or
    /// modified at all, and movement/combat is never blocked by this script -
    /// CurrentState/IsCalibrationComplete are exposed publicly only so some OTHER
    /// script could react to them later if ever wanted.
    /// </summary>
    public class CalibrationManager : MonoBehaviour
    {
        public enum State
        {
            WaitingForLeftGrab,
            WaitingForRightGrab,
            WaitingForConfirm,
            Complete
        }

        [Header("References (wired by GundamCockpitSetup.cs)")]
        [Tooltip("LeftJoystick's JoystickLever - only ever dragged using leftHandTracker's data, and only while its own (unmodified) isGrabbed is true.")]
        public JoystickLever leftStick;
        [Tooltip("RightJoystick's JoystickLever - only ever dragged using rightHandTracker's data, and only while its own (unmodified) isGrabbed is true.")]
        public JoystickLever rightStick;
        public HandJointTracker leftHandTracker;
        public HandJointTracker rightHandTracker;
        [Tooltip("World-fixed prompt text shown during calibration (see GundamCockpitSetup.cs's BuildCalibrationPromptUI). Optional - calibration still runs (silently) without it.")]
        public Text promptText;
        [Tooltip("Physical 'press to confirm' button (see GundamCockpitSetup.cs's BuildCalibrationConfirmButton). Only shown during WaitingForConfirm. If left unset, calibration completes immediately once both joysticks are placed instead of waiting for a press.")]
        public Transform confirmButton;

        [Header("Safety")]
        [Tooltip("Meters. While dragging, the Mount is clamped to within this distance of its scene-authored default position - guards against a single bad hand-tracking sample flinging it somewhere absurd. Generous by design so real, deliberate hand movement is never restricted.")]
        public float maxOffsetFromDefault = 0.6f;
        [Tooltip("During WaitingForConfirm, either hand gripping (fist) at least this much counts as a press - same signal/threshold JoystickLever's own grab detection uses, not a position check.")]
        [Range(0.1f, 1f)] public float confirmGripThreshold = 0.55f;
        [Tooltip("During WaitingForConfirm, either hand pinching at least this much counts as a press - same signal/threshold JoystickLever's own grab detection uses, not a position check.")]
        [Range(0.1f, 1f)] public float confirmPinchThreshold = 0.5f;

        public State CurrentState { get; private set; } = State.WaitingForLeftGrab;
        public bool IsCalibrationComplete => CurrentState == State.Complete;

        // Scene-authored default local positions (captured once at Start) - the
        // reference point the safety clamp above measures from. Not used for
        // persistence anymore, only as the clamp's anchor for this session.
        Vector3 _leftDefaultLocalPos;
        Vector3 _rightDefaultLocalPos;

        bool _leftWasGrabbed;
        bool _rightWasGrabbed;

        // Captured once on the frame a grab starts (see DragMountToHand) - the
        // hand's local position (within the Mount's parent) and the Mount's own
        // local position at that instant. Only one stick can be actively
        // dragging at a time (WaitingForLeftGrab only ever drags leftStick,
        // WaitingForRightGrab only ever drags rightStick), so a single shared
        // pair of fields is enough.
        Vector3 _grabStartHandLocal;
        Vector3 _grabStartMountLocal;

        void Start()
        {
            if (leftStick != null) _leftDefaultLocalPos = leftStick.transform.localPosition;
            if (rightStick != null) _rightDefaultLocalPos = rightStick.transform.localPosition;

            // Always run the guided flow fresh - no saved calibration to load or
            // skip to anymore (see the class doc comment above for why).
            CurrentState = State.WaitingForLeftGrab;
            UpdatePromptText();
        }

        void Update()
        {
            switch (CurrentState)
            {
                case State.WaitingForLeftGrab:
                    UpdateWaitingForGrab(isLeft: true);
                    break;
                case State.WaitingForRightGrab:
                    UpdateWaitingForGrab(isLeft: false);
                    break;
                case State.WaitingForConfirm:
                    UpdateWaitingForConfirm();
                    break;
                case State.Complete:
                    break;
            }
        }

        void UpdateWaitingForGrab(bool isLeft)
        {
            JoystickLever stick = isLeft ? leftStick : rightStick;
            if (stick == null) return;

            bool grabbedNow = stick.isGrabbed;
            bool wasGrabbed = isLeft ? _leftWasGrabbed : _rightWasGrabbed;

            if (grabbedNow)
            {
                // Held right now (per JoystickLever's own, untouched grab
                // detection) - drag the WHOLE Mount to follow the hand, real
                // physical movement, never a snap/teleport. justGrabbed is true
                // only on the very first frame of this grab, so DragMountToHand
                // can capture its starting reference point instead of moving.
                DragMountToHand(isLeft, justGrabbed: !wasGrabbed);
            }
            else if (wasGrabbed)
            {
                // Just released - wherever dragging left the Mount is this
                // stick's base position for the rest of this session.
                if (isLeft)
                {
                    CurrentState = State.WaitingForRightGrab;
                }
                else
                {
                    // Both joysticks placed - per request ("확인을 누르면 게임이
                    // 시작되는거고") this no longer completes calibration by
                    // itself; it waits for an explicit confirm-button press
                    // instead (see UpdateWaitingForConfirm).
                    CurrentState = State.WaitingForConfirm;
                }
                UpdatePromptText();
            }

            if (isLeft) _leftWasGrabbed = grabbedNow;
            else _rightWasGrabbed = grabbedNow;
        }

        /// <summary>Waits for either hand to press the physical confirm button
        /// (see IsHandPressingConfirm) before finishing calibration. If no button
        /// was wired (confirmButton left null in the Inspector), falls back to
        /// completing immediately so calibration can never get stuck waiting on
        /// something that doesn't exist.</summary>
        void UpdateWaitingForConfirm()
        {
            if (confirmButton == null)
            {
                CurrentState = State.Complete;
                HidePrompt();
                return;
            }

            if (IsHandPressingConfirm(leftHandTracker) || IsHandPressingConfirm(rightHandTracker))
            {
                CurrentState = State.Complete;
                HidePrompt();
            }
        }

        /// <summary>True if the given hand is currently gripping (fist) or
        /// pinching past the same thresholds JoystickLever's own grab detection
        /// uses (see CanGrab/StillGrabbing in JoystickLever.cs, untouched) -
        /// deliberately NOT a position/proximity check (see the class doc
        /// comment's FIXED note for why). confirmButton is still shown as a
        /// visual target for the player to reach toward, but pressing no
        /// longer depends on the hand actually being near it.</summary>
        bool IsHandPressingConfirm(HandJointTracker hand)
        {
            if (hand == null || !hand.IsTracked) return false;
            return hand.GripAmount >= confirmGripThreshold || hand.PinchAmount >= confirmPinchThreshold;
        }

        /// <summary>Moves the given joystick's whole Mount (never JoystickLever's
        /// own pivot/handle/grab math) so it follows the calibrating hand's
        /// MOVEMENT since the grab started - continuous, unclamped (aside from
        /// the generous safety clamp below), real dragging rather than a
        /// one-shot snap to an absolute position. Mirrors JoystickLever's own
        /// grab-offset design: on the first frame of a grab (justGrabbed) only
        /// a starting reference point is captured, with no movement yet; every
        /// frame after that moves the Mount by (hand's local position now minus
        /// its local position at grab start), so any constant bias in
        /// PalmPosition's absolute value cancels out and the stick starts
        /// moving from wherever it already was rather than jumping. LeftJoystick
        /// only ever reads leftHandTracker, RightJoystick only ever reads
        /// rightHandTracker.</summary>
        void DragMountToHand(bool isLeft, bool justGrabbed)
        {
            JoystickLever stick = isLeft ? leftStick : rightStick;
            HandJointTracker hand = isLeft ? leftHandTracker : rightHandTracker;
            if (stick == null || hand == null) return;

            // If tracking drops out for a frame mid-drag, simply hold the Mount
            // at its last position rather than move it from a stale/invalid
            // sample - the drag resumes the next frame tracking is valid again.
            if (!hand.IsTracked) return;

            Transform mount = stick.transform;      // the Mount GameObject JoystickLever itself lives on
            Transform mountParent = mount.parent;    // the cockpit interior

            if (mountParent == null) return;

            // Hand's position relative to the Mount's parent right now. Mount
            // and the interior are all built with identity local rotation
            // throughout this hierarchy (verified against
            // GundamCockpitSetup.cs's CreateJoystick), so this local-space
            // point is directly comparable frame to frame with no rotation
            // math needed.
            Vector3 handLocalNow = mountParent.InverseTransformPoint(hand.PalmPosition);

            if (justGrabbed)
            {
                // Grab just started this frame - capture where the hand and
                // the Mount currently are, but don't move anything yet (exactly
                // like JoystickLever's own grab-start handling). This is what
                // makes the drag relative rather than an absolute snap: nothing
                // ever teleports to the hand's raw position.
                _grabStartHandLocal = handLocalNow;
                _grabStartMountLocal = mount.localPosition;
                return;
            }

            Vector3 handMovement = handLocalNow - _grabStartHandLocal;
            Vector3 newMountLocalPos = _grabStartMountLocal + handMovement;

            // Safety clamp against a single wild hand-tracking sample.
            Vector3 defaultLocalPos = isLeft ? _leftDefaultLocalPos : _rightDefaultLocalPos;
            Vector3 offsetFromDefault = newMountLocalPos - defaultLocalPos;
            if (offsetFromDefault.magnitude > maxOffsetFromDefault)
            {
                offsetFromDefault = offsetFromDefault.normalized * maxOffsetFromDefault;
                newMountLocalPos = defaultLocalPos + offsetFromDefault;
            }

            mount.localPosition = newMountLocalPos;
        }

        void UpdatePromptText()
        {
            if (promptText == null) return;

            switch (CurrentState)
            {
                case State.WaitingForLeftGrab:
                    promptText.text =
                        "COCKPIT CALIBRATION\n" +
                        "지금부터 조종간 위치를 설정합니다\n" +
                        "왼손으로 왼쪽 조종간을 잡아\n" +
                        "원하는 위치로 옮긴 뒤 놓으세요";
                    promptText.gameObject.SetActive(true);
                    break;

                case State.WaitingForRightGrab:
                    promptText.text =
                        "COCKPIT CALIBRATION\n" +
                        "오른손으로 오른쪽 조종간을 잡아\n" +
                        "원하는 위치로 옮긴 뒤 놓으세요";
                    promptText.gameObject.SetActive(true);
                    break;

                case State.WaitingForConfirm:
                    promptText.text =
                        "COCKPIT CALIBRATION\n" +
                        "위치가 마음에 드시면\n" +
                        "앞의 확인 버튼을 눌러 시작하세요";
                    promptText.gameObject.SetActive(true);
                    if (confirmButton != null) confirmButton.gameObject.SetActive(true);
                    break;

                case State.Complete:
                    HidePrompt();
                    break;
            }
        }

        void HidePrompt()
        {
            if (promptText != null) promptText.gameObject.SetActive(false);
            if (confirmButton != null) confirmButton.gameObject.SetActive(false);
        }
    }
}
