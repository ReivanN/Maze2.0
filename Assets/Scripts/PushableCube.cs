using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Cube the players push onto the laser pressure plate (spawned by CaveDungeonGenerator).
///
/// Whoever pushes the cube becomes its owner, so the push is simulated on that player's machine without lag
/// and ClientNetworkTransform sends the position to the other player.
///
/// The body is always kinematic and moved by casts instead of physics: a dynamic box sliding over the floor
/// catches on the seams between the floor tiles and stops dead. The cast box is lifted off the floor,
/// so only walls and players stop the cube, and it slides along walls instead of sticking to them.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(BoxCollider))]
public class PushableCube : NetworkBehaviour
{
    [Tooltip("The cube fell off the level (for example from a falling platform): it goes back to its spawn point.")]
    [SerializeField] private float fallResetHeight = -5f;

    private const float PushHoldTime = 0.1f;  // keeps moving between two contacts of the pushing player
    private const float Skin = 0.02f;
    private const float FloorClearance = 0.15f; // floor bumps lower than this do not stop the cube
    private const float Gravity = -20f;

    private readonly NetworkVariable<bool> isLocked = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Rigidbody body;
    private BoxCollider box;
    private Vector3 spawnPosition;
    private bool ownershipRequested;
    private bool pusherReady;

    private Vector3 pushDirection;
    private float pushSpeed;
    private float lastPushTime = -1f;
    private float verticalSpeed;

    public bool IsLocked => isLocked.Value;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        box = GetComponent<BoxCollider>();

        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    public override void OnNetworkSpawn()
    {
        spawnPosition = transform.position;
    }

    public override void OnGainedOwnership()
    {
        ownershipRequested = false;
        verticalSpeed = 0f;
    }

    public override void OnLostOwnership()
    {
        ownershipRequested = false;
    }

    private void Update()
    {
        // The local player can spawn after the cube (a client joining an already built level).
        if (!pusherReady && IsSpawned)
            pusherReady = CubePusher.EnsureOnLocalPlayer();
    }

    private void FixedUpdate()
    {
        // Only the owner moves the cube; everyone else follows the synced transform.
        if (!IsSpawned || !IsOwner || isLocked.Value)
            return;

        Vector3 position = body.position;

        if (Time.time - lastPushTime <= PushHoldTime)
            position = MoveHorizontally(position, pushDirection * (pushSpeed * Time.fixedDeltaTime));

        position = ApplyGravity(position);

        if (position.y < fallResetHeight)
        {
            position = spawnPosition;
            verticalSpeed = 0f;
        }

        body.MovePosition(position);
    }

    /// <summary>Called by the local player's CubePusher when it walks into a cube it does not own.</summary>
    public void RequestControl()
    {
        if (IsOwner || ownershipRequested || isLocked.Value)
            return;

        ownershipRequested = true;
        RequestOwnershipRpc();
    }

    /// <summary>Owner only: the player is pushing the cube in this direction.</summary>
    public void Push(Vector3 direction, float speed)
    {
        if (!IsOwner || isLocked.Value)
            return;

        pushDirection = direction;
        pushSpeed = speed;
        lastPushTime = Time.time;
    }

    /// <summary>Server only: the cube reached the plate. Snaps it onto the plate and stops it from being pushed away.</summary>
    public void LockOnPlate(Vector3 position)
    {
        if (!IsServer || isLocked.Value)
            return;

        isLocked.Value = true;

        // The server becomes the authority, so the snapped position is what both players see.
        if (!IsOwner)
            NetworkObject.RemoveOwnership();

        transform.position = position;
        body.position = position;
    }

    [Rpc(SendTo.Server)]
    private void RequestOwnershipRpc(RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        if (isLocked.Value || OwnerClientId == senderId)
            return;

        NetworkObject.ChangeOwnership(senderId);
    }

    private Vector3 HalfExtents => Vector3.Scale(box.size * 0.5f, transform.lossyScale);

    // Moves as far as the walls allow; what is left of the move slides along the wall that was hit.
    private Vector3 MoveHorizontally(Vector3 position, Vector3 delta)
    {
        Vector3 half = HalfExtents;
        Vector3 castHalf = new Vector3(half.x - Skin, half.y - FloorClearance * 0.5f, half.z - Skin);
        Vector3 castOffset = Vector3.up * (FloorClearance * 0.5f);

        for (int i = 0; i < 2 && delta.sqrMagnitude > 0.000001f; i++)
        {
            float distance = delta.magnitude;
            Vector3 direction = delta / distance;

            // The cast starts inside the cube, and casts ignore colliders they start in, so the cube does not hit itself.
            if (!Physics.BoxCast(position + castOffset, castHalf, direction, out RaycastHit hit, transform.rotation,
                    distance + Skin, ~0, QueryTriggerInteraction.Ignore))
            {
                position += delta;
                break;
            }

            float allowed = Mathf.Max(0f, hit.distance - Skin);
            position += direction * allowed;

            Vector3 remaining = delta - direction * allowed;
            Vector3 normal = new Vector3(hit.normal.x, 0f, hit.normal.z);
            if (normal.sqrMagnitude < 0.0001f)
                break;

            normal.Normalize();
            delta = remaining - normal * Vector3.Dot(remaining, normal);
        }

        return position;
    }

    // Keeps the cube on the floor and lets it fall where there is none (a falling platform dropped).
    private Vector3 ApplyGravity(Vector3 position)
    {
        float halfHeight = HalfExtents.y;

        // The ray starts inside the cube, so it does not hit the cube's own collider.
        if (verticalSpeed <= 0f &&
            Physics.Raycast(position, Vector3.down, out RaycastHit hit, halfHeight + 0.3f, ~0, QueryTriggerInteraction.Ignore))
        {
            verticalSpeed = 0f;
            position.y = hit.point.y + halfHeight;
            return position;
        }

        verticalSpeed += Gravity * Time.fixedDeltaTime;
        position.y += verticalSpeed * Time.fixedDeltaTime;
        return position;
    }
}
