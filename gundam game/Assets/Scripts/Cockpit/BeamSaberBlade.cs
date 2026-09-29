using System.Collections.Generic;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Beam saber hits - per request ("빔샤벨은 데미지 일단은 200"). Sits on the
    /// BeamSaber root (which is only active in BEAM SABER mode); the blade runs
    /// along the root's +Y from bladeStart to bladeStart + bladeLength.
    ///
    /// Each frame the blade is checked as a thick line (radius hitRadius) AND swept
    /// from where it was last frame to where it is now, so a fast swing that passes
    /// clean through an enemy between two frames still connects. An enemy
    /// (EnemyHealth) takes `damage` once per contact, then can't be hit again for
    /// hitCooldown seconds - so holding the blade inside it doesn't drain 200 per
    /// frame; pull out and swing again to hit again. A short spark marks the hit.
    /// </summary>
    public class BeamSaberBlade : MonoBehaviour
    {
        public int damage = 200;
        [Tooltip("Where the beam starts along +Y (m, world scale) - the end of the hilt.")]
        public float bladeStart = 0.9f;
        public float bladeLength = 9f;
        [Tooltip("Hit thickness (m).")]
        public float hitRadius = 0.6f;
        [Tooltip("Seconds before the same enemy can be hit again.")]
        public float hitCooldown = 0.5f;
        [Tooltip("Points sampled along the blade.")]
        public int samples = 8;
        public Material sparkMaterial;
        public float sparkSize = 3f;

        Vector3[] _prev;
        bool _havePrev;
        readonly Dictionary<EnemyHealth, float> _lastHit = new Dictionary<EnemyHealth, float>();
        readonly Collider[] _overlap = new Collider[16];

        void OnEnable()
        {
            _havePrev = false;
        }

        void LateUpdate()
        {
            int n = Mathf.Max(2, samples);
            if (_prev == null || _prev.Length != n) { _prev = new Vector3[n]; _havePrev = false; }

            Vector3 up = transform.up;
            Vector3 origin = transform.position;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                Vector3 p = origin + up * (bladeStart + bladeLength * t);

                // Thick point at the blade's current position.
                int count = Physics.OverlapSphereNonAlloc(p, hitRadius, _overlap, ~0, QueryTriggerInteraction.Collide);
                for (int c = 0; c < count; c++) TryHit(_overlap[c], _overlap[c].ClosestPoint(p));

                // Swept path since last frame (fast swings).
                if (_havePrev)
                {
                    Vector3 seg = p - _prev[i];
                    float len = seg.magnitude;
                    if (len > 0.01f)
                    {
                        foreach (RaycastHit h in Physics.SphereCastAll(_prev[i], hitRadius, seg / len, len, ~0, QueryTriggerInteraction.Collide))
                            TryHit(h.collider, h.point == Vector3.zero ? p : h.point);
                    }
                }
                _prev[i] = p;
            }
            _havePrev = true;
        }

        void TryHit(Collider col, Vector3 point)
        {
            if (col == null) return;
            EnemyHealth hp = col.GetComponentInParent<EnemyHealth>();
            if (hp == null || hp.IsDead) return;
            if (_lastHit.TryGetValue(hp, out float last) && Time.time - last < hitCooldown) return;
            _lastHit[hp] = Time.time;
            hp.TakeDamage(damage, point);
            Spark(point);
        }

        void Spark(Vector3 point)
        {
            GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = "BeamSaberHitSpark";
            Collider c = s.GetComponent<Collider>();
            if (c != null) Destroy(c);
            if (sparkMaterial != null) s.GetComponent<Renderer>().sharedMaterial = sparkMaterial;
            s.transform.position = point;
            s.transform.localScale = Vector3.one * sparkSize;
            Destroy(s, 0.12f);
        }
    }
}
