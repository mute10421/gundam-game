using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// MOBILE SUIT selection at game start - per "게임을 시작하고 자쿠로 할지 건담으로
    /// 할지 고르게 하는거야". Shown right after calibration (before the SORTIE POINT
    /// panel, which waits for this one): two big touch buttons, GUNDAM and ZAKU II,
    /// pressed with a fingertip exactly like the WEAPON / SORTIE screens. Movement
    /// is paused until a suit is picked; the choice is handed to PilotMechSwitcher.
    /// </summary>
    public class MechSelectPanel : MonoBehaviour
    {
        [Serializable]
        public class TouchButton
        {
            public PilotMechSwitcher.Mech mech;
            public RectTransform rect;
            public Image background;
            public Text label;
        }

        public PilotMechSwitcher switcher;
        [Tooltip("Waits for calibration to finish first (optional).")]
        public CalibrationManager calibration;
        [Tooltip("Paused (enabled = false) until a suit is chosen.")]
        public Behaviour[] pauseUntilChosen;
        public HandJointTracker leftHand;
        public HandJointTracker rightHand;
        public TouchButton[] buttons;
        public GameObject panelRoot;

        [Header("Touch (metres)")]
        public float touchDepth = 0.012f;
        public float armDistance = 0.02f;
        public float behindTolerance = 0.03f;
        public float hoverDistance = 0.05f;
        public float hitMargin = 0.004f;

        public Color normalColor = new Color(0.08f, 0.12f, 0.2f, 0.95f);
        public Color hoverColor = new Color(0.15f, 0.3f, 0.5f, 0.95f);
        public Color pressFlashColor = new Color(0.9f, 1f, 0.9f, 1f);

        public bool Chosen { get; private set; }

        class Finger
        {
            public HandJointTracker hand;
            public XRHandTrackingEvents events;
            public Vector3 tipWorld;
            public bool valid;
            public bool armed;
            public UnityEngine.Events.UnityAction<XRHandJointsUpdatedEventArgs> listener;
        }

        Finger[] _fingers;
        bool _shown;
        bool _paused;
        int _flash = -1;
        float _flashUntil;
        float _applyAt = -1f;
        PilotMechSwitcher.Mech _pending;

        void Awake()
        {
            _fingers = new[] { new Finger { hand = leftHand }, new Finger { hand = rightHand } };
            if (panelRoot != null) panelRoot.SetActive(false);
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
            foreach (Finger f in _fingers)
            {
                if (f.events != null && f.listener != null) f.events.jointsUpdated.RemoveListener(f.listener);
                f.listener = null;
            }
            SetPaused(false);
        }

        void OnJoints(Finger f, XRHandJointsUpdatedEventArgs args)
        {
            if (args.hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tip))
            {
                Transform space = XRHandSpace.TrackingSpace(f.hand);
                f.tipWorld = space != null ? space.TransformPoint(tip.position) : tip.position;
                f.valid = true;
            }
            else f.valid = false;
        }

        void Update()
        {
            if (Chosen) return;
            if (!_shown)
            {
                if (calibration != null && !calibration.IsCalibrationComplete) return;
                _shown = true;
                if (panelRoot != null) panelRoot.SetActive(true);
                SetPaused(true);
            }

            if (_applyAt > 0f)
            {
                UpdateVisuals(-1);
                if (Time.time >= _applyAt) Choose(_pending);
                return;
            }

            int hover = -1;
            Transform canvas = panelRoot != null ? panelRoot.transform : transform;
            float scale = Mathf.Max(1e-6f, canvas.lossyScale.z);
            foreach (Finger f in _fingers)
            {
                if (f == null || f.hand == null || !f.valid || !f.hand.IsTracked) { if (f != null) f.armed = false; continue; }
                Vector3 local = canvas.InverseTransformPoint(f.tipWorld);
                float front = -local.z * scale;
                if (front > armDistance) f.armed = true;
                for (int i = 0; i < buttons.Length; i++)
                {
                    TouchButton b = buttons[i];
                    if (b.rect == null || !Inside(b.rect, local, hitMargin / scale)) continue;
                    if (front < hoverDistance && front > -behindTolerance) hover = i;
                    if (f.armed && front <= touchDepth && front > -behindTolerance)
                    {
                        f.armed = false;
                        _flash = i;
                        _flashUntil = Time.time + 0.25f;
                        _pending = b.mech;
                        _applyAt = Time.time + 0.25f;
                    }
                }
            }
            UpdateVisuals(hover);
        }

        static bool Inside(RectTransform r, Vector3 canvasLocal, float margin)
        {
            Vector2 c = r.localPosition;
            Vector2 half = r.rect.size * 0.5f + new Vector2(margin, margin);
            return Mathf.Abs(canvasLocal.x - c.x) <= half.x && Mathf.Abs(canvasLocal.y - c.y) <= half.y;
        }

        void UpdateVisuals(int hover)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                TouchButton b = buttons[i];
                if (b.background == null) continue;
                bool flashing = i == _flash && Time.time < _flashUntil;
                b.background.color = flashing ? pressFlashColor : (i == hover ? hoverColor : normalColor);
                if (b.label != null) b.label.color = flashing ? Color.black : new Color(0.8f, 0.9f, 1f);
            }
        }

        /// <summary>Picks the suit (also callable from code / the Inspector for testing).</summary>
        public void Choose(PilotMechSwitcher.Mech mech)
        {
            _applyAt = -1f;
            if (switcher != null) switcher.Apply(mech);
            Chosen = true;
            if (panelRoot != null) panelRoot.SetActive(false);
            SetPaused(false);
        }

        void SetPaused(bool pause)
        {
            if (pause == _paused || pauseUntilChosen == null) return;
            foreach (Behaviour b in pauseUntilChosen) if (b != null) b.enabled = !pause;
            _paused = pause;
        }
    }
}
