using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The 5 finger buttons on a joystick's grip ball - one per finger (Thumb/Index/
    /// Middle/Ring/Pinky), on BOTH joysticks, per requests:
    ///
    ///   "버튼 5개를 손가락 끝마디 위치로 정확히 옮겨줘 그리고 엄지 부분에 버튼이
    ///    눌렸을때 발칸이 나가게하자"
    ///   "구체 조종기 유지해 구체 조종기를 잡았을때 손가락 끝마디에 버튼이 있는거라고"
    ///   "엄지로 나가는거 확인됬는데 인식이 안좋은거 같아"
    ///   "조종장치에 손가락 별로 버튼이 다 있어야해 그래야 나중에 다른기능을 추가함"
    ///
    /// PLACEMENT: the controller is the sphere you grip with a fist; each button sits
    /// on the ball's surface where that finger's last segment (끝마디) lands. A short
    /// moment after the ball is grabbed (settleTime) each finger's pad (between its
    /// Distal joint and Tip) is projected onto the ball and its button moved there.
    ///
    /// PRESS DETECTION (reworked for "인식이 안좋은거 같아"). The first version
    /// measured each fingertip's distance to the BALL center. That was unreliable:
    ///   - the ball only follows the hand sideways (X/Z) and stops at its travel
    ///     limit, so moving/lifting the whole hand changed that distance by itself;
    ///   - a 15 mm push was a lot for a thumb squeezed inside a fist;
    ///   - raw joint positions jitter, and one frame of noise flipped the state;
    ///   - if a finger wasn't tracked at the fitting moment, it never got a resting
    ///     reference and could not be pressed for that whole grab.
    /// Now everything is measured RELATIVE TO THE HAND ITSELF, from the same joint
    /// sample, so moving the stick or the whole hand no longer matters:
    ///   a) push: the pad moving closer to the palm center than it rested
    ///      (1 point per pressDepth), and
    ///   b) curl: the finger's own joints folding more than they rested
    ///      (1 point per pressCurlDegrees) - pressing a button on a ball you're
    ///      gripping is mostly a fingertip flex.
    /// The points are added; the button presses once the total reaches pressScore
    /// and has stayed there for pressHoldSeconds, and never while either signal
    /// shows the finger straightening (see the "엄지를 펴도 발칸이 나갈때가 있어"
    /// tuning note on the fields). Releases below releaseScore. Both signals are
    /// low-pass filtered, with one-sample tracking jumps ignored. Each finger gets its
    /// resting reference on its own first tracked frame after the grab settles. The
    /// rest slowly re-adapts only while the finger is clearly released, so a held
    /// half-press can't creep into "rest".
    ///
    /// EXTENSIBLE: every finger has its own onPressed/onReleased UnityEvents (wire new
    /// functions in the Inspector) plus the ButtonChanged C# event and IsPressed(...),
    /// so later features can hook any finger on either stick without touching this
    /// class. HeadVulcanController uses the RightJoystick's ThumbPressed.
    ///
    /// Hand data: the joystick's own existing HandJointTracker's XRHandTrackingEvents,
    /// subscribed at runtime in OnEnable (the only way a UnityEvent listener survives
    /// into Play/the headset build). Joint poses are in XR Origin space
    /// (HandJointTracker's parent). Never touches JoystickLever's grab/tilt logic,
    /// HandJointTracker, or the handle's own transform - only the buttons.
    /// </summary>
    public class JoystickFingerButtons : MonoBehaviour
    {
        public enum Finger { Thumb = 0, Index = 1, Middle = 2, Ring = 3, Pinky = 4 }

        [System.Serializable]
        public class FingerButtonEvents
        {
            public UnityEvent onPressed = new UnityEvent();
            public UnityEvent onReleased = new UnityEvent();
        }

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
        [Tooltip("Move the buttons under the real tracked fingertips each time the ball is grabbed. Off = keep the default layout.")]
        public bool refitOnGrab = true;
        [Tooltip("Seconds after grabbing before resting references are taken (lets the fist finish closing).")]
        public float settleTime = 0.3f;

        // TUNED per report ("엄지를 펴도 발칸이 나갈때가 있어 약간 감도 수정이
        // 필요함" - false presses with the thumb straightened). Previously EITHER
        // signal alone (10 mm OR 18 deg) pressed, so one noisy joint - common for a
        // thumb half-hidden inside a fist - fired the vulcan by itself. Now:
        //   - both signals are combined into one score (push/pressDepth +
        //     curl/pressCurlDegrees) that must reach pressScore, and NEITHER may be
        //     pointing the "extended" way (a straightening or backing-off finger can
        //     never press, however noisy the other signal is);
        //   - the condition must hold for pressHoldSeconds before it counts;
        //   - single-sample tracking jumps (glitchDistance / glitchDegrees) are
        //     ignored instead of being fed into the filter;
        //   - defaults are a little stiffer (12 mm / 22 deg), and 'sensitivity'
        //     scales everything at once for quick tuning.
        [Header("Press detection (hand-relative)")]
        [Tooltip("Overall sensitivity. 1 = default, lower = needs a firmer press (fewer accidental presses), higher = lighter press.")]
        [Range(0.3f, 2f)] public float sensitivity = 1f;
        [Tooltip("Reference push (m): pad moving this much closer to the palm than it rested counts as 1 point.")]
        public float pressDepth = 0.012f;
        [Tooltip("Reference fold (deg): the finger's joints folding this much more than they rested counts as 1 point.")]
        public float pressCurlDegrees = 22f;
        [Tooltip("Points needed to press (push points + fold points). 1.6 = e.g. a full fold plus a bit of push, or a clear push plus a bit of fold.")]
        public float pressScore = 1.6f;
        [Tooltip("Points below which a pressed button releases.")]
        public float releaseScore = 0.6f;
        [Tooltip("The press condition must hold this long (s) before it counts - filters out momentary tracking spikes.")]
        public float pressHoldSeconds = 0.06f;
        [Tooltip("A single sample jumping more than this (m) from the filtered pad distance is treated as a tracking glitch and ignored.")]
        public float glitchDistance = 0.03f;
        [Tooltip("A single sample jumping more than this (deg) from the filtered fold is treated as a tracking glitch and ignored.")]
        public float glitchDegrees = 40f;
        [Tooltip("Low-pass filter time (s) on the tracked values - removes joint jitter.")]
        public float smoothingSeconds = 0.05f;
        [Tooltip("Seconds for the resting reference to re-adapt while the finger is clearly released (bigger = steadier).")]
        public float restAdaptSeconds = 2f;

        [Header("Per-finger events (hook future functions here)")]
        public FingerButtonEvents thumbEvents = new FingerButtonEvents();
        public FingerButtonEvents indexEvents = new FingerButtonEvents();
        public FingerButtonEvents middleEvents = new FingerButtonEvents();
        public FingerButtonEvents ringEvents = new FingerButtonEvents();
        public FingerButtonEvents pinkyEvents = new FingerButtonEvents();

        /// <summary>Raised whenever any finger's button changes (finger, pressed).</summary>
        public event System.Action<Finger, bool> ButtonChanged;

        public bool ThumbPressed => IsPressed(Finger.Thumb);
        public bool IsPressed(Finger f) => _pressed[(int)f];
        /// <summary>How far (m) the finger pad is currently pushed toward the palm past its rest (0 if not). For HUD/tuning.</summary>
        public float PressAmount(Finger f) => Mathf.Max(0f, _pushNow[(int)f]);
        /// <summary>How many degrees the finger is currently folded past its rest (0 if not). For HUD/tuning.</summary>
        public float CurlAmount(Finger f) => Mathf.Max(0f, _curlPushNow[(int)f]);
        /// <summary>Current press score (push points + fold points); presses at pressScore. For HUD/tuning.</summary>
        public float PressScore(Finger f) => _scoreNow[(int)f];
        /// <summary>True once this grab has settled and at least one finger has a resting reference.</summary>
        public bool ButtonsFitted => _anyFitted;

        // Joint chains, index 0..4 = Thumb, Index, Middle, Ring, Little.
        static readonly XRHandJointID[,] Chain =
        {
            { XRHandJointID.ThumbMetacarpal,  XRHandJointID.ThumbProximal,       XRHandJointID.ThumbDistal,  XRHandJointID.ThumbTip },
            { XRHandJointID.IndexProximal,    XRHandJointID.IndexIntermediate,   XRHandJointID.IndexDistal,  XRHandJointID.IndexTip },
            { XRHandJointID.MiddleProximal,   XRHandJointID.MiddleIntermediate,  XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
            { XRHandJointID.RingProximal,     XRHandJointID.RingIntermediate,    XRHandJointID.RingDistal,   XRHandJointID.RingTip },
            { XRHandJointID.LittleProximal,   XRHandJointID.LittleIntermediate,  XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
        };

        // Latest raw sample (written by the joints callback).
        readonly Vector3[] _padWorld = new Vector3[5];
        readonly bool[] _padValid = new bool[5];
        readonly float[] _rawPalmDist = new float[5];
        readonly float[] _rawCurl = new float[5];
        readonly bool[] _curlValid = new bool[5];

        // Filtered values, rest references and state.
        readonly float[] _palmDist = new float[5];
        readonly float[] _curl = new float[5];
        readonly bool[] _filterPrimed = new bool[5];
        readonly bool[] _fitted = new bool[5];
        readonly float[] _restPalmDist = new float[5];
        readonly float[] _restCurl = new float[5];
        readonly float[] _pushNow = new float[5];
        readonly float[] _curlPushNow = new float[5];
        readonly bool[] _pressed = new bool[5];
        readonly float[] _downTime = new float[5];
        readonly int[] _glitchFrames = new int[5];
        readonly float[] _scoreNow = new float[5];

        Renderer[] _renderers;
        Material[] _normalMaterials;
        Vector3[] _normalScales;

        XRHandTrackingEvents _events;
        bool _subscribed;
        bool _wasGrabbed;
        float _grabTime;
        bool _anyFitted;

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
            ReleaseAll();
        }

        void OnJointsUpdated(XRHandJointsUpdatedEventArgs args)
        {
            Transform space = hand != null ? hand.transform.parent : null;
            bool gotPalm = args.hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palm);

            for (int i = 0; i < 5; i++)
            {
                bool g0 = args.hand.GetJoint(Chain[i, 0]).TryGetPose(out Pose p0);
                bool g1 = args.hand.GetJoint(Chain[i, 1]).TryGetPose(out Pose p1);
                bool g2 = args.hand.GetJoint(Chain[i, 2]).TryGetPose(out Pose p2);
                bool g3 = args.hand.GetJoint(Chain[i, 3]).TryGetPose(out Pose p3);

                if (!g3) { _padValid[i] = false; _curlValid[i] = false; continue; }

                // Pad of the last segment (끝마디): between distal joint and tip.
                Vector3 padLocal = g2 ? (p2.position + p3.position) * 0.5f : p3.position;
                _padWorld[i] = space != null ? space.TransformPoint(padLocal) : padLocal;
                _padValid[i] = gotPalm;
                // Hand-relative: distance in the joints' own space (same sample).
                if (gotPalm) _rawPalmDist[i] = Vector3.Distance(padLocal, palm.position);

                // Hand-relative curl: how much the last two joints are folded.
                if (g0 && g1 && g2)
                {
                    Vector3 s1 = p1.position - p0.position;
                    Vector3 s2 = p2.position - p1.position;
                    Vector3 s3 = p3.position - p2.position;
                    _rawCurl[i] = Vector3.Angle(s1, s2) + Vector3.Angle(s2, s3);
                    _curlValid[i] = true;
                }
                else
                {
                    _curlValid[i] = false;
                }
            }
        }

        void LateUpdate()
        {
            if (lever == null || handle == null || buttons == null) return;

            bool grabbed = lever.isGrabbed;
            if (grabbed && !_wasGrabbed)
            {
                _grabTime = Time.time;
                _anyFitted = false;
                for (int i = 0; i < 5; i++) { _fitted[i] = false; _filterPrimed[i] = false; }
            }
            _wasGrabbed = grabbed;

            if (!grabbed)
            {
                ReleaseAll();
                return;
            }

            float k = smoothingSeconds > 0.001f ? 1f - Mathf.Exp(-Time.deltaTime / smoothingSeconds) : 1f;
            float adapt = restAdaptSeconds > 0.01f ? 1f - Mathf.Exp(-Time.deltaTime / restAdaptSeconds) : 1f;
            bool settled = Time.time - _grabTime >= settleTime;

            for (int i = 0; i < 5 && i < buttons.Length; i++)
            {
                // Tracking momentarily lost for this finger: hold its current
                // state (don't drop a held press, don't invent a new one).
                if (!_padValid[i]) continue;

                if (!_filterPrimed[i])
                {
                    _palmDist[i] = _rawPalmDist[i];
                    _curl[i] = _rawCurl[i];
                    _filterPrimed[i] = true;
                }
                else
                {
                    // Glitch rejection: a one-sample leap (typical when a joint
                    // hidden inside the fist gets re-estimated) is skipped, not
                    // filtered in - but a sustained change still gets through,
                    // since the filtered value keeps advancing toward real motion.
                    bool distGlitch = Mathf.Abs(_rawPalmDist[i] - _palmDist[i]) > glitchDistance;
                    bool curlGlitch = _curlValid[i] && Mathf.Abs(_rawCurl[i] - _curl[i]) > glitchDegrees;
                    if (distGlitch || curlGlitch)
                    {
                        _glitchFrames[i]++;
                        if (_glitchFrames[i] < 3) continue; // hold state through short spikes
                    }
                    _glitchFrames[i] = 0;
                    _palmDist[i] = Mathf.Lerp(_palmDist[i], _rawPalmDist[i], k);
                    if (_curlValid[i]) _curl[i] = Mathf.Lerp(_curl[i], _rawCurl[i], k);
                }

                if (!settled) continue;

                if (!_fitted[i])
                {
                    // This finger's own first tracked frame after settling.
                    if (refitOnGrab && buttons[i] != null)
                    {
                        Vector3 local = handle.InverseTransformPoint(_padWorld[i]);
                        if (local.sqrMagnitude > 1e-6f) buttons[i].localPosition = local.normalized * gripRadius;
                    }
                    _restPalmDist[i] = _palmDist[i];
                    _restCurl[i] = _curl[i];
                    _fitted[i] = true;
                    _anyFitted = true;
                    continue;
                }

                float push = _restPalmDist[i] - _palmDist[i];
                float curlPush = _curlValid[i] ? _curl[i] - _restCurl[i] : 0f;
                _pushNow[i] = push;
                _curlPushNow[i] = curlPush;

                float s = Mathf.Max(0.05f, sensitivity);
                float pushPts = push / Mathf.Max(1e-4f, pressDepth / s);
                float curlPts = curlPush / Mathf.Max(0.1f, pressCurlDegrees / s);
                float score = Mathf.Max(0f, pushPts) + Mathf.Max(0f, curlPts);
                _scoreNow[i] = score;

                // Extension veto: if either signal says the finger is straightening
                // or backing away from the palm (beyond a small noise margin), it
                // can't be pressing - "엄지를 펴도 발칸이 나갈때가 있어".
                bool extending = pushPts < -0.35f || curlPts < -0.35f;
                bool downNow = !extending && score >= pressScore;

                if (!_pressed[i])
                {
                    _downTime[i] = downNow ? _downTime[i] + Time.deltaTime : 0f;
                    if (_downTime[i] >= pressHoldSeconds) SetPressed(i, true);
                }
                else if (extending || score < releaseScore)
                {
                    SetPressed(i, false);
                    _downTime[i] = 0f;
                }

                // Re-adapt the rest only while clearly released, so a slowly held
                // half-press never becomes the new "rest".
                if (!_pressed[i] && score < releaseScore * 0.5f)
                {
                    _restPalmDist[i] = Mathf.Lerp(_restPalmDist[i], _palmDist[i], adapt);
                    _restCurl[i] = Mathf.Lerp(_restCurl[i], _curl[i], adapt);
                }
            }
        }

        FingerButtonEvents EventsFor(int i)
        {
            switch (i)
            {
                case 0: return thumbEvents;
                case 1: return indexEvents;
                case 2: return middleEvents;
                case 3: return ringEvents;
                default: return pinkyEvents;
            }
        }

        void SetPressed(int i, bool pressed)
        {
            if (_pressed[i] == pressed) return;
            _pressed[i] = pressed;

            if (i < buttons.Length && buttons[i] != null)
            {
                if (i < _normalScales.Length) buttons[i].localScale = pressed ? _normalScales[i] * 0.8f : _normalScales[i];
                if (i < _renderers.Length && _renderers[i] != null)
                {
                    _renderers[i].sharedMaterial = pressed && pressedMaterial != null ? pressedMaterial : _normalMaterials[i];
                }
            }

            FingerButtonEvents ev = EventsFor(i);
            if (ev != null)
            {
                if (pressed) ev.onPressed?.Invoke();
                else ev.onReleased?.Invoke();
            }
            ButtonChanged?.Invoke((Finger)i, pressed);
        }

        void ReleaseAll()
        {
            for (int i = 0; i < 5; i++)
            {
                if (_pressed[i]) SetPressed(i, false);
                _pushNow[i] = 0f;
                _curlPushNow[i] = 0f;
                _scoreNow[i] = 0f;
                _downTime[i] = 0f;
                _glitchFrames[i] = 0;
            }
        }
    }
}
