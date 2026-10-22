using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Cube the players carry onto the laser pressure plate (spawned by CaveDungeonGenerator).
/// E picks it up (it floats above the player's head), E again puts it down in front of the player,
/// the left mouse button throws it. Input and the on-screen hint live in CubeCarrier on the local player.
///
/// Whoever holds the cube becomes its owner, so carrying and throwing are simulated on that player's machine
/// without lag and ClientNetworkTransform sends the position to the other player.
///
/// The body is always kinematic and moved by casts instead of physics: a dynamic box sliding over the floor
/// catches on the seams between the floor tiles. The cast box is lifted off the floor, so only walls and players
/// stop a thrown cube, and it slides along walls instead of sticking to them.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(BoxCollider))]
public class PushableCube : NetworkBehaviour
{
    public const ulong NoCarrier = ulong.MaxValue;

    [Tooltip("The cube fell off the level (for example from a falling platform): it goes back to its spawn point.")]
    [SerializeField] private float fallResetHeight = -5f;

    [Header("Carrying")]
    [Tooltip("Height of the cube center above the carrier's feet: a little above the head.")]
    [SerializeField] private float carryHeight = 2.4f;
    [Tooltip("How far in front of the player the cube is put down.")]
    [SerializeField] private float dropDistance = 1.2f;
    [Tooltip("Server check: the player must be at most this far from the cube to pick it up.")]
    [SerializeField] private float maxPickUpDistance = 3.5f;

    [Header("Throwing")]
    [SerializeField] private float throwSpeed = 7f;
    [SerializeField] private float throwUpSpeed = 3f;
    [Tooltip("How fast a thrown cube stops sliding once it lands, m/s per second.")]
    [SerializeField] private float groundFriction = 12f;

    private const float Skin = 0.02f;
    private const float FloorClearance = 0.15f; // floor bumps lower than this do not stop the cube
    private const float Gravity = -20f;
    private const float PickUpRequestTimeout = 1f;

    private static readonly List<PushableCube> instances = new List<PushableCube>();

    private readonly NetworkVariable<bool> isLocked = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<ulong> carrierId = new NetworkVariable<ulong>(
        NoCarrier,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Rigidbody body;
    private BoxCollider box;
    private Vector3 spawnPosition;
    private bool carrierReady;

    private Vector3 horizontalVelocity;
    private float verticalSpeed;

    private bool carriedLocally;
    private bool releasePending;     // put down / thrown here, the server has not cleared the carrier yet
    private float pickUpRequestTime = -10f;
    private Transform carrierTransform;

    public static IReadOnlyList<PushableCube> Instances => instances;

    public bool IsLocked => isLocked.Value;
    public bool IsCarried => carrierId.Value != NoCarrier;
    public bool IsCarriedByLocalPlayer => carriedLocally;
    public bool CanBePickedUp => IsSpawned && !isLocked.Value && !IsCarried &&
                                 Time.time - pickUpRequestTime > PickUpRequestTimeout;

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
        instances.Add(this);
        carrierId.OnValueChanged += OnCarrierChanged;
        UpdateCollider();
    }

    public override void OnNetworkDespawn()
    {
        instances.Remove(this);
        carrierId.OnValueChanged -= OnCarrierChanged;
        carriedLocally = false;
    }

    public override void OnLostOwnership()
    {
        carriedLocally = false;
        releasePending = false;
    }

    private void Update()
    {
        if (!IsSpawned)
            return;

        // The local player can spawn after the cube (a client joining an already built level).
        if (!carrierReady)
            carrierReady = CubeCarrier.EnsureOnLocalPlayer();

        // The server gave us the cube: start holding it.
        if (!carriedLocally && !releasePending && IsOwner && NetworkManager != null &&
            carrierId.Value == NetworkManager.LocalClientId)
        {
            StartCarrying();
        }

        // The carrier left the game: the cube drops where it is.
        if (IsServer && IsCarried && !NetworkManager.ConnectedClients.ContainsKey(carrierId.Value))
            carrierId.Value = NoCarrier;
    }

    private void LateUpdate()
    {
        if (!carriedLocally)
            return;

        if (carrierTransform == null || !IsOwner)
        {
            carriedLocally = false;
            return;
        }

        // After the player moved this frame, so the cube does not lag behind.
        Vector3 position = carrierTransform.position + Vector3.up * carryHeight;
        transform.position = position;
        body.position = position;
    }

    private void FixedUpdate()
    {
        // Only the owner moves the cube; everyone else follows the synced transform.
        if (!IsSpawned || !IsOwner || isLocked.Value || carriedLocally)
            return;

        Vector3 position = body.position;

        if (horizontalVelocity.sqrMagnitude > 0.0001f)
            position = MoveHorizontally(position, horizontalVelocity * Time.fixedDeltaTime);

        position = ApplyGravity(position, out bool grounded);

        if (grounded)
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, Vector3.zero, groundFriction * Time.fixedDeltaTime);

        if (position.y < fallResetHeight)
        {
            position = spawnPosition;
            horizontalVelocity = Vector3.zero;
            verticalSpeed = 0f;
        }

