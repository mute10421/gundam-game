using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Continuous gunfire sound for automatic weapons - per "이거를 자쿠 총소리로
    /// 해주고 아까한거는 건담 해드 발칸 소리로 하자". The clips are recordings of a
    /// whole burst (many rounds), so instead of starting the clip again on every
    /// round (which piles up into noise) the clip loops while the weapon keeps
    /// firing and fades out shortly after the last round.
    ///
    /// Plain helper (not a component): the weapon calls Trigger() on every round
    /// and Tick() once per frame. 2D sound - the cockpit (and its AudioListener)
    /// sits far from the battlefield; the caller passes volume and stereo pan.
    /// </summary>
    public class LoopingFireSound
    {
        readonly AudioSource _src;
        float _keepUntil;
        float _targetVolume;
        float _volume;

        /// <summary>Seconds the sound keeps going after the last Trigger().</summary>
        public float holdTime = 0.25f;
        /// <summary>Fade in / out time (s).</summary>
        public float fadeIn = 0.03f;
        public float fadeOut = 0.12f;

        public LoopingFireSound(GameObject host, AudioClip clip)
        {
            if (host == null || clip == null) return;
            _src = host.AddComponent<AudioSource>();
            _src.clip = clip;
            _src.loop = true;
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.volume = 0f;
        }

        public bool Valid => _src != null;

        /// <summary>A round was fired: keep the loop going (start it if needed).</summary>
        public void Trigger(float volume, float pan = 0f, float hold = -1f)
        {
            if (_src == null) return;
            _targetVolume = Mathf.Clamp01(volume);
            _src.panStereo = Mathf.Clamp(pan, -1f, 1f);
            _keepUntil = Time.time + (hold > 0f ? hold : holdTime);
            if (!_src.isPlaying)
            {
                _volume = 0f;
                _src.volume = 0f;
                _src.time = 0f;
                _src.Play();
            }
        }

        public void Tick()
        {
            if (_src == null || !_src.isPlaying) return;
            float dt = Time.deltaTime;
            bool firing = Time.time < _keepUntil;
            float want = firing ? _targetVolume : 0f;
            float rate = firing ? 1f / Mathf.Max(0.005f, fadeIn) : 1f / Mathf.Max(0.005f, fadeOut);
            _volume = Mathf.MoveTowards(_volume, want, rate * dt * Mathf.Max(0.05f, _targetVolume));
            _src.volume = _volume;
            if (!firing && _volume <= 0.001f) _src.Stop();
        }

        public void Stop()
        {
            if (_src == null) return;
            _src.Stop();
            _volume = 0f;
        }
    }
}
