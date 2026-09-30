using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// One beam rifle bolt - spawned by BeamRifleController. A glowing elongated
    /// bolt flying straight along 'direction'. Each frame the path it travelled is
    /// sphere-cast, so it can't tunnel through a target at high speed. It only
    /// reacts to things that can take a hit (EnemyHealth, or the old HitTarget
    /// practice targets) and passes through everything else (cockpit parts, the
    /// pilot's own Gundam, debris). On a hit: damage, a short flash, gone.
    ///
    /// WALLS (per "총알이 건물을 뚫으면 안됨"): the colony's buildings, land, hull
    /// and caps (ColonyStructure.Raycast) stop the bolt with a flash.
    /// </summary>
    public class BeamRifleShot : MonoBehaviour
    {
        public Vector3 direction = Vector3.forward;
        public float speed = 350f;
        public int damage = 500;
        public float hitRadius = 1.0f;
        public float lifetime = 4f;
        public Material flashMaterial;
        public float flashSize = 6f;
        [Tooltip("This enemy can't be hit by this bolt (an unlocked shot sent past it).")]
        public EnemyHealth ignore;

        float _age;
        readonly RaycastHit[] _hits = new RaycastHit[16];

        void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_age > lifetime) { Destroy(gameObject); return; }

            Vector3 from = transform.position;
            float dist = speed * dt;
            ColonyStructure colony = ColonyStructure.Active;
            Vector3 wallPoint = default;
            bool wall = colony != null && colony.Raycast(from, from + direction * dist, out wallPoint);
            if (wall) dist = Vector3.Distance(from, wallPoint);
            int n = Physics.SphereCastNonAlloc(from, hitRadius, direction, _hits, dist, ~0, QueryTriggerInteraction.Collide);
            float best = float.MaxValue;
            int bestI = -1;
            for (int i = 0; i < n; i++)
            {
                Collider c = _hits[i].collider;
                if (c == null) continue;
                EnemyHealth hp = c.GetComponentInParent<EnemyHealth>();
                if (hp != null && hp == ignore) continue;
                bool hittable = (hp != null && !hp.IsDead) || c.GetComponentInParent<HitTarget>() != null;
                if (!hittable) continue;
                if (_hits[i].distance < best) { best = _hits[i].distance; bestI = i; }
            }
            if (bestI >= 0)
            {
                RaycastHit h = _hits[bestI];
                Vector3 p = h.point == Vector3.zero ? from + direction * h.distance : h.point;
                EnemyHealth hp = h.collider.GetComponentInParent<EnemyHealth>();
                if (hp != null) hp.TakeDamage(damage, p);
                else
                {
                    HitTarget ht = h.collider.GetComponentInParent<HitTarget>();
                    if (ht != null) ht.OnHit();
                }
                Flash(p);
                Destroy(gameObject);
                return;
            }
            if (wall)
            {
                Flash(wallPoint);
                Destroy(gameObject);
                return;
            }
            transform.position = from + direction * dist;
        }

        void Flash(Vector3 p)
        {
            GameObject f = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            f.name = "BeamRifleHitFlash";
            Collider col = f.GetComponent<Collider>();
            if (col != null) Destroy(col);
            if (flashMaterial != null) f.GetComponent<Renderer>().sharedMaterial = flashMaterial;
            f.transform.position = p;
            f.transform.localScale = Vector3.one * flashSize;
            Destroy(f, 0.15f);
        }
    }
}
