using UnityEngine;
using UnityEngine.UI;

namespace Gundam.Cockpit
{
    /// <summary>
    /// LeftDisplay (real GameObject name: "SysCheck_Left") - GUNDAM STATUS
    /// screen. Per request, replaces the head-cam feed that used to live on
    /// this screen (moved to a small inset on FrontDisplay instead - see
    /// GundamCockpitSetup.cs's BuildSystemCheckDisplay) with 9 gauges: HP,
    /// SHIELD, ENERGY, then HEAD/BODY/LEFT ARM/RIGHT ARM/LEFT LEG/RIGHT LEG.
    ///
    /// Purely a renderer - every number it shows comes from CockpitHUDManager's
    /// shared GundamVitals (see that class for the placeholder-until-a-real-
    /// damage-system design). This script never touches gameplay, movement,
    /// hand tracking or weapon logic.
    ///
    /// GaugeRefs (fill Image + label Text per row) are created and wired by
    /// GundamCockpitSetup.cs's BuildStatusScreenUI - this class only reads
    /// them every frame, it does not build any UI itself.
    /// </summary>
    public class CockpitStatusHUD : MonoBehaviour
    {
        [System.Serializable]
        public class GaugeRefs
        {
            public Image fill;
            public Text text;
        }

        public CockpitHUDManager hudManager;

        [Header("Overall vitals")]
        public GaugeRefs hpGauge;
        public GaugeRefs shieldGauge;
        public GaugeRefs energyGauge;

        [Header("Per-part health (placeholder = mirrors overall HP until a real damage system calls GundamVitals.SetPartFraction)")]
        public GaugeRefs headGauge;
        public GaugeRefs bodyGauge;
        public GaugeRefs leftArmGauge;
        public GaugeRefs rightArmGauge;
        public GaugeRefs leftLegGauge;
        public GaugeRefs rightLegGauge;

        void Update()
        {
            if (hudManager == null) return;
            GundamVitals v = hudManager.Vitals;

            SetGauge(hpGauge, "HP", v.HPFraction);
            SetGauge(shieldGauge, "SHIELD", v.ShieldFraction);
            SetGauge(energyGauge, "ENERGY", v.EnergyFraction);

            SetGauge(headGauge, "HEAD", v.PartFraction(GundamVitals.BodyPart.Head));
            SetGauge(bodyGauge, "BODY", v.PartFraction(GundamVitals.BodyPart.Body));
            SetGauge(leftArmGauge, "L ARM", v.PartFraction(GundamVitals.BodyPart.LeftArm));
            SetGauge(rightArmGauge, "R ARM", v.PartFraction(GundamVitals.BodyPart.RightArm));
            SetGauge(leftLegGauge, "L LEG", v.PartFraction(GundamVitals.BodyPart.LeftLeg));
            SetGauge(rightLegGauge, "R LEG", v.PartFraction(GundamVitals.BodyPart.RightLeg));
        }

        static void SetGauge(GaugeRefs g, string label, float frac01)
        {
            if (g == null) return;
            frac01 = Mathf.Clamp01(frac01);
            Color c = GundamVitals.StatusColor(frac01);
            if (g.fill != null) { g.fill.fillAmount = frac01; g.fill.color = c; }
            if (g.text != null) { g.text.text = $"{label} {Mathf.RoundToInt(frac01 * 100f)}%"; g.text.color = Color.white; }
        }
    }
}
