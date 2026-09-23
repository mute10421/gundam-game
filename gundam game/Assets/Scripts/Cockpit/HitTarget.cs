using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>Simple target that hides itself on hit and respawns after a delay.</summary>
    public class HitTarget : MonoBehaviour
    {
        public float respawnDelay = 2f;

        Renderer _renderer;
        Collider _collider;

        void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _collider = GetComponent<Collider>();
        }

        public void OnHit()
        {
            if (_renderer != null) _renderer.enabled = false;
            if (_collider != null) _collider.enabled = false;
            CancelInvoke(nameof(Respawn));
            Invoke(nameof(Respawn), respawnDelay);
        }

        void Respawn()
        {
            if (_renderer != null) _renderer.enabled = true;
            if (_collider != null) _collider.enabled = true;
        }
    }
}
