using UnityEngine;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Tracks a single physical hand (left or right) using the XR Hands package.
    /// Exposes the palm pose (in world space, after being parented under the XR
    /// Origin's tracking space) and a 0-1 pinch amount computed from the distance
    /// between the thumb tip and index tip joints.
    ///
    /// This does NOT depend on XR Interaction Toolkit interactors/prefabs - it only
    /// needs the XR Hands package (com.unity.xr.hands) plus an OpenXR runtime that
    /// reports hand tracking data (enable "Hand Tracking Subsystem" under
    /// Project Settings > XR Plug-in Management > OpenXR > the Android tab).
    /// </summary>
    [RequireComponent(typeof(XRHandTrackingEvents))]
    public class HandJointTracker : MonoBehaviour
    {
        public Handedness handedness = Handedness.Left;

        [Tooltip("Distance (meters) between thumb tip and index tip that counts as a full pinch (PinchAmount = 1).")]
        [Range(0.01f, 0.08f)]
        public float fullPinchDistance = 0.025f;

        [Tooltip("Distance (meters) between thumb tip and index tip that counts as an open hand (PinchAmount = 0).")]
        [Range(0.03f, 0.15f)]
        public float openHandDistance = 0.08f;

        // Added per report ("손이 만들어졌고 근데 그걸로 안에있는 컨트롤러가
        // 잡히지는 않아"): PinchAmount alone (thumb-tip to index-tip distance) is
        // the wrong signal for JoystickLever's grip ball, which was explicitly
        // built to be WRAPPED with the whole hand like a fist around a sphere
        // ("구형으로 만들어서 손으로 움켜쥐고 사진과 같이 잡고"), not pinched
        // between two fingertips. When you actually close a fist around a ball,
        // the thumb tip and index tip usually stay fairly far apart (the thumb
        // wraps one side, the other four fingers curl around the opposite side),
        // so PinchAmount stays low even though the hand is very clearly
        // "grabbing" something - this alone likely explains why the joystick
        // still wouldn't grab even once the hand mesh itself started showing up
        // correctly. GripAmount below measures the gesture that actually matches
        // that grab: how far the four non-thumb fingertips have curled in toward
        // the palm.
        [Tooltip("Distance (meters) from each of the index/middle/ring/little fingertips to the palm that counts as a fully closed fist/grip (GripAmount = 1).")]
        [Range(0.01f, 0.08f)]
        public float fullGripDistance = 0.045f;

        [Tooltip("Distance (meters) from each fingertip to the palm that counts as a fully open hand (GripAmount = 0).")]
        [Range(0.05f, 0.18f)]
        public float openGripDistance = 0.11f;

        public Vector3 PalmPosition { get; private set; }
        public Quaternion PalmRotation { get; private set; } = Quaternion.identity;
        public float PinchAmount { get; private set; }

        /// <summary>0 (open hand) to 1 (closed fist), averaged over whichever of the
        /// index/middle/ring/little fingertips report a valid pose this update. Use this
        /// - not PinchAmount - for a "grab this object by wrapping your whole hand around
        /// it" interaction (e.g. JoystickLever's grip ball); PinchAmount stays the right
        /// signal for a precise two-finger pinch gesture.</summary>
        public float GripAmount { get; private set; }

        public bool IsTracked => _events != null && _events.handIsTracked;

        XRHandTrackingEvents _events;

        void Awake()
        {
            _events = GetComponent<XRHandTrackingEvents>();
            _events.handedness = handedness;
            _events.jointsUpdated.AddListener(OnJointsUpdated);
        }

        void OnDestroy()
        {
            if (_events != null)
            {
                _events.jointsUpdated.RemoveListener(OnJointsUpdated);
            }
        }

        void OnJointsUpdated(XRHandJointsUpdatedEventArgs args)
        {
            // Joint poses are reported in the same local tracking space as the
            // XR Origin, so this object must be parented directly under it.
            if (args.hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose))
            {
                transform.localPosition = palmPose.position;
                transform.localRotation = palmPose.rotation;
            }

            PalmPosition = transform.position;
            PalmRotation = transform.rotation;

            bool gotThumb = args.hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose);
            bool gotIndex = args.hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexPose);
            if (gotThumb && gotIndex)
            {
                float dist = Vector3.Distance(thumbPose.position, indexPose.position);
                float t = Mathf.InverseLerp(openHandDistance, fullPinchDistance, dist);
                PinchAmount = Mathf.Clamp01(t);
            }

            // Whole-hand "fist grip" amount - see GripAmount's doc comment above.
            // Uses the same Palm joint pose already read above as the reference
            // point each fingertip curls toward.
            if (args.hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmForGrip))
            {
                float gripSum = 0f;
                int gripCount = 0;
                AccumulateFingerCurl(args.hand, XRHandJointID.IndexTip, palmForGrip.position, ref gripSum, ref gripCount);
                AccumulateFingerCurl(args.hand, XRHandJointID.MiddleTip, palmForGrip.position, ref gripSum, ref gripCount);
                AccumulateFingerCurl(args.hand, XRHandJointID.RingTip, palmForGrip.position, ref gripSum, ref gripCount);
                AccumulateFingerCurl(args.hand, XRHandJointID.LittleTip, palmForGrip.position, ref gripSum, ref gripCount);

                if (gripCount > 0)
                {
                    GripAmount = Mathf.Clamp01(gripSum / gripCount);
                }
            }
        }

        void AccumulateFingerCurl(XRHand hand, XRHandJointID tipId, Vector3 palmPosition, ref float sum, ref int count)
        {
            if (!hand.GetJoint(tipId).TryGetPose(out Pose tipPose)) return;
            float dist = Vector3.Distance(tipPose.position, palmPosition);
            float t = Mathf.InverseLerp(openGripDistance, fullGripDistance, dist);
            sum += t;
            count++;
        }
    }
}
