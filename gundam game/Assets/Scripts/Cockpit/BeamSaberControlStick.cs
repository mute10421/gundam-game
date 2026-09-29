using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The short hand-held control stick used in BEAM SABER mode - per request
    /// ("BEAM SABER 모드에서는 사용자의 오른손에 전용 컨트롤 스틱을 하나 쥐어주자").
    /// Shown only while WeaponModeController is in BEAM SABER mode; hidden (whole
    /// GameObject inactive) otherwise. BeamSaberArmController reads its pose to
    /// drive the Gundam's right arm and saber.
    ///
    /// Moves freely in 3D (position + rotation) while held - it's a hand-held
    /// grip, not a lever - but it never jumps to the hand:
    ///   grab start:  A = palm position, B = stick position (offset = A - B),
    ///                palm rotation / stick rotation also stored
    ///   while held:  stick position = B + (palm - A)
    ///                stick rotation = (palmRot * inverse(palmRotAtGrab)) * stickRotAtGrab
    /// so wherever on the stick you close your hand, it stays exactly where it was
    /// and only follows the hand's MOVEMENT and TWIST from then on. When released it
    /// simply stays where it was let go.
    ///
    /// Grab signals are the same two the cockpit sticks use, right hand only:
    /// the XRI Near-Far Interactor (selects coming from any other interactor are
    /// cancelled on the spot - same IsChildOf rule as HandExclusiveGrabAdapter), or
    /// the right HandJointTracker gripping/pinching within grabRadius of the stick.
    /// Position/rotation always come from the HandJointTracker palm, like JoystickLever.
    /// </summary>
    public class BeamSaberControlStick : MonoBehaviour
    {
        [Header("Hand (right only)")]
        public HandJointTracker hand;
        [Tooltip("Only this XRI interactor (the right hand's Near-Far Interactor) may select the stick.")]
        public Transform onlyAllowedInteractor;
        [Tooltip("Collider object carrying the XRSimpleInteractable (the stick body).")]
        public XRSimpleInteractable interactable;

        [Header("Grab")]
        public float grabRadius = 0.10f;
        [Range(0.1f, 1f)] public float gripToGrab = 0.55f;
        [Range(0.05f, 1f)] public float gripToRelease = 0.35f;
        [Range(0.1f, 1f)] public float pinchToGrab = 0.5f;
        [Range(0.05f, 1f)] public float pinchToRelease = 0.3f;
        [Tooltip("A palm jump bigger than this (m) in one frame is treated as a tracking glitch.")]
        public float maxHandDeltaPerFrame = 0.15f;
        [Tooltip("Half length (m) of the stick along its local Y - used for the grab distance test.")]
        public float halfLength = 0.07f;

        [Header("Spawn")]
        [Tooltip("Where the stick appears (parent-local) if the right hand isn't tracked when BEAM SABER is selected.")]
        public Vector3 defaultLocalPosition = new Vector3(0.28f, 1.0f, 0.05f);

        public bool IsGrabbed { get; private set; }

        bool _xriSelected;
        IXRSelectInteractor _xriInteractor;
        bool _fallbackHeld;
        Vector3 _handStart, _stickStart, _lastRawHand, _lastValidDelta;
        Quaternion _handRotStart, _stickRotStart;
        bool _subscribed;

        void OnEnable()
        {
            if (interactable != null && !_subscribed)
            {
                interactable.selectEntered.AddListener(OnSelectEntered);
                interactable.selectExited.AddListener(OnSelectExited);
                _subscribed = true;
            }
        }

        void OnDisable()
        {
            if (interactable != null && _subscribed)
            {
                interactable.selectEntered.RemoveListener(OnSelectEntered);
                interactable.selectExited.RemoveListener(OnSelectExited);
                _subscribed = false;
            }
            _xriSelected = false;
            _xriInteractor = null;
            _fallbackHeld = false;
            IsGrabbed = false;
        }

        /// <summary>Places the stick upright at the right palm (or the default spot) - called
        /// by WeaponModeController when BEAM SABER mode starts.</summary>
        public void SpawnAtHand()
        {
            Transform parent = transform.parent;
            Vector3 world;
            if (hand != null && hand.IsTracked) world = XRHandSpace.PalmPosition(hand);
            else world = parent != null ? parent.TransformPoint(defaultLocalPosition) : defaultLocalPosition;
            transform.position = world;
            transform.rotation = parent != null ? parent.rotation : Quaternion.identity; // upright
            IsGrabbed = false;
            _fallbackHeld = false;
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (onlyAllowedInteractor != null && !args.interactorObject.transform.IsChildOf(onlyAllowedInteractor))
            {
                if (interactable.interactionManager != null)
                    interactable.interactionManager.CancelInteractableSelection((IXRSelectInteractable)interactable);
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

        bool HandTracked => hand != null && hand.IsTracked;

        void Update()
        {
            bool was = IsGrabbed;

            if (!_fallbackHeld)
            {
                if (HandTracked && NearStick() && (hand.GripAmount >= gripToGrab || hand.PinchAmount >= pinchToGrab))
                    _fallbackHeld = true;
            }
            else if (!HandTracked || (hand.GripAmount < gripToRelease && hand.PinchAmount < pinchToRelease))
            {
                _fallbackHeld = false;
            }

            IsGrabbed = (_xriSelected || _fallbackHeld) && hand != null;
            if (!IsGrabbed) return;

            if (!was)
            {
                // Grab start: store reference points only - the stick does not move this frame.
                _handStart = HandTracked ? XRHandSpace.PalmPosition(hand) : transform.position;
                _lastRawHand = _handStart;
                _lastValidDelta = Vector3.zero;
                _stickStart = transform.position;
                _handRotStart = XRHandSpace.PalmRotation(hand);
                _stickRotStart = transform.rotation;
                return;
            }
            if (!HandTracked) return; // tracking dropped - hold

            Vector3 p = XRHandSpace.PalmPosition(hand);
            Vector3 delta;
            if ((p - _lastRawHand).magnitude > maxHandDeltaPerFrame)
            {
                delta = _lastValidDelta; // glitch - keep last good
            }
            else
            {
                delta = p - _handStart;
                _lastValidDelta = delta;
                transform.rotation = (XRHandSpace.PalmRotation(hand) * Quaternion.Inverse(_handRotStart)) * _stickRotStart;
            }
            _lastRawHand = p;
            transform.position = _stickStart + delta;
        }

        bool NearStick()
        {
            Vector3 lp = transform.InverseTransformPoint(XRHandSpace.PalmPosition(hand));
            Vector3 onAxis = transform.TransformPoint(new Vector3(0f, Mathf.Clamp(lp.y, -halfLength, halfLength), 0f));
            return Vector3.Distance(XRHandSpace.PalmPosition(hand), onAxis) <= grabRadius;
        }
    }
}
