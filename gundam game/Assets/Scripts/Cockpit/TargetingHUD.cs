using UnityEngine;
using System.Collections.Generic;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Scans for HitTarget enemies ahead of the ship's current aim and shows a
    /// marker on the OrbitHUD ring's plane for each one that's visible - a
    /// diamond for a detected contact, swapped to a brighter "locked" look
    /// (diamond color + a small ring, both shrunk down) for whichever visible
    /// contact is closest to dead-ahead. Per request, with a reference
    /// screenshot of a Gundam cockpit HUD locking onto an enemy: "저 링이
    /// 이런식으로 적을 보면 타겟팅이 되야함".
    ///
    /// Aim reference is the gun's aim pivot (driven by the RIGHT joystick via
    /// WeaponAimFireController), not a fixed ship-forward direction, per
    /// request: "내가 오른손 손잡이로 조종할수있어야해" - moving the right stick
    /// to aim also sweeps this targeting reticle, and whichever contact ends
    /// up under it shrinks and locks. The marker's size continuously shrinks
    /// as it approaches dead-center (from aimShrinkAngleDeg down to
    /// lockAngleDeg), per request: "조준이 되고 락온이 되는 과정에서 사이즈가
    /// 줄어들어야해".
    ///
    /// This script only toggles/repositions/rescales the marker pool that
    /// GundamCockpitSetup already built (markers/markerRenderers/lockRings) -
    /// it does not create or destroy any GameObjects itself, consistent with
    /// the rest of this project's split between the generator (owns all scene
    /// geometry) and runtime scripts (only move/toggle what already exists).
    /// Purely a visual sensor overlay: it does not fire the weapon itself.
    /// </summary>
    public class TargetingHUD : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The direction/position the targeting reticle points from - the gun's aim pivot (WeaponAimFireController.gunPivot), which the right joystick already steers, so this HUD sweeps with it.")]
        public Transform aimPivot;
        [Tooltip("The OrbitHUD ring's transform. Markers are placed in its local space, on the same plane as the ring.")]
        public Transform hudRoot;

        [Header("Marker pool (built once by GundamCockpitSetup - this script only toggles/moves/rescales these)")]
        public Transform[] markers;
        public Renderer[] markerRenderers;
        public Transform[] lockRings;
        public Material markerMat;
        public Material lockedMat;

        [Header("Detection")]
        [Tooltip("Must match the OrbitHUD ring's own radius so markers clamp to its edge.")]
        public float ringRadius = 1.5f;
        public float detectionRange = 60f;
        [Range(10f, 150f)] public float fieldOfViewDeg = 90f;
        [Tooltip("How close to dead-ahead (degrees) a contact must be to become the single locked target.")]
        [Range(1f, 30f)] public float lockAngleDeg = 6f;
        public float rescanInterval = 0.5f;

        [Header("Aim/lock shrink")]
        [Tooltip("Angle (degrees) at which the aimed-at marker starts shrinking as it approaches dead-center. Should be noticeably wider than lockAngleDeg so the shrink reads as a gradual approach, not a snap.")]
        public float aimShrinkAngleDeg = 24f;
        [Tooltip("Marker/lock-ring scale once fully locked (1 = original size).")]
        [Range(0.1f, 1f)] public float lockedMarkerScale = 0.45f;

        readonly List<HitTarget> _targets = new List<HitTarget>();
        float _rescanTimer;

        void Start()
        {
            RescanTargets();
        }

        void Update()
        {
            if (aimPivot == null || hudRoot == null || markers == null) return;

            _rescanTimer -= Time.deltaTime;
            if (_rescanTimer <= 0f)
            {
                RescanTargets();
                _rescanTimer = rescanInterval;
            }

            float centerZ = hudRoot.localPosition.z;
            int slot = 0;
            float bestAngle = float.MaxValue;
            int bestSlot = -1;

            for (int i = 0; i < _targets.Count && slot < markers.Length; i++)
            {
                HitTarget t = _targets[i];
                if (t == null || !t.isActiveAndEnabled) continue;

                Renderer targetRenderer = t.GetComponent<Renderer>();
                if (targetRenderer != null && !targetRenderer.enabled) continue; // hidden (just hit, respawning)

                Vector3 toTarget = t.transform.position - aimPivot.position;
                float dist = toTarget.magnitude;
                if (dist < 0.01f || dist > detectionRange) continue;

                Vector3 localDir = aimPivot.InverseTransformDirection(toTarget.normalized);
                if (localDir.z <= 0.05f) continue; // behind the aim direction

                float angle = Vector3.Angle(Vector3.forward, localDir);
                if (angle > fieldOfViewDeg * 0.5f) continue;

                // Simple perspective projection of the target's direction onto
                // the HUD ring's plane, clamped to the ring's own radius so an
                // edge-of-view contact reads as "near the ring's edge" rather
                // than flying off past it.
                float scale = centerZ / localDir.z;
                Vector2 p = new Vector2(localDir.x * scale, localDir.y * scale);
                if (p.magnitude > ringRadius) p = p.normalized * ringRadius;

                Transform marker = markers[slot];
                if (marker == null) continue;
                marker.gameObject.SetActive(true);
                marker.localPosition = new Vector3(p.x, p.y, 0f);
                marker.localScale = Vector3.one; // reset - only the aimed-at (best) marker shrinks, applied below

                if (markerRenderers != null && slot < markerRenderers.Length && markerRenderers[slot] != null)
                {
                    markerRenderers[slot].sharedMaterial = markerMat;
                }
                if (lockRings != null && slot < lockRings.Length && lockRings[slot] != null)
                {
                    lockRings[slot].gameObject.SetActive(false);
                }

                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    bestSlot = slot;
                }

                slot++;
            }

            for (int i = slot; i < markers.Length; i++)
            {
                if (markers[i] != null) markers[i].gameObject.SetActive(false);
                if (lockRings != null && i < lockRings.Length && lockRings[i] != null) lockRings[i].gameObject.SetActive(false);
            }

            // Only the single most-centered contact gets the shrink + the
            // brighter "locked" material/ring once inside lockAngleDeg,
            // applied after the loop so it's decided from ALL visible
            // contacts, not just the first one that happened to be close
            // enough. The shrink itself is continuous from aimShrinkAngleDeg
            // (full size) down to lockAngleDeg (fully shrunk), so it reads as
            // the reticle closing in while aiming, not a sudden snap.
            if (bestSlot >= 0 && bestAngle <= aimShrinkAngleDeg)
            {
                float shrinkT = Mathf.InverseLerp(aimShrinkAngleDeg, lockAngleDeg, bestAngle);
                float scale = Mathf.Lerp(1f, lockedMarkerScale, Mathf.Clamp01(shrinkT));

                if (markers[bestSlot] != null)
                {
                    markers[bestSlot].localScale = Vector3.one * scale;
                }

                bool locked = bestAngle <= lockAngleDeg;
                if (markerRenderers != null && bestSlot < markerRenderers.Length && markerRenderers[bestSlot] != null)
                {
                    markerRenderers[bestSlot].sharedMaterial = locked ? lockedMat : markerMat;
                }
                if (lockRings != null && bestSlot < lockRings.Length && lockRings[bestSlot] != null)
                {
                    lockRings[bestSlot].gameObject.SetActive(locked);
                    if (locked)
                    {
                        lockRings[bestSlot].localPosition = markers[bestSlot].localPosition;
                        lockRings[bestSlot].localScale = Vector3.one * scale;
                    }
                }
            }
        }

        void RescanTargets()
        {
            _targets.Clear();
            _targets.AddRange(FindObjectsOfType<HitTarget>());
        }
    }
}
