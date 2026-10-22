using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Cube the players carry onto the laser pressure plate (spawned by CaveDungeonGenerator, sometimes on the other
/// half of the maze, then it has to be sent through the item portal). Carrying, putting down and throwing are in
/// CarryableItem; this class only adds the lock on the plate.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class PushableCube : CarryableItem
{
    private readonly NetworkVariable<bool> isLocked = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public override string DisplayName => "cube";
    public bool IsLocked => isLocked.Value;
    protected override bool IsFrozen => isLocked.Value;

    /// <summary>Server only: the cube reached the plate. Snaps it onto the plate and stops it from being moved.</summary>
    public void LockOnPlate(Vector3 position)
    {
        if (!IsServer || isLocked.Value || IsCarried)
            return;

        isLocked.Value = true;

        // The server becomes the authority, so the snapped position is what both players see.
        ServerTakeOver();

        transform.position = position;
        Body.position = position;
    }
}
