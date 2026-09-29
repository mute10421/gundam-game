using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// BEAM SABER mode: drives the REAL Gundam model's right arm bones
    /// (RightArm -> RightForeArm -> RightHand) from the hand-held
    /// BeamSaberControlStick, and shows the arm + the beam saber in the pilot's
    /// exterior view - per request ("건담의 오른팔이 사용자의 손 움직임을
    /// 따라가도록", "기존 건담 모델의 팔을 활용").
    ///
    /// Visibility: ExternalGundam's own body mesh stays on its hidden layer (the
    /// pilot never sees their own Gundam). GundamCockpitSetup builds
    /// 'armView' - a second SkinnedMeshRenderer using ONLY the right-arm
    /// triangles of the same Gundam mesh, skinned to the same real bones - on a
    /// visible layer; it and the saber are enabled only while this is active.
    ///
    /// Mapping (every LateUpdate while active):
    ///   1. rel = stick position - pilot's right shoulder (both cockpit space;
    ///      shoulder = pilot eye + shoulderOffsetFromEye)
    ///   2. turned by the dome's view rotation (CockpitViewController.LookRotation),
    ///      so "forward" for the pilot is forward in what they see, then scaled by
    ///      (Gundam arm reach / human arm reach)
    ///   3. grip target = Gundam right shoulder (RightArm bone) + that offset;
    ///      two-bone IK bends RightArm/RightForeArm (elbow down/out/back) so the
    ///      hand reaches it (clamped to the arm's real length)
    ///   4. the saber sits in the fist and points where the stick's long axis
    ///      points (stick rotation -> saber rotation, same view turn).
    /// Arm bones are reset to their rest pose first every frame; nothing else in
    /// the project animates them. Blends smoothly in/out when the mode toggles.
    /// </summary>
    [DefaultExecutionOrder(-50)] // pose the arm before GundamHeadCam360 renders the exterior view
    public class BeamSaberArmController : MonoBehaviour
    {
        [Header("Gundam bones (real model)")]
        public Transform upperArm;   // RightArm
        public Transform foreArm;    // RightForeArm
        public Transform handBone;   // RightHand

        [Header("Visuals")]
        [Tooltip("Right-arm-only SkinnedMeshRenderer (same mesh/bones as the Gundam).")]
        public Renderer armView;
        [Tooltip("Beam saber root (child of RightHand). Its +Y is the blade direction.")]
        public Transform saber;

        [Header("Input")]
        public BeamSaberControlStick stick;
        [Tooltip("The pilot's head (Main Camera).")]
        public Transform pilotHead;
        [Tooltip("The cockpit interior (the stick's parent space).")]
        public Transform cockpitSpace;
        public CockpitViewController viewController;
        [Tooltip("Pilot's right shoulder relative to the eyes, in cockpit space (m).")]
        public Vector3 shoulderOffsetFromEye = new Vector3(0.19f, -0.27f, -0.04f);
        [Tooltip("Typical human shoulder-to-fist reach (m) - sets the human->Gundam scale.")]
        public float humanReach = 0.60f;
        [Tooltip("Distance (m, Gundam scale) from the RightHand bone to the fist center where the saber is held.")]
        public float gripOffset = 0.55f;
        [Tooltip("Target smoothing (s). Small = follows fast swings closely.")]
        public float smoothing = 0.03f;
        public float blendTime = 0.35f;

        public bool Active { get; private set; }

        Quaternion _upperRest, _foreRest, _handRest;
        float _l1, _l2, _scale;
        bool _init;
        float _blend;
        Vector3 _smoothTarget;
        bool _haveSmooth;
        Vector3 _eyeSmoothed;
        bool _haveEye;

        void Awake()
        {
            Init();
            SetVisuals(false);
        }

        void Init()
        {
            if (_init || upperArm == null || foreArm == null || handBone == null) return;
            _upperRest = upperArm.localRotation;
            _foreRest = foreArm.localRotation;
            _handRest = handBone.localRotation;
            _l1 = Vector3.Distance(upperArm.position, foreArm.position);
            _l2 = Vector3.Distance(foreArm.position, handBone.position);
            _scale = (_l1 + _l2 + gripOffset) / Mathf.Max(0.1f, humanReach);
            _init = true;
        }

        public void SetActive(bool on)
        {
            Init();
            Active = on;
            _haveSmooth = false;
            if (on) SetVisuals(true);
        }

        void SetVisuals(bool on)
        {
            if (armView != null) armView.enabled = on;
            if (saber != null) saber.gameObject.SetActive(on);
        }

        void LateUpdate()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            _blend = Mathf.MoveTowards(_blend, Active ? 1f : 0f, dt / Mathf.Max(0.01f, blendTime));

            // Always start from the rest pose.
            upperArm.localRotation = _upperRest;
            foreArm.localRotation = _foreRest;
            handBone.localRotation = _handRest;

            if (_blend <= 0f)
            {
                if (!Active) SetVisuals(false);
                return;
            }
            if (stick == null || pilotHead == null || cockpitSpace == null) return;

            Quaternion look = viewController != null ? viewController.LookRotation : Quaternion.identity;

            // Pilot shoulder (cockpit space), from a lightly smoothed eye position.
            Vector3 eye = cockpitSpace.InverseTransformPoint(pilotHead.position);
            float ke = 1f - Mathf.Exp(-dt / 0.25f);
            _eyeSmoothed = _haveEye ? Vector3.Lerp(_eyeSmoothed, eye, ke) : eye;
            _haveEye = true;
            Vector3 shoulderLocal = _eyeSmoothed + shoulderOffsetFromEye;
            Vector3 stickLocal = cockpitSpace.InverseTransformPoint(stick.transform.position);
            Vector3 relWorld = look * cockpitSpace.TransformDirection(stickLocal - shoulderLocal) * _scale;

            Vector3 a = upperArm.position;
            Vector3 gripTarget = a + relWorld;
            float k = smoothing > 0.0001f ? 1f - Mathf.Exp(-dt / smoothing) : 1f;
            _smoothTarget = _haveSmooth ? Vector3.Lerp(_smoothTarget, gripTarget, k) : gripTarget;
            _haveSmooth = true;

            // Wrist target = grip target pulled back toward the shoulder by the grip offset.
            Vector3 toGrip = _smoothTarget - a;
            Vector3 wristTarget = _smoothTarget - (toGrip.sqrMagnitude > 0.0001f ? toGrip.normalized : Vector3.down) * gripOffset;

            Quaternion upperIK, foreIK;
            SolveTwoBone(a, wristTarget, out upperIK, out foreIK);
            upperArm.rotation = Quaternion.Slerp(upperArm.rotation, upperIK, _blend);
            foreArm.rotation = Quaternion.Slerp(foreArm.rotation, foreIK, _blend);

            // Saber: in the fist, pointing along the stick's long axis (view-turned).
            if (saber != null)
            {
                Vector3 fist = handBone.position + (handBone.position - foreArm.position).normalized * gripOffset;
                saber.position = fist;
                saber.rotation = look * stick.transform.rotation;
            }
        }

        /// <summary>Two-bone IK: world rotations for the upper arm and forearm that put
        /// the wrist (hand bone) on 'target', elbow bending down/out/back.</summary>
        void SolveTwoBone(Vector3 a, Vector3 target, out Quaternion upperRot, out Quaternion foreRot)
        {
            Vector3 d = target - a;
            float dist = Mathf.Clamp(d.magnitude, 0.15f * (_l1 + _l2), 0.999f * (_l1 + _l2));
            Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.down;

            Transform root = transform;
            Vector3 pole = (root.right * 0.5f - root.up * 0.7f - root.forward * 0.3f).normalized;
            Vector3 bendAxis = Vector3.ProjectOnPlane(pole, dir);
            if (bendAxis.sqrMagnitude < 1e-6f) bendAxis = Vector3.ProjectOnPlane(-root.up, dir);
            bendAxis.Normalize();

            float cosA = Mathf.Clamp((_l1 * _l1 + dist * dist - _l2 * _l2) / (2f * _l1 * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 elbow = a + dir * (_l1 * cosA) + bendAxis * (_l1 * sinA);
            Vector3 wrist = a + dir * dist;

            // Upper arm: aim its current bone direction (shoulder->elbow) at the solved elbow.
            Vector3 curUpper = foreArm.position - a;
            upperRot = Quaternion.FromToRotation(curUpper, elbow - a) * upperArm.rotation;

            // Forearm: where it would be after the upper rotation, then aim at the wrist.
            Quaternion upperDelta = upperRot * Quaternion.Inverse(upperArm.rotation);
            Vector3 elbowPos = a + upperDelta * curUpper;
            Vector3 curFore = upperDelta * (handBone.position - foreArm.position);
            Quaternion foreWorldAfterUpper = upperDelta * foreArm.rotation;
            foreRot = Quaternion.FromToRotation(curFore, wrist - elbowPos) * foreWorldAfterUpper;
        }
    }
}
