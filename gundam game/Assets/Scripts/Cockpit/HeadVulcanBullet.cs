using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Hit detection for one Head Vulcan tracer - per request ("해드 발칸에
    /// 데미지는 1이야"). Added to every bullet by HeadVulcanController.SpawnBullet.
    ///
    /// The tracer has no collider of its own (so it can't shove the Gundam or
    /// block itself), so instead each frame it sweeps a ray over the distance its
    /// tip travelled since the last frame - no tunnelling even at 80 m/s. Only
    /// things that can actually take a hit stop it: an EnemyHealth (takes
    /// `damage`) or a practice HitTarget (OnHit). Every other collider (cockpit
    /// interior, controls, ...) is ignored and the tracer flies through.
    ///
    /// WALLS (per "총알이 건물을 뚫으면 안됨"): the colony's buildings, land, hull
    /// and caps (ColonyStructure.Raycast) stop the tracer where it hits them.
    /// </summary>
    public class HeadVulcanBullet : MonoBehaviour
    {
        public int damage = 1;
        [Tooltip("Distance from this object's pivot to the tracer's front tip, along its flight direction.")]
        public float tipOffset = 2f;
        [Tooltip("Flight direction (world). Set by HeadVulcanController.")]
        public Vector3 direction = Vector3.forward;
        [Tooltip("Emissive material for the small impact spark.")]
        public Material hitEffectMaterial;
        public float hitSparkSize = 1.6f;
        public float hitSparkDuration = 0.08f;

        Vector3 _lastTip;
        bool _started;
        bool _done;

        /// <summary>Where the sweep starts on the first frame (normally the muzzle).</summary>
        public void Begin(Vector3 startPoint)
        {
            _lastTip = startPoint;
            _started = true;
        }

        void Update()
        {
            if (_done) return;
            Vector3 tip = transform.position + direction * tipOffset;
            if (!_started)
            {
                _lastTip = tip;
                _started = true;
                return;
            }

            Vector3 seg = tip - _lastTip;
            float len = seg.magnitude;
            ColonyStructure colony = ColonyStructure.Active;
            Vector3 wallPoint = default;
            bool wall = colony != null && len > 0.0001f && colony.Raycast(_lastTip, tip, out wallPoint);
            float reach = wall ? Vector3.Distance(_lastTip, wallPoint) : len;
            if (len > 0.0001f)
            {
                RaycastHit[] hits = Physics.RaycastAll(_lastTip, seg / len, reach, ~0, QueryTriggerInteraction.Collide);
                if (hits.Length > 1) System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (RaycastHit h in hits)
                {
                    if (TryHit(h)) return;
                }
            }
            if (wall) { Impact(wallPoint); return; }
            _lastTip = tip;
        }

        bool TryHit(RaycastHit h)
        {
            EnemyHealth hp = h.collider.GetComponentInParent<EnemyHealth>();
            if (hp != null)
            {
                if (hp.IsDead) return false;
                hp.TakeDamage(damage, h.point);
                Impact(h.point);
                return true;
            }
            HitTarget ht = h.collider.GetComponentInParent<HitTarget>();
            if (ht != null)
            {
                ht.OnHit();
                Impact(h.point);
                return true;
            }
            return false;
        }

        void Impact(Vector3 point)
        {
            _done = true;
            if (hitSparkSize > 0f)
            {
                GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                spark.name = "HeadVulcanHitSpark";
                Collider col = spark.GetComponent<Collider>();
                if (col != null) Destroy(col);
                if (hitEffectMaterial != null) spark.GetComponent<Renderer>().sharedMaterial = hitEffectMaterial;
                spark.transform.position = point;
                spark.transform.localScale = Vector3.one * hitSparkSize;
                Destroy(spark, hitSparkDuration);
            }
            Destroy(gameObject);
        }
    }
}
