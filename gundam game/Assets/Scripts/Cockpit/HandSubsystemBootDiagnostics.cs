using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// One-shot boot-time diagnostic for XRHandSubsystem - per request:
    /// "XRHandSubsystem 존재 여부 / running 여부 / left tracked / right tracked"
    /// logged once at app start, NOT every frame. Read this from logcat/adb
    /// after a device run (filter for "[Gundam][HandDiag]") to see, straight
    /// from the actual runtime subsystem (not from any of this project's own
    /// scripts, which all sit downstream of it), whether the headset is
    /// really delivering hand-tracking data to the app at all:
    ///
    /// - found=false: the XR Hands package never created a subsystem at all
    ///   (very unlikely given the OpenXR "Hand Tracking Subsystem" feature is
    ///   enabled for Android - this would point at a build/package problem).
    /// - found=true, running=false: the subsystem exists but the OpenXR
    ///   runtime hasn't started it - often the real headset/OS is not
    ///   actually handing hand data to this app (system set to controllers,
    ///   or hand tracking off at the OS level), separate from anything in
    ///   this Unity project.
    /// - running=true, tracked=false for both: the subsystem IS live, so the
    ///   runtime IS trying to report hands, but isn't currently seeing any
    ///   (hands out of the headset's camera view, or momentarily lost) -
    ///   different from the case above, and worth re-testing with hands
    ///   held clearly in front of the headset.
    /// - running=true, tracked=true: hand data is flowing correctly. If the
    ///   hand mesh/joystick grab still don't work at that point, the bug is
    ///   genuinely in this project's own scene wiring, not upstream of it.
    /// </summary>
    public class HandSubsystemBootDiagnostics : MonoBehaviour
    {
        [Tooltip("Seconds to wait after Start before logging, so the OpenXR session/subsystem has time to actually spin up first.")]
        public float delaySeconds = 3f;

        static readonly List<XRHandSubsystem> s_SubsystemsReuse = new List<XRHandSubsystem>();

        void Start()
        {
            Invoke(nameof(LogOnce), delaySeconds);
        }

        void LogOnce()
        {
            SubsystemManager.GetSubsystems(s_SubsystemsReuse);

            XRHandSubsystem subsystem = null;
            for (int i = 0; i < s_SubsystemsReuse.Count; i++)
            {
                if (s_SubsystemsReuse[i].running)
                {
                    subsystem = s_SubsystemsReuse[i];
                    break;
                }
            }
            if (subsystem == null && s_SubsystemsReuse.Count > 0)
            {
                subsystem = s_SubsystemsReuse[0]; // exists but not running yet
            }

            bool found = subsystem != null;
            bool running = found && subsystem.running;
            bool leftTracked = running && subsystem.leftHand.isTracked;
            bool rightTracked = running && subsystem.rightHand.isTracked;

            Debug.Log("[Gundam][HandDiag] XR Hand Subsystem found = " + found);
            Debug.Log("[Gundam][HandDiag] XR Hand Subsystem running = " + running);
            Debug.Log("[Gundam][HandDiag] Left tracked = " + leftTracked);
            Debug.Log("[Gundam][HandDiag] Right tracked = " + rightTracked);

            if (!found)
            {
                Debug.LogWarning("[Gundam][HandDiag] No XRHandSubsystem exists at all - the XR Hands package never " +
                    "created one. Check that 'Hand Tracking Subsystem' is enabled under Project Settings > XR " +
                    "Plug-in Management > OpenXR > Android.");
            }
            else if (!running)
            {
                Debug.LogWarning("[Gundam][HandDiag] Subsystem exists but is NOT running - the OpenXR runtime itself " +
                    "hasn't started delivering hand data to this app. This is usually outside this project (the " +
                    "headset/OS not actually handing hands to the app), not a scene or script bug.");
            }
            else if (!leftTracked && !rightTracked)
            {
                Debug.LogWarning("[Gundam][HandDiag] Subsystem is running but neither hand is tracked right now - " +
                    "try holding both hands clearly in front of the headset and re-check.");
            }
        }
    }
}
