using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Marks a GameObject as an enemy mobile suit (currently just the
    /// placed Zaku) - per request ("자쿠를 적으로 배치해줘": place it AS an
    /// enemy, not just as a prop). No AI, aiming, health, or combat
    /// behavior yet - that is deliberately separate follow-up work; this
    /// only exists so enemies are identifiable/taggable in code (and in
    /// the Editor hierarchy) from the moment they're placed.
    ///
    /// Lives in its own script file (not GundamCockpitSetup.cs) so real
    /// enemy behavior can be added here later without touching, or being
    /// overwritten by, the scene generator.
    /// </summary>
    public class EnemyMarker : MonoBehaviour
    {
    }
}
