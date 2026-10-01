using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Re-tints the cockpit for the ZAKU - per "같은 콕핏, 색만 지온풍". The cockpit
    /// itself is shared by both suits; this only shifts colors: every blue / cyan
    /// light (screens, HUD text, OrbitHUD ticks, steering stick accents, panel
    /// buttons) becomes Zeon green, while reds, ambers and greys stay as they are.
    /// Materials are copied before being changed (never edits shared assets) and
    /// textures (camera feeds, UI images) are left alone - only their tint colors.
    /// </summary>
    public static class CockpitTheme
    {
        /// <summary>Hue (0..1) blue/cyan colors are moved to - a Zeon monitor green.</summary>
        public const float ZeonHue = 0.31f;

        public static Color ToZeon(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            if (s < 0.12f) return c;                       // greys / whites stay
            if (h < 0.42f || h > 0.76f) return c;          // reds, oranges, yellows, greens, magentas stay
            Color o = Color.HSVToRGB(ZeonHue, Mathf.Min(1f, s * 1.05f), v, true);
            o.a = c.a;
            return o;
        }

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        /// <summary>Tints every renderer material and UI color under root.</summary>
        public static void ApplyZeon(Transform root)
        {
            if (root == null) return;
            var copies = new Dictionary<Material, Material>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null) continue;
                    if (!copies.TryGetValue(m, out Material z))
                    {
                        z = Retint(m);
                        copies[m] = z;
                    }
                    if (z != m) { mats[i] = z; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
            foreach (Graphic g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (g is RawImage) continue; // camera feed
                g.color = ToZeon(g.color);
            }
        }

        static Material Retint(Material m)
        {
            bool any = false;
            Color bc = Color.white, c = Color.white, e = Color.black;
            if (m.HasProperty(BaseColorId)) { bc = m.GetColor(BaseColorId); any |= ToZeon(bc) != bc; }
            if (m.HasProperty(ColorId)) { c = m.GetColor(ColorId); any |= ToZeon(c) != c; }
            if (m.HasProperty(EmissionId)) { e = m.GetColor(EmissionId); any |= ToZeon(e) != e; }
            if (!any) return m;
            Material z = new Material(m) { name = m.name + " (Zeon)" };
            if (m.HasProperty(BaseColorId)) z.SetColor(BaseColorId, ToZeon(bc));
            if (m.HasProperty(ColorId)) z.SetColor(ColorId, ToZeon(c));
            if (m.HasProperty(EmissionId)) z.SetColor(EmissionId, ToZeon(e));
            return z;
        }
    }
}
