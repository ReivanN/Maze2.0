using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Floor spikes that keep coming out and going back in (spawned by CaveDungeonGenerator).
/// A player standing on them while they are out loses a heart but stays where they are.
///
/// The cycle is computed from the network server time plus a per-trap offset, so the animation is the same on
/// both players and nothing has to be sent every frame. The server alone decides who gets hurt.
///
/// The model (Spike_trap_Mk2/Spike-trap prefab) is created locally as a child and its "Scene" animation is driven
/// by setting its time every frame, so it always matches the damage window.
/// </summary>
public class SpikeTrap : NetworkBehaviour
{
    [Header("Visual")]
    [SerializeField] private GameObject visualPrefab;
    [SerializeField] private RuntimeAnimatorController animatorController;
    [SerializeField] private string animationState = "Scene";
    [Tooltip("The model is scaled to fit this size on X and Z (maze cells are 2 m).")]
    [SerializeField] private float visualSize = 1.8f;

    [Header("Cycle")]
    [Tooltip("Length of the spike animation: 51 frames at 25 fps.")]
    [SerializeField] private float animationLength = 2.04f;
    [Tooltip("Full cycle; after the animation the spikes stay hidden for the rest of it.")]
    [SerializeField] private float cycleDuration = 3f;
    [Tooltip("Part of the animation (0..1) during which the spikes are out and hurt: they shoot up at about 0.06 " +
             "and are back under the floor at about 0.52.")]
    [SerializeField] private float dangerStart = 0.07f;
    [SerializeField] private float dangerEnd = 0.48f;

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
    private Animator animator;
    private int stateHash;
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
        float normalizedTime = GetAnimationTime();

        if (animator != null && animator.runtimeAnimatorController != null)
            animator.Play(stateHash, 0, normalizedTime);

        bool spikesOut = normalizedTime >= dangerStart && normalizedTime <= dangerEnd;

        if (spikesOut && !spikesWereOut)
        {
            hurtThisCycle.Clear();
            AudioManager.PlayAt(Sfx.SpikesOut, transform.position, soundMaxDistance);
        }

        spikesWereOut = spikesOut;

        if (spikesOut && IsServer && IsSpawned)
            DamagePlayersOnSpikes();
    }

    // 0..1 position in the animation; stays at 1 (spikes hidden) after the animation until the next cycle.
    private float GetAnimationTime()
    {
        double time = IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
        double cycle = Mathf.Max(animationLength, cycleDuration);
        double cycleTime = (time + phaseOffset.Value) % cycle;
        if (cycleTime < 0)
            cycleTime += cycle;

        return Mathf.Clamp01((float)(cycleTime / animationLength));
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

        // Fit the model into a maze cell whatever scale it was imported with.
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            float size = Mathf.Max(bounds.size.x, bounds.size.z);
            if (size > 0.01f)
                visual.transform.localScale *= visualSize / size;
        }

        // The model has no colliders of its own: players and the cube walk over it, only the damage area matters.
        animator = visual.GetComponentInChildren<Animator>();
        if (animator == null)
            animator = visual.AddComponent<Animator>();

        if (animatorController != null)
            animator.runtimeAnimatorController = animatorController;

        animator.applyRootMotion = false;
        animator.speed = 0f; // the time is set by hand in Update
        stateHash = Animator.StringToHash(animationState);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.4f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position + Vector3.up * (damageAreaSize.y * 0.5f), transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, damageAreaSize);
    }
}
