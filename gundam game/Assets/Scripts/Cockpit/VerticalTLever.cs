using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Separate T-shaped VERTICAL lever (rise / descend) - per request ("새로운
    /// 수직 이동 T자 레버"). A standalone device: it does not touch JoystickLever,
    /// ShipMovementController, WeaponAimFireController, HandJointTracker, the XR
    /// rig or the cameras. This step only produces VerticalInput; nothing reads
    /// it yet ("이번 단계에서는 실제 Gundam의 Y축 이동까지 연결하지 말고").
    ///
    /// Structure (built by GundamCockpitSetup.CreateVerticalTLever):
    ///   VerticalTLever (this, the mount)
    ///     VerticalTLever_Base    fixed hinge block under the handle
    ///     VerticalTLever_Stem    visual rod, re-aimed every frame from Base to Handle
    ///     VerticalTLever_Handle  the part that moves - slides along the mount's
    ///                            local Z ONLY (forward/back), never sideways/up
    ///       VerticalTLever_Crossbar  horizontal T grip (the grab collider)
    ///
    /// GRAB - same two signals the existing sticks use, both hand-exclusive:
    ///   1. XR Interaction Toolkit: an XRSimpleInteractable on the Handle (it
    ///      never moves anything itself). Only a select coming from under
    ///      'onlyAllowedInteractor' (this hand's own Near-Far Interactor) counts;
    ///      any other interactor's select is cancelled on the spot - the same
    ///      IsChildOf rule HandExclusiveGrabAdapter uses for the sticks.
    ///   2. Fallback: 'hand' (a HandJointTracker) gripping/pinching within
    ///      grabRadius of the crossbar - the same thresholds JoystickLever uses.
    ///   While the same hand is holding one of 'blockWhileGrabbed' (normally
    ///   LeftJoystick), this lever refuses to be grabbed, so one hand can never
    ///   drive both devices at once.
    ///
    /// NO JUMP ON GRAB: at the first grabbed frame only two reference points are
    /// stored - the hand's position and the handle's position (both in the
    /// mount's local space). Every later frame:
    ///     handleZ = clamp(grabStartHandleZ + (handZ - grabStartHandZ))
    /// so wherever on the crossbar you grab (left/middle/right), the handle stays
    /// exactly where it was and then follows only the hand's MOVEMENT - it never
    /// snaps to the hand or flies toward the body. Hand position always comes from
    /// the HandJointTracker palm (as in JoystickLever), and a one-frame
    /// tracking teleport larger than maxHandDeltaPerFrame is ignored.
    ///
    /// RANGE: +-travel (default 0.035 m = 7 cm total), symmetric; beyond it the
    /// handle simply stops. RELEASE: the handle eases back to the exact center
    /// (and snaps the last fraction of a millimetre), and VerticalInput is 0 the
    /// whole time it's not held.
    ///
    /// SHOULDER HEIGHT: GundamCockpitSetup places the mount at seated-shoulder
    /// height derived from the seat geometry. With adaptToPilotHeadHeight on, the
    /// first ~1.5 s of valid head tracking re-derives it from the pilot's REAL
    /// eye height (eye - eyeToShoulder), limited to +-maxHeightAdapt around the
    /// seat-based value, applied only while nobody is holding the lever.
    /// </summary>
    public class VerticalTLever : MonoBehaviour
    {
        [Header("Parts (wired by GundamCockpitSetup)")]
        public Transform handle;
        public Transform stem;
        public Transform stemBase;
        public Transform crossbar;

        [Header("Hand (exclusive)")]
        [Tooltip("The only hand tracker allowed to grab this lever (fallback grab + position source).")]
        public HandJointTracker hand;
        [Tooltip("The only XRI interactor allowed to select this lever (this hand's own Near-Far Interactor). Anything else is refused.")]
        public Transform onlyAllowedInteractor;
        [Tooltip("If any of these is currently grabbed (by the same hand), this lever can't be grabbed.")]
        public JoystickLever[] blockWhileGrabbed;

        [Header("Grab")]
        [Tooltip("Max palm distance (m) from the crossbar for the fallback grip/pinch grab. Kept small so the neighbouring stick is never in range.")]
        public float grabRadius = 0.12f;
        [Range(0.1f, 1f)] public float gripToGrab = 0.55f;
        [Range(0.05f, 1f)] public float gripToRelease = 0.35f;
        [Range(0.1f, 1f)] public float pinchToGrab = 0.5f;
        [Range(0.05f, 1f)] public float pinchToRelease = 0.3f;
        [Tooltip("A hand jump bigger than this (m) in one frame is treated as a tracking glitch and ignored.")]
        public float maxHandDeltaPerFrame = 0.15f;

        [Header("Travel / input")]
        [Tooltip("Half of the total travel (m): the handle moves from -travel (pulled back) to +travel (pushed forward). 0.035 = 7 cm total.")]
        public float travel = 0.035f;
        [Range(0f, 0.4f)] public float deadZone = 0.1f;
        [Tooltip("Speed of the smooth return to center after release.")]
        public float returnSpeed = 8f;

        [Header("Shoulder-height adaptation")]
        public bool adaptToPilotHeadHeight = true;
        [Tooltip("The pilot's head (Main Camera). Found via Camera.main if empty.")]
        public Transform pilotHead;
        [Tooltip("Typical vertical distance (m) from the eyes down to the shoulder joint.")]
        public float eyeToShoulder = 0.27f;
        [Tooltip("How far (m) above the shoulder the grip sits (0 = at shoulder height).")]
        public float gripAboveShoulder = -0.02f;
        [Tooltip("Max change (m) from the seat-derived height.")]
        public float maxHeightAdapt = 0.12f;
        public float adaptSampleSeconds = 1.5f;

        /// <summary>-1 (pulled fully back = descend) .. 0 (center) .. +1 (pushed fully forward = rise). 0 whenever not held.</summary>
        public float VerticalInput { get; private set; }
        /// <summary>Raw handle offset as -1..1, without dead zone (for HUD/debug).</summary>
        public float RawPosition { get; private set; }
        public bool IsGrabbed { get; private set; }
        public bool GrabbedByXRI => _xriSelected;

        XRSimpleInteractable _interactable;
        IXRSelectInteractor _xriInteractor;
        bool _xriSelected;
        bool _fallbackHeld;

        float _grabStartHandZ;
        float _grabStartHandleZ;
        float _lastRawHandZ;
        float _lastValidDelta;
        Vector3 _handleRestLocal;

        float _adaptTimer;
        float _adaptSum;
        int _adaptCount;
        bool _adaptDone;
        float _seatDerivedY;
        float _targetMountY;

        void Awake()
        {
            if (handle != null) _handleRestLocal = handle.localPosition;
            _seatDerivedY = transform.localPosition.y;
            _targetMountY = _seatDerivedY;
            if (handle != null) _interactable = handle.GetComponent<XRSimpleInteractable>();
        }

        void OnEnable()
        {
            if (_interactable == null && handle != null) _interactable = handle.GetComponent<XRSimpleInteractable>();
            if (_interactable != null)
            {
                _interactable.selectEntered.AddListener(OnSelectEntered);
                _interactable.selectExited.AddListener(OnSelectExited);
            }
        }

        void OnDisable()
        {
            if (_interactable != null)
            {
                _interactable.selectEntered.RemoveListener(OnSelectEntered);
                _interactable.selectExited.RemoveListener(OnSelectExited);
            }
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            bool wrongHand = onlyAllowedInteractor != null && !args.interactorObject.transform.IsChildOf(onlyAllowedInteractor);
            if (wrongHand || HandBusyElsewhere())
            {
                if (_interactable.interactionManager != null)
                {
                    _interactable.interactionManager.CancelInteractableSelection((IXRSelectInteractable)_interactable);
                }
                return;
            }
            _xriInteractor = args.interactorObject;
            _xriSelected = true;
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject != _xriInteractor) return;
            _xriInteractor = null;
            _xriSelected = false;
        }

        bool HandBusyElsewhere()
        {
            if (blockWhileGrabbed == null) return false;
            foreach (JoystickLever j in blockWhileGrabbed)
            {
                if (j != null && j.isGrabbed) return true;
            }
            return false;
        }

        bool HandTracked => hand != null && hand.IsTracked;

        float HandLocalZ()
        {
            return transform.InverseTransformPoint(hand.PalmPosition).z;
        }

        void Update()
        {
            if (handle == null) return;

            UpdateHeightAdaptation();

            bool wasGrabbed = IsGrabbed;

            // Fallback grab state (grip/pinch near the crossbar).
            if (!_fallbackHeld)
            {
                if (HandTracked && !HandBusyElsewhere() && NearCrossbar(grabRadius)
                    && (hand.GripAmount >= gripToGrab || hand.PinchAmount >= pinchToGrab))
                {
                    _fallbackHeld = true;
                }
            }
            else if (!HandTracked || (hand.GripAmount < gripToRelease && hand.PinchAmount < pinchToRelease))
            {
                _fallbackHeld = false;
            }

            // Held while EITHER signal holds (like JoystickLever: release only
            // when both agree the hand has let go). A position source (the palm)
            // is required to actually move it.
            IsGrabbed = (_xriSelected || _fallbackHeld) && hand != null;

            if (IsGrabbed)
            {
                if (!wasGrabbed)
                {
                    // Grab just started: store the two reference points only.
                    // The handle is NOT moved this frame.
                    float hz = HandTracked ? HandLocalZ() : 0f;
                    _grabStartHandZ = hz;
                    _lastRawHandZ = hz;
                    _lastValidDelta = 0f;
                    _grabStartHandleZ = handle.localPosition.z;
                }
                else if (HandTracked)
                {
                    float hz = HandLocalZ();
                    float delta;
                    if (Mathf.Abs(hz - _lastRawHandZ) > maxHandDeltaPerFrame)
                    {
                        delta = _lastValidDelta; // tracking glitch - keep last good value
                    }
                    else
                    {
                        delta = hz - _grabStartHandZ;
                        _lastValidDelta = delta;
                    }
                    _lastRawHandZ = hz;
                    float z = Mathf.Clamp(_grabStartHandleZ + delta, _handleRestLocal.z - travel, _handleRestLocal.z + travel);
                    handle.localPosition = new Vector3(_handleRestLocal.x, _handleRestLocal.y, z);
                }
                // else: tracking dropped for a moment - hold position.
            }
            else
            {
                // Released: smooth return to the exact center.
                Vector3 p = handle.localPosition;
                float k = 1f - Mathf.Exp(-returnSpeed * Time.deltaTime);
                float z = Mathf.Lerp(p.z, _handleRestLocal.z, k);
                if (Mathf.Abs(z - _handleRestLocal.z) < 0.0005f) z = _handleRestLocal.z;
                handle.localPosition = new Vector3(_handleRestLocal.x, _handleRestLocal.y, z);
            }

            float raw = travel > 0.0001f ? Mathf.Clamp((handle.localPosition.z - _handleRestLocal.z) / travel, -1f, 1f) : 0f;
            RawPosition = raw;
            VerticalInput = IsGrabbed ? ApplyDeadZone(raw) : 0f;
        }

        void LateUpdate()
        {
            if (stem == null || stemBase == null || handle == null) return;
            // Re-aim the rod from the fixed base to the (sliding) handle so it
            // reads as a lever tilting forward/back.
            Vector3 a = stemBase.localPosition;
            Vector3 b = handle.localPosition;
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.0001f) return;
            stem.localPosition = (a + b) * 0.5f;
            stem.localRotation = Quaternion.FromToRotation(Vector3.up, d / len);
            Vector3 s = stem.localScale;
            stem.localScale = new Vector3(s.x, len * 0.5f, s.z); // Unity cylinder is 2 units tall
        }

        bool NearCrossbar(float radius)
        {
            Transform t = crossbar != null ? crossbar : handle;
            // Distance to the crossbar's axis segment (its local Y is the long axis
            // of a Capsule primitive), so grabbing near either end counts too.
            Vector3 p = t.InverseTransformPoint(hand.PalmPosition);
            p.y = Mathf.Clamp(p.y, -1f, 1f);
            Vector3 closest = t.TransformPoint(new Vector3(0f, p.y, 0f));
            return Vector3.Distance(hand.PalmPosition, closest) <= radius;
        }

        void UpdateHeightAdaptation()
        {
            if (!adaptToPilotHeadHeight) return;

            if (!_adaptDone)
            {
                if (pilotHead == null && Camera.main != null) pilotHead = Camera.main.transform;
                if (pilotHead != null && transform.parent != null)
                {
                    float eyeY = transform.parent.InverseTransformPoint(pilotHead.position).y;
                    // Ignore obviously invalid samples (tracking not started yet).
                    if (eyeY > 0.6f && eyeY < 2.2f)
                    {
                        _adaptSum += eyeY;
                        _adaptCount++;
                        _adaptTimer += Time.deltaTime;
                        if (_adaptTimer >= adaptSampleSeconds && _adaptCount > 0)
                        {
                            float shoulderY = _adaptSum / _adaptCount - eyeToShoulder;
                            _targetMountY = Mathf.Clamp(shoulderY + gripAboveShoulder,
                                _seatDerivedY - maxHeightAdapt, _seatDerivedY + maxHeightAdapt);
                            _adaptDone = true;
                        }
                    }
                }
            }

            // Only ever moved while nobody is holding it.
            if (!IsGrabbed)
            {
                Vector3 lp = transform.localPosition;
                if (Mathf.Abs(lp.y - _targetMountY) > 0.0005f)
                {
                    lp.y = Mathf.MoveTowards(lp.y, _targetMountY, 0.25f * Time.deltaTime);
                    transform.localPosition = lp;
                }
            }
        }

        static float ApplyDeadZone(float v, float dz)
        {
            float m = Mathf.Abs(v);
            if (m <= dz || dz >= 1f) return 0f;
            return Mathf.Sign(v) * Mathf.Clamp01((m - dz) / (1f - dz));
        }

        float ApplyDeadZone(float v) => ApplyDeadZone(v, deadZone);
    }
}
