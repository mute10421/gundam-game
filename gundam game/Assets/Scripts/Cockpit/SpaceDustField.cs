using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Endless star/space-dust field around the Gundam's HeadCam - per request
    /// ("배경에 별들이 더 있어야할거같아 움직이는게 안느겨져서"): the original
    /// Starfield is only 60 cubes 25-70m around the start point, so once the suit
    /// moves away there's nothing nearby to fly past and motion can't be felt.
    ///
    /// Two layers:
    ///  - NEAR DUST: small glowing specks filling a 3x3x3 block of tiles around
    ///    the viewer. The tiles are fixed in the WORLD (so specks stream past with
    ///    real parallax as you move) and simply re-snap to the grid cell the
    ///    viewer is in, so the field never runs out in any direction. Each cell
    ///    always shows the same one of a few random variants (picked by hashing
    ///    the cell), so the pattern doesn't visibly repeat or jump when re-snapping.
    ///  - FAR STARS: a dense shell of stars far away that follows the viewer's
    ///    position (like a sky), so there are many more stars in every direction
    ///    and turning the view reads clearly too.
    ///
    /// Meshes are generated at runtime (not stored in the scene). No colliders.
    /// Everything uses this GameObject's layer - GundamCockpitSetup puts it on a
    /// layer the pilot's own XR camera skips (it's sealed inside the opaque dome
    /// anyway), so specks drifting through the cockpit's position are only ever
    /// seen through HeadCam's exterior view, never floating inside the cockpit.
    /// </summary>
    public class SpaceDustField : MonoBehaviour
    {
        [Tooltip("What the field is centered on - the Gundam's HeadCam. Found by name if empty.")]
        public Transform viewer;
        public string viewerName = "HeadCam";
        [Tooltip("Emissive material for the specks/stars (wired by GundamCockpitSetup).")]
        public Material material;
        public int seed = 20260928;

        [Header("Near dust (parallax)")]
        [Tooltip("Edge length (m) of one tile; 3x3x3 tiles surround the viewer, so dust reaches ~1.5 tiles out.")]
        public float tileSize = 40f;
        [Tooltip("Specks per tile (x27 tiles in total). Lowered from 220 to 55 per \"너무 많은데?\".")]
        public int particlesPerTile = 55;
        [Tooltip("Speck size range (m).")]
        public Vector2 particleSize = new Vector2(0.15f, 0.45f);
        [Tooltip("Number of different random tile layouts.")]
        public int variants = 4;

        [Header("Far stars (sky)")]
        [Tooltip("Lowered from 2500 to 700 per \"너무 많은데?\".")]
        public int farStarCount = 700;
        public Vector2 farStarDistance = new Vector2(450f, 900f);
        public Vector2 farStarSize = new Vector2(2.5f, 6f);

        Mesh[] _tileMeshes;
        MeshFilter[] _tileFilters;
        Transform[] _tiles;
        Transform _far;
        Vector3Int _cell;
        bool _haveCell;
        float _findTimer;

        void Awake()
        {
            Random.State saved = Random.state;
            Random.InitState(seed);

            int v = Mathf.Max(1, variants);
            _tileMeshes = new Mesh[v];
            for (int i = 0; i < v; i++) _tileMeshes[i] = BuildTileMesh(i);

            _tiles = new Transform[27];
            _tileFilters = new MeshFilter[27];
            for (int i = 0; i < 27; i++)
            {
                GameObject t = new GameObject("DustTile_" + i);
                t.layer = gameObject.layer;
                t.transform.SetParent(transform, false);
                _tileFilters[i] = t.AddComponent<MeshFilter>();
                SetupRenderer(t.AddComponent<MeshRenderer>());
                _tiles[i] = t.transform;
            }

            if (farStarCount > 0)
            {
                GameObject far = new GameObject("FarStars");
                far.layer = gameObject.layer;
                far.transform.SetParent(transform, false);
                far.AddComponent<MeshFilter>().sharedMesh = BuildFarMesh();
                SetupRenderer(far.AddComponent<MeshRenderer>());
                _far = far.transform;
            }

            Random.state = saved;
        }

        void OnDestroy()
        {
            if (_tileMeshes != null) foreach (Mesh m in _tileMeshes) if (m != null) Destroy(m);
            if (_far != null)
            {
                MeshFilter mf = _far.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            }
        }

        void LateUpdate()
        {
            if (viewer == null)
            {
                _findTimer -= Time.deltaTime;
                if (_findTimer > 0f) return;
                _findTimer = 1f;
                GameObject g = GameObject.Find(viewerName);
                if (g == null) return;
                viewer = g.transform;
            }

            Vector3 p = viewer.position;
            if (_far != null) _far.position = p;

            float s = Mathf.Max(1f, tileSize);
            Vector3Int cell = new Vector3Int(Mathf.FloorToInt(p.x / s), Mathf.FloorToInt(p.y / s), Mathf.FloorToInt(p.z / s));
            if (_haveCell && cell == _cell) return;
            _cell = cell;
            _haveCell = true;

            int i = 0;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3Int c = new Vector3Int(cell.x + x, cell.y + y, cell.z + z);
                _tiles[i].position = new Vector3(c.x * s, c.y * s, c.z * s);
                _tileFilters[i].sharedMesh = _tileMeshes[VariantFor(c)];
                i++;
            }
        }

        int VariantFor(Vector3Int c)
        {
            int h = (c.x * 73856093) ^ (c.y * 19349663) ^ (c.z * 83492791);
            h &= 0x7fffffff;
            return h % _tileMeshes.Length;
        }

        void SetupRenderer(MeshRenderer r)
        {
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        Mesh BuildTileMesh(int index)
        {
            float s = Mathf.Max(1f, tileSize);
            int n = Mathf.Max(1, particlesPerTile);
            Vector3[] verts = new Vector3[n * 6];
            int[] tris = new int[n * 24];
            for (int i = 0; i < n; i++)
            {
                Vector3 c = new Vector3(Random.value * s, Random.value * s, Random.value * s);
                AddOctahedron(verts, tris, i, c, Random.Range(particleSize.x, particleSize.y) * 0.5f);
            }
            Mesh m = new Mesh { name = "SpaceDustTile_" + index };
            m.vertices = verts;
            m.triangles = tris;
            m.RecalculateNormals();
            m.bounds = new Bounds(new Vector3(s, s, s) * 0.5f, new Vector3(s, s, s) + Vector3.one * particleSize.y);
            return m;
        }

        Mesh BuildFarMesh()
        {
            int n = farStarCount;
            Vector3[] verts = new Vector3[n * 6];
            int[] tris = new int[n * 24];
            for (int i = 0; i < n; i++)
            {
                Vector3 c = Random.onUnitSphere * Random.Range(farStarDistance.x, farStarDistance.y);
                AddOctahedron(verts, tris, i, c, Random.Range(farStarSize.x, farStarSize.y) * 0.5f);
            }
            Mesh m = new Mesh { name = "FarStars" };
            if (verts.Length > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts;
            m.triangles = tris;
            m.RecalculateNormals();
            float r = farStarDistance.y + farStarSize.y;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * r * 2f);
            return m;
        }

        // Wound so every face points outward (front faces visible from outside).
        static readonly int[] OctaTris =
        {
            0, 4, 2,  2, 4, 1,  1, 4, 3,  3, 4, 0,
            2, 5, 0,  1, 5, 2,  3, 5, 1,  0, 5, 3,
        };

        static void AddOctahedron(Vector3[] verts, int[] tris, int i, Vector3 c, float r)
        {
            int v = i * 6;
            verts[v + 0] = c + new Vector3(r, 0f, 0f);
            verts[v + 1] = c + new Vector3(-r, 0f, 0f);
            verts[v + 2] = c + new Vector3(0f, 0f, r);
            verts[v + 3] = c + new Vector3(0f, 0f, -r);
            verts[v + 4] = c + new Vector3(0f, r, 0f);
            verts[v + 5] = c + new Vector3(0f, -r, 0f);
            int t = i * 24;
            for (int k = 0; k < 24; k++) tris[t + k] = v + OctaTris[k];
        }
    }
}
