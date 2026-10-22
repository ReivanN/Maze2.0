using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Floor spikes that keep coming out and going back in (spawned by CaveDungeonGenerator).
/// A player standing on them while they are out loses a heart but stays where they are.
///
/// The cycle is computed from the network server time plus a per-trap offset, so the spikes move the same way on
/// both players and nothing has to be sent every frame. The server alone decides who gets hurt.
///
/// The model (Spike_trap_Mk2/Spike-trap prefab) is created locally as a child. Its .blend file holds two versions of
/// the trap on top of each other: an old one (a static "Spiketrap_mainbody" frame and separate "Spiketrap_spikes")
/// and a rigged one ("Spiketrap", moved by a bone together with its lid), plus a big ground "Plane" from the Blender
/// scene. Its single imported clip plays all of that at once, which looked like the lid rising over the spikes.
/// So the rigged copy and the plane are hidden, the Animator is off, and only the spikes are moved here, in step
/// with the damage.
/// </summary>
public class SpikeTrap : NetworkBehaviour
{
    private const string BodyName = "Spiketrap_mainbody";
    private const string SpikesName = "Spiketrap_spikes";
    private static readonly string[] HiddenParts = { "Spiketrap", "Plane" };

    [Header("Visual")]
    [SerializeField] private GameObject visualPrefab;
    [Tooltip("The model is scaled to fit this size on X and Z (maze cells are 2 m).")]
    [SerializeField] private float visualSize = 1.8f;

    [Header("Cycle")]
    [Tooltip("Length of one strike: up, hold, back down.")]
    [SerializeField] private float animationLength = 2.04f;
    [Tooltip("Full cycle; after the strike the spikes stay hidden for the rest of it.")]
    [SerializeField] private float cycleDuration = 3f;
    [Tooltip("Parts of the strike (0..1): the spikes shoot up until riseEnd, stay up until holdEnd, are back down at lowerEnd.")]
    [SerializeField] private float riseEnd = 0.06f;
    [SerializeField] private float holdEnd = 0.4f;
    [SerializeField] private float lowerEnd = 0.55f;
    [Tooltip("The spikes hurt while they are out at least this much (0 = hidden, 1 = fully out).")]
    [SerializeField] private float dangerHeight = 0.4f;

    [Header("Damage")]
    [Tooltip("Area above the trap where a player is hurt, centered on the trap and standing on the floor.")]
    [SerializeField] private Vector3 damageAreaSize = new Vector3(1.6f, 1.2f, 1.6f);
    [SerializeField] private float soundMaxDistance = 12f;

    private readonly NetworkVariable<float> phaseOffset = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly HashSet<ulong> hurtThisCycle = new HashSet<ulong>();
    private Transform spikes;
    private Vector3 spikesUpLocal;
    private Vector3 spikesDownLocal;
    private bool spikesWereOut;

    /// <summary>Server: shifts this trap's cycle so neighbouring traps do not move in sync.</summary>
    public void SetPhaseOffset(float offset)
    {
        if (IsServer)
            phaseOffset.Value = offset;
    }

    private void Awake()
    {
        CreateVisual();
    }

    private void Update()
    {
        float height = GetSpikeHeight(GetStrikeTime());

        if (spikes != null)
            spikes.localPosition = Vector3.LerpUnclamped(spikesDownLocal, spikesUpLocal, height);

        bool spikesOut = height >= dangerHeight;

        if (spikesOut && !spikesWereOut)
        {
            hurtThisCycle.Clear();
            AudioManager.PlayAt(Sfx.SpikesOut, transform.position, soundMaxDistance);
        }

        spikesWereOut = spikesOut;

        if (spikesOut && IsServer && IsSpawned)
            DamagePlayersOnSpikes();
    }

    // 0..1 position in the strike; stays at 1 (spikes hidden) after the strike until the next cycle.
    private float GetStrikeTime()
    {
        double time = IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
        double cycle = Mathf.Max(animationLength, cycleDuration);
        double cycleTime = (time + phaseOffset.Value) % cycle;
        if (cycleTime < 0)
            cycleTime += cycle;

        return Mathf.Clamp01((float)(cycleTime / animationLength));
    }

