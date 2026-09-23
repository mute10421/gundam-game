using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Makes ExternalGundam (the big visible mecha body that HeadCam - see
    /// GundamCockpitSetup.PlaceExternalGundam - is parented deep inside, via
    /// its "Head" bone) translate together with the player's MobileSuitRoot.
    ///
    /// Per report ("LeftJoystick을 움직여도 실제 Gundam이 움직이는 것이 화면에서
    /// 보이지 않는다"): ExternalGundam was deliberately instantiated in WORLD
    /// space (SetParent(null)) at a fixed position, independent of
    /// MobileSuitRoot, so it wouldn't inherit the suit's own facing/rotation
    /// like the cockpit interior does (see PlaceExternalGundam's own comment).
    /// That also meant it never TRANSLATED either - and HeadCam (this object's
    /// own descendant) is the actual camera that renders the Cockpit_Dome /
    /// SysCheck_Left aux-screen exterior view (both the flat feed and the live
    /// 360 skybox, see GundamHeadCam360), so a joystick push that correctly
    /// moved MobileSuitRoot (see ShipMovementController - untouched by this
    /// fix) still produced zero visible change in either of those, because the
    /// camera generating that view simply never moved.
    ///
    /// This is the minimal fix: it captures ExternalGundam's ORIGINAL relative
    /// offset from MobileSuitRoot once (so it still starts out exactly where
    /// PlaceExternalGundam placed it), then every frame re-applies that same
    /// offset on top of MobileSuitRoot's CURRENT position - so ExternalGundam
    /// (and everything parented under it, including the Head bone and HeadCam)
    /// now translates rigidly along with the player's own movement.
    ///
    /// Position only - never rotation, and nothing here writes to
    /// MobileSuitRoot, JoystickLever, or any camera/HMD transform.
    /// ExternalGundam's own rotation stays exactly as PlaceExternalGundam set
    /// it (Quaternion.identity); GundamHeadCam360 separately still owns turning
    /// the Head BONE's rotation to track the pilot's own look direction, and
    /// this script never touches that.
    ///
    /// LateUpdate (not Update) so this applies AFTER ShipMovementController has
    /// already moved MobileSuitRoot for the frame - no one-frame lag between
    /// the suit moving and the exterior view following it.
    /// </summary>
    public class ExternalGundamFollower : MonoBehaviour
    {
        [Tooltip("MobileSuitRoot - the player's own suit/cockpit root this object should translate together with.")]
        public Transform target;

        Vector3 _offset;
        bool _haveOffset;

        void LateUpdate()
        {
            if (target == null) return;

            if (!_haveOffset)
            {
                // Captured on first use (not at scene-build time in the editor
                // script) so this is robust to whichever order components end
                // up initializing in - it's just "wherever I am relative to the
                // target right now" the very first time this runs, which is
                // still PlaceExternalGundam's original placement since nothing
                // moves either object before Play starts.
                _offset = transform.position - target.position;
                _haveOffset = true;
            }

            transform.position = target.position + _offset;
        }
    }
}
