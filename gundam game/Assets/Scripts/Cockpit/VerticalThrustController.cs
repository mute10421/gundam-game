using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Rise / descend from the T-shaped vertical lever - per request ("레버를
    /// 앞으로 올리면 건담이 위로 날아야 하는데 ... 아래로는 내려와야하고").
    ///
    /// Sits on MobileSuitRoot next to ShipMovementController and only ADDS a
    /// vertical offset to the same transform - ShipMovementController (forward /
    /// back / sideways, planar only, it zeroes its own Y) is not modified and the
    /// two simply add up. ExternalGundam (and the HeadCam inside it) already
    /// follows MobileSuitRoot's full position through ExternalGundamFollower, so
    /// the exterior view rises and sinks with it.
    ///
    ///   lever pushed forward  (VerticalInput +1) -> climbs at up to maxClimbSpeed
    ///   lever pulled back     (VerticalInput -1) -> descends at up to maxClimbSpeed
    ///   lever centered / released (0)            -> eases to a stop (no drift)
    /// </summary>
    public class VerticalThrustController : MonoBehaviour
    {
        [Tooltip("The T-shaped vertical lever (VerticalTLever).")]
        public VerticalTLever lever;
        [Tooltip("Top climb / descent speed (m/s) at full lever travel.")]
        public float maxClimbSpeed = 8f;
        [Tooltip("How fast (m/s per second) the vertical speed changes - smooths starts and stops.")]
        public float acceleration = 12f;
        [Tooltip("Optional altitude limits relative to the start height (m). Leave min >= max for no limit.")]
        public float minAltitude = 0f;
        public float maxAltitude = 0f;

        /// <summary>Current vertical speed (m/s), + = climbing.</summary>
        public float CurrentVerticalSpeed { get; private set; }
        /// <summary>Height above the starting position (m).</summary>
        public float Altitude => transform.position.y - _startY;

        float _startY;

        void Awake()
        {
            _startY = transform.position.y;
        }

        void Update()
        {
            float input = lever != null ? Mathf.Clamp(lever.VerticalInput, -1f, 1f) : 0f;
            float target = input * maxClimbSpeed;
            CurrentVerticalSpeed = Mathf.MoveTowards(CurrentVerticalSpeed, target, acceleration * Time.deltaTime);
            if (Mathf.Abs(CurrentVerticalSpeed) < 0.0001f) { CurrentVerticalSpeed = 0f; return; }

            Vector3 p = transform.position;
            p.y += CurrentVerticalSpeed * Time.deltaTime;
            if (maxAltitude > minAltitude)
            {
                float clamped = Mathf.Clamp(p.y, _startY + minAltitude, _startY + maxAltitude);
                if (!Mathf.Approximately(clamped, p.y)) CurrentVerticalSpeed = 0f;
                p.y = clamped;
            }
            transform.position = p;
        }
    }
}
