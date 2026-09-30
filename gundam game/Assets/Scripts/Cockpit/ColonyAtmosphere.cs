using System.Collections.Generic;
using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Makes the space colony read as a real place - per "디테일을 더 살려줘 지금은
    /// 너무 비현실적이야":
    ///
    ///  - AIR: while the viewer (HeadCam) is inside the colony there is an
    ///    atmosphere - linear distance fog in a pale sky color, so the land
    ///    curving up overhead, the far end cap 12 km away and the far districts
    ///    fade into haze like a real sky, and the ambient light is raised to the
    ///    soft daylight coming through the windows. Outside in space: no fog, the
    ///    scene's own ambient.
    ///    (GundamCockpitSetup keeps the scene's fog ENABLED with an unreachable
    ///    start distance, so the fog shader variants are kept in the build; this
    ///    only moves the distances.)
    ///  - COST (the head camera renders the whole scene six times a frame for the
    ///    360 view): inside, nothing outside the hull can be seen (the window
    ///    strips are glass lit by the mirrors), so the exterior - outer hull,
    ///    mirrors, lights, port cranes, antennas - is switched off; outside, the
    ///    street-level detail (cars, street lights, road paint, street trees) is
    ///    switched off, and inside it is limited to 'detailCullDistance' via the
    ///    camera's per-layer cull distance.
    ///
    /// Only toggles renderers under the given roots and RenderSettings; never
    /// touches the colony collision, the Gundam, cameras' positions or the XR rig.
    /// </summary>
    public class ColonyAtmosphere : MonoBehaviour
    {
        public ColonyStructure colony;
        [Tooltip("The external view camera (HeadCam).")]
        public Transform viewer;
        [Tooltip("Hull outside, mirrors, exterior lights and props - off while inside.")]
        public Transform exteriorRoot;
        [Tooltip("Street-level detail (layer detailLayer) - off while outside.")]
        public Transform streetDetailRoot;
        public int detailLayer = 27;
        public float detailCullDistance = 1400f;

        [Header("Air inside")]
        public Color fogColor = new Color(0.64f, 0.72f, 0.84f);
        public float fogStart = 250f;
        public float fogEnd = 7000f;
        public Color insideAmbient = new Color(0.46f, 0.5f, 0.56f);
        [Tooltip("Must be past the viewer's own position this far (m) into the colony to count as inside (so the breach itself still shows the outside).")]
        public float insideMargin = 60f;

        public bool Inside { get; private set; }

        readonly List<Renderer> _exterior = new List<Renderer>();
        readonly List<Renderer> _detail = new List<Renderer>();
        bool _init;
        bool _state;
        float _outFogStart, _outFogEnd;
        Color _outFogColor, _outAmbient;
        UnityEngine.Rendering.AmbientMode _outAmbientMode;

        void Start()
        {
            if (colony == null) colony = GetComponent<ColonyStructure>();
            if (exteriorRoot != null) _exterior.AddRange(exteriorRoot.GetComponentsInChildren<Renderer>(true));
            if (streetDetailRoot != null) _detail.AddRange(streetDetailRoot.GetComponentsInChildren<Renderer>(true));
            _outFogStart = RenderSettings.fogStartDistance;
            _outFogEnd = RenderSettings.fogEndDistance;
            _outFogColor = RenderSettings.fogColor;
            _outAmbient = RenderSettings.ambientLight;
            _outAmbientMode = RenderSettings.ambientMode;

            Camera cam = viewer != null ? viewer.GetComponent<Camera>() : null;
            if (cam != null && detailLayer >= 0 && detailLayer < 32)
            {
                float[] d = cam.layerCullDistances;
                if (d == null || d.Length != 32) d = new float[32];
                d[detailLayer] = detailCullDistance;
                cam.layerCullDistances = d;
            }
            _init = true;
            Apply(false, true);
        }

        void LateUpdate()
        {
            if (!_init || colony == null || viewer == null) return;
            Vector3 p = viewer.position;
            bool inside = colony.IsInside(p) && p.z > colony.nearCapCenter.z + insideMargin;
            if (inside != _state) Apply(inside, false);
        }

        void Apply(bool inside, bool force)
        {
            if (!force && inside == _state) return;
            _state = inside;
            Inside = inside;
            for (int i = 0; i < _exterior.Count; i++) if (_exterior[i] != null) _exterior[i].enabled = !inside;
            for (int i = 0; i < _detail.Count; i++) if (_detail[i] != null) _detail[i].enabled = inside;

            if (inside)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogStartDistance = fogStart;
                RenderSettings.fogEndDistance = fogEnd;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = insideAmbient;
            }
            else
            {
                RenderSettings.fogColor = _outFogColor;
                RenderSettings.fogStartDistance = _outFogStart;
                RenderSettings.fogEndDistance = _outFogEnd;
                RenderSettings.ambientMode = _outAmbientMode;
                RenderSettings.ambientLight = _outAmbient;
            }
        }

        void OnDisable()
        {
            if (_init && _state) Apply(false, true);
        }
    }
}
