using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// BEAM SABER mode view assist - per request ("빔샤벨 모드에서는 화면조작을
    /// 못하니까 락온된 상대에게 시선이 고정되서 따라가게하자"). In BEAM SABER the
    /// right hand holds the saber stick, so it can't turn the view with RightJoystick;
    /// instead the view (CockpitViewController) keeps the target in front of the
    /// pilot and follows it as it moves.
    ///
    /// Target choice: the enemy the OrbitHUD ring is locked on; if nothing is locked
    /// at that moment, the last enemy it was locked on; if that one is gone or
    /// destroyed, the nearest living enemy (EnemyMarker). Switches to the next
    /// nearest when the current one is destroyed. Leaving BEAM SABER hands the view
    /// straight back to RightJoystick, continuing from where it's looking.
    /// </summary>
    [DefaultExecutionOrder(-20)] // before CockpitViewController reads the target
    public class SaberLookAssist : MonoBehaviour
    {
        public WeaponModeController modes;
        public CockpitViewController viewController;
        public OrbitHUDTargetLock targetLock;

        /// <summary>The enemy the view is currently following (null = none).</summary>
        public Transform Target { get; private set; }

        Transform _lastLocked;
        float _rescanTimer;

        void Update()
        {
            if (viewController == null) return;

            if (targetLock != null && targetLock.CurrentTarget != null && Alive(targetLock.CurrentTarget))
                _lastLocked = targetLock.CurrentTarget;

            bool saber = modes != null && modes.CurrentMode == WeaponModeController.Mode.BeamSaber;
            if (!saber)
            {
                Target = null;
                if (viewController.AutoLookActive) viewController.ClearAutoLook();
                return;
            }

            if (targetLock != null && targetLock.CurrentTarget != null && Alive(targetLock.CurrentTarget))
                Target = targetLock.CurrentTarget;
            else if (Target == null || !Alive(Target))
                Target = _lastLocked != null && Alive(_lastLocked) ? _lastLocked : null;

            if (Target == null)
            {
                _rescanTimer -= Time.deltaTime;
                if (_rescanTimer <= 0f)
                {
                    _rescanTimer = 0.5f;
                    Target = NearestEnemy();
                }
            }

            if (Target != null) viewController.SetAutoLookTarget(AimPoint(Target));
            else viewController.ClearAutoLook();
        }

        static bool Alive(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            EnemyHealth hp = t.GetComponent<EnemyHealth>();
            return hp == null || !hp.IsDead;
        }

        Transform NearestEnemy()
        {
            Vector3 from = viewController.viewCamera != null ? viewController.viewCamera.transform.position : transform.position;
            Transform best = null;
            float bestD = float.MaxValue;
            foreach (EnemyMarker e in FindObjectsByType<EnemyMarker>(FindObjectsInactive.Exclude))
            {
                if (!Alive(e.transform)) continue;
                float d = (e.transform.position - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e.transform; }
            }
            return best;
        }

        /// <summary>Center of the enemy's visible body (renderer bounds), so the view
        /// centers on the suit rather than on its feet (the transform pivot).</summary>
        static Vector3 AimPoint(Transform t)
        {
            Renderer[] rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return t.position;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b.center;
        }
    }
}