    // 0 = hidden under the floor, 1 = fully out. A sharp strike up, a hold, then a slower retreat.
    private float GetSpikeHeight(float t)
    {
        if (t < riseEnd)
        {
            float k = t / Mathf.Max(0.0001f, riseEnd);
            return 1f - (1f - k) * (1f - k); // ease out: fast at first
        }

        if (t < holdEnd)
            return 1f;

        if (t < lowerEnd)
        {
            float k = (t - holdEnd) / Mathf.Max(0.0001f, lowerEnd - holdEnd);
            return 1f - k * k; // ease in: starts slowly
        }

        return 0f;
    }

    private void DamagePlayersOnSpikes()
    {
        DungeonProgressManager manager = DungeonProgressManager.Instance;
        if (manager == null)
            return;

        Vector3 center = transform.position + Vector3.up * (damageAreaSize.y * 0.5f);
        Collider[] hits = Physics.OverlapBox(center, damageAreaSize * 0.5f, transform.rotation, ~0, QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            NetworkObject player = hit.GetComponentInParent<NetworkObject>();
            if (player == null || !player.CompareTag("Player"))
                continue;

            // One heart per spike strike at most; the player is not moved, they just get hurt.
            ulong clientId = player.OwnerClientId;
            if (!hurtThisCycle.Add(clientId))
                continue;

            if (manager.DamagePlayer(clientId))
                SpikeHitRpc(RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void SpikeHitRpc(RpcParams rpcParams)
    {
        AudioManager.Play(Sfx.SpikeHit);
    }

    private void CreateVisual()
    {
        if (visualPrefab == null)
            return;

        GameObject visual = Instantiate(visualPrefab, transform, false);
        visual.name = "Spike Trap Model";
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;

        // The spikes are moved by hand, the imported clip would fight it.
        foreach (Animator animator in visual.GetComponentsInChildren<Animator>(true))
            animator.enabled = false;

        foreach (string partName in HiddenParts)
        {
            Transform part = FindChild(visual.transform, partName);
            if (part != null)
                part.gameObject.SetActive(false);
        }

        Transform body = FindChild(visual.transform, BodyName);
        spikes = FindChild(visual.transform, SpikesName);

        // Fit the trap frame (not the hidden plane) into a maze cell whatever scale the model was imported with.
        Renderer fitRenderer = body != null ? body.GetComponent<Renderer>() : null;
        Bounds fitBounds = fitRenderer != null ? fitRenderer.bounds : GetBounds(visual);
        float size = Mathf.Max(fitBounds.size.x, fitBounds.size.z);
        if (size > 0.01f)
            visual.transform.localScale *= visualSize / size;

        if (spikes == null || body == null)
        {
            Debug.LogWarning($"SpikeTrap: '{SpikesName}' or '{BodyName}' not found in the model, the spikes will not move.");
            spikes = null;
            return;
        }

        Renderer spikesRenderer = spikes.GetComponent<Renderer>();
        Bounds bodyBounds = body.GetComponent<Renderer>().bounds;
        Bounds spikesBounds = spikesRenderer != null ? spikesRenderer.bounds : new Bounds(spikes.position, Vector3.one * 0.3f);

        // Out: the spike base sits in the middle of the frame. Hidden: the tips are just under the floor,
        // which covers them. Worked out from the current pose, whatever frame the model was imported at.
        float raise = bodyBounds.center.y - spikesBounds.min.y;
        Vector3 upWorld = spikes.position + Vector3.up * raise;
        Vector3 downWorld = upWorld - Vector3.up * (spikesBounds.size.y + bodyBounds.size.y * 0.5f + 0.05f);

        spikesUpLocal = spikes.parent.InverseTransformPoint(upWorld);
        spikesDownLocal = spikes.parent.InverseTransformPoint(downWorld);
        spikes.localPosition = spikesDownLocal;
    }

    private static Bounds GetBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(root.transform.position, Vector3.zero);

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return bounds;
    }

    private static Transform FindChild(Transform parent, string childName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == childName)
                return child;
        }

        return null;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.4f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position + Vector3.up * (damageAreaSize.y * 0.5f), transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, damageAreaSize);
    }
}
