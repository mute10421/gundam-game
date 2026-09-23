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
    /// signal, never to move the object itself.
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
    /// hand's own position the instant a grab started (see the class's edit
    /// history / previous doc comment here) - that is exactly the bug that made
    /// the stick appear to fly toward the player's hand/body on every grab,
    /// wherever on the handle they actually grabbed. That snap is GONE. The
    /// current model instead:
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
    ///   - 'gripPoint' is now purely a cosmetic/visual marker (the grip ball's
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
    ///   - Ongoing tracking is still lightly SMOOTHED and spike-guarded (see
    ///     "Input Stabilization" below) - only the grab-START frame is special
    ///     (it must do nothing at all to handle.localPosition, not even a
    ///     smoothed nudge, so there is truly zero motion the instant of grab).
    ///
    /// Movement model (per request - "실제 비행기/로봇 조종간처럼 손으로 잡고
    /// 앞뒤/좌우로 밀고 당기는 방식"):
    ///   - The Base (this GameObject / pivot) NEVER moves. Only the Stick
    ///     ('handle') moves, within a flat X (left/right) / Z (forward/back push)
    ///     plane relative to the Base - Y (height) never changes, and the Stick
    ///     never rotates in place.
    ///   - tiltInput keeps its original public meaning/shape (X = left/right,
    ///     Y = forward/back push, each -1..1) so every existing consumer
    ///     (ShipMovementController, WeaponAimFireController, CockpitHUD) keeps
    ///     working completely unmodified - it's just derived from clamped
    ///     position now instead of a tilt angle.
    ///
    /// Input stabilization (per report - "손 움직임을 조이스틱이 살짝 늦게 따라오는
    /// 느낌... 감도가 너무 민감함... 가끔 손 추적값이 순간적으로 튀면서 기체가
    /// 갑자기 엉뚱한 방향으로 마구 날아감" - AND a later, contradicting report that
    /// this made the stick barely follow the hand at all: "손을 앞으로 밀어도
    /// 조종간이 거의 안 움직이거나 입력이 제대로 나오지 않는다"): applied ONLY to
    /// ongoing tracking, never to the grab-start frame above. The two reports
    /// are reconciled by NOT double-lagging the signal:
    ///   1) The spike/glitch guard now compares this frame's RAW hand position
    ///      against LAST frame's RAW hand position (not the target against
    ///      itself) - a genuine, deliberate hand push of even the whole travel
    ///      range in one frame is never mistaken for a glitch, only a true
    ///      tracking-loss-style teleport (far larger than the entire lateral/
    ///      depth range) is rejected, in which case last frame's valid hand
    ///      movement is reused instead of jumping.
    ///   2) The tracked TARGET itself (_trackedTargetLocal) is then assigned
    ///      directly every valid frame - grabStartJoystickPosition +
    ///      (sensitivity-scaled) handMovement, clamped to range - with no
    ///      separate per-frame rate limit of its own. An earlier version also
    ///      clamped how far the TARGET could move each frame (on top of this),
    ///      which - combined with hand-tracking data that can arrive in
    ///      already-somewhat-discrete jumps rather than a perfectly smooth
    ///      stream - produced exactly the "barely moves" symptom: legitimate
    ///      hand motion kept re-triggering that clamp. Removed.
    ///   3) positionSensitivity scales handMovement down before it's added to
    ///      the target, so the same real hand movement produces less stick
    ///      travel - lower sensitivity without changing the physical travel
    ///      range (lateralRange/depthRange) or adding any lag.
    ///   4) smoothingSpeed exponentially smooths the handle's actual position
    ///      toward that (already-correct, unlagged) target every frame (the
    ///      same 1 - exp(-speed * dt) idiom already used for the release
    ///      spring-back below) - this is now the ONLY source of tracking lag,
    ///      deliberately tuned fast enough to feel immediate and only filter
    ///      out small per-frame jitter, not real pushes.
    ///   5) When the currently-grabbed hand's tracking briefly becomes invalid
    ///      (IsActiveHandTrackingValid false), the tracked target is simply
    ///      held/frozen for that frame rather than advanced from a stale or
    ///      garbage sample - so nothing jumps to a default/zero value, and once
    ///      tracking resumes the raw-hand-delta glitch guard from (1) still
    ///      protects against a wild first sample after the drop-out.
    /// Finally, deadZone is applied to the derived tiltInput itself (not the
    /// raw position) using the same "zero below threshold, then rescale the
    /// remainder back up to the full -1..1 range" technique already used by
    /// ShipMovementController.ApplyDeadZone, so there is no discontinuity/notch
    /// right at the dead zone boundary - and a final explicit clamp/normalize
    /// guards tiltInput's magnitude as defense-in-depth beyond the per-axis
    /// Mathf.Clamp already present.
    /// </summary>
    public class JoystickLever : MonoBehaviour
    {
        [Header("Hinge / Base (never moves - only 'handle'/Stick moves)")]
        [Tooltip("The fixed Base. Never moved by this script.")]
        public Transform pivot;
        [Tooltip("The Stick that slides. Must be a child of Pivot.")]
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
        public float returnLerpSpeed = 6f;

        [Header("Input Stabilization (ongoing tracking only - grab-start snap stays instant)")]
        [Tooltip("Scales handMovement (hand's movement since grab start) before it's added to the tracked target, each frame, while held. Lower = less sensitive (hand must move further for the same stick travel). ~0.65 is roughly 30-40% less sensitive than 1:1 (1.0). Does NOT add lag - it scales distance, not speed.")]
        [Range(0.1f, 1f)] public float positionSensitivity = 0.65f;
        [Tooltip("Exponential smoothing speed applied to the handle's position while tracking toward the target. This is the ONLY intended source of tracking lag/lag-feel - keep it high (20-30) so a real push is followed almost immediately and only small per-frame jitter gets filtered. Does not affect the instant grab-start snap.")]
        public float smoothingSpeed = 25f;
        [Tooltip("Meters. A sanity check only, NOT a speed limiter: if the RAW hand position jumps more than this between two consecutive frames, that sample is treated as a tracking-loss glitch and rejected (last valid hand movement is reused) rather than applied. Set well above the stick's own travel range (lateralRange+depthRange) so no ordinary push - however fast - is ever mistaken for a glitch.")]
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

        // The EXACT interactor Transform HandExclusiveGrabAdapter's own
        // OnSelectEntered already confirmed matches this stick's
        // onlyAllowedInteractor (see that class) - set only alongside
        // _xriGrabbed becoming true, cleared alongside it becoming false.
        // Movement math below reads THIS directly while it's set, instead of
        // leftHandTracker/rightHandTracker, so an XRI-driven grab's position
        // never passes through any shared/looked-up hand reference at all.
        Transform _grabbedInteractor;

        /// <summary>Called by HandExclusiveGrabAdapter when the real XR Interaction
        /// Toolkit interactor it's watching selects/deselects this stick's handle.
        /// 'interactorTransform' is that exact interactor's own Transform (e.g.
        /// this rig's "Left Hand" > "Near-Far Interactor") - already verified by
        /// the adapter to be the one allowed interactor for this stick before this
        /// is ever called with grabbed=true. Not meant to be called from anywhere
        /// else.</summary>
        public void NotifyXRIGrabbed(bool grabbed, Transform interactorTransform = null)
        {
            _xriGrabbed = grabbed;
            _grabbedInteractor = grabbed ? interactorTransform : null;
        }

        /// <summary>The world position to move this stick's handle toward this
        /// frame: the exact XRI interactor's Transform while it's grabbed that
        /// way (see _grabbedInteractor above - the most direct, unambiguous
        /// source available), otherwise the fallback HandJointTracker
        /// (leftHandTracker/rightHandTracker - already null-guarded per stick,
        /// never both non-null on the same JoystickLever) that CanGrab/
        /// StillGrabbing determined is holding it.</summary>
        Vector3 GetActiveHandWorldPosition()
        {
            if (_xriGrabbed && _grabbedInteractor != null) return _grabbedInteractor.position;
            return _activeHand != null ? _activeHand.PalmPosition : Vector3.zero;
        }

        /// <summary>True while the currently-grabbed hand's position can be
        /// trusted this frame. An XRI-driven grab reads a live Transform
        /// (_grabbedInteractor), which has no "untracked" state of its own, so
        /// it's always considered valid while set. The fallback HandJointTracker
        /// path is only valid while that tracker itself reports IsTracked. Used
        /// to freeze the stick's target/position on a momentary hand-tracking
        /// drop-out instead of advancing it from a stale/garbage sample.</summary>
        bool IsActiveHandTrackingValid()
        {
            if (_xriGrabbed && _grabbedInteractor != null) return true;
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
        // and the handle never re-snaps to the hand's own position.
        Vector3 _grabStartHandLocal;
        Vector3 _grabStartJoystickLocal;

        // Raw-hand-position glitch guard state (see maxHandDeltaPerFrame) -
        // deliberately separate from the target/smoothing state above so a
        // rejected glitch sample never lags legitimate tracking.
        Vector3 _lastRawHandLocal;
        Vector3 _lastValidHandMovement;

        Vector3 _trackedTargetLocal;     // this frame's exact (sensitivity-scaled, glitch-guarded) target for handle.localPosition, relative to pivot - handle.localPosition itself only ever smooths TOWARD this, never snaps to it
        Vector3 _centerLocalPos;         // resting/center local position the handle returns to
        bool _centerCaptured;

        void Start()
        {
            CaptureCenterIfNeeded();
        }

        void CaptureCenterIfNeeded()
        {
            if (_centerCaptured || handle == null) return;
            _centerLocalPos = handle.localPosition;
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
                    // Grab just started. Record ONLY the two reference points
                    // - grabStartHandLocalPosition and
                    // grabStartJoystickLocalPosition (wherever the handle
                    // already was) - handle.localPosition is not assigned
                    // anywhere in this branch, so the stick provably does not
                    // move this frame, regardless of where on the handle the
                    // hand grabbed.
                    _grabStartHandLocal = handLocalNow;
                    _grabStartJoystickLocal = handle.localPosition;
                    _trackedTargetLocal = handle.localPosition;

                    // Glitch-guard state also (re)starts clean from this exact
                    // frame's real sample, so the very first held frame right
                    // after this one has a valid baseline to compare against.
                    _lastRawHandLocal = handLocalNow;
                    _lastValidHandMovement = Vector3.zero;
                }
                else
                {
                    bool trackingValid = IsActiveHandTrackingValid();

                    if (trackingValid)
                    {
                        // Glitch guard operates on the RAW hand position,
                        // frame-to-frame - NOT on the target - so it can never
                        // mistake a genuinely fast/large intentional push for
                        // a glitch (the whole travel range is only 8cm either
                        // way, so legitimate motion is always small in
                        // absolute terms; maxHandDeltaPerFrame's default sits
                        // well above that). Only a true tracking-loss-style
                        // teleport gets rejected, in which case last frame's
                        // valid movement is reused instead of jumping.
                        Vector3 rawHandFrameDelta = handLocalNow - _lastRawHandLocal;
                        rawHandFrameDelta.y = 0f;

                        Vector3 handMovement = handLocalNow - _grabStartHandLocal;
                        handMovement.y = 0f;

                        if (rawHandFrameDelta.magnitude > maxHandDeltaPerFrame)
                        {
                            handMovement = _lastValidHandMovement;
                        }
                        else
                        {
                            _lastValidHandMovement = handMovement;
                        }
                        _lastRawHandLocal = handLocalNow;

                        // targetJoystickPosition = grabStartJoystickPosition +
                        // handMovement (sensitivity-scaled) - assigned
                        // DIRECTLY every valid frame, not accumulated/rate-
                        // limited frame-over-frame, so the target itself never
                        // lags behind the hand - only the final smoothing step
                        // below (smoothingSpeed) adds any lag, and only a
                        // small, deliberately fast amount of it.
                        _trackedTargetLocal = ClampToRange(_grabStartJoystickLocal + handMovement * positionSensitivity);
                    }
                    // else: hand tracking is momentarily invalid - hold
                    // _trackedTargetLocal, _lastRawHandLocal and
                    // _lastValidHandMovement exactly as they were, rather than
                    // advancing from a stale/garbage sample. Once tracking
                    // resumes, the glitch guard above still protects against a
                    // wild first sample right after the drop-out.

                    // Smooth (not rigid) approach toward the tracked target -
                    // this is the ONLY source of tracking lag, deliberately
                    // fast (see smoothingSpeed) so a real push is followed
                    // almost immediately and only small per-frame jitter is
                    // filtered out. The grab-start frame above never reaches
                    // here (handle.localPosition already equals
                    // _trackedTargetLocal that frame, so this is a no-op the
                    // instant a grab begins - still zero motion on grab).
                    float smoothT = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
                    handle.localPosition = Vector3.Lerp(handle.localPosition, _trackedTargetLocal, smoothT);
                }
            }
            else
            {
                // Released - smoothly ease back to center.
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
