using System.Collections.Generic;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// The space colony battlefield - per requests ("우주공간에 거대한 구조물이 있으면
    /// 좋을거같아 건담이 들어갈수있는", "콜로니를 엄청 크게 만들어서 콜로니 안에 도시를",
    /// "더 커야하고 더 자세하고 더 콜로니 같아야하고 콜로니 안에서는 중력이 있을거야
    /// 진짜 엄청커야해 도시가 진짜크게 복잡한도시고 그래서 도시에서 싸우는느낌이 있게",
    /// collision "부딪히게").
    ///
    /// A full-size O'Neill "Island 3" style cylinder (the geometry is built once by
    /// GundamCockpitSetup.BuildColony). This component holds only its collision
    /// description and answers "push this body out of the structure" queries - no
    /// physics colliders, so weapons, XR grabs and lock-on are unaffected.
    ///
    /// Colony frame: the axis runs along world +Z from nearCapCenter for 'length'
    /// metres. The city stands on the bottom land strip - the curved inner hull,
    /// GroundY(x) - whose lowest line (x = axis x) is at y = axis y - radius.
    /// Gravity inside points straight down (world -Y; see SuitCollision). The
    /// near end cap is solid except for a huge war-damage BREACH at ground level
    /// (|x| &lt; breachHalfWidth, up to breachTop) - the way in. The far cap is
    /// solid. Buildings are axis-aligned boxes (boxMin/boxMax, world).
    /// </summary>
    public class ColonyStructure : MonoBehaviour
    {
        public Vector3 nearCapCenter = new Vector3(0f, 3200f, 1500f);
        public float radius = 3200f;
        public float length = 12000f;
        [Tooltip("Half width (m) of the breach in the near end cap (the entrance).")]
        public float breachHalfWidth = 320f;
        [Tooltip("World Y of the breach's top edge.")]
        public float breachTop = 420f;
        [Tooltip("Half width (m) of the land strip the city stands on (walkers stay inside it).")]
        public float landHalfWidth = 1550f;
        public Vector3[] boxMin = new Vector3[0];
        public Vector3[] boxMax = new Vector3[0];
        [Tooltip("A body whose feet are within this height of a roof top lands on it instead of being pushed sideways.")]
        public float roofStep = 6f;

        /// <summary>Is this point inside the colony cylinder?</summary>
        public bool IsInside(Vector3 p)
        {
            float z = p.z - nearCapCenter.z;
            if (z < 0f || z > length) return false;
            return Radial(p) < radius;
        }

        float Radial(Vector3 p)
        {
            float dx = p.x - nearCapCenter.x, dy = p.y - nearCapCenter.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Height of the curved land (inner hull, bottom strip) at x.</summary>
        public float GroundY(float x)
        {
            float dx = x - nearCapCenter.x;
            return nearCapCenter.y - Mathf.Sqrt(Mathf.Max(0f, radius * radius - dx * dx));
        }

        bool InBreach(Vector3 feet, float height, float r)
        {
            return Mathf.Abs(feet.x - nearCapCenter.x) < breachHalfWidth - r && feet.y + height < breachTop;
        }

        /// <summary>
        /// Pushes an upright body (feet point, height, radius) out of the hull, the
        /// end caps, the land and the buildings. Returns the corrected feet
        /// position. 'touching' = any contact; 'grounded' = standing on the land or
        /// a roof (for gravity).
        /// </summary>
        public Vector3 ResolveBody(Vector3 feet, float height, float r, out bool touching, out bool grounded)
        {
            touching = false;
            grounded = false;
            Vector3 mid = feet + Vector3.up * (height * 0.5f);
            bool inside = IsInside(mid);
            float zc = nearCapCenter.z;

            // --- Hull ---
            float zMid = mid.z - zc;
            if (zMid > -r && zMid < length + r)
            {
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = feet + Vector3.up * (height * (0.1f + 0.4f * k));
                    // Over the land strip the land itself is the floor (GroundY below),
                    // so the hull only acts as a wall/ceiling elsewhere.
                    if (inside && p.y < nearCapCenter.y && Mathf.Abs(p.x - nearCapCenter.x) < landHalfWidth) continue;
                    Vector2 d = new Vector2(p.x - nearCapCenter.x, p.y - nearCapCenter.y);
                    float rho = d.magnitude;
                    if (rho < 0.01f) continue;
                    Vector2 n = d / rho;
                    float push = 0f;
                    if (inside && rho > radius - r) push = (radius - r) - rho;
                    else if (!inside && rho < radius + r && zMid > 0f && zMid < length) push = (radius + r) - rho;
                    if (push != 0f)
                    {
                        feet += new Vector3(n.x * push, n.y * push, 0f);
                        touching = true;
                    }
                }
            }

            // --- End caps (near one has the breach) ---
            mid = feet + Vector3.up * (height * 0.5f);
            float z = mid.z - zc;
            float rm = Radial(mid);
            float up = mid.z - feet.z; // 0 (kept for clarity)
            if (rm < radius + r && !InBreach(feet, height, r))
            {
                if (inside && z < r) { feet.z = zc + r - up; touching = true; }
                else if (!inside && z > -r && z < r) { feet.z = zc - r - up; touching = true; }
            }
            if (rm < radius + r)
            {
                if (inside && z > length - r) { feet.z = zc + length - r - up; touching = true; }
                else if (!inside && z < length + r && z > length - r) { feet.z = zc + length + r - up; touching = true; }
            }

            if (!inside) return feet;

            // --- Land ---
            float g = GroundY(feet.x);
            if (feet.y <= g + 0.05f)
            {
                if (feet.y < g) feet.y = g;
                grounded = true;
                touching = true;
            }

            // --- Buildings ---
            if (ResolveBoxes(ref feet, height, r, out bool onRoof)) touching = true;
            if (onRoof) grounded = true;
            return feet;
        }

        public Vector3 ResolveBody(Vector3 feet, float height, float r, out bool touching)
        {
            return ResolveBody(feet, height, r, out touching, out bool _);
        }

        /// <summary>For ground walkers (the Zakus): stand on the land inside the
        /// colony's land strip and stay out of the buildings.</summary>
        public Vector3 ResolveWalker(Vector3 feet, float height, float r)
        {
            float zc = nearCapCenter.z;
            feet.x = Mathf.Clamp(feet.x, nearCapCenter.x - landHalfWidth + r, nearCapCenter.x + landHalfWidth - r);
            feet.z = Mathf.Clamp(feet.z, zc + r, zc + length - r);
            feet.y = GroundY(feet.x);
            ResolveBoxes(ref feet, height, r, out bool _, false);
            feet.y = GroundY(feet.x);
            return feet;
        }

        /// <summary>The colony in the scene (set while enabled) - for bullets and
        /// other things spawned at runtime that need to know about its walls.</summary>
        public static ColonyStructure Active { get; private set; }

        void OnEnable() { if (Active == null) Active = this; }
        void OnDisable() { if (Active == this) Active = null; }

        /// <summary>
        /// Line of sight (per "건물뒤에 보이지 않는 적은 락온되면 안됨"): true if the
        /// straight line from a to b is blocked by the colony - a building box, the
        /// land, the end caps (except through the breach) or the hull.
        /// Inside-to-inside lines never touch the hull (the cylinder is convex).
        /// </summary>
        public bool SegmentBlocked(Vector3 a, Vector3 b)
        {
            return Raycast(a, b, out Vector3 _);
        }

        /// <summary>
        /// First point where the segment a->b hits the colony (building, land,
        /// hull, end caps; the breach is open) - per "총알이 건물을 뚫으면 안됨".
        /// </summary>
        public bool Raycast(Vector3 a, Vector3 b, out Vector3 hit)
        {
            float best = 2f;
            if (SegmentFirstBoxHit(a, b, out float tb)) best = tb;

            bool ia = IsInside(a), ib = IsInside(b);
            if (ia != ib)
            {
                // Where the line crosses the colony's skin; only the breach is open.
                float tIn = ia ? 0f : 1f, tOut = ia ? 1f : 0f;
                for (int i = 0; i < 24; i++)
                {
                    float tm = (tIn + tOut) * 0.5f;
                    if (IsInside(Vector3.Lerp(a, b, tm))) tIn = tm; else tOut = tm;
                }
                Vector3 pIn = Vector3.Lerp(a, b, tIn);
                float z = pIn.z - nearCapCenter.z;
                bool throughBreach = z < 2f && Mathf.Abs(pIn.x - nearCapCenter.x) < breachHalfWidth && pIn.y < breachTop;
                if (!throughBreach && tIn < best) best = tIn;
            }
            else if (!ia)
            {
                // Both outside: blocked where the line enters the colony (unless it
                // enters through the breach).
                const int N = 32;
                for (int i = 1; i < N; i++)
                {
                    float ti = i / (float)N;
                    if (ti >= best) break;
                    if (!IsInside(Vector3.Lerp(a, b, ti))) continue;
                    float tOut = (i - 1) / (float)N, tIn = ti;
                    for (int k = 0; k < 20; k++)
                    {
                        float tm = (tIn + tOut) * 0.5f;
                        if (IsInside(Vector3.Lerp(a, b, tm))) tIn = tm; else tOut = tm;
                    }
                    Vector3 pIn = Vector3.Lerp(a, b, tIn);
                    float z = pIn.z - nearCapCenter.z;
                    bool throughBreach = z < 2f && Mathf.Abs(pIn.x - nearCapCenter.x) < breachHalfWidth && pIn.y < breachTop;
                    if (!throughBreach && tIn < best) best = tIn;
                    break;
                }
            }
            if (best > 1f) { hit = b; return false; }
            hit = Vector3.Lerp(a, b, best);
            return true;
        }

        /// <summary>Does the segment a-b pass through any building box?</summary>
        public bool SegmentHitsBoxes(Vector3 a, Vector3 b)
        {
            return SegmentFirstBoxHit(a, b, out float _);
        }

        /// <summary>Nearest building box the segment a-b enters (t = 0..1 along it).</summary>
        public bool SegmentFirstBoxHit(Vector3 a, Vector3 b, out float tHit)
        {
            tHit = 2f;
            if (boxMin == null || boxMax == null) return false;
            EnsureGrid();
            Vector3 d = b - a;
            Vector3 lo = Vector3.Min(a, b), hi = Vector3.Max(a, b);
            _stampId++;
            int cx0 = Cell(lo.x), cx1 = Cell(hi.x), cz0 = Cell(lo.z), cz1 = Cell(hi.z);
            if ((long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1) > 4096) return SegmentFirstBoxHitBrute(a, b, out tHit);
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    if (!_grid.TryGetValue(Key(cx, cz), out List<int> list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        int i = list[k];
                        if (_stamp[i] == _stampId) continue;
                        _stamp[i] = _stampId;
                        Vector3 mn = boxMin[i], mx = boxMax[i];
                        if (mx.x < lo.x || mn.x > hi.x || mx.y < lo.y || mn.y > hi.y || mx.z < lo.z || mn.z > hi.z) continue;
                        float t0 = 0f, t1 = 1f;
                        if (!Slab(a.x, d.x, mn.x, mx.x, ref t0, ref t1)) continue;
                        if (!Slab(a.y, d.y, mn.y, mx.y, ref t0, ref t1)) continue;
                        if (!Slab(a.z, d.z, mn.z, mx.z, ref t0, ref t1)) continue;
                        if (t0 < tHit) tHit = t0;
                    }
                }
            return tHit <= 1f;
        }

        bool SegmentFirstBoxHitBrute(Vector3 a, Vector3 b, out float tHit)
        {
            tHit = 2f;
            Vector3 d = b - a;
            int n = Mathf.Min(boxMin.Length, boxMax.Length);
            for (int i = 0; i < n; i++)
            {
                Vector3 mn = boxMin[i], mx = boxMax[i];
                float t0 = 0f, t1 = 1f;
                if (!Slab(a.x, d.x, mn.x, mx.x, ref t0, ref t1)) continue;
                if (!Slab(a.y, d.y, mn.y, mx.y, ref t0, ref t1)) continue;
                if (!Slab(a.z, d.z, mn.z, mx.z, ref t0, ref t1)) continue;
                if (t0 < tHit) tHit = t0;
            }
            return tHit <= 1f;
        }

        // --- Uniform grid over the building boxes (x/z), so line-of-sight, bullet
        //     and walking queries only test the few boxes nearby. ---
        const float GridCell = 128f;
        Dictionary<long, List<int>> _grid;
        int _gridCount = -1;
        int[] _stamp;
        int _stampId;

        static int Cell(float v) => Mathf.FloorToInt(v / GridCell);
        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        void EnsureGrid()
        {
            int n = boxMin != null && boxMax != null ? Mathf.Min(boxMin.Length, boxMax.Length) : 0;
            if (_grid != null && _gridCount == n) return;
            _grid = new Dictionary<long, List<int>>();
            _stamp = new int[Mathf.Max(1, n)];
            _stampId = 0;
            _gridCount = n;
            for (int i = 0; i < n; i++)
            {
                int cx0 = Cell(boxMin[i].x), cx1 = Cell(boxMax[i].x), cz0 = Cell(boxMin[i].z), cz1 = Cell(boxMax[i].z);
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        long key = Key(cx, cz);
                        if (!_grid.TryGetValue(key, out List<int> list)) { list = new List<int>(); _grid[key] = list; }
                        list.Add(i);
                    }
            }
        }

        static bool Slab(float o, float d, float mn, float mx, ref float t0, ref float t1)
        {
            if (Mathf.Abs(d) < 1e-6f) return o >= mn && o <= mx;
            float ta = (mn - o) / d, tb = (mx - o) / d;
            if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
            if (ta > t0) t0 = ta;
            if (tb < t1) t1 = tb;
            return t0 <= t1;
        }

        readonly List<int> _near = new List<int>();

        bool ResolveBoxes(ref Vector3 feet, float height, float r, out bool onRoof, bool allowRoof = true)
        {
            onRoof = false;
            bool hit = false;
            if (boxMin == null || boxMax == null) return false;
            EnsureGrid();
            _stampId++;
            int gx0 = Cell(feet.x - r), gx1 = Cell(feet.x + r), gz0 = Cell(feet.z - r), gz1 = Cell(feet.z + r);
            _near.Clear();
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gz = gz0; gz <= gz1; gz++)
                {
                    if (!_grid.TryGetValue(Key(gx, gz), out List<int> list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        int bi = list[k];
                        if (_stamp[bi] == _stampId) continue;
                        _stamp[bi] = _stampId;
                        _near.Add(bi);
                    }
                }
            for (int ni = 0; ni < _near.Count; ni++)
            {
                int i = _near[ni];
                Vector3 mn = boxMin[i], mx = boxMax[i];
                if (feet.x < mn.x - r || feet.x > mx.x + r || feet.z < mn.z - r || feet.z > mx.z + r) continue;
                if (feet.y > mx.y + 0.05f || feet.y + height <= mn.y) continue;              // over / under it

                float cx = Mathf.Clamp(feet.x, mn.x, mx.x);
                float cz = Mathf.Clamp(feet.z, mn.z, mx.z);
                float dx = feet.x - cx, dz = feet.z - cz;
                float d2 = dx * dx + dz * dz;

                if (allowRoof && feet.y > mx.y - roofStep && d2 < r * r)
                {
                    feet.y = mx.y;   // standing on the roof
                    onRoof = true;
                    hit = true;
                    continue;
                }
                if (d2 >= r * r) continue;
                if (d2 > 1e-6f)
                {
                    float d = Mathf.Sqrt(d2);
                    float push = r - d;
                    feet.x += dx / d * push;
                    feet.z += dz / d * push;
                }
                else
                {
                    float left = feet.x - mn.x + r, right = mx.x + r - feet.x;
                    float back = feet.z - mn.z + r, front = mx.z + r - feet.z;
                    float m = Mathf.Min(Mathf.Min(left, right), Mathf.Min(back, front));
                    if (m == left) feet.x -= left;
                    else if (m == right) feet.x += right;
                    else if (m == back) feet.z -= back;
                    else feet.z += front;
                }
                hit = true;
            }
            return hit;
        }
    }
}
