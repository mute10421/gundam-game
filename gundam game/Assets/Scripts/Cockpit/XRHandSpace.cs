using UnityEngine;
using Unity.XR.CoreUtils;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Correct world-space conversion for XR Hands joint poses - found while fixing
    /// "디스플레이가 터치가안됨".
    ///
    /// XR Hands reports joint poses in the XR Origin's TRACKING space, which is the
    /// XR Origin's "Camera Offset" object (XROrigin.CameraFloorOffsetObject). This rig
    /// runs in Device tracking mode, so Camera Offset sits 1.36 m above the XR Origin
    /// root. HandJointTracker lives directly under the XR Origin ROOT, so its
    /// PalmPosition is 1.36 m too low in world space. The existing sticks / T lever /
    /// finger buttons never noticed because they only use hand MOVEMENT (deltas) or
    /// directions, but anything that compares a hand to a real object's position
    /// (the display touch, the saber stick) needs the real position.
    ///
    /// HandJointTracker itself is not modified - this just re-reads the raw pose it
    /// already stores in its localPosition/localRotation through the correct space.
    /// </summary>
    public static class XRHandSpace
    {
        /// <summary>The space XR Hands joint poses are expressed in for this tracker's rig.</summary>
        public static Transform TrackingSpace(Component c)
        {
            if (c == null) return null;
            XROrigin origin = c.GetComponentInParent<XROrigin>();
            if (origin != null && origin.CameraFloorOffsetObject != null) return origin.CameraFloorOffsetObject.transform;
            if (origin != null) return origin.transform;
            return c.transform.parent;
        }

        /// <summary>Real world position of the tracked palm.</summary>
        public static Vector3 PalmPosition(HandJointTracker h)
        {
            Transform s = TrackingSpace(h);
            return s != null ? s.TransformPoint(h.transform.localPosition) : h.PalmPosition;
        }

        /// <summary>Real world rotation of the tracked palm.</summary>
        public static Quaternion PalmRotation(HandJointTracker h)
        {
            Transform s = TrackingSpace(h);
            return s != null ? s.rotation * h.transform.localRotation : h.PalmRotation;
        }
    }
}
