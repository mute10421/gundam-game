using UnityEngine;
using System.Collections.Generic;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Turns the cockpit's big OrbitHUD ring (the ring of OrbitHUD_Tick_* marks +
    /// OrbitHUD_Gate markers built by GundamCockpitSetup.BuildOrbitHUD) into a
    /// lock-on reticle, per request:
    ///
    ///   "OrbitHUD_Tick_3 이거 만들어 둔게 적을 포착하면 적 사이즈로 모여서
    ///    줄어들어야해 원형으로 적을 타겟하는거야"
    ///
    /// When an enemy (EnemyMarker - e.g. ZakuEnemy) appears INSIDE the ring as the
    /// pilot sees it, the whole ring gathers onto the enemy: every tick slides toward
    /// the enemy's on-screen position and the circle shrinks to the enemy's apparent
    /// size, the ticks themselves get smaller, and once fully closed the ring turns
    /// lockedMaterial (red). It keeps following the enemy while it stays captured;
    /// when the enemy leaves the ring (or is gone) the ring opens back out to its
    /// original shape.
    ///
    /// Where the pilot "sees" the enemy: the pilot never looks at the world directly
    /// - they're inside the opaque Cockpit_Dome, which shows HeadCam's cubemap,
    /// sampled by the dome's surface normal and turned by the RightJoystick view
    /// (CockpitViewController.LookRotation: dome direction d shows world direction
    /// LookRotation*d). So the enemy's world direction from HeadCam is mapped back to
    /// the exact point on the (non-uniformly scaled) dome surface that displays it,
    /// and the ring is placed where the pilot's eye-ray to that point crosses the
    /// ring's own plane. Apparent size = the enemy's top and side bounds edges
    /// pushed through that same mapping, so it matches what's actually on the dome.
    ///
    /// Only moves/scales/re-materials the ring's own child ticks (restored exactly
    /// when released). Never touches the enemy, cameras, MobileSuitRoot or the view.
    /// </summary>
    public class OrbitHUDTargetLock : MonoBehaviour
    {
        [Header("References (wired by GundamCockpitSetup)")]
        [Tooltip("The pilot's own XR Main Camera (only read).")]
        public Camera pilotCamera;
        [Tooltip("RightJoystick view controller - gives HeadCam (the view origin) and the view rotation.")]
        public CockpitViewController viewController;
        [Tooltip("Cockpit_Dome (the enclosing screen the outside world is shown on).")]
        public Transform dome;
        [Tooltip("Ring color once fully locked. Leave empty to keep the normal colors.")]
        public Material lockedMaterial;

        [Header("Ring")]
        [Tooltip("Must match BuildOrbitHUD's ringRadius.")]
        public float ringRadius = 1.5f;
        [Tooltip("An enemy must appear within ringRadius x this to be captured.")]
        public float captureRadiusFactor = 1.0f;
        [Tooltip("Once locked, the enemy stays locked until it's farther out than ringRadius x this (hysteresis).")]
        public float releaseRadiusFactor = 1.25f;

        [Header("Lock-on look")]
        [Tooltip("Seconds for the ring to fully close onto a captured enemy.")]
        public float lockTime = 0.5f;
        [Tooltip("Seconds for the ring to open back out after the enemy is lost.")]
        public float releaseTime = 0.4f;
        [Tooltip("Locked circle = enemy's apparent size x this (a little room around it).")]
        public float sizeMargin = 1.1f;
        [Tooltip("Smallest the locked circle gets (m, on the ring plane). Was 0.08 - per \"락온이 거리에 따라서 작아졌으면\" it now keeps shrinking with distance almost to the enemy's real apparent size.")]
        public float minLockRadius = 0.035f;
        [Tooltip("Largest tick size when fully locked (1 = unchanged).")]
        [Range(0.1f, 1f)] public float lockedTickScale = 0.5f;

        [Header("Small (far) lock (per \"멀리있는 적을 락온하면 너무 뭉쳐서 덩어리로 보임\")")]
        [Tooltip("Locked tick size = locked circle radius x this (capped at lockedTickScale), so a small far-away lock gets proportionally small ticks instead of a clump.")]
        public float tickScalePerRadius = 2.2f;
        [Tooltip("Ticks never shrink below this scale.")]
        public float minLockedTickScale = 0.06f;
        [Tooltip("Locked circle radius (m) below which the minor ticks fade out (start, end).")]
        public Vector2 minorTicksFade = new Vector2(0.5f, 0.25f);
        [Tooltip("Locked circle radius (m) below which the major ticks fade out too, leaving only the 4 gate brackets (start, end).")]
        public Vector2 majorTicksFade = new Vector2(0.2f, 0.1f);
        [Tooltip("How quickly the locked circle follows the enemy's position/size.")]
        public float followSharpness = 12f;

        [Header("Detection")]
        public float maxRange = 500f;
        [Tooltip("Also lock onto the practice Target cubes (HitTarget), not just enemies (EnemyMarker).")]
        public bool includeHitTargets = false;
        public float rescanInterval = 0.5f;

        [Header("Line of sight (per \"건물뒤에 보이지 않는 적은 락온되면 안됨\")")]
        [Tooltip("Colony battlefield: enemies hidden behind its buildings / walls can't be locked. Found automatically if empty.")]
        public ColonyStructure colony;
        [Tooltip("A locked enemy that goes out of sight stays locked this long (s) - so a thin pillar doesn't break the lock.")]
        public float occludedGrace = 0.6f;
        [Tooltip("A new target must stay in clear sight this long (s) before it can be captured - stops the ring flashing on/off at building edges.")]
        public float captureSightTime = 0.15f;

        /// <summary>The currently captured target (null = none).</summary>
        public Transform CurrentTarget { get; private set; }
        /// <summary>0 = ring fully open, 1 = fully closed onto the target.</summary>
        public float LockProgress => _t;
        public bool IsLocked => _t >= 0.99f && CurrentTarget != null;

        struct Tick
        {
            public Transform t;
            public Vector3 pos;
            public Vector3 scale;
            public Vector2 dir;
            public Renderer[] renderers;
            public Material[] materials;
            public int tier; // 0 minor tick, 1 major tick, 2 gate bracket
        }

        readonly List<Tick> _ticks = new List<Tick>();
        readonly List<Transform> _candidates = new List<Transform>();
        float _rescanTimer;
        float _t;
        Vector2 _center;
        float _radius;
        bool _haveLockPose;
        bool _lockedMatOn;
        float _occludedTimer;
        readonly System.Collections.Generic.Dictionary<Transform, float> _seenSince = new System.Collections.Generic.Dictionary<Transform, float>();
        bool _colonySearched;

        void Awake()
        {
            _ticks.Clear();
            foreach (Transform child in transform)
            {
                Vector3 p = child.localPosition;
                Vector2 d = new Vector2(p.x, p.y);
                // Tier from the ring layout (BuildOrbitHUD: 60 marks, every 6 deg;
                // every 5th is a major tick, the 4 at 0/90/180/270 are gates).
                int tier = 0;
                if (child.name.Contains("Gate")) tier = 2;
                else if (d.sqrMagnitude > 1e-6f)
                {
                    float ang = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                    int idx = ((Mathf.RoundToInt(ang / 6f) % 60) + 60) % 60;
                    tier = idx % 5 == 0 ? 1 : 0;
                }
                Renderer[] rs = child.GetComponentsInChildren<Renderer>(true);
                Material[] ms = new Material[rs.Length];
                for (int i = 0; i < rs.Length; i++) ms[i] = rs[i].sharedMaterial;
                _ticks.Add(new Tick
                {
                    t = child,
                    pos = p,
                    scale = child.localScale,
                    dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector2.up,
                    renderers = rs,
                    materials = ms,
                    tier = tier,
                });
            }
            _radius = ringRadius;
        }

        void Update()
        {
            if (colony == null && !_colonySearched)
            {
                _colonySearched = true;
                colony = FindFirstObjectByType<ColonyStructure>();
            }
            _rescanTimer -= Time.deltaTime;
            if (_rescanTimer <= 0f)
            {
                Rescan();
                _rescanTimer = rescanInterval;
            }

            // Pick the captured target: keep the current one while it's within the
            // (looser) release radius, otherwise take whichever enemy appears
            // closest to the ring's center inside the capture radius.
            Transform best = null;
            Vector2 bestCenter = Vector2.zero;
            float bestRadius = 0f;
            float bestScore = float.MaxValue;
            for (int i = 0; i < _candidates.Count; i++)
            {
                Transform c = _candidates[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                // Destroyed enemies (EnemyHealth, waiting to respawn) can't be locked.
                EnemyHealth hp = c.GetComponent<EnemyHealth>();
                if (hp != null && hp.IsDead) continue;
                if (!TryProject(c, out Vector2 center, out float radius)) continue;
                float limit = ringRadius * (c == CurrentTarget ? releaseRadiusFactor : captureRadiusFactor);
                float off = center.magnitude;
                if (off > limit) continue;
                // Hidden behind a building/wall: can't be captured; the current lock
                // survives only a short occlusion.
                if (!InSight(c))
                {
                    _seenSince.Remove(c);
                    if (c != CurrentTarget) continue;
                    _occludedTimer += Time.deltaTime;
                    if (_occludedTimer > occludedGrace) continue;
                }
                else
                {
                    if (!_seenSince.TryGetValue(c, out float since)) { since = Time.time; _seenSince[c] = since; }
                    if (c == CurrentTarget) _occludedTimer = 0f;
                    else if (Time.time - since < captureSightTime) continue;
                }
                float score = c == CurrentTarget ? off * 0.5f : off; // prefer keeping the current lock
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                    bestCenter = center;
                    bestRadius = radius;
                }
            }

            if (best != null)
            {
                if (best != CurrentTarget || !_haveLockPose)
                {
                    // New target: start the circle from the full ring around its
                    // own center, then close in - reads as "the ring gathers onto it".
                    if (CurrentTarget == null && _t <= 0.01f) { _center = Vector2.zero; _radius = ringRadius; }
                    _haveLockPose = true;
                }
                if (best != CurrentTarget) _occludedTimer = 0f;
                CurrentTarget = best;
                float f = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
                _center = Vector2.Lerp(_center, bestCenter, f);
                _radius = Mathf.Lerp(_radius, Mathf.Clamp(bestRadius, minLockRadius, ringRadius), f);
                _t = Mathf.MoveTowards(_t, 1f, Time.deltaTime / Mathf.Max(0.01f, lockTime));
            }
            else
            {
                CurrentTarget = null;
                _occludedTimer = 0f;
                _t = Mathf.MoveTowards(_t, 0f, Time.deltaTime / Mathf.Max(0.01f, releaseTime));
                if (_t <= 0f) _haveLockPose = false;
            }

            ApplyRing();
        }

        /// <summary>The ring's own colors changed (cockpit re-tint for the ZAKU): take
        /// the ticks' current materials as their normal (unlocked) look.</summary>
        public void RefreshMaterials()
        {
            if (_lockedMatOn) return;
            for (int i = 0; i < _ticks.Count; i++)
            {
                Tick k = _ticks[i];
                for (int r = 0; r < k.renderers.Length; r++)
                    if (k.renderers[r] != null) k.materials[r] = k.renderers[r].sharedMaterial;
            }
        }

        void ApplyRing()
        {
            float e = _t * _t * (3f - 2f * _t); // smoothstep
            // Locked ticks scale with the locked circle, so a far (small) target
            // gets a small, clean reticle instead of full-size ticks piled up.
            float lockedScale = Mathf.Clamp(_radius * tickScalePerRadius, minLockedTickScale, lockedTickScale);
            float tickScale = Mathf.Lerp(1f, lockedScale, e);
            // ...and on a small circle the minor, then the major ticks fade out,
            // leaving just the 4 gate brackets around a distant enemy.
            float minorMul = Mathf.Lerp(1f, Mathf.InverseLerp(minorTicksFade.y, minorTicksFade.x, _radius), e);
            float majorMul = Mathf.Lerp(1f, Mathf.InverseLerp(majorTicksFade.y, majorTicksFade.x, _radius), e);
            for (int i = 0; i < _ticks.Count; i++)
            {
                Tick k = _ticks[i];
                if (k.t == null) continue;
                Vector3 locked = new Vector3(_center.x + k.dir.x * _radius, _center.y + k.dir.y * _radius, k.pos.z);
                k.t.localPosition = Vector3.Lerp(k.pos, locked, e);
                float mul = k.tier == 0 ? minorMul : (k.tier == 1 ? majorMul : 1f);
                k.t.localScale = k.scale * (tickScale * mul);
                bool show = mul > 0.01f;
                for (int r = 0; r < k.renderers.Length; r++)
                {
                    if (k.renderers[r] != null && k.renderers[r].enabled != show) k.renderers[r].enabled = show;
                }
            }

            bool wantLockedMat = lockedMaterial != null && e >= 0.98f;
            if (wantLockedMat != _lockedMatOn)
            {
                _lockedMatOn = wantLockedMat;
                for (int i = 0; i < _ticks.Count; i++)
                {
                    Tick k = _ticks[i];
                    for (int r = 0; r < k.renderers.Length; r++)
                    {
                        if (k.renderers[r] != null) k.renderers[r].sharedMaterial = wantLockedMat ? lockedMaterial : k.materials[r];
                    }
                }
            }
        }

        /// <summary>Where (ring-local x/y) and how big (ring-plane radius) the target
        /// looks to the pilot. False if it's behind, out of range, or unprojectable.</summary>
        bool TryProject(Transform target, out Vector2 center, out float radius)
        {
            center = Vector2.zero;
            radius = 0f;
            if (pilotCamera == null) return false;

            Bounds b = TargetBounds(target);
            Vector3 origin = ViewOrigin();
            Vector3 toTarget = b.center - origin;
            float dist = toTarget.magnitude;
            if (dist < 0.5f || dist > maxRange) return false;

            if (!ProjectPoint(b.center, out center)) return false;

            // Apparent size: project the target's top and side edges through the
            // SAME dome mapping and measure how far they land from its center on
            // the ring plane - this includes the dome's own stretch, so the locked
            // circle matches what the pilot actually sees.
            Vector3 worldDir = toTarget / dist;
            Vector3 side = Vector3.Cross(Vector3.up, worldDir);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
            side.Normalize();
            float r = 0f;
            if (ProjectPoint(b.center + Vector3.up * b.extents.y, out Vector2 top)) r = Mathf.Max(r, (top - center).magnitude);
            if (ProjectPoint(b.center + side * Mathf.Max(b.extents.x, b.extents.z), out Vector2 edge)) r = Mathf.Max(r, (edge - center).magnitude);
            radius = r * sizeMargin;
            return true;
        }

        /// <summary>Can the pilot (HeadCam) see the target - its middle or its top
        /// (so a head over a roof still counts)?</summary>
        bool InSight(Transform target)
        {
            if (colony == null) return true;
            Bounds b = TargetBounds(target);
            Vector3 o = ViewOrigin();
            if (!colony.SegmentBlocked(o, b.center)) return true;
            return !colony.SegmentBlocked(o, b.center + Vector3.up * (b.extents.y * 0.8f));
        }

        Vector3 ViewOrigin()
        {
            if (viewController != null && viewController.viewCamera != null) return viewController.viewCamera.transform.position;
            return pilotCamera.transform.position;
        }

        /// <summary>Where a world point appears to the pilot, in ring-local x/y on the
        /// ring's plane (see the class comment for the dome mapping).</summary>
        bool ProjectPoint(Vector3 worldPoint, out Vector2 onPlane)
        {
            onPlane = Vector2.zero;
            Vector3 eye = pilotCamera.transform.position;
            Quaternion look = viewController != null ? viewController.LookRotation : Quaternion.identity;

            Vector3 toPoint = worldPoint - ViewOrigin();
            if (toPoint.sqrMagnitude < 1e-6f) return false;
            // Dome direction that displays this world direction.
            Vector3 domeDir = Quaternion.Inverse(look) * toPoint.normalized;

            // Point on the dome surface showing it (dome samples by its surface
            // normal: n_world ~ R * (n_obj / S), so n_obj ~ S * (R^-1 * d)).
            Vector3 shownPoint;
            if (dome != null)
            {
                Vector3 local = dome.InverseTransformDirection(domeDir);
                Vector3 nObj = Vector3.Scale(dome.lossyScale, local).normalized;
                shownPoint = dome.TransformPoint(nObj * 0.5f);
            }
            else
            {
                shownPoint = eye + domeDir * 5f;
            }

            // Pilot's eye-ray to that point, crossed with the ring's plane.
            Vector3 ray = shownPoint - eye;
            if (ray.sqrMagnitude < 1e-6f) return false;
            ray.Normalize();
            Vector3 n = transform.forward;
            float denom = Vector3.Dot(ray, n);
            if (denom <= 1e-4f) return false; // looking away from the ring plane
            float tPlane = Vector3.Dot(transform.position - eye, n) / denom;
            if (tPlane <= 0f) return false;
            Vector3 hl = transform.InverseTransformPoint(eye + ray * tPlane);
            onPlane = new Vector2(hl.x, hl.y);
            return true;
        }

        static Bounds TargetBounds(Transform target)
        {
            Renderer[] rs = target.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(target.position, Vector3.one);
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        void Rescan()
        {
            _candidates.Clear();
            foreach (EnemyMarker e in FindObjectsByType<EnemyMarker>(FindObjectsInactive.Exclude))
            {
                _candidates.Add(e.transform);
            }
            if (includeHitTargets)
            {
                foreach (HitTarget h in FindObjectsByType<HitTarget>(FindObjectsInactive.Exclude))
                {
                    _candidates.Add(h.transform);
                }
            }
        }
    }
}
