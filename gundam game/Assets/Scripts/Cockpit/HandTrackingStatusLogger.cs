using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Minimal on-device confirmation logging for XR hand tracking - per
    /// request ("손 추적이 정상적으로 들어오는지 확인할 수 있는 최소한의
    /// 로그"). Logs exactly one line whenever a hand's tracked state
    /// changes (acquired/lost) - never every frame - so it's cheap to leave
    /// running and easy to spot in adb logcat / the Unity console while
    /// testing hand tracking on-device.
    ///
    /// Reuses the existing HandJointTracker.IsTracked (already reading live
    /// XR Hands joint data for the joystick grab logic) instead of
    /// subscribing to XRHandSubsystem separately - one source of truth for
    /// "is this hand tracked right now".
    /// </summary>
    public class HandTrackingStatusLogger : MonoBehaviour
    {
        public HandJointTracker leftHandTracker;
        public HandJointTracker rightHandTracker;

        bool _leftWasTracked;
        bool _rightWasTracked;

        void Update()
        {
            LogTransition(leftHandTracker, "Left", ref _leftWasTracked);
            LogTransition(rightHandTracker, "Right", ref _rightWasTracked);
        }

        static void LogTransition(HandJointTracker tracker, string label, ref bool wasTracked)
        {
            if (tracker == null) return;

            bool isTracked = tracker.IsTracked;
            if (isTracked == wasTracked) return;

            wasTracked = isTracked;
            Debug.Log(isTracked
                ? "[Gundam] " + label + " hand tracking ACQUIRED."
                : "[Gundam] " + label + " hand tracking LOST.");
        }
    }
}
