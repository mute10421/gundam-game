using System.Collections.Generic;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Keeps the player's Gundam from passing through enemy mobile suits - per
    /// request ("건담이 상대를 통과하지않게 충돌을 넣어").
    ///
    /// Sits on MobileSuitRoot (the thing ShipMovementController / the T lever
    /// move). Every frame, after the movement scripts and BEFORE
    /// ExternalGundamFollower carries the Gundam body along, it checks the
    /// Gundam's body (an upright capsule, bodyRadius x bodyHeight around where
    /// the body stands) against every living enemy's CapsuleCollider hitbox; where
    /// they overlap it pushes MobileSuitRoot straight back out sideways. So you
    /// stop at the enemy and slide around it instead of flying through, with no
    /// physics bodies involved and nothing about the movement code changed. Over
    /// or under an enemy (no height overlap) there's no contact.
    ///
    /// Also the colony battlefield (ColonyStructure, per "콜로니 ... 부딪히게"): the
    /// hull, the end caps, the city floor and every building push the body back
    /// the same way - the only way in or out is the open near end, and you can
    /// land on rooftops.
    ///
    /// GRAVITY inside the colony (per "콜로니 안에서는 중력이 있을거야"): once the
    /// body is inside, it falls (gravity m/s^2, up to maxFallSpeed) until it stands
    /// on the land or a roof. Pushing the T lever up fires the thrusters and
    /// cancels the fall (the lever's own climb then lifts you); let go and you
    /// drop again. Outside in space there is no gravity, as before.
    /// </summary>
    [DefaultExecutionOrder(-70)] // before ExternalGundamFollower (-60)
    public class SuitCollision : MonoBehaviour
    {
        [Tooltip("The Gundam body (ExternalGundam) - its position relative to this root is captured at start.")]
        public Transform body;
        public float bodyRadius = 3.5f;
        public float bodyHeight = 18f;
        [Tooltip("Extra gap (m) kept between the two suits.")]
        public float skin = 0.3f;
        public float rescanInterval = 1f;
        [Tooltip("Optional colony battlefield (hull / caps / floor / buildings).")]
        public ColonyStructure colony;
        [Tooltip("The T-lever vertical thrust - climbing cancels gravity.")]
        public VerticalThrustController thrust;
        public float gravity = 9.8f;
        public float maxFallSpeed = 70f;

        float _fallSpeed;
        public bool Grounded { get; private set; }
        public bool InColony { get; private set; }

        readonly List<CapsuleCollider> _enemies = new List<CapsuleCollider>();
        readonly List<EnemyHealth> _health = new List<EnemyHealth>();
        Vector3 _bodyOffset;
        bool _haveOffset;
        float _rescan;

        public bool Touching { get; private set; }

        void LateUpdate()
        {
            if (body == null) return;
            if (!_haveOffset)
            {
                _bodyOffset = body.position - transform.position;
                _haveOffset = true;
            }

            _rescan -= Time.deltaTime;
            if (_rescan <= 0f)
            {
                _rescan = rescanInterval;
                _enemies.Clear();
                _health.Clear();
                foreach (EnemyHealth e in FindObjectsByType<EnemyHealth>(FindObjectsInactive.Exclude))
                {
                    CapsuleCollider c = e.GetComponent<CapsuleCollider>();
                    if (c == null) continue;
                    _enemies.Add(c);
                    _health.Add(e);
                }
            }

            Touching = false;
            Grounded = false;
            if (colony != null)
            {
                Vector3 feet0 = transform.position + _bodyOffset;
                InColony = colony.IsInside(feet0 + Vector3.up * (bodyHeight * 0.5f));
                if (InColony)
                {
                    float dt = Time.deltaTime;
                    bool climbing = thrust != null && thrust.CurrentVerticalSpeed > 0.5f;
                    if (climbing) _fallSpeed = Mathf.MoveTowards(_fallSpeed, 0f, 40f * dt);
                    else _fallSpeed = Mathf.Max(_fallSpeed - gravity * dt, -maxFallSpeed);
                    transform.position += Vector3.up * (_fallSpeed * dt);
                }
                else _fallSpeed = 0f;

                Vector3 f0 = transform.position + _bodyOffset;
                Vector3 f1 = colony.ResolveBody(f0, bodyHeight, bodyRadius, out bool hitColony, out bool grounded);
                if (hitColony)
                {
                    transform.position += f1 - f0;
                    Touching = true;
                }
                if (grounded)
                {
                    Grounded = true;
                    if (_fallSpeed < 0f) _fallSpeed = 0f;
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 feet = transform.position + _bodyOffset;
                for (int i = 0; i < _enemies.Count; i++)
                {
                    CapsuleCollider c = _enemies[i];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    if (_health[i] != null && _health[i].IsDead) continue;

                    Transform t = c.transform;
                    Vector3 s = t.lossyScale;
                    Vector3 center = t.TransformPoint(c.center);
                    float half = c.height * 0.5f * Mathf.Abs(s.y);
                    float r = c.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));

                    // Height overlap?
                    if (feet.y + bodyHeight < center.y - half || feet.y > center.y + half) continue;

                    Vector3 d = feet - center;
                    d.y = 0f;
                    float dist = d.magnitude;
                    float min = bodyRadius + r + skin;
                    if (dist >= min) continue;

                    Vector3 dir = dist > 0.01f ? d / dist : -t.forward;
                    Vector3 push = dir * (min - dist);
                    transform.position += push;
                    feet += push;
                    Touching = true;
                }
            }
        }
    }
}
