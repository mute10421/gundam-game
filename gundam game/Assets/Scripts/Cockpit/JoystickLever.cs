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
    /// GripPoint / exact hand attach (per report - "손과 조종간 손잡이가 정확히
    /// 붙어야 한다... Grab이 시작되는 즉시 GripPoint가 실제 손바닥 위치에 정확히
    /// 맞는다... Grab 중 손이 움직이면 손잡이가 손을 정확하게 따라간다"):
    ///   - 'handle' (the Stick) is the object that actually moves; 'gripPoint' is
    ///     a separate child Transform under it marking the exact point that must
    ///     coincide with the hand's palm/grip position - NOT the same thing as
    ///     copying the whole handle Transform onto the hand.
    ///   - The instant a grab starts, the Stick SNAPS (no lerp/delay) so that
    ///     GripPoint lands exactly on the hand's current position (clamped to the
    ///     travel range) - so there's never a visible gap at the moment of grab.
    ///   - While held, the Stick tracks the hand RIGIDLY (also no lerp) - every
    ///     frame's hand movement is applied 1:1 (clamped), so GripPoint stays
    ///     glued to the hand with no lag or drift as long as the hand stays
    ///     within the travel range. Smoothing is reserved for the release-to-
    ///     center spring-back only (returnLerpSpeed below).
    ///   - Both the snap and the per-frame tracking measure the hand's position
    ///     RELATIVE TO THIS STICK'S OWN Base/pivot, recomputed fresh every frame
    ///     via pivot.InverseTransformPoint (not a cached world-space start
    ///     position + rotated delta). This matters because the Base (this
    ///     joystick's pivot) is itself a descendant of MobileSuitRoot, which
    ///     LeftJoystick's own input can translate through the cockpit - a
    ///     world-space delta would pick up the suit's own translation as if it
    ///     were hand movement (causing the OTHER stick, or even this same stick,
    ///     to visibly drift whenever the suit moves, with no real hand input
    ///     behind it). Re-deriving the hand's PIVOT-LOCAL position each frame
    ///     cancels out any common parent motion automatically, since the hand
    ///     tracker rides along with the same moving cockpit as the pivot does.
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
    /// </summary>
    public class JoystickLever : MonoBehaviour
    {
        [Header("Hinge / Base (never moves - only 'handle'/Stick moves)")]
        [Tooltip("The fixed Base. Never moved by this script.")]
        public Transform pivot;
        [Tooltip("The Stick that slides. Must be a child of Pivot.")]
        public Transform handle;
        [Tooltip("Child of 'handle' marking the exact point that must coincide with the hand's palm/grip while grabbed. Defaults to handle's own origin if left unassigned.")]
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

        [Header("Release spring-back speed (grab tracking itself is rigid/1:1, not smoothed)")]
        public float returnLerpSpeed = 5f;

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
        /// Not meant to be called from anywhere else.</summary>
        public void NotifyXRIGrabbed(bool grabbed)
        {
            _xriGrabbed = grabbed;
        }

        /// <summary>Left/right (X) and forward/back-push (Y), each -1..1 - the
        /// same public shape/meaning as before the position-based redesign, so
        /// ShipMovementController / WeaponAimFireController / CockpitHUD all
        /// keep working unmodified.</summary>
        public Vector2 tiltInput { get; private set; }
        public bool isGrabbed => _activeHand != null;

        HandJointTracker _activeHand;
        Vector3 _handStartLocal;         // hand's position relative to pivot, at grab start
        Vector3 _handleStartLocalPos;    // handle.localPosition (relative to pivot) at grab start
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
                // leaking into the stick's tracked position.
                Vector3 handLocalNow = pivot.InverseTransformPoint(_activeHand.PalmPosition);
                Vector3 gripOffset = gripPoint != null ? gripPoint.localPosition : Vector3.zero;

                if (!wasGrabbed)
                {
                    // Just grabbed this frame - snap immediately (no lerp) so
                    // GripPoint lands exactly on the hand's current position,
                    // clamped to the travel range. No visible gap at grab start.
                    _handStartLocal = handLocalNow;
                    _handleStartLocalPos = ClampToRange(handLocalNow - gripOffset);
                    handle.localPosition = _handleStartLocalPos;
                }
                else
                {
                    // Rigid (unsmoothed) 1:1 tracking of the hand's movement
                    // since grab start, clamped to the travel range - GripPoint
                    // stays glued to the hand with no lag while within range.
                    Vector3 localDelta = handLocalNow - _handStartLocal;
                    Vector3 wanted = _handleStartLocalPos + new Vector3(localDelta.x, 0f, localDelta.z);
                    handle.localPosition = ClampToRange(wanted);
                }
            }
            else
            {
                // Released - smoothly ease back to center.
                float lerpT = 1f - Mathf.Exp(-returnLerpSpeed * Time.deltaTime);
                handle.localPosition = Vector3.Lerp(handle.localPosition, _centerLocalPos, lerpT);
            }

            Vector3 offset = handle.localPosition - _centerLocalPos;
            tiltInput = new Vector2(
                lateralRange > 0.0001f ? Mathf.Clamp(offset.x / lateralRange, -1f, 1f) : 0f,
                depthRange > 0.0001f ? Mathf.Clamp(offset.z / depthRange, -1f, 1f) : 0f);
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
