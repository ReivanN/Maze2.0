using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Item portal: one on each half of the maze, linked in a pair by CaveDungeonGenerator. An item (the cube) that is
/// put down or lands on a portal comes out of the other one. Players walk over portals without being moved -
/// the halves stay separate, only items can be passed.
///
/// The linking and the teleport happen on the server only; clients just show the portal and the effect.
/// The model is built in code (glowing discs, a floating crystal and a light), so no art asset is needed.
/// </summary>
public class ItemPortal : NetworkBehaviour
{
    [Header("Visual")]
    [SerializeField] private Material baseMaterial;
    [SerializeField] private Material glowMaterial;
    [SerializeField] private Color lightColor = new Color(0.65f, 0.35f, 1f, 1f);

    [Header("Teleport")]
    [Tooltip("How far from the portal center (on X and Z) an item counts as lying in the portal.")]
    [SerializeField] private float zoneHalfSize = 0.85f;
    [Tooltip("Items higher than this above the portal (carried, flying over) are not teleported.")]
    [SerializeField] private float zoneHeight = 1.3f;

    private ItemPortal linkedPortal;
    private Transform crystal;
    private Transform glowDisc;

    /// <summary>Server: the portal on the other half.</summary>
    public void Link(ItemPortal other)
    {
        linkedPortal = other;
    }

    private void Awake()
    {
        CreateVisual();
    }

    private void Update()
    {
        AnimateVisual();

        if (IsServer && IsSpawned && linkedPortal != null && linkedPortal.IsSpawned)
            TeleportItemsInZone();
    }

    private void TeleportItemsInZone()
    {
        // Copy: teleporting does not change the list, but despawns elsewhere could.
        CarryableItem[] items = new CarryableItem[CarryableItem.Instances.Count];
        for (int i = 0; i < items.Length; i++)
            items[i] = CarryableItem.Instances[i];

        foreach (CarryableItem item in items)
        {
            if (item == null || !item.IsFree)
                continue;

            bool inZone = IsInZone(item.transform.position);

            // The item came out of this portal: ignore it until it has been taken out of the zone.
            if (item.PortalImmunity == this)
            {
                if (!inZone)
                    item.PortalImmunity = null;

                continue;
            }

            if (!inZone)
                continue;

            Vector3 destination = linkedPortal.transform.position + Vector3.up * (item.HalfHeight + 0.05f);
            item.PortalImmunity = linkedPortal;
            item.ServerTeleport(destination);
            PlayTeleportEffectRpc(transform.position, linkedPortal.transform.position);
        }
    }

    private bool IsInZone(Vector3 position)
    {
        Vector3 offset = position - transform.position;
        return Mathf.Abs(offset.x) <= zoneHalfSize &&
               Mathf.Abs(offset.z) <= zoneHalfSize &&
               offset.y >= -0.5f && offset.y <= zoneHeight;
    }

    // Both players hear the item leave and arrive, each at their own portal.
    [Rpc(SendTo.ClientsAndHost)]
    private void PlayTeleportEffectRpc(Vector3 from, Vector3 to)
    {
        AudioManager.PlayAt(Sfx.PortalTeleport, from, 20f);
        AudioManager.PlayAt(Sfx.PortalTeleport, to, 20f);
    }

    // ================= VISUAL =================

    private void CreateVisual()
    {
        Transform visual = new GameObject("Portal Model").transform;
        visual.SetParent(transform, false);

        CreatePart(visual, PrimitiveType.Cylinder, new Vector3(0f, 0.02f, 0f), new Vector3(1.7f, 0.02f, 1.7f), baseMaterial);
        glowDisc = CreatePart(visual, PrimitiveType.Cylinder, new Vector3(0f, 0.045f, 0f), new Vector3(1.3f, 0.02f, 1.3f), glowMaterial);
        crystal = CreatePart(visual, PrimitiveType.Cube, new Vector3(0f, 1.3f, 0f), new Vector3(0.28f, 0.28f, 0.28f), glowMaterial);

        GameObject lightObject = new GameObject("Portal Light");
        lightObject.transform.SetParent(visual, false);
        lightObject.transform.localPosition = new Vector3(0f, 1f, 0f);

        Light portalLight = lightObject.AddComponent<Light>();
        portalLight.type = LightType.Point;
        portalLight.color = lightColor;
        portalLight.range = 4f;
        portalLight.intensity = 2f;
        portalLight.shadows = LightShadows.None;
    }

    private void AnimateVisual()
    {
        if (crystal != null)
        {
            crystal.localRotation = Quaternion.Euler(45f, Time.time * 80f, 45f);
            crystal.localPosition = new Vector3(0f, 1.3f + Mathf.Sin(Time.time * 2f) * 0.12f, 0f);
        }

        if (glowDisc != null)
        {
            float pulse = 1.3f + Mathf.Sin(Time.time * 3f) * 0.08f;
            glowDisc.localScale = new Vector3(pulse, 0.02f, pulse);
        }
    }

    private static Transform CreatePart(Transform parent, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        DestroyImmediate(part.GetComponent<Collider>()); // players and items pass over the portal
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;

        if (material != null)
            part.GetComponent<Renderer>().sharedMaterial = material;

        return part.transform;
    }
}
