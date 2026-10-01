using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

namespace Gundam.Cockpit
{
    /// <summary>
    /// SORTIE POINT selection - per "처음에 스폰 위치를 지정해야할듯 어디서
    /// 시작할지 우주 콜로니 외관 콜로니 안 이런식으로" (option "게임 시작 시 선택
    /// 화면"). Once Cockpit Calibration is done (or right away if there is no
    /// calibration), a floating panel in front of the pilot offers four starting
    /// points, touched with an index fingertip exactly like the WEAPON display:
    ///
    ///   SPACE            - where the suit already is (1.5 km in front of the colony)
    ///   COLONY EXTERIOR  - floating in front of the torn breach in the end cap
    ///   COLONY ENTRANCE  - on the ground just inside the breach (plaza)
    ///   DOWNTOWN         - the middle of the main boulevard, near the Zakus
    ///
    /// While the panel is up the suit's movement components are paused (their
    /// 'enabled' flag only - their code is untouched), so nothing drifts before
    /// the sortie. Choosing moves MobileSuitRoot so the Gundam's feet land on the
    /// chosen point (ExternalGundamFollower and everything else follow it as
    /// usual), then hides the panel and gives the controls back. Colony points
    /// are computed from the ColonyStructure at runtime (ground height included)
    /// and hidden if there is no colony.
    /// </summary>
    public class SpawnSelectPanel : MonoBehaviour
    {
        public enum Point { Space = 0, ColonyExterior = 1, ColonyEntrance = 2, Downtown = 3 }

        [Serializable]
        public class TouchButton
        {
            public Point point;
            public RectTransform rect;
            public Image background;
            public Text label;
        }

        [Header("References (wired by GundamCockpitSetup)")]
        [Tooltip("MobileSuitRoot - moved to the chosen point.")]
        public Transform suitRoot;
        [Tooltip("ExternalGundam - its position is the Gundam's feet.")]
        public Transform body;
        public ColonyStructure colony;
        [Tooltip("The panel waits for calibration to finish first (optional).")]
        public CalibrationManager calibration;
        [Tooltip("...and for the mobile suit to be chosen (GUNDAM / ZAKU), if that panel exists.")]
        public MechSelectPanel mechSelect;
        [Tooltip("Paused (enabled = false) until a point is chosen.")]
        public Behaviour[] pauseUntilChosen;
        public HandJointTracker leftHand;
        public HandJointTracker rightHand;
        public TouchButton[] buttons;
        [Tooltip("Root of the panel (hidden once a point is chosen).")]
        public GameObject panelRoot;
        public Text titleText;

        [Header("Points (colony points are relative to the colony)")]
        [Tooltip("In front of the breach: x, height above the breach floor, distance (m) in front of the near cap.")]
        public Vector3 exteriorPoint = new Vector3(0f, 150f, 520f);
        [Tooltip("Inside: x and distance (m) in from the near cap; stands on the ground.")]
        public Vector2 entrancePoint = new Vector2(0f, 260f);
        public Vector2 downtownPoint = new Vector2(0f, 1900f);

        [Header("Touch (metres)")]
        public float touchDepth = 0.012f;
        public float armDistance = 0.02f;
        public float behindTolerance = 0.03f;
        public float hoverDistance = 0.05f;
        public float hitMargin = 0.004f;

        public Color normalColor = new Color(0.08f, 0.12f, 0.2f, 0.95f);
        public Color hoverColor = new Color(0.15f, 0.3f, 0.5f, 0.95f);
        public Color pressFlashColor = new Color(0.9f, 1f, 0.9f, 1f);

        /// <summary>True once a sortie point has been chosen.</summary>
        public bool Chosen { get; private set; }
        public Point ChosenPoint { get; private set; }

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
        Point _pending;

        void Awake()
        {
            _fingers = new[] { new Finger { hand = leftHand }, new Finger { hand = rightHand } };
            if (panelRoot != null) panelRoot.SetActive(false);
            if (colony == null)
            {
                // No colony: only SPACE is offered.
                foreach (TouchButton b in buttons)
                    if (b.point != Point.Space && b.rect != null) b.rect.gameObject.SetActive(false);
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
            // Only our own listener - the same events feed the other hand scripts.
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
                if (mechSelect != null && !mechSelect.Chosen) return;
                _shown = true;
                if (panelRoot != null) panelRoot.SetActive(true);
                SetPaused(true);
            }

            // A short flash on the pressed button, then the jump.
            if (_applyAt > 0f)
            {
                UpdateVisuals(-1);
                if (Time.time >= _applyAt) Apply(_pending);
                return;
            }

            int hover = -1;
            Transform canvas = panelRoot != null ? panelRoot.transform : transform;
            float scale = Mathf.Max(1e-6f, canvas.lossyScale.z);
            foreach (Finger f in _fingers)
            {
                if (f == null || f.hand == null || !f.valid || !f.hand.IsTracked) { if (f != null) f.armed = false; continue; }
                Vector3 local = canvas.InverseTransformPoint(f.tipWorld);
                float front = -local.z * scale; // metres in front of the panel (pilot side)
                if (front > armDistance) f.armed = true;
                for (int i = 0; i < buttons.Length; i++)
                {
                    TouchButton b = buttons[i];
                    if (b.rect == null || !b.rect.gameObject.activeInHierarchy || !Inside(b.rect, local, hitMargin / scale)) continue;
                    if (front < hoverDistance && front > -behindTolerance) hover = i;
                    if (f.armed && front <= touchDepth && front > -behindTolerance)
                    {
                        f.armed = false;
                        _flash = i;
                        _flashUntil = Time.time + 0.25f;
                        _pending = b.point;
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

        /// <summary>Move the suit so the Gundam's feet are on the chosen point.</summary>
        public void Apply(Point p)
        {
            _applyAt = -1f;
            Vector3? feet = PointPosition(p);
            if (feet.HasValue && suitRoot != null)
            {
                Vector3 offset = body != null ? body.position - suitRoot.position : Vector3.zero;
                suitRoot.position = feet.Value - offset;
                if (body != null) body.position = feet.Value; // no one-frame lag before the follower runs
            }
            Chosen = true;
            ChosenPoint = p;
            if (panelRoot != null) panelRoot.SetActive(false);
            SetPaused(false);
            Debug.Log("[Gundam] Sortie point: " + p + (feet.HasValue ? " at " + feet.Value : " (stays where it is)"));
        }

        /// <summary>World position of a point (feet), or null = stay where the suit is.</summary>
        public Vector3? PointPosition(Point p)
        {
            if (p == Point.Space || colony == null) return null;
            Vector3 c = colony.nearCapCenter;
            switch (p)
            {
                case Point.ColonyExterior:
                    return new Vector3(c.x + exteriorPoint.x, colony.GroundY(c.x) + exteriorPoint.y, c.z - exteriorPoint.z);
                case Point.ColonyEntrance:
                    return new Vector3(c.x + entrancePoint.x, colony.GroundY(c.x + entrancePoint.x) + 0.3f, c.z + entrancePoint.y);
                default:
                    return new Vector3(c.x + downtownPoint.x, colony.GroundY(c.x + downtownPoint.x) + 0.3f, c.z + downtownPoint.y);
            }
        }

        void SetPaused(bool pause)
        {
            if (pause == _paused || pauseUntilChosen == null) return;
            _paused = pause;
            foreach (Behaviour b in pauseUntilChosen) if (b != null) b.enabled = !pause;
        }
    }
}
