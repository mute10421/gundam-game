using UnityEngine;

namespace Gundam.Cockpit
{
    /// <summary>
    /// Reads the right joystick to aim a gun pivot, and fires when the stick is
    /// pushed forward past a threshold (like squeezing a rifle grip forward).
    /// Fires either a projectile prefab (if assigned) or a simple raycast hit
    /// against anything with a HitTarget component.
    /// </summary>
    /// <summary>One selectable weapon: display name, ammo capacity, fire rate.</summary>
    [System.Serializable]
    public class WeaponDef
    {
        public string weaponName = "MACHINE GUN";
        public int maxAmmo = 30;
        public float fireCooldown = 0.15f;
    }

    public class WeaponAimFireController : MonoBehaviour
    {
        public JoystickLever rightStick;
        public Transform gunPivot;
        public Transform muzzlePoint;
        public GameObject projectilePrefab;

        public float aimSpeed = 90f; // degrees/sec at full tilt
        public float maxPitch = 30f;
        public float maxYaw = 45f;

        public WeaponDef[] weapons = new WeaponDef[]
        {
            new WeaponDef { weaponName = "MACHINE GUN", maxAmmo = 30, fireCooldown = 0.15f },
            new WeaponDef { weaponName = "BEAM RIFLE",  maxAmmo = 6,  fireCooldown = 0.6f },
        };
        public int currentWeaponIndex = 0;

        public float projectileSpeed = 60f;
        public float fireCooldown = 0.2f;
        [Range(0.3f, 1f)] public float pushToFireThreshold = 0.85f;
        public float raycastRange = 200f;

        float _pitch;
        float _yaw;
        float _cooldownTimer;
        int _currentAmmo;

        /// <summary>Name of the currently selected weapon, for the dashboard readout.</summary>
        public string CurrentWeaponName =>
            (weapons != null && weapons.Length > 0) ? weapons[currentWeaponIndex].weaponName : "---";
        public int CurrentAmmo => _currentAmmo;
        public int CurrentMaxAmmo =>
            (weapons != null && weapons.Length > 0) ? weapons[currentWeaponIndex].maxAmmo : 0;

        void Awake()
        {
            ApplyCurrentWeapon();
        }

        /// <summary>Switches to the next weapon in the list and resets its ammo. Not yet wired to a physical control - call this from an input event once one is chosen (e.g. a joystick button).</summary>
        public void CycleWeapon()
        {
            if (weapons == null || weapons.Length == 0) return;
            currentWeaponIndex = (currentWeaponIndex + 1) % weapons.Length;
            ApplyCurrentWeapon();
        }

        void ApplyCurrentWeapon()
        {
            if (weapons != null && weapons.Length > 0)
            {
                fireCooldown = weapons[currentWeaponIndex].fireCooldown;
                _currentAmmo = weapons[currentWeaponIndex].maxAmmo;
            }
        }

        void Update()
        {
            if (rightStick == null || gunPivot == null) return;

            Vector2 t = rightStick.tiltInput;

            _yaw = Mathf.Clamp(_yaw + t.x * aimSpeed * Time.deltaTime, -maxYaw, maxYaw);
            _pitch = Mathf.Clamp(_pitch - t.y * aimSpeed * Time.deltaTime, -maxPitch, maxPitch);
            gunPivot.localRotation = Quaternion.Euler(_pitch, _yaw, 0f);

            _cooldownTimer -= Time.deltaTime;

            if (rightStick.isGrabbed && t.y > pushToFireThreshold)
            {
                TryFire();
            }
        }

        public void TryFire()
        {
            if (_cooldownTimer > 0f) return;
            if (_currentAmmo <= 0) return;
            _cooldownTimer = fireCooldown;
            _currentAmmo--;

            Transform origin = muzzlePoint != null ? muzzlePoint : gunPivot;

            if (projectilePrefab != null)
            {
                GameObject go = Instantiate(projectilePrefab, origin.position, origin.rotation);
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.linearVelocity = origin.forward * projectileSpeed;
                }
                Destroy(go, 3f);
            }
            else if (Physics.Raycast(origin.position, origin.forward, out RaycastHit hit, raycastRange))
            {
                HitTarget target = hit.collider.GetComponent<HitTarget>();
                if (target != null)
                {
                    target.OnHit();
                }
                Debug.DrawLine(origin.position, hit.point, Color.cyan, 0.15f);
            }
            else
            {
                Debug.DrawRay(origin.position, origin.forward * raycastRange, Color.cyan, 0.15f);
            }
        }
    }
}
