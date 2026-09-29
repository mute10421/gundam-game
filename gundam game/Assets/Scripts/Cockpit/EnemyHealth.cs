using System;
using System.Collections;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Hit points for an enemy mobile suit (currently the ZakuEnemy) - per request
    /// ("자쿠에게... 체력을 1000으로 설정해줘"). maxHealth defaults to 1000.
    ///
    /// Damage comes in through TakeDamage() - the Head Vulcan's bullets
    /// (HeadVulcanBullet) call it once per bullet that reaches this object's
    /// collider, with HeadVulcanController.damage (1) each.
    ///
    /// At 0 HP (per "폭발 후 재등장"): an explosion effect plays, the suit's
    /// renderers/colliders are switched off (so it can't be hit or locked onto),
    /// and after respawnDelay it reappears at its original spawn point with full
    /// health. The GameObject itself stays active the whole time so this
    /// coroutine and ZakuCombatAI keep running.
    ///
    /// Also draws a small floating HP bar with the exact number above the suit so
    /// each 1-point hit is visible in the headset. The bar is its OWN root object
    /// (not a child of the suit) so it never counts toward the suit's renderer
    /// bounds (OrbitHUDTargetLock sizes its lock circle from those).
    /// </summary>
    public class EnemyHealth : MonoBehaviour
    {
        [Header("Health")]
        public int maxHealth = 1000;
        [Tooltip("Seconds after being destroyed before the suit reappears at its spawn point with full health.")]
        public float respawnDelay = 5f;

        [Header("Effects")]
        [Tooltip("Emissive material used for the explosion fireballs (tinted copies are also made from it for the HP bar). Wired by GundamCockpitSetup.")]
        public Material effectMaterial;
        [Tooltip("Diameter (m) of the biggest explosion fireball.")]
        public float explosionSize = 22f;
        [Tooltip("How long (s) the explosion fireballs take to bloom and fade.")]
        public float explosionDuration = 1.2f;
        public int explosionFireballs = 7;

        [Header("HP bar")]
        public bool showHealthBar = true;
        [Tooltip("Height (m) of the HP bar above this object's pivot (the feet). Set by GundamCockpitSetup to just above the suit's head.")]
        public float barHeightAboveRoot = 20f;
        public float barWidth = 10f;
        public float barThickness = 0.8f;
        [Tooltip("What the HP bar turns to face (normally the player's Gundam). Falls back to ZakuCombatAI.target, then Camera.main.")]
        public Transform faceTowards;

        public int CurrentHealth { get; private set; }
        public bool IsDead { get; private set; }
        public float HealthFraction => maxHealth > 0 ? (float)CurrentHealth / maxHealth : 0f;
        public float LastHitTime { get; private set; } = -999f;

        public event Action<EnemyHealth, int> Damaged;
        public event Action<EnemyHealth> Died;
        public event Action<EnemyHealth> Respawned;

        Vector3 _spawnPosition;
        Quaternion _spawnRotation;
        Renderer[] _renderers;
        Collider[] _colliders;

        GameObject _bar;
        Transform _barFill;
        Renderer _barFillRenderer;
        TextMesh _barText;
        Material _barFillMat;
        int _shownHealth = -1;

        void Awake()
        {
            CurrentHealth = maxHealth;
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);
        }

        void Start()
        {
            if (showHealthBar) BuildHealthBar();
        }

        void OnDestroy()
        {
            if (_bar != null) Destroy(_bar);
        }

        /// <summary>Applies damage. Returns false if already dead (hit ignored).</summary>
        public bool TakeDamage(int amount, Vector3 hitPoint)
        {
            if (IsDead || amount <= 0) return false;
            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            LastHitTime = Time.time;
            Damaged?.Invoke(this, amount);
            if (CurrentHealth <= 0) Die();
            return true;
        }

        public bool TakeDamage(int amount) => TakeDamage(amount, transform.position);

        void Die()
        {
            if (IsDead) return;
            IsDead = true;
            Died?.Invoke(this);
            StartCoroutine(ExplodeAndRespawn());
        }

        IEnumerator ExplodeAndRespawn()
        {
            Vector3 center = transform.position + Vector3.up * (barHeightAboveRoot * 0.45f);
            SpawnExplosion(center);

            // Brief delay so the fireball covers the suit before it vanishes.
            yield return new WaitForSeconds(explosionDuration * 0.25f);
            SetBodyVisible(false);

            yield return new WaitForSeconds(Mathf.Max(0f, respawnDelay));

            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            CurrentHealth = maxHealth;
            IsDead = false;
            SetBodyVisible(true);
            Respawned?.Invoke(this);
        }

        void SetBodyVisible(bool visible)
        {
            foreach (Renderer r in _renderers) if (r != null) r.enabled = visible;
            foreach (Collider c in _colliders) if (c != null) c.enabled = visible;
            if (_bar != null) _bar.SetActive(visible);
        }

        void SpawnExplosion(Vector3 center)
        {
            int n = Mathf.Max(1, explosionFireballs);
            for (int i = 0; i < n; i++)
            {
                Vector3 offset = i == 0 ? Vector3.zero : UnityEngine.Random.insideUnitSphere * explosionSize * 0.35f;
                float size = i == 0 ? explosionSize : explosionSize * UnityEngine.Random.Range(0.35f, 0.7f);
                float delay = i == 0 ? 0f : UnityEngine.Random.Range(0f, explosionDuration * 0.4f);
                StartCoroutine(Fireball(center + offset, size, delay));
            }
        }

        IEnumerator Fireball(Vector3 position, float size, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "EnemyExplosion";
            Collider col = ball.GetComponent<Collider>();
            if (col != null) Destroy(col);
            if (effectMaterial != null) ball.GetComponent<Renderer>().sharedMaterial = effectMaterial;
            ball.transform.position = position;

            float life = Mathf.Max(0.05f, explosionDuration * 0.7f);
            float t = 0f;
            while (t < life)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / life);
                // Fast bloom to full size, then shrink away.
                float s = k < 0.3f ? Mathf.SmoothStep(0.1f, 1f, k / 0.3f) : Mathf.SmoothStep(1f, 0f, (k - 0.3f) / 0.7f);
                ball.transform.localScale = Vector3.one * (size * s);
                yield return null;
            }
            Destroy(ball);
        }

        // --------------------------------------------------------------
        // HP bar
        // --------------------------------------------------------------

        void BuildHealthBar()
        {
            _bar = new GameObject(name + "_HPBar");

            Material bgMat = MakeTint(new Color(0.02f, 0.02f, 0.02f), Color.black);
            _barFillMat = MakeTint(new Color(0.1f, 0.6f, 0.15f), new Color(0.2f, 1f, 0.3f));

            GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bg.name = "Background";
            StripCollider(bg);
            bg.transform.SetParent(_bar.transform, false);
            bg.transform.localScale = new Vector3(barWidth + 0.3f, barThickness + 0.3f, 0.1f);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.08f);
            if (bgMat != null) bg.GetComponent<Renderer>().sharedMaterial = bgMat;

            GameObject fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fill.name = "Fill";
            StripCollider(fill);
            fill.transform.SetParent(_bar.transform, false);
            _barFill = fill.transform;
            _barFillRenderer = fill.GetComponent<Renderer>();
            if (_barFillMat != null) _barFillRenderer.sharedMaterial = _barFillMat;

            try
            {
                GameObject textGo = new GameObject("Label");
                textGo.transform.SetParent(_bar.transform, false);
                textGo.transform.localPosition = new Vector3(0f, barThickness * 0.5f + 1.3f, 0f);
                _barText = textGo.AddComponent<TextMesh>();
                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font != null)
                {
                    _barText.font = font;
                    textGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                }
                _barText.anchor = TextAnchor.MiddleCenter;
                _barText.alignment = TextAlignment.Center;
                _barText.characterSize = 0.25f;
                _barText.fontSize = 48;
                _barText.color = Color.white;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EnemyHealth] HP number label unavailable (" + e.Message + ") - showing the bar only.");
                _barText = null;
            }

            RefreshBar(true);
            UpdateBarTransform();
        }

        void LateUpdate()
        {
            if (_bar == null) return;
            RefreshBar(false);
            UpdateBarTransform();
        }

        void RefreshBar(bool force)
        {
            if (!force && _shownHealth == CurrentHealth) return;
            _shownHealth = CurrentHealth;
            float f = HealthFraction;
            if (_barFill != null)
            {
                float w = Mathf.Max(0.001f, barWidth * f);
                _barFill.localScale = new Vector3(w, barThickness, 0.12f);
                _barFill.localPosition = new Vector3(-barWidth * 0.5f + w * 0.5f, 0f, 0f);
            }
            if (_barFillMat != null)
            {
                Color c = GundamVitals.StatusColor(f);
                if (_barFillMat.HasProperty("_BaseColor")) _barFillMat.SetColor("_BaseColor", c * 0.6f);
                if (_barFillMat.HasProperty("_EmissionColor")) _barFillMat.SetColor("_EmissionColor", c);
            }
            if (_barText != null) _barText.text = $"HP {CurrentHealth}/{maxHealth}";
        }

        void UpdateBarTransform()
        {
            Vector3 pos = transform.position + Vector3.up * barHeightAboveRoot;
            _bar.transform.position = pos;

            Transform face = faceTowards;
            if (face == null)
            {
                ZakuCombatAI ai = GetComponent<ZakuCombatAI>();
                if (ai != null) face = ai.target;
            }
            if (face == null && Camera.main != null) face = Camera.main.transform;
            if (face == null) return;

            Vector3 away = pos - face.position;
            away.y = 0f;
            if (away.sqrMagnitude > 0.01f) _bar.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        Material MakeTint(Color baseColor, Color emission)
        {
            if (effectMaterial == null) return null;
            Material m = new Material(effectMaterial);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseColor);
            if (m.HasProperty("_Color")) m.SetColor("_Color", baseColor);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", emission);
            return m;
        }

        static void StripCollider(GameObject go)
        {
            Collider c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
        }
    }
}
