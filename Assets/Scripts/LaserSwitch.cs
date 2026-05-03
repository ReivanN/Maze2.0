using Unity.Netcode;
using UnityEngine;

public class LaserSwitch : NetworkBehaviour
{
    [SerializeField] private LaserTrap targetLaser;
    [SerializeField] private bool disableLaser = true;
    [SerializeField] private bool oneTimeUse = false;

    private bool used;

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (used && oneTimeUse) return;
        if (!other.CompareTag("Player")) return;
        if (targetLaser == null) return;

        if (disableLaser)
            targetLaser.Disable();
        else
            targetLaser.Toggle();

        used = true;
    }

    public void SetTargetLaser(LaserTrap laserTrap)
    {
        targetLaser = laserTrap;
    }
}
