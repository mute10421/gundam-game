using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Wind / rush sound while the Gundam moves - per request ("건담으로 움직일때
    /// 이소리가 나오게해줘"). Silent while standing still, fades in as the suit
    /// picks up speed (louder and slightly higher-pitched the faster it goes) and
    /// fades out when it stops.
    ///
    /// Speed is measured from how far 'target' (MobileSuitRoot) actually moved each
    /// frame, so it covers every kind of movement - LeftJoystick, the T-lever climb
    /// - without reading or changing any of the movement scripts. A single jump
    /// faster than teleportSpeed (the sortie-point teleport) is ignored.
    ///
    /// The clip is one wind gust (swells up and dies away), not a loop, so a
    /// continuous wind is made from its middle part (segment) played by two
    /// voices half a segment apart, each faded in and out with a sine envelope -
    /// the two envelopes always sum to constant power, so there's no gap or click.
    /// 2D sound: the cockpit (and its AudioListener) is far from the Gundam.
    /// </summary>
    public class GundamMoveSound : MonoBehaviour
    {
        [Tooltip("What moves (MobileSuitRoot). Defaults to this object.")]
        public Transform target;
        public AudioClip windSound;

        [Header("Loudness")]
        [Range(0f, 1f)] public float maxVolume = 0.7f;
        [Tooltip("Below this speed (m/s) it's silent.")]
        public float minSpeed = 1.5f;
        [Tooltip("Full volume at this speed (m/s) - the suit's top speed.")]
        public float fullSpeed = 40f;
        [Tooltip("Pitch at minSpeed and at fullSpeed.")]
        public Vector2 pitchRange = new Vector2(0.85f, 1.15f);
        [Tooltip("Seconds to fade in when speeding up / fade out when slowing down.")]
        public float attackTime = 0.35f;
        public float releaseTime = 0.9f;
        [Tooltip("Faster than this (m/s) in one frame = a teleport, not movement.")]
        public float teleportSpeed = 600f;

        [Header("Loop from the gust")]
        [Tooltip("Part of the clip (s) used for the continuous wind.")]
        public Vector2 segment = new Vector2(1.0f, 4.0f);

        AudioSource[] _voices;
        Vector3 _lastPos;
        bool _haveLast;
        float _speed;
        float _level;
        bool _running;

        void Start()
        {
            if (target == null) target = transform;
            if (windSound == null) { enabled = false; return; }
            _voices = new AudioSource[2];
            for (int i = 0; i < 2; i++)
            {
                AudioSource a = gameObject.AddComponent<AudioSource>();
                a.clip = windSound;
                a.playOnAwake = false;
                a.loop = false;
                a.spatialBlend = 0f;
                a.volume = 0f;
                _voices[i] = a;
            }
            segment.x = Mathf.Clamp(segment.x, 0f, windSound.length - 0.2f);
            segment.y = Mathf.Clamp(segment.y, segment.x + 0.2f, windSound.length);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 1e-5f || _voices == null) return;

            // Measured speed (smoothed a little so frame jitter doesn't flutter it).
            Vector3 p = target.position;
            if (_haveLast)
            {
                float s = (p - _lastPos).magnitude / dt;
                if (s < teleportSpeed) _speed = Mathf.Lerp(_speed, s, 1f - Mathf.Exp(-dt * 8f));
            }
            _lastPos = p;
            _haveLast = true;

            float k = Mathf.InverseLerp(minSpeed, fullSpeed, _speed);
            float want = k > 0f ? Mathf.Lerp(0.25f, 1f, Mathf.SmoothStep(0f, 1f, k)) : 0f;
            float rate = want > _level ? 1f / Mathf.Max(0.01f, attackTime) : 1f / Mathf.Max(0.01f, releaseTime);
            _level = Mathf.MoveTowards(_level, want, rate * dt);

            if (_level <= 0.001f)
            {
                if (_running)
                {
                    for (int i = 0; i < 2; i++) _voices[i].Stop();
                    _running = false;
                }
                return;
            }

            float len = segment.y - segment.x;
            float half = len * 0.5f;
            if (!_running)
            {
                // Start: voice 0 from the segment start, voice 1 already halfway.
                _voices[0].time = segment.x;
                _voices[1].time = segment.x + half;
                _voices[0].Play();
                _voices[1].Play();
                _running = true;
            }

            // A voice that reached the segment's end (its envelope is at zero
            // there) starts over - the two stay half a segment apart.
            for (int i = 0; i < 2; i++)
            {
                AudioSource v = _voices[i];
                if (!v.isPlaying || v.time >= segment.y)
                {
                    v.Stop();
                    v.time = segment.x;
                    v.Play();
                }
            }

            float pitch = Mathf.Lerp(pitchRange.x, pitchRange.y, k);
            for (int i = 0; i < 2; i++)
            {
                AudioSource v = _voices[i];
                v.pitch = pitch;
                // Position of this voice within the segment (0..1) -> sine envelope.
                float u = Mathf.Clamp01((v.time - segment.x) / len);
                v.volume = maxVolume * _level * Mathf.Sin(u * Mathf.PI);
                if (v.time >= segment.y) v.volume = 0f;
            }
        }

        void OnDisable()
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i] != null) _voices[i].Stop();
            _running = false;
            _level = 0f;
        }
    }
}
