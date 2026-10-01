using System.Collections.Generic;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Booster flames for an enemy mobile suit (GUNDAM or ZAKU) - per "유니티 엣셋에
    /// 부스터 다운했거든 자쿠랑 건담 등뒤에 부스터에서 효과가 나와야하고 발바닥에서도
    /// 나와야하는데 발바닥은 우주에서만". Uses the downloaded "2D Booster Flame" shader
    /// (RockingProjects BoosterFlameShader) on three crossed, double-sided planes per
    /// nozzle, so the 2D flame reads as a 3D jet from any side.
    ///
    ///   * BACK (backpack nozzles): always lit - a small idle flame, growing longer and
    ///     brighter the faster the suit moves (per "움직일 때 세게").
    ///   * SOLES (foot nozzles): ONLY IN SPACE - outside the colony (or when there is
    ///     no colony at all). Inside the colony they're off (the suits walk there).
    ///   * Dead (EnemyHealth.IsDead) or switched off: no flames.
    ///
    /// The nozzles are empty Transforms (built by the scene setup) parented to the
    /// suit's bones - +Z = the exhaust direction, origin = the nozzle mouth - so the
    /// flames follow the body, and the feet follow the walk / float poses. The flame
    /// objects themselves are NOT children of the suit: the aim point / lock-on ring
    /// measure the suit from its renderers, and a 4 m flame under the feet must not
    /// shift those. They're moved onto their nozzles every frame instead.
    /// </summary>
    [DefaultExecutionOrder(200)] // after the walk / AI / weapon poses
    public class MechBoosterFlames : MonoBehaviour
    {
        [Header("Nozzles (+Z = exhaust direction)")]
        public Transform[] backNozzles;
        public Transform[] footNozzles;

        [Header("Look")]
        [Tooltip("Booster flame material (RockingProjects BoosterFlameShader).")]
        public Material flameMaterial;
        [Tooltip("Flame color (HDR) - per \"파랑색\": a clear blue. The asset's own blue material is so bright it renders white without HDR/bloom.")]
        [ColorUsage(false, true)] public Color flameColor = new Color(0.08f, 0.3f, 1.6f);
        [Tooltip("Back flame length / width (m) at full speed. The flame texture fills about the middle half of its width.")]
        public float backLength = 6f;
        public float backWidth = 3.2f;
        [Tooltip("Sole flame length / width (m) at full speed.")]
        public float footLength = 4.5f;
        public float footWidth = 2.8f;
        [Tooltip("Flame size while standing still (fraction of full).")]
        [Range(0f, 1f)] public float idleLevel = 0.35f;
        [Tooltip("Sole flames never drop below this while in space (they hold the suit).")]
        [Range(0f, 1f)] public float footIdleLevel = 0.5f;
        [Tooltip("Speed (m/s) at which the flames are at full size.")]
        public float fullSpeed = 30f;
        [Tooltip("Shader brightness (_intensity) at idle and at full speed.")]
        public Vector2 intensityRange = new Vector2(0.55f, 1.25f);
        public float riseTime = 0.15f;
        public float fallTime = 0.5f;

        [Header("Space / colony")]
        [Tooltip("Colony - inside it the sole flames are off. Empty = ColonyStructure.Active.")]
        public ColonyStructure colony;
        [Tooltip("Body height (m) - the colony test uses the body's middle.")]
        public float bodyHeight = 18f;
        public EnemyHealth health;

        class Flame
        {
            public Transform nozzle;
            public Transform fx;
            public MeshRenderer renderer;
            public bool foot;
            public float phase;
        }

        readonly List<Flame> _flames = new List<Flame>();
        MaterialPropertyBlock _mpb;
        static Mesh s_mesh;
        static readonly int IntensityId = Shader.PropertyToID("_intensity");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        GameObject _root;
        Vector3 _lastPos;
        bool _haveLast;
        float _speed;
        float _level;
        float _footLevel;

        void Awake()
        {
            if (health == null) health = GetComponent<EnemyHealth>();
            _mpb = new MaterialPropertyBlock();
            _root = new GameObject(name + "_BoosterFlames");
            Add(backNozzles, false);
            Add(footNozzles, true);
            SetVisible(false);
        }

        void Add(Transform[] nozzles, bool foot)
        {
            if (nozzles == null) return;
            foreach (Transform n in nozzles)
            {
                if (n == null) continue;
                GameObject go = new GameObject(foot ? "SoleFlame" : "BackFlame");
                go.layer = gameObject.layer;
                go.transform.SetParent(_root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = FlameMesh();
                MeshRenderer r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = flameMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                _flames.Add(new Flame { nozzle = n, fx = go.transform, renderer = r, foot = foot, phase = Random.Range(0f, 100f) });
            }
        }

        void OnEnable() { _haveLast = false; }
        void OnDisable() { SetVisible(false); }
        void OnDestroy() { if (_root != null) Destroy(_root); }

        void SetVisible(bool on)
        {
            foreach (Flame f in _flames) if (f.renderer != null) f.renderer.enabled = on;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 1e-5f || flameMaterial == null) return;

            // Movement speed (teleports / respawns ignored).
            Vector3 p = transform.position;
            if (_haveLast)
            {
                float v = (p - _lastPos).magnitude / dt;
                if (v < 600f) _speed = Mathf.Lerp(_speed, v, 1f - Mathf.Exp(-dt * 8f));
            }
            _lastPos = p;
            _haveLast = true;

            bool dead = health != null && health.IsDead;
            float want = dead ? 0f : Mathf.Lerp(idleLevel, 1f, Mathf.Clamp01(_speed / Mathf.Max(0.1f, fullSpeed)));
            _level = Mathf.MoveTowards(_level, want, dt / Mathf.Max(0.01f, want > _level ? riseTime : fallTime));

            ColonyStructure c = colony != null ? colony : ColonyStructure.Active;
            bool inSpace = c == null || !c.IsInside(p + transform.up * (bodyHeight * 0.5f));
            float footWant = dead || !inSpace ? 0f : Mathf.Max(footIdleLevel, want);
            _footLevel = Mathf.MoveTowards(_footLevel, footWant, dt / Mathf.Max(0.01f, footWant > _footLevel ? riseTime * 2f : fallTime));

            float t = Time.time;
            foreach (Flame f in _flames)
            {
                if (f.nozzle == null || f.renderer == null) continue;
                float lvl = f.foot ? _footLevel : _level;
                bool on = lvl > 0.02f && f.nozzle.gameObject.activeInHierarchy;
                if (f.renderer.enabled != on) f.renderer.enabled = on;
                if (!on) continue;
                // A little flicker on top of the shader's own animation.
                float flick = 1f + 0.07f * Mathf.Sin(t * 31f + f.phase) + 0.05f * Mathf.Sin(t * 53f + f.phase * 1.7f);
                float len = (f.foot ? footLength : backLength) * lvl * flick;
                float wid = (f.foot ? footWidth : backWidth) * Mathf.Lerp(0.6f, 1f, lvl);
                f.fx.SetPositionAndRotation(f.nozzle.position, f.nozzle.rotation);
                f.fx.localScale = new Vector3(wid, wid, Mathf.Max(0.05f, len));
                _mpb.SetFloat(IntensityId, Mathf.Lerp(intensityRange.x, intensityRange.y, lvl));
                _mpb.SetColor(ColorId, flameColor);
                f.renderer.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Unit flame: three crossed double-sided planes along +Z (z 0 = nozzle,
        /// z 1 = tip), 1 wide. UV v = 1 at the nozzle (the texture's wide end) -> 0 at
        /// the tip; vertex colors white (the shader multiplies by them).</summary>
        static Mesh FlameMesh()
        {
            if (s_mesh != null) return s_mesh;
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();
            for (int k = 0; k < 3; k++)
            {
                Quaternion r = Quaternion.AngleAxis(k * 60f, Vector3.forward);
                Vector3 side = r * Vector3.right * 0.5f;
                int b = v.Count;
                v.Add(-side); uv.Add(new Vector2(0f, 1f));
                v.Add(side); uv.Add(new Vector2(1f, 1f));
                v.Add(side + Vector3.forward); uv.Add(new Vector2(1f, 0f));
                v.Add(-side + Vector3.forward); uv.Add(new Vector2(0f, 0f));
                tri.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });   // front
                tri.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });   // back
            }
            s_mesh = new Mesh { name = "BoosterFlameMesh" };
            s_mesh.SetVertices(v);
            s_mesh.SetUVs(0, uv);
            var col = new Color[v.Count];
            for (int i = 0; i < col.Length; i++) col[i] = Color.white;
            s_mesh.colors = col;
            s_mesh.SetTriangles(tri, 0);
            s_mesh.RecalculateNormals();
            s_mesh.bounds = new Bounds(new Vector3(0f, 0f, 0.5f), new Vector3(1.2f, 1.2f, 1.2f));
            return s_mesh;
        }

        /// <summary>For editor previews: show the flames at a fixed level right away.</summary>
        public void PreviewNow(float level, bool feet)
        {
            _level = level;
            _footLevel = feet ? Mathf.Max(footIdleLevel, level) : 0f;
            _speed = level * fullSpeed;
            foreach (Flame f in _flames)
            {
                if (f.nozzle == null || f.renderer == null) continue;
                float lvl = f.foot ? _footLevel : _level;
                f.renderer.enabled = lvl > 0.02f;
                f.fx.SetPositionAndRotation(f.nozzle.position, f.nozzle.rotation);
                float len = (f.foot ? footLength : backLength) * lvl;
                float wid = (f.foot ? footWidth : backWidth) * Mathf.Lerp(0.6f, 1f, lvl);
                f.fx.localScale = new Vector3(wid, wid, Mathf.Max(0.05f, len));
                _mpb.SetFloat(IntensityId, Mathf.Lerp(intensityRange.x, intensityRange.y, lvl));
                _mpb.SetColor(ColorId, flameColor);
                f.renderer.SetPropertyBlock(_mpb);
            }
        }
    }
}
