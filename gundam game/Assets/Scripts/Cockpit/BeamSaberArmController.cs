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
    ///   4. the saber is HELD in the fist (it's a child of RightHand, seated through
    ///      the closed fist by GundamCockpitSetup): the HAND is turned so the saber
    ///      points where the stick's long axis points (stick rotation -> saber
    ///      rotation, same view turn), the wrist is placed so the fist lands on the
    ///      grip target, and part of the wrist roll is passed to the forearm so the
    ///      wrist doesn't look twisted off.
    ///
    /// RIGHTJOYSTICK MODE (rightStick assigned - the current setup, per "빔샤벨
    /// 조종기를 빼고 오른쪽 조종관으로 밀고 당기기는 찌르기 옆으로 당기면 옆으로
    /// 휘둘러"): no hand-held stick; the saber pose comes from RightJoystick's
    /// tiltInput, in the pilot's view frame (forward = where the view looks, which
    /// in BEAM SABER is the locked target):
    ///   center        GUARD  - fist in front of the chest, blade up and forward
    ///   push  (+Y)    THRUST - arm extends straight ahead, blade pointing forward
    ///   pull  (-Y)    CHAMBER - fist drawn back to the side, blade forward (ready
    ///                 to thrust)
    ///   sideways (X)  SWING - blade laid horizontal and carried around the front
    ///                 in an arc; the stick's side angle = the swing angle, so
    ///                 moving it from one side to the other slashes across
    /// The pose follows the stick continuously (a fast stick flick = a fast
    /// swing); the blade hit check is swept (BeamSaberBlade).
    ///
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
        [Tooltip("RightJoystick: its tiltInput drives the saber poses (thrust / swing). When set, 'stick' is ignored.")]
        public JoystickLever rightStick;
        [Tooltip("False = RightJoystick is turning the view (nothing locked, see WeaponModeController): the saber holds the guard pose.")]
        public bool inputEnabled = true;
        [Tooltip("Old input: hand-held control stick (used only when rightStick is empty).")]
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
        [Tooltip("Fallback distance (m, Gundam scale) from the RightHand bone to the fist center - used only if there's no saber to measure the real grip from.")]
        public float gripOffset = 0.55f;
        [Tooltip("Share of the hand's roll around the forearm that the forearm takes (0 = wrist only).")]
        [Range(0f, 1f)] public float forearmTwistShare = 0.5f;
        [Tooltip("Target smoothing (s). Small = follows fast swings closely.")]
        public float smoothing = 0.03f;
        public float blendTime = 0.35f;

        [Header("RightJoystick poses (fractions of the Gundam arm reach, view frame: x right, y up, z forward, from the right shoulder)")]
        public Vector3 guardGrip = new Vector3(-0.10f, -0.22f, 0.55f);
        public Vector3 guardBlade = new Vector3(0f, 0.8f, 0.6f);
        public Vector3 thrustGrip = new Vector3(-0.22f, -0.06f, 0.97f);
        public Vector3 thrustBlade = new Vector3(-0.05f, 0f, 1f);
        public Vector3 chamberGrip = new Vector3(0.10f, -0.30f, 0.22f);
        public Vector3 chamberBlade = new Vector3(0f, 0.15f, 1f);
        [Tooltip("Swing arc center, from the right shoulder toward the chest (fraction of reach, view frame).")]
        public Vector3 swingPivot = new Vector3(-0.35f, -0.12f, 0f);
        [Tooltip("Swing arc radius (fraction of reach).")]
        public float swingRadius = 0.62f;
        [Tooltip("Arc angle (deg) at full side deflection - left and right of straight ahead.")]
        public float swingAngle = 80f;
        [Tooltip("Blade is laid fully horizontal once the side deflection reaches this.")]
        [Range(0.05f, 1f)] public float swingFullAt = 0.45f;
        [Tooltip("Direction smoothing (s).")]
        public float directionSmoothing = 0.04f;

        public bool Active { get; private set; }

        Quaternion _upperRest, _foreRest, _handRest;
        Vector3 _saberLocalPos;             // grip point in RightHand space (bone units)
        Quaternion _saberLocalRot = Quaternion.identity;
        bool _haveSaberGrip;
        float _l1, _l2, _scale;
        bool _init;
        float _blend;
        Vector3 _smoothTarget;
        bool _haveSmooth;
        Vector3 _eyeSmoothed;
        Vector3 _smoothBlade;
        bool _haveBlade;
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
            if (saber != null && saber.parent == handBone)
            {
                // The hand->saber relation set up in the editor (hilt through the fist).
                _saberLocalPos = saber.localPosition;
                _saberLocalRot = saber.localRotation;
                _haveSaberGrip = true;
                gripOffset = Vector3.Scale(_saberLocalPos, handBone.lossyScale).magnitude;
            }
            _scale = (_l1 + _l2 + gripOffset) / Mathf.Max(0.1f, humanReach);
            _init = true;
        }

        public void SetActive(bool on)
        {
            Init();
            Active = on;
            _haveSmooth = false;
            _haveBlade = false;
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
            bool useJoystick = rightStick != null;
            if (cockpitSpace == null) return;
            if (!useJoystick && (stick == null || pilotHead == null)) return;

            Quaternion look = viewController != null ? viewController.LookRotation : Quaternion.identity;
            Vector3 a = upperArm.position;
            Vector3 gripTarget;
            Quaternion saberRot;

            if (useJoystick)
            {
                // View frame (what the pilot sees as forward/up/right).
                Vector3 F = look * cockpitSpace.forward;
                Vector3 U = look * cockpitSpace.up;
                Vector3 R = look * cockpitSpace.right;
                Vector3 bladeDir;
                JoystickPose(inputEnabled ? rightStick.tiltInput : Vector2.zero, out Vector3 gripV, out bladeDir);
                float reach = _l1 + _l2 + gripOffset;
                gripTarget = a + (R * gripV.x + U * gripV.y + F * gripV.z) * reach;
                Vector3 bladeW = (R * bladeDir.x + U * bladeDir.y + F * bladeDir.z).normalized;

                float kd = directionSmoothing > 0.0001f ? 1f - Mathf.Exp(-dt / directionSmoothing) : 1f;
                _smoothBlade = _haveBlade ? Vector3.Slerp(_smoothBlade, bladeW, kd) : bladeW;
                _haveBlade = true;
                saberRot = SaberRotation(_smoothBlade, U, R);
            }
            else
            {
                // Pilot shoulder (cockpit space), from a lightly smoothed eye position.
                Vector3 eye = cockpitSpace.InverseTransformPoint(pilotHead.position);
                float ke = 1f - Mathf.Exp(-dt / 0.25f);
                _eyeSmoothed = _haveEye ? Vector3.Lerp(_eyeSmoothed, eye, ke) : eye;
                _haveEye = true;
                Vector3 shoulderLocal = _eyeSmoothed + shoulderOffsetFromEye;
                Vector3 stickLocal = cockpitSpace.InverseTransformPoint(stick.transform.position);
                Vector3 relWorld = look * cockpitSpace.TransformDirection(stickLocal - shoulderLocal) * _scale;
                gripTarget = a + relWorld;
                // The saber points along the stick's long axis (view-turned).
                saberRot = look * stick.transform.rotation;
            }

            float k = smoothing > 0.0001f ? 1f - Mathf.Exp(-dt / smoothing) : 1f;
            _smoothTarget = _haveSmooth ? Vector3.Lerp(_smoothTarget, gripTarget, k) : gripTarget;
            _haveSmooth = true;

            // The hand is turned so the saber it's holding points along saberRot.
            Quaternion handRot = saberRot * Quaternion.Inverse(_saberLocalRot);

            // Wrist target = grip target minus the fist's grip offset in that hand pose.
            Vector3 wristTarget;
            if (_haveSaberGrip)
            {
                wristTarget = _smoothTarget - handRot * Vector3.Scale(_saberLocalPos, handBone.lossyScale);
            }
            else
            {
                Vector3 toGrip = _smoothTarget - a;
                wristTarget = _smoothTarget - (toGrip.sqrMagnitude > 0.0001f ? toGrip.normalized : Vector3.down) * gripOffset;
            }

            Quaternion upperIK, foreIK;
            SolveTwoBone(a, wristTarget, out upperIK, out foreIK);
            upperArm.rotation = Quaternion.Slerp(upperArm.rotation, upperIK, _blend);
            foreArm.rotation = Quaternion.Slerp(foreArm.rotation, foreIK, _blend);

            if (_haveSaberGrip)
            {
                // Pass part of the wrist roll to the forearm (rotation about its own
                // axis, so the wrist position doesn't move).
                Vector3 foreAxis = handBone.position - foreArm.position;
                if (foreAxis.sqrMagnitude > 1e-6f && forearmTwistShare > 0f)
                {
                    foreAxis.Normalize();
                    Quaternion delta = handRot * Quaternion.Inverse(handBone.rotation);
                    if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
                    Vector3 proj = Vector3.Project(new Vector3(delta.x, delta.y, delta.z), foreAxis);
                    Quaternion twist = new Quaternion(proj.x, proj.y, proj.z, delta.w);
                    float mag = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
                    if (mag > 1e-5f)
                    {
                        twist = new Quaternion(twist.x / mag, twist.y / mag, twist.z / mag, twist.w / mag);
                        twist = Quaternion.Slerp(Quaternion.identity, twist, forearmTwistShare * _blend);
                        foreArm.rotation = twist * foreArm.rotation;
                    }
                }
                handBone.rotation = Quaternion.Slerp(handBone.rotation, handRot, _blend);
                if (saber != null)
                {
                    saber.localPosition = _saberLocalPos; // stays gripped in the fist
                    saber.localRotation = _saberLocalRot;
                }
            }
            else if (saber != null)
            {
                Vector3 fist = handBone.position + (handBone.position - foreArm.position).normalized * gripOffset;
                saber.position = fist;
                saber.rotation = saberRot;
            }
        }

        /// <summary>RightJoystick deflection -> grip point (fractions of reach, view
        /// frame, from the right shoulder) and blade direction (view frame).</summary>
        void JoystickPose(Vector2 t, out Vector3 grip, out Vector3 blade)
        {
            float y = Mathf.Clamp(t.y, -1f, 1f);
            float x = Mathf.Clamp(t.x, -1f, 1f);

            // Push/pull: guard -> thrust (push) or guard -> chamber (pull).
            float py = Mathf.SmoothStep(0f, 1f, Mathf.Abs(y));
            Vector3 endGrip = y >= 0f ? thrustGrip : chamberGrip;
            Vector3 endBlade = y >= 0f ? thrustBlade : chamberBlade;
            grip = Vector3.Lerp(guardGrip, endGrip, py);
            blade = Vector3.Slerp(guardBlade.normalized, endBlade.normalized, py);

            // Sideways: horizontal swing arc around the chest.
            float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Abs(x) / swingFullAt));
            if (w > 0f)
            {
                float ang = x * swingAngle * Mathf.Deg2Rad;
                Vector3 arcDir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                // A thrust while swinging reaches a bit further.
                float radius = swingRadius * (1f + 0.25f * Mathf.Max(0f, y));
                Vector3 swingGrip = swingPivot + arcDir * radius;
                Vector3 swingBlade = new Vector3(Mathf.Sin(ang * 1.15f), 0.05f, Mathf.Cos(ang * 1.15f));
                grip = Vector3.Lerp(grip, swingGrip, w);
                blade = Vector3.Slerp(blade.normalized, swingBlade.normalized, w);
            }
        }

        /// <summary>Saber rotation with +Y along the blade and the fist's knuckles
        /// (saber +Z) rolling naturally: forward when the blade is up, down when it
        /// is laid forward or out to the side.</summary>
        static Quaternion SaberRotation(Vector3 blade, Vector3 viewUp, Vector3 viewRight)
        {
            Vector3 side = Vector3.Cross(viewUp, blade);
            if (side.sqrMagnitude < 1e-4f) side = viewRight;
            side.Normalize();
            Vector3 knuckles = Vector3.Cross(side, blade);
            return Quaternion.LookRotation(knuckles, blade);
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
