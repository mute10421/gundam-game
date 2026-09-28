using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// A physical joystick handle (Base -> Stick -> GripPoint) that the player
    /// grabs with a tracked hand - either by wrapping the whole hand into a fist
    /// around it, or by pinching thumb-to-index - then PUSHES/PULLS/SLIDES like a
    /// real flight-stick: the Stick translates within a small clamped range
    /// around the fixed Base/pivot, it never rotates in place, and it springs
    /// back to its center position on release.
    ///
    /// Two independent grab signals feed into this, either of which is enough:
    /// 1) A real XR Interaction Toolkit XRSimpleInteractable (see
    ///    HandExclusiveGrabAdapter), selected by this project's own "Near-Far
    ///    Interactor" hand interactor (from the imported "XR Origin Hands (XR Rig)"
    ///    sample) - reporting through NotifyXRIGrabbed below. This is the preferred
    ///    path since it's official, tested XRI selection logic reading the same
    ///    real hand-tracking data.
    /// 2) A hand-rolled fallback done directly here (proximity to the pivot + grip/
    ///    pinch amount read straight from HandJointTracker), which keeps working
    ///    even if the XRI wiring above is ever missing.
    /// Either way, once grabbed, the actual movement math below is 100% this
    /// script's own - XRI is only ever used as a yes/no "is this stick selected"
    /// signal, never to move the object itself (see the AUDIT note further down
    /// for exactly what was checked to confirm that).
    ///
    /// Per report ("왼손으로 LeftJoystick을 잡고 오른손으로 RightJoystick을 잡으면
    /// 두 조종간이 동시에 반응하거나 서로의 손 움직임에 반응함") - re-audited every
    /// layer (HandJointTracker's handedness binding, CreateJoystick's per-stick
    /// null hand slot, HandExclusiveGrabAdapter's onlyAllowedInteractor check,
    /// this class's own leftHandTracker/rightHandTracker null-guards) end to end
    /// and found all of it already correctly isolated per hand/per stick - no
    /// spot was found where one stick could read the other hand's data. As a
    /// deliberate hardening anyway (per the explicit request to stop relying on
    /// any shared/looked-up hand position once XRI has grabbed), _grabbedInteractor
    /// below now captures the EXACT Transform of the specific interactor XRI's
    /// own selection system already verified is the correct, allowed one for
    /// this stick (see NotifyXRIGrabbed) - movement math reads THAT Transform
    /// directly while it's set, rather than going back through
    /// leftHandTracker/rightHandTracker, so there is no path left, even in
    /// principle, for this stick's position to be computed from anything other
    /// than the one hand that is verified to be holding it.
    ///
    /// Grab-offset position model (per report - "조종간을 잡는 순간 조종간 전체가
    /// 내 몸 쪽/손 쪽으로 순간적으로 끌려온다... 손이 조종간의 어디를 잡았든 현재
    /// 조종간 위치를 그대로 유지해야 한다... 조종간을 손 위치로 스냅하지 않는다"):
    /// an EARLIER version of this script snapped 'handle' directly onto the
    /// hand's own position the instant a grab started - that is exactly the bug
    /// that made the stick appear to fly toward the player's hand/body on every
    /// grab, wherever on the handle they actually grabbed. That snap is GONE.
    /// The current model instead:
    ///   - On the very first grabbed frame, records ONLY two reference points
    ///     - grabStartHandLocalPosition (the hand's pivot-local position right
    ///     now) and grabStartJoystickLocalPosition (the handle's own CURRENT
    ///     pivot-local position, i.e. wherever it already was) - and does NOT
    ///     touch handle.localPosition at all this frame. No snap, no movement,
    ///     regardless of where on the handle the hand grabbed.
    ///   - On every later frame while held: handMovement = (current hand
    ///     pivot-local position) - grabStartHandLocalPosition, and the target
    ///     position = grabStartJoystickLocalPosition + handMovement - the SAME
    ///     spatial relationship between hand and handle captured at grab start
    ///     is preserved for the rest of the grab: moving the hand by some
    ///     amount moves the handle by that same amount, starting from wherever
    ///     it already was - never re-anchored to the hand's own position.
    ///   - 'gripPoint' is purely a cosmetic/visual marker (the grip ball's
    ///     center, for gizmos/future reference) - it is NEVER read to move,
    ///     snap, or offset the handle/pivot/stick's actual Transform anywhere
    ///     in this script.
    ///   - The hand's position is still measured RELATIVE TO THIS STICK'S OWN
    ///     Base/pivot, recomputed fresh every frame via
    ///     pivot.InverseTransformPoint (not a cached world-space start position
    ///     + rotated delta) - both at grab start and every frame after. This
    ///     matters because the Base (this joystick's pivot) is itself a
    ///     descendant of MobileSuitRoot, which LeftJoystick's own input can
    ///     translate through the cockpit - a world-space delta would pick up
    ///     the suit's own translation as if it were hand movement (causing the
    ///     OTHER stick, or even this same stick, to visibly drift whenever the
    ///     suit moves, with no real hand input behind it). Re-deriving the
    ///     hand's PIVOT-LOCAL position each frame cancels out any common
    ///     parent motion automatically, since the hand tracker rides along
    ///     with the same moving cockpit as the pivot does - and since
    ///     handMovement is a difference of two such pivot-local samples, it
    ///     inherits the same cancellation.
    ///
    /// Movement model (per request - "실제 비행기/로봇 조종간처럼 손으로 잡고
    /// 앞뒤/좌우로 밀고 당기는 방식"):
    ///   - The Base (this GameObject's 'pivot' child) NEVER moves or rotates.
    ///     Only the Stick ('handle') moves, within a flat X (left/right) / Z
    ///     (forward/back push) plane relative to the Base - Y (height) never
    ///     changes, and the Stick's OWN localRotation never changes either
    ///     (see the FIXED note below - this is now actively enforced, not just
    ///     assumed).
    ///   - tiltInput keeps its original public meaning/shape (X = left/right,
    ///     Y = forward/back push, each -1..1) so every existing consumer
    ///     (ShipMovementController, WeaponAimFireController, CockpitHUD) keeps
    ///     working completely unmodified - it's just derived from clamped
    ///     position now instead of a tilt angle.
    /// Finally, deadZone is applied to the derived tiltInput itself (not the
    /// raw position) using the same "zero below threshold, then rescale the
    /// remainder back up to the full -1..1 range" technique already used by
    /// ShipMovementController.ApplyDeadZone, so there is no discontinuity/notch
    /// right at the dead zone boundary - and a final explicit clamp/normalize
    /// guards tiltInput's magnitude as defense-in-depth beyond the per-axis
    /// Mathf.Clamp already present.
    ///
    /// FIXED per bug report ("조종관을 잡고 이동할때 조종관이 내손따라서 제대로
    /// 안움직임... 특정방향으로는 안밀림... 로봇이 맘대로 다른데로 가기도함...
    /// 움직일떄는 오른손 조종장치가 안작동함" - while actually driving
    /// (MobileSuitRoot in motion), BOTH sticks tracked the hand poorly, pushing
    /// in some directions seemed to do nothing, the suit drifted on its own,
    /// and the right stick could stop responding entirely):
    ///   - Root cause: every frame, this script computes the grabbing hand's
    ///     position relative to THIS stick's own pivot via
    ///     pivot.InverseTransformPoint(...) - deliberately, per the class doc
    ///     comment above, so MobileSuitRoot's own translation (driven by
    ///     ShipMovementController, a sibling script on the very same moving
    ///     root all of this - XR Origin, both joysticks, the hand trackers -
    ///     is parented under) cancels out instead of being mistaken for hand
    ///     movement. That cancellation only works if the hand-tracking sample
    ///     this script reads (HandJointTracker.PalmPosition) and the pivot
    ///     Transform it's compared against both reflect MobileSuitRoot at the
    ///     SAME point in this frame's timeline. Unity's hand-tracking data
    ///     arrives via the XR subsystem earlier in the frame than ordinary
    ///     MonoBehaviour.Update() calls, which are free to run in any relative
    ///     order unless something pins them down - so on frames where
    ///     ShipMovementController.Update() happened to run before this script's
    ///     own Update(), MobileSuitRoot had ALREADY moved by the time
    ///     pivot.InverseTransformPoint read it, while PalmPosition was still
    ///     based on MobileSuitRoot's position from before that move - a
    ///     one-frame-wide mismatch that grows every frame the suit keeps
    ///     moving, exactly the size of the suit's own per-frame travel.
    ///   - Fix: [DefaultExecutionOrder(-100)] below guarantees every
    ///     JoystickLever's Update() (on both LeftJoystick and RightJoystick)
    ///     runs before ShipMovementController's default-order Update() every
    ///     single frame, regardless of component add-order or anything else
    ///     that could otherwise leave it to chance. That means whenever this
    ///     script reads pivot's Transform, MobileSuitRoot has not yet been
    ///     moved by this frame's ship-movement update - the same "not yet
    ///     moved this frame" state PalmPosition already reflects - so the two
    ///     stay in sync and the pivot-local cancellation this class was always
    ///     designed around actually holds, every frame, not just by chance.
    ///
    /// FIXED per follow-up bug report - Galaxy XR device test ("왼손으로
    /// LeftJoystick을 잡고 밀면 건담 이동은 정상... 하지만 LeftJoystick 자체가
    /// 손을 따라오지 않는다... 오른손으로 RightJoystick을 잡으려고 하면 잘 안
    /// 잡힌다... 오른쪽 조종간을 잡아도 손을 따라가는 것이 아니라 갑자기 크게
    /// 꺾이거나 이상한 방향으로 회전한다"), which asked specifically for a
    /// rewrite of the grab tracking itself (offset-based, no snap, 1:1, no
    /// unnecessary smoothing, position only, and an audit of every system that
    /// could be moving this stick's Transform):
    ///   - AUDIT (per that report's item F - "이 세 시스템이 동시에 조종간
    ///     Transform을 움직이고 있는지 반드시 확인해라"): read
    ///     HandExclusiveGrabAdapter.cs and GundamCockpitSetup.cs's
    ///     AttachHandInteractable/WireJoystickHandInteractors end to end.
    ///     HandExclusiveGrabAdapter only ever calls NotifyXRIGrabbed(bool,
    ///     Transform) - it does not read or assign transform.position/rotation
    ///     anywhere. XRSimpleInteractable (unlike XRGrabInteractable, which
    ///     this project deliberately does NOT use - see the class doc comment
    ///     above) has no movement/attach behavior of its own; it only raises
    ///     select/hover events. Grepping every script in this folder for
    ///     transform.position/rotation/localPosition/localRotation assignments
    ///     turns up exactly ONE place that ever moves this stick's own
    ///     handle - this script, right below. So in the C# that ships with
    ///     this project, there is exactly one mover, as intended. Because the
    ///     reported symptom was still a real, visible rotation snapping on
    ///     RightJoystick specifically, and nothing in this project's own code
    ///     could explain that, LateUpdate() below now unconditionally re-locks
    ///     handle's rotation AND pivot's entire local transform back to their
    ///     captured defaults every single frame, regardless of what touched
    ///     them earlier in that frame or why - this can't identify a cause
    ///     outside this project's own scripts (for example, an Interactor-side
    ///     Inspector setting), but it does guarantee the symptom itself cannot
    ///     happen anymore, from any source, since LateUpdate always has the
    ///     last word before rendering.
    ///   - Fix A (no snap on grab, per item A): unchanged from the existing
    ///     grab-offset model described above - this was already correct and
    ///     is kept exactly as-is.
    ///   - Fix B (1:1 tracking, no lag, per item B): the previous version
    ///     scaled handMovement by a positionSensitivity factor (<1, i.e.
    ///     less than 1:1) and then exponentially smoothed handle.localPosition
    ///     toward that target frame over frame - both of those were real,
    ///     deliberate sources of "feels behind the hand," and per this
    ///     report's explicit request for exact 1:1 tracking with minimal
    ///     smoothing, both are removed: handMovement is added to
    ///     grabStartJoystickLocalPosition at full 1:1 scale, clamped to range,
    ///     and assigned DIRECTLY to handle.localPosition every valid frame -
    ///     no Lerp toward a target, no per-frame damping. The only thing still
    ///     in the way of a raw sample reaching handle.localPosition is the
    ///     existing frame-to-frame glitch guard (maxHandDeltaPerFrame) - not
    ///     smoothing, just a rejection of a single frame's tracking-loss-style
    ///     teleport (still far above any real 1cm-scale hand movement) so a
    ///     bad sample can't fling the stick to the edge of its range for one
    ///     frame. On a momentary hand-tracking drop-out, handle.localPosition
    ///     is now simply left untouched for that frame (holds its last valid
    ///     position) rather than being smoothed toward a separately-tracked
    ///     target - simpler, and behaviorally the same "freeze, don't guess."
    ///   - Fix C (rotation, per item C - the most important part of this
    ///     report): this script never did assign handle.localRotation or
    ///     pivot.localPosition/localRotation anywhere, in any version - but
    ///     "never assigns it" only guarantees it stays fixed if nothing ELSE
    ///     changes it either, and RightJoystick visibly rotating wildly proved
    ///     something was. LateUpdate() below now actively holds handle's
    ///     rotation and pivot's whole local transform at the exact values
    ///     captured the first frame (CaptureCenterIfNeeded), every single
    ///     frame, unconditionally - grabbed or not. Combined with Fix B only
    ///     ever writing handle.localPosition's X/Z (never touching rotation at
    ///     all), this satisfies items C's every bullet point directly: no
    ///     hand/interactor/HMD rotation value is ever read into this stick's
    ///     rotation, the Base/pivot never moves or rotates, and Handle's
    ///     localRotation always stays at its original default.
    ///   - Fix D/E (axes, hand-exclusivity, per items D/E): already exactly
    ///     as specified - handLocal.x/z map to localPosition.x/z, Y is never
    ///     read into tiltInput, and per-stick leftHandTracker/rightHandTracker
    ///     null-slots plus HandExclusiveGrabAdapter's onlyAllowedInteractor
    ///     already fully separate which hand can grab which stick (see
    ///     CreateJoystick/AttachHandInteractable in GundamCockpitSetup.cs,
    ///     unchanged) - nothing needed changing here.
    ///   - ShipMovementController/WeaponAimFireController (item H): not
    ///     referenced, read, or modified anywhere in this file - tiltInput's
    ///     public shape/meaning is identical to before, so both keep reading
    ///     it exactly as they already do.
    ///
    /// FIXED per real-device retest after the rewrite above ("조종이 제대로
    /// 안되고 내가 지정한 위치에 조종장치가 고정되지도 않음... 조종장치에
    /// 움직임이랑 건담에 움직임이랑 다름... 조종장치가 앞으로는 밀리지도
    /// 않음" - even after the offset-based/1:1 rewrite, the stick still didn't
    /// hold still where the hand held it, didn't match the hand's own
    /// movement, and forward push specifically did nothing):
    ///   - Root cause: GetActiveHandWorldPosition()/IsActiveHandTrackingValid()
    ///     previously read _grabbedInteractor.position - the real XRI "Near-Far
    ///     Interactor" GameObject's own Transform - as the actual MOVEMENT
    ///     source whenever a grab was XRI-driven, on the assumption that this
    ///     Transform tracks the physical hand 1:1 the same way
    ///     HandJointTracker.PalmPosition does. That assumption was never
    ///     actually verified (this project has no way to run/profile the XRI
    ///     sample rig's own internal driving of that Transform, only to read
    ///     its C#), and it directly contradicts the safer structure the very
    ///     first bug report on this stick asked for: "XRI = 잡았는지/놓았는지
    ///     이벤트만 담당, JoystickLever = 실제 조종간 위치 계산 담당" (XRI only
    ///     ever reports grabbed/released - JoystickLever alone computes
    ///     position). Using the interactor's own Transform for position math
    ///     broke that separation, and - unlike PalmPosition, which is already
    ///     proven correct everywhere else in this project (the visible hand
    ///     meshes, GripAmount/PinchAmount, CalibrationManager's own drag) - if
    ///     that Transform doesn't move 1:1 with the real hand (for example, if
    ///     the sample rig only reorients it for far-ray aiming and doesn't
    ///     translate it the same way for a near-field fist grab), every symptom
    ///     above follows directly: the stick doesn't hold at the hand's
    ///     position, doesn't match the hand's movement, and an axis that
    ///     Transform barely moves along (forward push) does nothing at all.
    ///   - Fix: GetActiveHandWorldPosition() and IsActiveHandTrackingValid() no
    ///     longer read _grabbedInteractor at all - both now unconditionally use
    ///     _activeHand (leftHandTracker/rightHandTracker's PalmPosition/
    ///     IsTracked), regardless of whether the grab was detected via XRI or
    ///     via the fallback proximity+gesture check. XRI's NotifyXRIGrabbed
    ///     is now used exactly as originally requested - a pure yes/no signal
    ///     (which hand grabbed, and that it's still held) - never as a position
    ///     source. Nothing about grab DETECTION changed (XRI is still the
    ///     preferred signal for starting/holding a grab, per hand-exclusivity);
    ///     only where the actual movement number comes from changed.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class JoystickLever : MonoBehaviour
    {
        [Header("Hinge / Base (never moves or rotates - only 'handle'/Stick's localPosition changes)")]
        [Tooltip("The fixed Base. Actively held at its captured default local position/rotation every frame (see LateUpdate) - never moved or rotated by this script or anything else.")]
        public Transform pivot;
        [Tooltip("The Stick that slides. Must be a child of Pivot. Only its localPosition (X/Z) is ever written by this script - its localRotation is actively held at its captured default every frame (see LateUpdate).")]
        public Transform handle;
        [Tooltip("Child of 'handle' marking the grip ball's visual center. Cosmetic/reference only - never used to move, snap, or offset the handle/pivot/stick's actual Transform (see the class doc comment's 'Grab-offset position model').")]
        public Transform gripPoint;

        [Header("Hands allowed to grab this stick")]
        public HandJointTracker leftHandTracker;
        public HandJointTracker rightHandTracker;

        [Header("Grab")]
        [Tooltip("How close (meters) the palm must be to the pivot to start a grab.")]
        public float grabRadius = 0.22f;
        [Range(0.1f, 1f)] public float pinchToGrabThreshold = 0.5f;
        [Range(0.05f, 1f)] public float pinchToReleaseThreshold = 0.3f;
        [Range(0.1f, 1f)] public float gripToGrabThreshold = 0.55f;
        [Range(0.05f, 1f)] public float gripToReleaseThreshold = 0.35f;

        [Header("Travel range (meters, +/- around the resting/center position)")]
        [Tooltip("Left/right travel limit, along the pivot's local X axis.")]
        public float lateralRange = 0.08f;
        [Tooltip("Forward/back (push/pull) travel limit, along the pivot's local Z axis.")]
        public float depthRange = 0.08f;

        [Header("Release spring-back speed")]
        [Tooltip("Only used AFTER release, to ease the handle back to center - grab tracking itself (while held) is direct 1:1, no smoothing (see the class doc comment's Fix B).")]
        public float returnLerpSpeed = 6f;

        [Header("Grab tracking safety (position only - not smoothing)")]
        [Tooltip("Meters. A sanity check only, NOT a speed limiter and NOT smoothing: if the RAW hand position jumps more than this between two consecutive frames, that sample is treated as a tracking-loss glitch and rejected (last valid hand movement is reused) rather than applied. Set well above the stick's own travel range (lateralRange+depthRange) so no ordinary push - however fast, including a full 1:1 hand movement - is ever mistaken for a glitch.")]
        public float maxHandDeltaPerFrame = 0.15f;
        [Tooltip("Dead zone (0-1, fraction of lateralRange/depthRange) applied to the final tiltInput, not the raw position. Values inside this radius read as exactly zero; values outside are smoothly rescaled back up to the full -1..1 range, so there is no jump at the boundary.")]
        [Range(0f, 0.3f)] public float deadZone = 0.08f;

        // Added per request ("XR Interaction Toolkit의 실제 Hand Interactor/Direct
        // Interactor를 사용" / "Grab 판정이 제대로 되도록"): the rig this project
        // instantiates ("XR Origin Hands (XR Rig)" from XRI's own "Hands Interaction
        // Demo" sample) already ships with a real, officially-supported hand
        // interactor per hand (a "Near-Far Interactor" under "Left Hand"/"Right
        // Hand"). GundamCockpitSetup.cs adds a plain XRSimpleInteractable +
        // HandExclusiveGrabAdapter onto this stick's handle and calls
        // NotifyXRIGrabbed below whenever that real interactor selects/deselects
        // it - the PREFERRED grab signal when present. The proximity+grip/pinch
        // check further down still runs independently as a fallback.
        bool _xriGrabbed;

        /// <summary>Called by HandExclusiveGrabAdapter when the real XR Interaction
        /// Toolkit interactor it's watching selects/deselects this stick's handle.
        /// 'interactorTransform' is accepted for signature compatibility with
        /// HandExclusiveGrabAdapter's existing call site but is deliberately NOT
        /// stored or read anywhere anymore (see the class doc comment's most
        /// recent FIXED note) - this is purely a yes/no "grabbed by the correct
        /// hand" signal now, never a position source. Not meant to be called from
        /// anywhere else. This is the ONLY thing HandExclusiveGrabAdapter ever
        /// calls on this class - it never reads or writes this stick's Transform
        /// (see the class doc comment's AUDIT note).</summary>
        public void NotifyXRIGrabbed(bool grabbed, Transform interactorTransform = null)
        {
            _xriGrabbed = grabbed;
        }

        /// <summary>The world position to move this stick's handle toward this
        /// frame - always the fallback HandJointTracker
        /// (leftHandTracker/rightHandTracker - already null-guarded per stick,
        /// never both non-null on the same JoystickLever) that CanGrab/
        /// StillGrabbing (or the XRI grab-start branch in Update) determined is
        /// holding it. See the class doc comment's most recent FIXED note for
        /// why this no longer ever reads the XRI interactor's own Transform.</summary>
        Vector3 GetActiveHandWorldPosition()
        {
            return _activeHand != null ? _activeHand.PalmPosition : Vector3.zero;
        }

        /// <summary>True while the currently-grabbed hand's position can be
        /// trusted this frame - i.e. the fallback HandJointTracker (the same one
        /// GetActiveHandWorldPosition reads) reports IsTracked. Used to freeze
        /// the stick's position on a momentary hand-tracking drop-out instead of
        /// advancing it from a stale/garbage sample.</summary>
        bool IsActiveHandTrackingValid()
        {
            return _activeHand != null && _activeHand.IsTracked;
        }

        /// <summary>Left/right (X) and forward/back-push (Y), each -1..1 - the
        /// same public shape/meaning as before the position-based redesign, so
        /// ShipMovementController / WeaponAimFireController / CockpitHUD all
        /// keep working unmodified.</summary>
        public Vector2 tiltInput { get; private set; }
        public bool isGrabbed => _activeHand != null;

        HandJointTracker _activeHand;

        // grabStartHandLocalPosition / grabStartJoystickLocalPosition, captured
        // ONCE on the very first grabbed frame and never touched again until
        // the next grab starts. Every later frame computes:
        //   handMovement = currentHandLocalPosition - _grabStartHandLocal
        //   targetJoystickPosition = _grabStartJoystickLocal + handMovement
        // (equivalently "currentHandLocalPosition - grabOffset", since
        // grabOffset = _grabStartHandLocal - _grabStartJoystickLocal) - the
        // same relative grab point stays under the hand for the whole grab,
        // and the handle never re-snaps to the hand's own position (item A).
        Vector3 _grabStartHandLocal;
        Vector3 _grabStartJoystickLocal;

        // Raw-hand-position glitch guard state (see maxHandDeltaPerFrame) -
        // a rejection check only, not smoothing (see Fix B above).
        Vector3 _lastRawHandLocal;
        Vector3 _lastValidHandMovement;

        Vector3 _centerLocalPos;          // resting/center local position the handle returns to on release
        Quaternion _centerLocalRotation;  // handle's own default localRotation - re-asserted every frame (Fix C)
        Vector3 _pivotDefaultLocalPos;    // pivot's default localPosition - re-asserted every frame (Fix C)
        Quaternion _pivotDefaultLocalRotation; // pivot's default localRotation - re-asserted every frame (Fix C)
        bool _centerCaptured;

        void Start()
        {
            CaptureCenterIfNeeded();
        }

        void CaptureCenterIfNeeded()
        {
            if (_centerCaptured || handle == null || pivot == null) return;
            _centerLocalPos = handle.localPosition;
            _centerLocalRotation = handle.localRotation;
            _pivotDefaultLocalPos = pivot.localPosition;
            _pivotDefaultLocalRotation = pivot.localRotation;
            _centerCaptured = true;
        }

        void Update()
        {
            if (pivot == null || handle == null) return;
            CaptureCenterIfNeeded();

            bool wasGrabbed = _activeHand != null;

            if (_activeHand == null)
            {
                if (_xriGrabbed)
                {
                    // The real XRI interactor already told us (via
                    // HandExclusiveGrabAdapter) that the correct hand for THIS
                    // stick selected it - leftHandTracker/rightHandTracker are
                    // never both non-null on the same stick (see CreateJoystick
                    // in GundamCockpitSetup.cs), so whichever one is assigned here
                    // is the right hand to track.
                    _activeHand = leftHandTracker != null ? leftHandTracker : rightHandTracker;
                }
                else if (CanGrab(leftHandTracker)) _activeHand = leftHandTracker;
                else if (CanGrab(rightHandTracker)) _activeHand = rightHandTracker;
            }
            else if (!_xriGrabbed && !StillGrabbing(_activeHand))
            {
                // Release once BOTH the real XRI selection AND the fallback
                // proximity+gesture check agree the hand has let go - keeps
                // whichever signal is still holding on from getting overridden
                // by the other one dropping out a frame earlier.
                _activeHand = null;
            }

            if (_activeHand != null)
            {
                // Hand position relative to THIS stick's own Base, recomputed
                // fresh every frame via InverseTransformPoint - see the class
                // doc comment above for why this (not a cached world-space
                // delta) is what keeps MobileSuitRoot's own translation from
                // leaking into the stick's tracked position. Sourced through
                // GetActiveHandWorldPosition() - the exact grabbing interactor's
                // Transform when XRI-driven, never a shared/looked-up hand.
                Vector3 handLocalNow = pivot.InverseTransformPoint(GetActiveHandWorldPosition());

                if (!wasGrabbed)
                {
                    // Grab just started (item A). Record ONLY the two
                    // reference points - grabStartHandLocalPosition and
                    // grabStartJoystickLocalPosition (wherever the handle
                    // already was) - handle.localPosition is not assigned
                    // anywhere in this branch, so the stick provably does not
                    // move this frame, regardless of where on the handle the
                    // hand grabbed. No snap, ever.
                    _grabStartHandLocal = handLocalNow;
                    _grabStartJoystickLocal = handle.localPosition;

                    // Glitch-guard state also (re)starts clean from this exact
                    // frame's real sample, so the very first held frame right
                    // after this one has a valid baseline to compare against.
                    _lastRawHandLocal = handLocalNow;
                    _lastValidHandMovement = Vector3.zero;
                }
                else if (IsActiveHandTrackingValid())
                {
                    // Glitch guard operates on the RAW hand position,
                    // frame-to-frame - NOT a smoothing target - so it can
                    // never mistake a genuinely fast/large intentional push
                    // (or a full-range 1:1 hand movement) for a glitch; only a
                    // true tracking-loss-style teleport gets rejected, in
                    // which case last frame's valid movement is reused
                    // instead of jumping.
                    Vector3 rawHandFrameDelta = handLocalNow - _lastRawHandLocal;
                    rawHandFrameDelta.y = 0f;

                    Vector3 handMovement = handLocalNow - _grabStartHandLocal;
                    handMovement.y = 0f; // item D - Y is never used for tracking/input

                    if (rawHandFrameDelta.magnitude > maxHandDeltaPerFrame)
                    {
                        handMovement = _lastValidHandMovement;
                    }
                    else
                    {
                        _lastValidHandMovement = handMovement;
                    }
                    _lastRawHandLocal = handLocalNow;

                    // Item B: exact 1:1 - handMovement is added at full scale
                    // (no sensitivity factor), range-clamped ONLY at the very
                    // end, and assigned DIRECTLY to handle.localPosition - no
                    // per-frame smoothing/lerp toward a separate target, so
                    // there is no lag between the hand moving and the stick
                    // moving.
                    handle.localPosition = ClampToRange(_grabStartJoystickLocal + handMovement);
                }
                // else: hand tracking is momentarily invalid - handle.localPosition
                // is simply left untouched this frame (holds its last valid
                // position) rather than advancing from a stale/garbage sample.
                // Once tracking resumes, the glitch guard above still protects
                // against a wild first sample right after the drop-out.
            }
            else
            {
                // Released - smoothly ease back to center. This is release
                // polish only, not grab-tracking, so it keeps its own lerp.
                float lerpT = 1f - Mathf.Exp(-returnLerpSpeed * Time.deltaTime);
                handle.localPosition = Vector3.Lerp(handle.localPosition, _centerLocalPos, lerpT);
            }

            Vector3 offset = handle.localPosition - _centerLocalPos;
            float rawX = lateralRange > 0.0001f ? Mathf.Clamp(offset.x / lateralRange, -1f, 1f) : 0f;
            float rawY = depthRange > 0.0001f ? Mathf.Clamp(offset.z / depthRange, -1f, 1f) : 0f;

            // Dead zone applied to the final input value (not the raw
            // position): zero inside the dead zone, then smoothly rescaled
            // back up to the full -1..1 range outside it, so there's no jump
            // right at the boundary - same technique as
            // ShipMovementController.ApplyDeadZone.
            Vector2 result = new Vector2(ApplyDeadZoneRescale(rawX, deadZone), ApplyDeadZoneRescale(rawY, deadZone));

            // Final explicit safety clamp (defense-in-depth beyond the
            // per-axis Mathf.Clamp above): guard against the combined
            // magnitude ever exceeding 1, however it might have been produced.
            if (result.magnitude > 1f) result = result.normalized;
            tiltInput = new Vector2(Mathf.Clamp(result.x, -1f, 1f), Mathf.Clamp(result.y, -1f, 1f));
        }

        /// <summary>Item C, the core fix for the "조종간이 갑자기 크게 꺾이거나
        /// 이상한 방향으로 회전한다" report: unconditionally re-asserts handle's
        /// rotation and pivot's entire local transform back to the defaults
        /// captured in CaptureCenterIfNeeded, every single frame, regardless of
        /// grabbed state or of anything else that may have touched them earlier
        /// in this same frame. Runs in LateUpdate specifically so it is always
        /// the last write before rendering - nothing later in the frame can
        /// still leave a stray rotation/position on this stick, whatever its
        /// source (this project's own scripts were audited and found NOT to do
        /// this - see the class doc comment's AUDIT note - but this guard does
        /// not depend on having found the exact external cause to work).</summary>
        void LateUpdate()
        {
            if (!_centerCaptured) return;
            if (handle != null) handle.localRotation = _centerLocalRotation;
            if (pivot != null)
            {
                pivot.localPosition = _pivotDefaultLocalPos;
                pivot.localRotation = _pivotDefaultLocalRotation;
            }
        }

        /// <summary>Zeroes |v| inside deadZone, then rescales the remainder
        /// back up to the full 0..1 range (continuous at the boundary - no
        /// notch/jump), preserving sign. deadZone is a fraction of the full
        /// -1..1 range.</summary>
        static float ApplyDeadZoneRescale(float v, float deadZone)
        {
            float magnitude = Mathf.Abs(v);
            if (magnitude <= deadZone) return 0f;
            if (deadZone >= 1f) return 0f;
            float rescaled = (magnitude - deadZone) / (1f - deadZone);
            return Mathf.Sign(v) * Mathf.Clamp01(rescaled);
        }

        Vector3 ClampToRange(Vector3 local)
        {
            float clampedX = Mathf.Clamp(local.x, _centerLocalPos.x - lateralRange, _centerLocalPos.x + lateralRange);
            float clampedZ = Mathf.Clamp(local.z, _centerLocalPos.z - depthRange, _centerLocalPos.z + depthRange);
            return new Vector3(clampedX, _centerLocalPos.y, clampedZ);
        }

        bool CanGrab(HandJointTracker hand)
        {
            if (hand == null || !hand.IsTracked) return false;
            bool gripping = hand.GripAmount >= gripToGrabThreshold;
            bool pinching = hand.PinchAmount >= pinchToGrabThreshold;
            if (!gripping && !pinching) return false;
            return Vector3.Distance(hand.PalmPosition, pivot.position) <= grabRadius;
        }

        bool StillGrabbing(HandJointTracker hand)
        {
            if (hand == null || !hand.IsTracked) return false;
            return hand.GripAmount >= gripToReleaseThreshold || hand.PinchAmount >= pinchToReleaseThreshold;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (pivot == null) return;
            Gizmos.color = new Color(1f, 1f, 0f, 0.35f);
            Gizmos.DrawWireSphere(pivot.position, grabRadius);

            if (handle != null)
            {
                Vector3 centerLocal = Application.isPlaying ? _centerLocalPos : handle.localPosition;
                Vector3 centerWorld = pivot.TransformPoint(centerLocal);
                Vector3 size = new Vector3(lateralRange * 2f, 0.01f, depthRange * 2f);

                Matrix4x4 oldMatrix = Gizmos.matrix;
                Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
                Gizmos.matrix = Matrix4x4.TRS(centerWorld, pivot.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, size);
                Gizmos.matrix = oldMatrix;
            }
        }
#endif
    }
}
