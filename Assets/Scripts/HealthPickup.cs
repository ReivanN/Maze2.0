using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A heart lying in the maze (one per half, spawned by CaveDungeonGenerator). A player who walks over it puts it into
/// the inventory (DungeonProgressManager keeps the count, HeartsHud shows it); F uses one and restores a heart.
/// With a full inventory the heart stays on the floor. The model is built in code (a red orb with a white cross).
/// </summary>
[RequireComponent(typeof(SphereCollider))]
public class HealthPickup : NetworkBehaviour
{
    [Header("Pick up")]
    [SerializeField] private float pickUpRadius = 1.1f;

    [Header("Visual")]
    [SerializeField] private Material orbMaterial;
    [SerializeField] private Material crossMaterial;
    [SerializeField] private float spinSpeed = 90f;
    [SerializeField] private float bobHeight = 0.08f;

    private Transform visual;
    private bool pickedUp;

    private void Awake()
    {
        // It used to be carried like the cube; now nothing pushes against it and it never falls.
        GetComponent<SphereCollider>().isTrigger = true;

        if (TryGetComponent(out Rigidbody body))
        {
            body.isKinematic = true;
            body.useGravity = false;
        }

        CreateVisual();
    }

    private void Update()
    {
        // Only the child model moves; the networked root stays where the item really is.
        if (visual != null)
        {
            visual.localRotation = Quaternion.Euler(0f, Time.time * spinSpeed, 0f);
            visual.localPosition = Vector3.up * (Mathf.Sin(Time.time * 2.5f) * bobHeight);
        }

        if (IsServer && IsSpawned && !pickedUp)
            TryGiveToPlayer();
    }

    private void TryGiveToPlayer()
    {
        DungeonProgressManager manager = DungeonProgressManager.Instance;
        if (manager == null)
            return;

        foreach (NetworkClient client in NetworkManager.ConnectedClientsList)
        {
            NetworkObject player = client.PlayerObject;
            if (player == null)
                continue;

            Vector3 offset = player.transform.position - transform.position;
            if (Mathf.Abs(offset.y) > 1.6f || new Vector2(offset.x, offset.z).magnitude > pickUpRadius)
                continue;

            if (!manager.AddHeal(client.ClientId))
                continue;

            pickedUp = true;
            PickedUpRpc(RpcTarget.Single(client.ClientId, RpcTargetUse.Temp));
            NetworkObject.Despawn();
            return;
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void PickedUpRpc(RpcParams rpcParams)
    {
        AudioManager.Play(Sfx.KeyPickup);
    }

    private void CreateVisual()
    {
        visual = new GameObject("Heart Model").transform;
        visual.SetParent(transform, false);

        // Sized to the collider; the root's scale is 1.
        float radius = GetComponent<SphereCollider>().radius;

        CreatePart(PrimitiveType.Sphere, Vector3.zero, Vector3.one * radius * 2f, orbMaterial);
        // A white cross through the orb, visible from every side.
        CreatePart(PrimitiveType.Cube, Vector3.zero, new Vector3(radius * 2.3f, radius * 0.6f, radius * 0.6f), crossMaterial);
        CreatePart(PrimitiveType.Cube, Vector3.zero, new Vector3(radius * 0.6f, radius * 2.3f, radius * 0.6f), crossMaterial);
        CreatePart(PrimitiveType.Cube, Vector3.zero, new Vector3(radius * 0.6f, radius * 0.6f, radius * 2.3f), crossMaterial);
    }

    private void CreatePart(PrimitiveType type, Vector3 position, Vector3 scale, Material material)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        DestroyImmediate(part.GetComponent<Collider>()); // only the root's collider counts
        part.transform.SetParent(visual, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;

        if (material != null)
            part.GetComponent<Renderer>().sharedMaterial = material;
    }
}
