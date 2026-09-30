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

        /// <summary>
        /// Line of sight (per "건물뒤에 보이지 않는 적은 락온되면 안됨"): true if the
        /// straight line from a to b is blocked by the colony - a building box, the
        /// end caps (except through the breach) or the hull (seen from outside).
        /// Inside-to-inside lines never touch the hull (the cylinder is convex).
        /// </summary>
        public bool SegmentBlocked(Vector3 a, Vector3 b)
        {
            bool ia = IsInside(a), ib = IsInside(b);
            if (ia != ib)
            {
                // Find where the line crosses the colony's skin; only the breach is open.
                Vector3 inP = ia ? a : b, outP = ia ? b : a;
                for (int i = 0; i < 24; i++)
                {
                    Vector3 m = (inP + outP) * 0.5f;
                    if (IsInside(m)) inP = m; else outP = m;
                }
                float z = inP.z - nearCapCenter.z;
                bool throughBreach = z < 2f && Mathf.Abs(inP.x - nearCapCenter.x) < breachHalfWidth && inP.y < breachTop;
                if (!throughBreach) return true;
            }
            else if (!ia)
            {
                // Both outside: blocked if the line passes through the colony.
                for (int i = 1; i < 32; i++)
                    if (IsInside(Vector3.Lerp(a, b, i / 32f))) return true;
                return false;
            }
            return SegmentHitsBoxes(a, b);
        }

        /// <summary>Does the segment a-b pass through any building box?</summary>
        public bool SegmentHitsBoxes(Vector3 a, Vector3 b)
        {
            if (boxMin == null || boxMax == null) return false;
            Vector3 d = b - a;
            Vector3 lo = Vector3.Min(a, b), hi = Vector3.Max(a, b);
            int n = Mathf.Min(boxMin.Length, boxMax.Length);
            for (int i = 0; i < n; i++)
            {
                Vector3 mn = boxMin[i], mx = boxMax[i];
                if (mx.x < lo.x || mn.x > hi.x || mx.y < lo.y || mn.y > hi.y || mx.z < lo.z || mn.z > hi.z) continue;
                float t0 = 0f, t1 = 1f;
                if (!Slab(a.x, d.x, mn.x, mx.x, ref t0, ref t1)) continue;
                if (!Slab(a.y, d.y, mn.y, mx.y, ref t0, ref t1)) continue;
                if (!Slab(a.z, d.z, mn.z, mx.z, ref t0, ref t1)) continue;
                return true;
            }
            return false;
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

        bool ResolveBoxes(ref Vector3 feet, float height, float r, out bool onRoof, bool allowRoof = true)
        {
            onRoof = false;
            bool hit = false;
            if (boxMin == null || boxMax == null) return false;
            int n = Mathf.Min(boxMin.Length, boxMax.Length);
            for (int i = 0; i < n; i++)
            {
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
