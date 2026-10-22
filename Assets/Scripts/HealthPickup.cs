using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A heart lying in the maze (one per half, spawned by CaveDungeonGenerator). It is carried like the cube, so it can be
/// sent to the other player through the item portal; F while carrying it restores one heart and uses it up.
/// The model is built in code (a red orb with a white cross), so no art asset is needed.
/// </summary>
[RequireComponent(typeof(SphereCollider))]
public class HealthPickup : CarryableItem
{
    [Header("Visual")]
    [SerializeField] private Material orbMaterial;
    [SerializeField] private Material crossMaterial;
    [SerializeField] private float spinSpeed = 90f;
    [SerializeField] private float bobHeight = 0.08f;

    private Transform visual;

    public override string DisplayName => "heart";
    public override bool HasUseAction => true;

    protected override void Awake()
    {
        base.Awake();
        CreateVisual();
    }

    protected override void Update()
    {
        base.Update();

        // Only the child model moves; the networked root stays where the item really is.
        if (visual != null)
        {
            visual.localRotation = Quaternion.Euler(0f, Time.time * spinSpeed, 0f);
            visual.localPosition = Vector3.up * (Mathf.Sin(Time.time * 2.5f) * bobHeight);
        }
    }

    public override bool TryUse(out string refusal)
    {
        refusal = null;
        DungeonProgressManager manager = DungeonProgressManager.Instance;

        if (!IsCarriedByLocalPlayer || manager == null || NetworkManager == null)
            return false;

        if (manager.GetHearts(NetworkManager.LocalClientId) >= manager.MaxHearts)
        {
            refusal = "Your hearts are full - pass it to your friend";
            return false;
        }

        UseRpc();
        return true;
    }

    [Rpc(SendTo.Server)]
    private void UseRpc(RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        DungeonProgressManager manager = DungeonProgressManager.Instance;

        if (!IsSpawned || manager == null || !manager.HealPlayer(senderId))
            return;

        HealedRpc(RpcTarget.Single(senderId, RpcTargetUse.Temp));
        NetworkObject.Despawn();
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void HealedRpc(RpcParams rpcParams)
    {
        AudioManager.Play(Sfx.Heal);
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
