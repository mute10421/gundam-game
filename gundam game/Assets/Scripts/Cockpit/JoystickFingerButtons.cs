using UnityEngine;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The 5 finger buttons on a joystick's grip ball (Thumb/Index/Middle/Ring/Pinky),
    /// per request:
    ///
    ///   "지금 조종기에 있는 버튼 5개를 손가락 끝마디 위치로 정확히 옮겨줘 그리고
    ///    엄지 부분에 버튼이 눌렸을때 발칸이 나가게하자"
    ///
    /// 1) Placement - "exactly at each fingertip segment": fixed guessed positions can
    ///    never match a real hand, so this uses the pilot's REAL tracked hand. A short
    ///    moment after the stick is grabbed (settleTime, so the fist has closed), each
    ///    finger's last segment (끝마디 - the pad between its Distal joint and Tip) is
    ///    projected onto the grip ball's surface and that finger's button is moved
    ///    there. The buttons then stay put (they're part of the stick) until the next
    ///    grab, which re-fits them.
    ///
    /// 2) Pressing - the grip ball is virtual, so there's nothing physical to push
    ///    against. A button counts as PRESSED when its finger's pad moves in toward
    ///    the ball's center by pressDepth beyond where it rested; released again when
    ///    it comes back out past half that. The resting depth slowly re-adapts while
    ///    NOT pressed, so small grip adjustments don't cause phantom presses. Pressed
    ///    buttons swap to pressedMaterial and shrink slightly as visible feedback.
    ///    HeadVulcanController reads ThumbPressed to fire.
    ///
    /// Hand data: reuses the joystick's own existing HandJointTracker's
    /// XRHandTrackingEvents (subscribed at runtime in OnEnable - the only way a
    /// UnityEvent listener survives into Play/the headset build). Joint poses are in
    /// XR Origin space, the same space HandJointTracker converts from (its parent).
    /// Does not touch JoystickLever's grab/tilt logic, HandJointTracker, or the
    /// handle's own transform - only the button GameObjects' localPosition/scale/
    /// material.
    /// </summary>
    public class JoystickFingerButtons : MonoBehaviour
    {
        public enum Finger { Thumb = 0, Index = 1, Middle = 2, Ring = 3, Pinky = 4 }

        [Header("References (wired by GundamCockpitSetup)")]
        public JoystickLever lever;
        [Tooltip("This joystick's own hand (Left for LeftJoystick, Right for RightJoystick).")]
        public HandJointTracker hand;
        [Tooltip("The grip ball's transform (JoystickLever's handle). The ball is centered on its origin.")]
        public Transform handle;
        [Tooltip("Buttons in order: Thumb, Index, Middle, Ring, Pinky.")]
        public Transform[] buttons = new Transform[5];
        [Tooltip("Shown on a button while it's pressed.")]
        public Material pressedMaterial;

        [Header("Fit")]
        [Tooltip("Grip ball radius (m). The GripBall sphere is 0.10 across.")]
        public float gripRadius = 0.05f;
        [Tooltip("Seconds after grabbing before the buttons are fitted to the fingertips (lets the fist finish closing).")]
        public float settleTime = 0.3f;

        [Header("Press")]
        [Tooltip("How far (m) a fingertip pad must move in toward the ball center past its resting spot to press its button.")]
        public float pressDepth = 0.015f;
        [Tooltip("Seconds for the resting depth to re-adapt while not pressed (bigger = steadier).")]
        public float restAdaptSeconds = 2f;

        public bool ThumbPressed => IsPressed(Finger.Thumb);
        public bool IsPressed(Finger f) => _pressed[(int)f];
        /// <summary>How far (m) each finger is currently pushed in past its rest (0 if not). For HUD/tuning.</summary>
        public float PressAmount(Finger f) => _pressAmount[(int)f];
        public bool ButtonsFitted => _fitted;

        static readonly XRHandJointID[] DistalIds =
        {
            XRHandJointID.ThumbDistal, XRHandJointID.IndexDistal, XRHandJointID.MiddleDistal,
            XRHandJointID.RingDistal, XRHandJointID.LittleDistal,
        };
        static readonly XRHandJointID[] TipIds =
        {
            XRHandJointID.ThumbTip, XRHandJointID.IndexTip, XRHandJointID.MiddleTip,
            XRHandJointID.RingTip, XRHandJointID.LittleTip,
        };

        readonly Vector3[] _padWorld = new Vector3[5];
        readonly bool[] _padValid = new bool[5];
        readonly float[] _restDepth = new float[5];
        readonly bool[] _pressed = new bool[5];
        readonly float[] _pressAmount = new float[5];
        Renderer[] _renderers;
        Material[] _normalMaterials;
        Vector3[] _normalScales;

        XRHandTrackingEvents _events;
        bool _subscribed;
        bool _wasGrabbed;
        float _grabTime;
        bool _fitted;

        void Awake()
        {
            int n = buttons != null ? buttons.Length : 0;
            _renderers = new Renderer[n];
            _normalMaterials = new Material[n];
            _normalScales = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (buttons[i] == null) continue;
                _renderers[i] = buttons[i].GetComponent<Renderer>();
                _normalMaterials[i] = _renderers[i] != null ? _renderers[i].sharedMaterial : null;
                _normalScales[i] = buttons[i].localScale;
            }
        }

        void OnEnable()
        {
            if (!Application.isPlaying || hand == null) return;
            _events = hand.GetComponent<XRHandTrackingEvents>();
            if (_events != null && !_subscribed)
            {
                _events.jointsUpdated.AddListener(OnJointsUpdated);
                _subscribed = true;
            }
        }

        void OnDisable()
        {
            if (_subscribed && _events != null) _events.jointsUpdated.RemoveListener(OnJointsUpdated);
            _subscribed = false;
            ClearPresses();
        }

        void OnJointsUpdated(XRHandJointsUpdatedEventArgs args)
        {
            // Same space conversion HandJointTracker relies on: joint poses are
            // local to the XR Origin, which is HandJointTracker's parent.
            Transform space = hand != null ? hand.transform.parent : null;
            for (int i = 0; i < 5; i++)
            {
                bool gotTip = args.hand.GetJoint(TipIds[i]).TryGetPose(out Pose tip);
                bool gotDistal = args.hand.GetJoint(DistalIds[i]).TryGetPose(out Pose distal);
                if (!gotTip) { _padValid[i] = false; continue; }
                // The pad of the last segment (끝마디): midway between the distal
                // joint and the tip; just the tip if the distal joint is missing.
                Vector3 local = gotDistal ? (tip.position + distal.position) * 0.5f : tip.position;
                _padWorld[i] = space != null ? space.TransformPoint(local) : local;
                _padValid[i] = true;
            }
        }

        void LateUpdate()
        {
            if (lever == null || handle == null || buttons == null) return;

            bool grabbed = lever.isGrabbed;
            if (grabbed && !_wasGrabbed)
            {
                _grabTime = Time.time;
                _fitted = false;
            }
            _wasGrabbed = grabbed;

            if (!grabbed)
            {
                ClearPresses();
                return;
            }

            if (!_fitted && Time.time - _grabTime >= settleTime)
            {
                FitButtonsToFingertips();
            }
            if (!_fitted) return;

            float adapt = restAdaptSeconds > 0.01f ? 1f - Mathf.Exp(-Time.deltaTime / restAdaptSeconds) : 1f;
            for (int i = 0; i < buttons.Length && i < 5; i++)
            {
                if (buttons[i] == null || !_padValid[i]) continue;
                float depth = handle.InverseTransformPoint(_padWorld[i]).magnitude;
                float pushed = _restDepth[i] - depth;
                _pressAmount[i] = Mathf.Max(0f, pushed);

                if (!_pressed[i] && pushed >= pressDepth) SetPressed(i, true);
                else if (_pressed[i] && pushed < pressDepth * 0.5f) SetPressed(i, false);

                if (!_pressed[i]) _restDepth[i] = Mathf.Lerp(_restDepth[i], depth, adapt);
            }
        }

        void FitButtonsToFingertips()
        {
            bool any = false;
            for (int i = 0; i < buttons.Length && i < 5; i++)
            {
                if (buttons[i] == null || !_padValid[i]) continue;
                Vector3 local = handle.InverseTransformPoint(_padWorld[i]);
                if (local.sqrMagnitude < 1e-6f) continue;
                // Sit the button on the ball's surface right under this finger's
                // last segment (the button sphere's own center on the surface, so
                // half of it pokes out toward the finger).
                buttons[i].localPosition = local.normalized * gripRadius;
                _restDepth[i] = local.magnitude;
                any = true;
            }
            _fitted = any;
        }

        void SetPressed(int i, bool pressed)
        {
            _pressed[i] = pressed;
            if (buttons[i] == null) return;
            buttons[i].localScale = pressed ? _normalScales[i] * 0.8f : _normalScales[i];
            if (_renderers[i] != null)
            {
                _renderers[i].sharedMaterial = pressed && pressedMaterial != null ? pressedMaterial : _normalMaterials[i];
            }
        }

        void ClearPresses()
        {
            for (int i = 0; i < 5; i++)
            {
                if (_pressed[i] && buttons != null && i < buttons.Length) SetPressed(i, false);
                _pressed[i] = false;
                _pressAmount[i] = 0f;
            }
        }
    }
}
