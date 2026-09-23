using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Bridges a real XR Interaction Toolkit XRSimpleInteractable - added onto a
    /// JoystickLever's handle by GundamCockpitSetup.cs (see AttachHandInteractable) -
    /// into that JoystickLever's own grab state via NotifyXRIGrabbed.
    ///
    /// Per request ("XR Interaction Toolkit의 실제 Hand Interactor/Direct Interactor를
    /// 사용... Grab 판정이 제대로 되도록"): this project's XR Origin already
    /// instantiates XRI's own "Hands Interaction Demo" sample rig ("XR Origin Hands
    /// (XR Rig)"), which ships with a real "Near-Far Interactor" per hand, reading
    /// actual Galaxy XR pinch/grip selection - the same underlying hand-tracking
    /// data HandJointTracker reads, just through XRI's own tested selection logic.
    /// XRSimpleInteractable was chosen specifically because it does NOT move or
    /// rotate the object itself (unlike XRGrabInteractable) - it only fires
    /// select/hover events, so JoystickLever keeps 100% control of the actual tilt/
    /// twist/return math and this adapter only ever reports a yes/no "is grabbed by
    /// the correct hand" signal.
    ///
    /// Hand-exclusivity (LeftJoystick only ever grabbable by the left hand,
    /// RightJoystick only by the right) is enforced here by comparing the
    /// selecting interactor's own Transform against 'onlyAllowedInteractor' -
    /// rather than via Interaction Layer Mask settings - so nothing in Project
    /// Settings/the XR Interaction Toolkit's own layer configuration needs to be
    /// touched for this to work.
    /// </summary>
    [RequireComponent(typeof(XRSimpleInteractable))]
    public class HandExclusiveGrabAdapter : MonoBehaviour
    {
        [Tooltip("Only this interactor's Transform (e.g. this rig's own Left Hand > Near-Far Interactor) is allowed to select/grab this stick. Leave null to allow any interactor - not recommended, since then either hand could grab either stick.")]
        public Transform onlyAllowedInteractor;

        [Tooltip("The JoystickLever this adapter reports grab state into.")]
        public JoystickLever joystick;

        XRSimpleInteractable _interactable;
        IXRSelectInteractor _currentInteractor;

        void Awake()
        {
            _interactable = GetComponent<XRSimpleInteractable>();
        }

        void OnEnable()
        {
            if (_interactable == null) return;
            _interactable.selectEntered.AddListener(OnSelectEntered);
            _interactable.selectExited.AddListener(OnSelectExited);
        }

        void OnDisable()
        {
            if (_interactable == null) return;
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
            _interactable.selectExited.RemoveListener(OnSelectExited);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (onlyAllowedInteractor != null && args.interactorObject.transform != onlyAllowedInteractor)
            {
                // Wrong hand reached across and selected this stick (e.g. the
                // Near-Far Interactor's far-ray component, or simply the other
                // hand) - refuse it immediately by force-releasing the selection,
                // rather than letting it sit "held" by the wrong hand and block
                // the correct one from ever grabbing it. Safe to do here since
                // XRSimpleInteractable never physically attaches to anything.
                if (_interactable.interactionManager != null)
                {
                    _interactable.interactionManager.CancelInteractableSelection((IXRSelectInteractable)_interactable);
                }
                return;
            }

            _currentInteractor = args.interactorObject;
            if (joystick != null) joystick.NotifyXRIGrabbed(true);
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject != _currentInteractor) return;
            _currentInteractor = null;
            if (joystick != null) joystick.NotifyXRIGrabbed(false);
        }
    }
}
