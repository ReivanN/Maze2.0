using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// An item the players can carry: E picks it up (it floats above the player's head), E again puts it down
/// in front of the player, the left mouse button throws it. Input and the on-screen hints live in ItemCarrier
/// on the local player. ItemPortal moves items that lie in it to the other half of the maze.
///
/// Whoever holds the item becomes its owner, so carrying and throwing are simulated on that player's machine
/// without lag and ClientNetworkTransform sends the position to the other player.
///
/// The body is always kinematic and moved by casts instead of physics: a dynamic body sliding over the floor
/// catches on the seams between the floor tiles. The cast box is lifted off the floor, so only walls and players
/// stop a thrown item, and it slides along walls instead of sticking to them.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public abstract class CarryableItem : NetworkBehaviour
{
    public const ulong NoCarrier = ulong.MaxValue;

    [Tooltip("The item fell off the level (for example from a falling platform): it goes back to its spawn point.")]
    [SerializeField] private float fallResetHeight = -5f;

    [Header("Carrying")]
    [Tooltip("Height of the item center above the carrier's feet: a little above the head.")]
    [SerializeField] private float carryHeight = 2.4f;
    [Tooltip("How far in front of the player the item is put down.")]
    [SerializeField] private float dropDistance = 1.2f;
    [Tooltip("Server check: the player must be at most this far from the item to pick it up.")]
    [SerializeField] private float maxPickUpDistance = 3.5f;

    [Header("Throwing")]
    [SerializeField] private float throwSpeed = 7f;
    [SerializeField] private float throwUpSpeed = 3f;
    [Tooltip("How fast a thrown item stops sliding once it lands, m/s per second.")]
    [SerializeField] private float groundFriction = 12f;

    private const float Skin = 0.02f;
    private const float FloorClearance = 0.15f; // floor bumps lower than this do not stop the item
    private const float Gravity = -20f;
    private const float PickUpRequestTimeout = 1f;

    private static readonly List<CarryableItem> instances = new List<CarryableItem>();

    private readonly NetworkVariable<ulong> carrierId = new NetworkVariable<ulong>(
        NoCarrier,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    protected Rigidbody Body { get; private set; }
    private Collider itemCollider;
    private Vector3 halfExtents;
    private Vector3 spawnPosition;
    private bool carrierReady;

    private Vector3 horizontalVelocity;
    private float verticalSpeed;

    private bool carriedLocally;
    private bool releasePending;     // put down / thrown here, the server has not cleared the carrier yet
    private float pickUpRequestTime = -10f;
    private Transform carrierTransform;

    public static IReadOnlyList<CarryableItem> Instances => instances;

    /// <summary>Name in the on-screen hints ("[E] Pick up cube").</summary>
    public abstract string DisplayName { get; }

    /// <summary>Items with an extra action while carried (F), for example eating something.</summary>
    public virtual bool HasUseAction => false;

    /// <summary>An item that must not be picked up or moved any more (the cube locked on the plate).</summary>
    protected virtual bool IsFrozen => false;

    /// <summary>Server: the portal this item just came out of; it is ignored until the item leaves it.</summary>
    public ItemPortal PortalImmunity { get; set; }

    public bool IsCarried => carrierId.Value != NoCarrier;
    /// <summary>Client id of the player holding the item, NoCarrier when it lies free (synced to everyone).</summary>
    public ulong CarrierId => carrierId.Value;
    public bool IsCarriedByLocalPlayer => carriedLocally;
    public bool IsFree => IsSpawned && !IsCarried && !IsFrozen;
    public bool CanBePickedUp => IsFree && Time.time - pickUpRequestTime > PickUpRequestTimeout;
    public float HalfHeight => halfExtents.y;

    protected virtual void Awake()
    {
        Body = GetComponent<Rigidbody>();
        itemCollider = GetComponent<Collider>();

        // Measured while the collider is still on: a disabled collider has empty bounds.
        halfExtents = itemCollider != null ? itemCollider.bounds.extents : Vector3.one * 0.25f;

        Body.isKinematic = true;
        Body.useGravity = false;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
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

    protected virtual void Update()
    {
        if (!IsSpawned)
            return;

        // The local player can spawn after the item (a client joining an already built level).
        if (!carrierReady)
            carrierReady = ItemCarrier.EnsureOnLocalPlayer();

        // The server gave us the item: start holding it.
        if (!carriedLocally && !releasePending && IsOwner && NetworkManager != null &&
            carrierId.Value == NetworkManager.LocalClientId)
        {
            StartCarrying();
        }

        // The carrier left the game: the item drops where it is.
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

        // After the player moved this frame, so the item does not lag behind.
        Vector3 position = carrierTransform.position + Vector3.up * carryHeight;
        transform.position = position;
        Body.position = position;
    }

    private void FixedUpdate()
    {
        // Only the owner moves the item; everyone else follows the synced transform.
        if (!IsSpawned || !IsOwner || IsFrozen || carriedLocally)
            return;

        Vector3 position = Body.position;

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

        Body.MovePosition(position);
    }

    // ================= CALLED BY THE LOCAL PLAYER (ItemCarrier) =================

    public void RequestPickUp()
    {
        if (!CanBePickedUp)
            return;

        pickUpRequestTime = Time.time;
        RequestPickUpRpc();
    }

    /// <summary>Puts the item on the floor next to the player (in front if there is room). False if there is no room anywhere.</summary>
    public bool TryPutDown(Transform player)
    {
        if (!carriedLocally)
            return false;

        Vector3 forward = Flatten(player.forward, Vector3.forward);
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3[] directions = { forward, -forward, right, -right };

        foreach (Vector3 direction in directions)
        {
            Vector3 position = player.position + direction * dropDistance + Vector3.up * (halfExtents.y + 0.05f);

            // The item's own collider is off while it is carried, so it does not block its own landing spot.
            if (Physics.CheckBox(position, halfExtents * 0.95f, transform.rotation, ~0, QueryTriggerInteraction.Ignore))
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

    /// <summary>The extra action while carried (F). Returns false with a hint for the player when it cannot be done.</summary>
    public virtual bool TryUse(out string refusal)
    {
        refusal = null;
        return false;
    }

    // ================= SERVER =================

    /// <summary>
    /// Server: moves a free item somewhere else instantly (item portal). The server takes the item over so it is the
    /// authority for the jump, and NetworkTransform.Teleport makes the other player see a jump, not a slide across the maze.
    /// </summary>
    public void ServerTeleport(Vector3 position)
    {
        if (!IsServer || IsCarried)
            return;

        if (!IsOwner)
            NetworkObject.RemoveOwnership();

        horizontalVelocity = Vector3.zero;
        verticalSpeed = 0f;

        NetworkTransform networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform != null && networkTransform.CanCommitToTransform)
            networkTransform.Teleport(position, transform.rotation, transform.localScale);
        else
            transform.position = position;

        Body.position = position;
    }

    /// <summary>Server: takes the item away from whoever holds it, so the server can move it.</summary>
    protected void ServerTakeOver()
    {
        if (IsServer && !IsOwner)
            NetworkObject.RemoveOwnership();
    }

    [Rpc(SendTo.Server)]
    private void RequestPickUpRpc(RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        if (IsFrozen || IsCarried)
            return;

        if (!NetworkManager.ConnectedClients.TryGetValue(senderId, out NetworkClient client) || client.PlayerObject == null)
            return;

        if (Vector3.Distance(client.PlayerObject.transform.position, transform.position) > maxPickUpDistance)
            return;

        if (OwnerClientId != senderId)
            NetworkObject.ChangeOwnership(senderId);

        PortalImmunity = null;
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
        Body.position = position;
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

    // A carried item must not collide with its carrier (it would push the player into the air).
    private void UpdateCollider()
    {
        if (itemCollider != null)
            itemCollider.enabled = !IsCarried || releasePending;
    }

    private static Vector3 Flatten(Vector3 direction, Vector3 fallback)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : fallback;
    }

    // Moves as far as the walls allow; what is left of the move slides along the wall that was hit,
    // and the thrown velocity loses the part that went into the wall.
    private Vector3 MoveHorizontally(Vector3 position, Vector3 delta)
    {
        Vector3 castHalf = new Vector3(halfExtents.x - Skin, Mathf.Max(0.02f, halfExtents.y - FloorClearance * 0.5f), halfExtents.z - Skin);
        Vector3 castOffset = Vector3.up * (FloorClearance * 0.5f);

        for (int i = 0; i < 2 && delta.sqrMagnitude > 0.000001f; i++)
        {
            float distance = delta.magnitude;
            Vector3 direction = delta / distance;

            // The cast starts inside the item, and casts ignore colliders they start in, so the item does not hit itself.
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

    // Keeps the item on the floor and lets it fall where there is none (a falling platform dropped, a throw).
    private Vector3 ApplyGravity(Vector3 position, out bool grounded)
    {
        // The ray starts inside the item, so it does not hit the item's own collider.
        if (verticalSpeed <= 0f &&
            Physics.Raycast(position, Vector3.down, out RaycastHit hit, halfExtents.y + 0.3f, ~0, QueryTriggerInteraction.Ignore))
        {
            grounded = true;
            verticalSpeed = 0f;
            position.y = hit.point.y + halfExtents.y;
            return position;
        }

        grounded = false;
        verticalSpeed += Gravity * Time.fixedDeltaTime;
        position.y += verticalSpeed * Time.fixedDeltaTime;
        return position;
    }
}
