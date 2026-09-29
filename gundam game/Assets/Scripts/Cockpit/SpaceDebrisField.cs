using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Distant space wreckage (broken colony / station / battleship pieces) - per
    /// request ("우주에 건물을 넣을려고 하는데 우주에 잔해같은거 거슬리지 않게",
    /// option "멀리 배경으로"). The pieces themselves are placed by
    /// GundamCockpitSetup (PlaceSpaceDebris) as children of this object, far
    /// outside the combat area; this script only keeps them in the background:
    ///
    ///  - The whole field follows the viewer (HeadCam) by `followFactor` of its
    ///    movement, so the wrecks drift past very slowly, as if they were many
    ///    kilometres away - they give a sense of place and motion but can never
    ///    be flown into, and never end up in the middle of a fight.
    ///  - Each piece tumbles very slowly around its own random axis.
    ///
    /// No colliders (the vulcan's aim ray and bullets ignore them). Only moves its
    /// own transforms.
    /// </summary>
    public class SpaceDebrisField : MonoBehaviour
    {
        [Tooltip("The Gundam's HeadCam. Found by name if empty.")]
        public Transform viewer;
        public string viewerName = "HeadCam";
        [Range(0f, 1f)]
        [Tooltip("How much of the viewer's movement the field copies. 0 = fixed in the world, 1 = moves with you (looks infinitely far). 0.9 = drifts past very slowly.")]
        public float followFactor = 0.9f;
        [Tooltip("Tumble speed range (deg/s) for each piece.")]
        public Vector2 tumbleSpeed = new Vector2(0.3f, 1.5f);
        public int seed = 777;

        Vector3 _startPos;
        Vector3 _viewerStart;
        bool _haveStart;
        Transform[] _pieces;
        Vector3[] _axes;
        float[] _speeds;
        float _findTimer;

        void Awake()
        {
            _startPos = transform.position;
            int n = transform.childCount;
            _pieces = new Transform[n];
            _axes = new Vector3[n];
            _speeds = new float[n];
            Random.State saved = Random.state;
            Random.InitState(seed);
            for (int i = 0; i < n; i++)
            {
                _pieces[i] = transform.GetChild(i);
                _axes[i] = Random.onUnitSphere;
                _speeds[i] = Random.Range(tumbleSpeed.x, tumbleSpeed.y) * (Random.value < 0.5f ? -1f : 1f);
            }
            Random.state = saved;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _pieces.Length; i++)
            {
                if (_pieces[i] != null) _pieces[i].Rotate(_axes[i], _speeds[i] * dt, Space.World);
            }

            if (viewer == null)
            {
                _findTimer -= dt;
                if (_findTimer > 0f) return;
                _findTimer = 1f;
                GameObject g = GameObject.Find(viewerName);
                if (g == null) return;
                viewer = g.transform;
            }
            if (!_haveStart)
            {
                _viewerStart = viewer.position;
                _haveStart = true;
            }
            transform.position = _startPos + (viewer.position - _viewerStart) * followFactor;
        }
    }
}
