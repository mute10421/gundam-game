using System;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The player's Gundam hit points - per request ("건담에 체력은 3000이고").
    /// Sits on ExternalGundam (the body the Zakus shoot at). Its hit volume is a
    /// simple upright capsule over the body (CapsuleHit) that enemy bullets
    /// test against directly - no collider is added, so nothing about the
    /// player's own weapons, the XR grabs or the cockpit physics changes.
    ///
    /// Damage goes into the cockpit's shared GundamVitals (CockpitHUDManager), so
    /// the STATUS display's HP / part gauges and the front NORMAL / WARNING /
    /// CRITICAL badge show it with no HUD changes. At 0 HP the suit is knocked
    /// out for repairDelay seconds (no more damage taken), then fully repaired.
    /// </summary>
    public class PlayerHealth : MonoBehaviour
    {
        public int maxHealth = 3000;
        [Tooltip("The cockpit's shared vitals (HP gauges / status badge).")]
        public CockpitHUDManager hudManager;
        [Tooltip("Seconds knocked out at 0 HP before being fully repaired.")]
        public float repairDelay = 5f;

        [Header("Hit volume (m, around this transform, world up)")]
        public float capsuleBottom = 1f;
        public float capsuleTop = 17f;
        public float capsuleRadius = 3.2f;

        public int CurrentHealth { get; private set; }
        public bool IsDown { get; private set; }
        public float LastHitTime { get; private set; } = -999f;
        public float HealthFraction => maxHealth > 0 ? (float)CurrentHealth / maxHealth : 0f;

        public event Action<PlayerHealth, int, Vector3> Damaged;
        public event Action<PlayerHealth> Downed;
        public event Action<PlayerHealth> Repaired;

        float _repairAt;

        void Awake()
        {
            CurrentHealth = maxHealth;
            SyncVitals();
        }

        void Start()
        {
            SyncVitals();
        }

        void Update()
        {
            if (IsDown && Time.time >= _repairAt)
            {
                IsDown = false;
                CurrentHealth = maxHealth;
                SyncVitals();
                Repaired?.Invoke(this);
            }
        }

        public bool TakeDamage(int amount, Vector3 point)
        {
            if (IsDown || amount <= 0) return false;
            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            LastHitTime = Time.time;
            SyncVitals();
            Damaged?.Invoke(this, amount, point);
            if (CurrentHealth <= 0)
            {
                IsDown = true;
                _repairAt = Time.time + repairDelay;
                Downed?.Invoke(this);
                Debug.Log("[Gundam] Gundam HP 0 - knocked out, repairing in " + repairDelay + "s.");
            }
            return true;
        }

        void SyncVitals()
        {
            if (hudManager == null) return;
            hudManager.Vitals.MaxHP = maxHealth;
            hudManager.Vitals.HP = CurrentHealth;
        }

        /// <summary>Segment a->b (swept with 'radius') against the body capsule.
        /// Returns the closest point on the body axis when it hits.</summary>
        public bool CapsuleHit(Vector3 a, Vector3 b, float radius, out Vector3 point)
        {
            Vector3 p0 = transform.position + Vector3.up * capsuleBottom;
            Vector3 p1 = transform.position + Vector3.up * capsuleTop;
            ClosestPoints(a, b, p0, p1, out Vector3 onSeg, out Vector3 onAxis);
            point = onSeg;
            return (onSeg - onAxis).sqrMagnitude <= (capsuleRadius + radius) * (capsuleRadius + radius);
        }

        static void ClosestPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-6f && e <= 1e-6f) { c1 = p1; c2 = p2; return; }
            if (a <= 1e-6f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-6f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
    }
}
