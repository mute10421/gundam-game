using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Finger-touch buttons on the WEAPON display (SysCheck_Right) - per request
    /// ("디스플레이는 실제 XR 손가락 터치 방식으로 구현"). Lives on the display's
    /// world-space canvas; each button is a UI rect on that canvas.
    ///
    /// Finger data comes from the EXISTING hand tracking - the XRHandTrackingEvents
    /// already on LeftHandTracker / RightHandTracker (the same source
    /// JoystickFingerButtons reads). Nothing new is added to the XR rig. The index
    /// fingertip is converted to world space through the tracker's parent (the XR
    /// Origin's tracking space), then into this canvas' local space.
    ///
    /// A button is pressed when an index fingertip that was clearly IN FRONT of
    /// the screen (pilot side) comes within touchDepth of the surface inside the
    /// button's rectangle - so reaching behind/around the display never triggers
    /// it. Feedback: the button lights up while a finger hovers over it, flashes
    /// and sinks slightly on press, and the selected weapon's button stays
    /// highlighted. Pressing calls WeaponModeController.SelectWeapon.
    /// </summary>
    public class WeaponTouchPanel : MonoBehaviour
    {
        [Serializable]
        public class TouchButton
        {
            public WeaponModeController.Mode mode;
            public RectTransform rect;
            public Image background;
            public Text label;
        }

        public WeaponModeController weapons;
        public HandJointTracker leftHand;
        public HandJointTracker rightHand;
        public TouchButton[] buttons;
        [Tooltip("Optional line showing the current weapon.")]
        public Text currentText;

        [Header("Touch (metres)")]
        [Tooltip("Fingertip within this distance of the screen surface counts as touching.")]
        public float touchDepth = 0.01f;
        [Tooltip("Fingertip must first be at least this far in front of the screen to arm a press.")]
        public float armDistance = 0.02f;
        [Tooltip("How far behind the surface a fingertip still counts (pushing into the glass).")]
        public float behindTolerance = 0.03f;
        [Tooltip("Hover highlight distance in front of the screen.")]
        public float hoverDistance = 0.05f;
        public float pressCooldown = 0.4f;
        [Tooltip("Extra touch area (m) around each button's visible edge - keeps the buttons easy to hit now that the display cluster is half size.")]
        public float hitMargin = 0.0015f;

        [Header("Colors")]
        public Color normalColor = new Color(0.08f, 0.12f, 0.2f, 0.95f);
        public Color hoverColor = new Color(0.15f, 0.3f, 0.5f, 0.95f);
        public Color selectedColor = new Color(0.1f, 0.55f, 0.35f, 0.95f);
        public Color pressFlashColor = new Color(0.9f, 1f, 0.9f, 1f);

        class Finger
        {
            public HandJointTracker hand;
            public XRHandTrackingEvents events;
            public Vector3 tipWorld;
            public bool valid;
            public bool armed;      // was clearly in front of the screen since last press
            public UnityEngine.Events.UnityAction<XRHandJointsUpdatedEventArgs> listener;
        }

        Finger[] _fingers;
        float _lastPressTime = -99f;
        int _flashButton = -1;
        float _flashUntil;
        Vector3[] _restLocal;
        float _debugFront = float.MaxValue;
        string _debugHand = "";

        [Tooltip("Show the closest fingertip's distance to the screen on the SELECT line (for tuning touch).")]
        public bool showTouchDebug = true;

        void Awake()
        {
            _fingers = new[] { new Finger { hand = leftHand }, new Finger { hand = rightHand } };
            if (buttons != null)
            {
                _restLocal = new Vector3[buttons.Length];
                for (int i = 0; i < buttons.Length; i++) if (buttons[i].rect != null) _restLocal[i] = buttons[i].rect.localPosition;
            }
        }

        void OnEnable()
        {
            if (_fingers == null) Awake();
            foreach (Finger f in _fingers)
            {
                if (f.hand == null) continue;
                f.events = f.hand.GetComponent<XRHandTrackingEvents>();
                if (f.events == null) continue;
                Finger captured = f;
                f.listener = args => OnJoints(captured, args);
                f.events.jointsUpdated.AddListener(f.listener);
            }
        }

        void OnDisable()
        {
            // Remove ONLY our own listener - the same events also feed
            // HandJointTracker and JoystickFingerButtons.
            foreach (Finger f in _fingers)
            {
                if (f.events != null && f.listener != null) f.events.jointsUpdated.RemoveListener(f.listener);
                f.listener = null;
            }
        }

        void OnJoints(Finger f, XRHandJointsUpdatedEventArgs args)
        {
            if (args.hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tip))
            {
                // Joint poses are in the XR Origin's tracking space (Camera Offset) -
                // NOT the hand tracker's parent (the XR Origin root, 1.36 m lower in
                // this rig). See XRHandSpace.
                Transform space = XRHandSpace.TrackingSpace(f.hand);
                f.tipWorld = space != null ? space.TransformPoint(tip.position) : tip.position;
                f.valid = true;
            }
            else f.valid = false;
        }

        void Update()
        {
            if (buttons == null) return;
            float scale = Mathf.Max(1e-6f, transform.lossyScale.z);
            int hover = -1;
            _debugFront = float.MaxValue;
            _debugHand = "";

            foreach (Finger f in _fingers)
            {
                if (f == null || f.hand == null || !f.valid || !f.hand.IsTracked) { if (f != null) f.armed = false; continue; }

                Vector3 local = transform.InverseTransformPoint(f.tipWorld);
                // Canvas faces the pilot along its -Z: negative z = in front of the glass.
                float front = -local.z * scale; // metres in front of the surface

                if (front > armDistance) f.armed = true;

                // Debug readout: closest fingertip over the screen (see currentText).
                if (Mathf.Abs(local.x) <= 190f && Mathf.Abs(local.y) <= 220f && front < 0.1f && front < _debugFront)
                {
                    _debugFront = front;
                    _debugHand = f == _fingers[0] ? "L" : "R";
                }

                for (int i = 0; i < buttons.Length; i++)
                {
                    TouchButton b = buttons[i];
                    if (b.rect == null || !Inside(b.rect, local, hitMargin / scale)) continue;
                    if (front < hoverDistance && front > -behindTolerance) hover = i;
                    if (f.armed && front <= touchDepth && front > -behindTolerance && Time.time - _lastPressTime > pressCooldown)
                    {
                        f.armed = false;
                        _lastPressTime = Time.time;
                        _flashButton = i;
                        _flashUntil = Time.time + 0.18f;
                        if (weapons != null) weapons.SelectWeapon(b.mode);
                    }
                }
            }

            UpdateVisuals(hover);
        }

        static bool Inside(RectTransform r, Vector3 canvasLocal, float margin)
        {
            // Buttons are direct children of the canvas, so canvas-local == their parent-local.
            Vector3 p = canvasLocal;
            Vector2 c = r.localPosition;
            Vector2 half = r.rect.size * 0.5f + new Vector2(margin, margin);
            return Mathf.Abs(p.x - c.x) <= half.x && Mathf.Abs(p.y - c.y) <= half.y;
        }

        void UpdateVisuals(int hover)
        {
            WeaponModeController.Mode cur = weapons != null ? weapons.CurrentMode : WeaponModeController.Mode.BeamRifle;
            for (int i = 0; i < buttons.Length; i++)
            {
                TouchButton b = buttons[i];
                if (b.background == null) continue;
                bool flashing = i == _flashButton && Time.time < _flashUntil;
                bool selected = b.mode == cur;
                bool pending = weapons != null && weapons.PendingMode.HasValue && weapons.PendingMode.Value == b.mode;
                Color c = selected ? selectedColor : (pending || i == hover ? hoverColor : normalColor);
                if (flashing) c = pressFlashColor;
                b.background.color = c;
                if (b.label != null) b.label.color = flashing ? Color.black : (selected ? Color.white : new Color(0.75f, 0.85f, 1f));
                if (b.rect != null && _restLocal != null)
                {
                    // Sink 3 mm into the glass while flashing (canvas units = m / scale).
                    float sink = flashing ? 0.003f / Mathf.Max(1e-6f, transform.lossyScale.z) : 0f;
                    b.rect.localPosition = _restLocal[i] + new Vector3(0f, 0f, sink);
                }
            }
            if (currentText != null)
            {
                string pend = weapons != null && weapons.PendingMode.HasValue ? "  (RELEASE R-STICK)" : "";
                string dbg = showTouchDebug && _debugFront < 0.1f ? $"  [{_debugHand} {Mathf.RoundToInt(_debugFront * 1000f)}mm]" : "";
                currentText.text = "SELECT: " + WeaponModeController.DisplayName(cur) + pend + dbg;
            }
        }
    }
}