        body.MovePosition(position);
    }

    // ================= CALLED BY THE LOCAL PLAYER (CubeCarrier) =================

    public void RequestPickUp()
    {
        if (!CanBePickedUp)
            return;

        pickUpRequestTime = Time.time;
        RequestPickUpRpc();
    }

    /// <summary>Puts the cube on the floor next to the player (in front if there is room). False if there is no room anywhere.</summary>
    public bool TryPutDown(Transform player)
    {
        if (!carriedLocally)
            return false;

        Vector3 half = HalfExtents;
        Vector3 forward = Flatten(player.forward, Vector3.forward);
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3[] directions = { forward, -forward, right, -right };

        foreach (Vector3 direction in directions)
        {
            Vector3 position = player.position + direction * dropDistance + Vector3.up * (half.y + 0.05f);

            // The cube's own collider is off while it is carried, so it does not block its own landing spot.
            if (Physics.CheckBox(position, half * 0.95f, transform.rotation, ~0, QueryTriggerInteraction.Ignore))
                continue;

            Release(position, Vector3.zero, 0f);
            return true;
        }

        return false;
    }

    public void Throw(Vector3 direction)
    {
        if (!carriedLocally)
            return;

        Release(transform.position, Flatten(direction, Vector3.forward) * throwSpeed, throwUpSpeed);
    }

    // ================= SERVER =================

    /// <summary>Server only: the cube reached the plate. Snaps it onto the plate and stops it from being moved.</summary>
    public void LockOnPlate(Vector3 position)
    {
        if (!IsServer || isLocked.Value || IsCarried)
            return;

        isLocked.Value = true;

        // The server becomes the authority, so the snapped position is what both players see.
        if (!IsOwner)
            NetworkObject.RemoveOwnership();

        transform.position = position;
        body.position = position;
    }

    [Rpc(SendTo.Server)]
    private void RequestPickUpRpc(RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        if (isLocked.Value || IsCarried)
            return;

        if (!NetworkManager.ConnectedClients.TryGetValue(senderId, out NetworkClient client) || client.PlayerObject == null)
            return;

        if (Vector3.Distance(client.PlayerObject.transform.position, transform.position) > maxPickUpDistance)
            return;

        if (OwnerClientId != senderId)
            NetworkObject.ChangeOwnership(senderId);

        carrierId.Value = senderId;
    }

    [Rpc(SendTo.Server)]
    private void ReleaseRpc(RpcParams rpcParams = default)
    {
        if (carrierId.Value == rpcParams.Receive.SenderClientId)
            carrierId.Value = NoCarrier;
    }

    // ================= LOCAL =================

    private void StartCarrying()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.LocalClient == null || networkManager.LocalClient.PlayerObject == null)
            return;

        carrierTransform = networkManager.LocalClient.PlayerObject.transform;
        carriedLocally = true;
        horizontalVelocity = Vector3.zero;
        verticalSpeed = 0f;
        UpdateCollider();
    }

    private void Release(Vector3 position, Vector3 velocity, float upSpeed)
    {
        carriedLocally = false;
        releasePending = true;

        transform.position = position;
        body.position = position;
        horizontalVelocity = velocity;
        verticalSpeed = upSpeed;

        UpdateCollider();
        ReleaseRpc();
    }

    private void OnCarrierChanged(ulong oldValue, ulong newValue)
    {
        if (newValue == NoCarrier)
            releasePending = false;

        UpdateCollider();
    }

    // A carried cube must not collide with its carrier (it would push the player into the air).
    private void UpdateCollider()
    {
        if (box != null)
            box.enabled = !IsCarried || releasePending;
    }

    private Vector3 HalfExtents => Vector3.Scale(box.size * 0.5f, transform.lossyScale);

    private static Vector3 Flatten(Vector3 direction, Vector3 fallback)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : fallback;
    }

    // Moves as far as the walls allow; what is left of the move slides along the wall that was hit,
    // and the thrown velocity loses the part that went into the wall.
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

            Vector3 normal = new Vector3(hit.normal.x, 0f, hit.normal.z);
            if (normal.sqrMagnitude < 0.0001f)
                break;

            normal.Normalize();
            Vector3 remaining = delta - direction * allowed;
            delta = remaining - normal * Vector3.Dot(remaining, normal);
            horizontalVelocity -= normal * Mathf.Min(0f, Vector3.Dot(horizontalVelocity, normal));
        }

        return position;
    }

    // Keeps the cube on the floor and lets it fall where there is none (a falling platform dropped, a throw).
    private Vector3 ApplyGravity(Vector3 position, out bool grounded)
    {
        float halfHeight = HalfExtents.y;

        // The ray starts inside the cube, so it does not hit the cube's own collider.
        if (verticalSpeed <= 0f &&
            Physics.Raycast(position, Vector3.down, out RaycastHit hit, halfHeight + 0.3f, ~0, QueryTriggerInteraction.Ignore))
        {
            grounded = true;
            verticalSpeed = 0f;
            position.y = hit.point.y + halfHeight;
            return position;
        }

        grounded = false;
        verticalSpeed += Gravity * Time.fixedDeltaTime;
        position.y += verticalSpeed * Time.fixedDeltaTime;
        return position;
    }
}
