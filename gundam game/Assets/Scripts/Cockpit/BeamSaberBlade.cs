using System.Collections.Generic;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Beam saber hits - per request ("빔샤벨은 데미지 일단은 200"). Sits on the
    /// BeamSaber root (which is only active in BEAM SABER mode); the blade runs
    /// along the root's +Y from bladeStart to bladeStart + bladeLength.
    ///
    /// Each frame the blade is checked as a thick line (radius hitRadius) AND swept
    /// from where it was last frame to where it is now, so a fast swing that passes
    /// clean through an enemy between two frames still connects. An enemy
    /// (EnemyHealth) takes `damage` once per contact, then can't be hit again for
    /// hitCooldown seconds - so holding the blade inside it doesn't drain 200 per
    /// frame; pull out and swing again to hit again. A short spark marks the hit.
    /// </summary>
    public class BeamSaberBlade : MonoBehaviour
    {
        public int damage = 200;
        [Tooltip("Where the beam starts along +Y (m, world scale) - the end of the hilt.")]
        public float bladeStart = 0.9f;
        public float bladeLength = 9f;
        [Tooltip("Hit thickness (m).")]
        public float hitRadius = 0.6f;
        [Tooltip("Seconds before the same enemy can be hit again.")]
        public float hitCooldown = 0.5f;
        [Tooltip("Points sampled along the blade.")]
        public int samples = 8;
        public Material sparkMaterial;
        public float sparkSize = 3f;

        [Header("Swing sound (per \"빔샤벨을 휘두를때만 나오게해줘\")")]
        [Tooltip("Played once per swing - only when the blade actually sweeps fast, never while it's just held out.")]
        public AudioClip swingSound;
        [Range(0f, 1f)] public float swingVolume = 0.9f;
        [Range(0f, 0.2f)] public float swingPitchJitter = 0.06f;
        [Tooltip("Blade-tip speed (blade lengths per second, relative to the Gundam's own body - so flying or turning doesn't count) that starts a swing sound.")]
        public float swingStartSpeed = 3.5f;
        [Tooltip("The tip must slow below this before the next swing can sound again.")]
        public float swingRearmSpeed = 1.2f;
        [Tooltip("Shortest time (s) between two swing sounds.")]
        public float swingMinInterval = 0.3f;
        [Tooltip("No swing sound this long (s) after the saber is drawn (the arm moving into its guard pose isn't a swing).")]
        public float drawQuietTime = 0.5f;
        [Tooltip("The clip holds two separate swooshes; each swing plays one of these (start, end in seconds), alternating. Empty = the whole clip.")]
        public Vector2[] swingSegments = { new Vector2(0f, 0.95f), new Vector2(1.6f, 2.95f) };
        [Tooltip("Fade-out (s) at the end of a segment, so it doesn't click.")]
        public float swingFadeOut = 0.06f;
        int _segIndex;
        float _segStopAt = -1f;
        float _segVolume;
        AudioSource _swingAudio;
        Vector3 _prevTipLocal;
        bool _haveTip;
        bool _swingArmed = true;
        float _lastSwingTime = -999f;
        float _enabledTime;

        Vector3[] _prev;
        bool _havePrev;
        readonly Dictionary<EnemyHealth, float> _lastHit = new Dictionary<EnemyHealth, float>();
        readonly Collider[] _overlap = new Collider[16];

        void OnEnable()
        {
            _havePrev = false;
            _haveTip = false;
            _swingArmed = true;
            _enabledTime = Time.time;
            if (swingSound != null && _swingAudio == null)
            {
                _swingAudio = gameObject.AddComponent<AudioSource>();
                _swingAudio.playOnAwake = false;
                _swingAudio.loop = false;
                _swingAudio.spatialBlend = 0f; // heard in the (far-away) cockpit
            }
        }

        /// <summary>Plays the swing sound on the rising edge of a fast blade sweep.
        /// The tip is measured in the Gundam root's own frame (position and
        /// rotation removed, scale kept out), so moving/turning the suit or a
        /// slow view turn never sounds - only the arm swinging the blade.</summary>
        void UpdateSwingSound(Vector3 tip)
        {
            if (_swingAudio == null || swingSound == null) return;
            Transform root = transform.root;
            Vector3 local = Quaternion.Inverse(root.rotation) * (tip - root.position);
            float dt = Time.deltaTime;
            if (!_haveTip || dt <= 1e-5f) { _prevTipLocal = local; _haveTip = true; return; }
            float speed = (local - _prevTipLocal).magnitude / dt / Mathf.Max(0.1f, bladeStart + bladeLength);
            _prevTipLocal = local;

            if (speed < swingRearmSpeed) _swingArmed = true;
            if (!_swingArmed || speed < swingStartSpeed) return;
            if (Time.time - _enabledTime < drawQuietTime || Time.time - _lastSwingTime < swingMinInterval) return;
            _swingArmed = false;
            _lastSwingTime = Time.time;
            float loud = Mathf.Clamp(speed / (swingStartSpeed * 2f), 0.6f, 1f);
            float pitch = 1f + Random.Range(-swingPitchJitter, swingPitchJitter);
            Vector2 seg = new Vector2(0f, swingSound.length);
            if (swingSegments != null && swingSegments.Length > 0)
            {
                seg = swingSegments[_segIndex % swingSegments.Length];
                _segIndex++;
            }
            seg.x = Mathf.Clamp(seg.x, 0f, swingSound.length - 0.01f);
            seg.y = Mathf.Clamp(seg.y, seg.x + 0.05f, swingSound.length);
            _swingAudio.Stop();
            _swingAudio.clip = swingSound;
            _swingAudio.pitch = pitch;
            _segVolume = swingVolume * loud;
            _swingAudio.volume = _segVolume;
            _swingAudio.time = seg.x;
            _swingAudio.Play();
            _segStopAt = Time.time + (seg.y - seg.x) / pitch;
        }

        void Update()
        {
            // End the current swoosh at its segment end, with a short fade.
            if (_swingAudio == null || _segStopAt < 0f || !_swingAudio.isPlaying) return;
            float left = _segStopAt - Time.time;
            if (left <= 0f) { _swingAudio.Stop(); _segStopAt = -1f; return; }
            if (left < swingFadeOut) _swingAudio.volume = _segVolume * (left / Mathf.Max(0.001f, swingFadeOut));
        }

        void LateUpdate()
        {
            int n = Mathf.Max(2, samples);
            if (_prev == null || _prev.Length != n) { _prev = new Vector3[n]; _havePrev = false; }

            Vector3 up = transform.up;
            Vector3 origin = transform.position;
            UpdateSwingSound(origin + up * (bladeStart + bladeLength));
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                Vector3 p = origin + up * (bladeStart + bladeLength * t);

                // Thick point at the blade's current position.
                int count = Physics.OverlapSphereNonAlloc(p, hitRadius, _overlap, ~0, QueryTriggerInteraction.Collide);
                for (int c = 0; c < count; c++) TryHit(_overlap[c], _overlap[c].ClosestPoint(p));

                // Swept path since last frame (fast swings).
                if (_havePrev)
                {
                    Vector3 seg = p - _prev[i];
                    float len = seg.magnitude;
                    if (len > 0.01f)
                    {
                        foreach (RaycastHit h in Physics.SphereCastAll(_prev[i], hitRadius, seg / len, len, ~0, QueryTriggerInteraction.Collide))
                            TryHit(h.collider, h.point == Vector3.zero ? p : h.point);
                    }
                }
                _prev[i] = p;
            }
            _havePrev = true;
        }

        void TryHit(Collider col, Vector3 point)
        {
            if (col == null) return;
            EnemyHealth hp = col.GetComponentInParent<EnemyHealth>();
            if (hp == null || hp.IsDead) return;
            if (_lastHit.TryGetValue(hp, out float last) && Time.time - last < hitCooldown) return;
            _lastHit[hp] = Time.time;
            hp.TakeDamage(damage, point);
            Spark(point);
        }

        void Spark(Vector3 point)
        {
            GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = "BeamSaberHitSpark";
            Collider c = s.GetComponent<Collider>();
            if (c != null) Destroy(c);
            if (sparkMaterial != null) s.GetComponent<Renderer>().sharedMaterial = sparkMaterial;
            s.transform.position = point;
            s.transform.localScale = Vector3.one * sparkSize;
            Destroy(s, 0.12f);
        }
    }
}
