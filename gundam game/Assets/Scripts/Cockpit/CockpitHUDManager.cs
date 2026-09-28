using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Shared Gundam vitals data (HP/SHIELD/ENERGY + per-part health), read by
    /// the LeftDisplay STATUS HUD (CockpitStatusHUD) and the FrontDisplay
    /// tactical HUD's NORMAL/WARNING/CRITICAL badge (CockpitHUD).
    ///
    /// Per request ("아직 실제 부위별 데미지 시스템이 없으면 우선 전체 HP/상태를
    /// 기반으로 동작하는 구조로 만들고, 나중에 실제 데미지 시스템과 연결할 수
    /// 있도록 분리해줘"): this is a plain (non-MonoBehaviour) data class, kept
    /// completely separate from the HUD rendering scripts that read it. Right
    /// now every body part's gauge just mirrors the overall HP fraction
    /// (PartFraction falls back to HPFraction whenever a part has no explicit
    /// override), so the HUD is fully functional with zero real damage system
    /// wired up. When a real per-part damage system exists, it plugs in with
    /// no HUD changes at all: it just calls SetPartFraction(part, health01) to
    /// override one part's gauge independently (or ApplyDamage/Heal for
    /// overall HP) - the HUD scripts never need to know the difference.
    /// </summary>
    [System.Serializable]
    public class GundamVitals
    {
        public enum BodyPart { Head, Body, LeftArm, RightArm, LeftLeg, RightLeg }
        public enum Status { Normal, Warning, Critical }

        [Header("Overall vitals (placeholder values until a real damage system exists)")]
        public float MaxHP = 1000f;
        public float HP = 1000f;
        public float MaxShield = 500f;
        public float Shield = 500f;
        public float MaxEnergy = 100f;
        public float Energy = 100f;

        [Header("NORMAL / WARNING / CRITICAL thresholds (fraction of HP)")]
        [Range(0f, 1f)] public float warningThreshold = 0.6f;
        [Range(0f, 1f)] public float criticalThreshold = 0.3f;

        // -1 for a part means "no override yet" - PartFraction falls back to
        // the overall HP fraction. A future per-part damage system calls
        // SetPartFraction to start driving one part independently.
        readonly float[] _partOverride = { -1f, -1f, -1f, -1f, -1f, -1f };

        public float HPFraction => MaxHP > 0f ? Mathf.Clamp01(HP / MaxHP) : 0f;
        public float ShieldFraction => MaxShield > 0f ? Mathf.Clamp01(Shield / MaxShield) : 0f;
        public float EnergyFraction => MaxEnergy > 0f ? Mathf.Clamp01(Energy / MaxEnergy) : 0f;

        /// <summary>0..1 health fraction for one body part - the real per-part
        /// value once a damage system has called SetPartFraction for it, or the
        /// overall HP fraction as a placeholder until then.</summary>
        public float PartFraction(BodyPart part)
        {
            float o = _partOverride[(int)part];
            return o >= 0f ? o : HPFraction;
        }

        /// <summary>Future damage-system entry point: overrides one part's gauge
        /// independently of overall HP. Pass a negative value to clear the
        /// override and go back to mirroring overall HP for that part.</summary>
        public void SetPartFraction(BodyPart part, float frac01)
        {
            _partOverride[(int)part] = frac01 < 0f ? -1f : Mathf.Clamp01(frac01);
        }

        public void ApplyDamage(float amount) => HP = Mathf.Clamp(HP - amount, 0f, MaxHP);
        public void Heal(float amount) => HP = Mathf.Clamp(HP + amount, 0f, MaxHP);

        public Status OverallStatus
        {
            get
            {
                float f = HPFraction;
                if (f <= criticalThreshold) return Status.Critical;
                if (f <= warningThreshold) return Status.Warning;
                return Status.Normal;
            }
        }

        /// <summary>Shared green/yellow/red color for any 0..1 gauge fraction -
        /// used by every gauge on both the STATUS and WEAPON displays so the
        /// whole cockpit HUD reads consistently.</summary>
        public static Color StatusColor(float frac01)
        {
            if (frac01 <= 0.3f) return new Color(1f, 0.25f, 0.2f);
            if (frac01 <= 0.6f) return new Color(1f, 0.8f, 0.2f);
            return new Color(0.3f, 1f, 0.4f);
        }
    }

    /// <summary>
    /// Cockpit HUD coordinator - the single shared owner of GundamVitals, so
    /// FrontDisplay's status badge (CockpitHUD) and LeftDisplay's gauges
    /// (CockpitStatusHUD) always read the exact same numbers. Added onto
    /// MobileSuitRoot alongside ShipMovementController/CockpitHUD/
    /// CockpitStatusHUD/CockpitWeaponHUD/HeadVulcanController by
    /// GundamCockpitSetup.cs. Holds no UI references itself - purely data plus
    /// wiring, per the requested CockpitHUDManager/CockpitStatusHUD/
    /// CockpitWeaponHUD file split.
    /// </summary>
    public class CockpitHUDManager : MonoBehaviour
    {
        public GundamVitals Vitals = new GundamVitals();
    }
}
