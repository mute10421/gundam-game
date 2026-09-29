using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// One Zaku machine gun round (spawned by ZakuMachineGun). Flies straight;
    /// each frame the path it covered is tested against the player's Gundam body
    /// capsule (PlayerHealth.CapsuleHit) - no physics collider involved, so it
    /// can't tunnel and never touches anything else. On a hit: damage + a spark.
    /// </summary>
    public class ZakuBullet : MonoBehaviour
    {
        public Vector3 direction = Vector3.forward;
        public float speed = 320f;
        public int damage = 50;
        public float radius = 0.4f;
        public float lifetime = 2.5f;
        public PlayerHealth target;
        public Material sparkMaterial;
        public float sparkSize = 2.5f;

        float _age;

        void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_age > lifetime) { Destroy(gameObject); return; }

            Vector3 from = transform.position;
            Vector3 to = from + direction * speed * dt;
            if (target != null && !target.IsDown && target.CapsuleHit(from, to, radius, out Vector3 p))
            {
                target.TakeDamage(damage, p);
                if (sparkMaterial != null)
                {
                    GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    s.name = "ZakuBulletHit";
                    Collider c = s.GetComponent<Collider>();
                    if (c != null) Destroy(c);
                    s.GetComponent<Renderer>().sharedMaterial = sparkMaterial;
                    s.transform.position = p;
                    s.transform.localScale = Vector3.one * sparkSize;
                    Destroy(s, 0.07f);
                }
                Destroy(gameObject);
                return;
            }
            transform.position = to;
        }
    }
}
