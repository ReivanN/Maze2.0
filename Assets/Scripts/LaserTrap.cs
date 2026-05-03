using Unity.Netcode;
using UnityEngine;

public class LaserTrap : NetworkBehaviour
{
    [Header("Laser")]
    [SerializeField] private Transform laserOrigin;
    [SerializeField] private Vector3 localDirection = Vector3.forward;
    [SerializeField] private float maxDistance = 8f;
    [SerializeField] private float hitRadius = 0.2f;
    [SerializeField] private LayerMask hitMask = ~0;

    [Header("Visual")]
    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Color activeColor = Color.red;
    [SerializeField] private Color disabledColor = Color.gray;

    [Header("Respawn")]
    [SerializeField] private float respawnHeightOffset = 1f;
    [SerializeField] private float hitCooldown = 1f;

    private NetworkVariable<bool> isActive = new NetworkVariable<bool>(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private float lastHitTime;

    public override void OnNetworkSpawn()
    {
        isActive.OnValueChanged += OnActiveChanged;
        ApplyActiveState(isActive.Value);
    }

    public override void OnNetworkDespawn()
    {
        isActive.OnValueChanged -= OnActiveChanged;
    }

    private void Awake()
    {
        if (laserOrigin == null)
            laserOrigin = transform;

        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();

        if (lineRenderer != null)
            lineRenderer.positionCount = 2;
    }

    private void Update()
    {
        Vector3 origin = laserOrigin.position;
        Vector3 direction = laserOrigin.TransformDirection(localDirection.normalized);
        Vector3 forwardEnd = GetLaserEnd(origin, direction, out RaycastHit forwardHit);
        Vector3 backwardEnd = GetLaserEnd(origin, -direction, out RaycastHit backwardHit);

        UpdateVisual(backwardEnd, forwardEnd);

        if (!IsServer || !isActive.Value) return;
        if (Time.time - lastHitTime < hitCooldown) return;

        TryRespawnHitPlayer(forwardHit);
        TryRespawnHitPlayer(backwardHit);
    }

    private Vector3 GetLaserEnd(Vector3 start, Vector3 direction, out RaycastHit hit)
    {
        if (isActive.Value && Physics.SphereCast(start, hitRadius, direction, out hit, maxDistance, hitMask, QueryTriggerInteraction.Ignore))
            return hit.point;

        hit = default;
        return start + direction * maxDistance;
    }

    private void UpdateVisual(Vector3 start, Vector3 end)
    {
        if (lineRenderer == null) return;

        lineRenderer.enabled = isActive.Value;
        if (!isActive.Value) return;

        lineRenderer.SetPosition(0, start);
        lineRenderer.SetPosition(1, end);
    }

    private void TryRespawnHitPlayer(RaycastHit hit)
    {
        if (hit.collider == null || !hit.collider.CompareTag("Player"))
            return;

        NetworkObject playerNetworkObject = hit.collider.GetComponentInParent<NetworkObject>();
        if (playerNetworkObject == null)
            return;

        lastHitTime = Time.time;
        RespawnPlayer(playerNetworkObject.OwnerClientId);
    }

    private void RespawnPlayer(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)) return;
        if (client.PlayerObject == null) return;

        Vector3 spawnPos = GetSpawnPosition(clientId);
        client.PlayerObject.transform.position = spawnPos;

        RespawnPlayerClientRpc(spawnPos, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        });
    }

    private Vector3 GetSpawnPosition(ulong clientId)
    {
        CaveDungeonGenerator generator = FindAnyObjectByType<CaveDungeonGenerator>();

        if (generator != null)
        {
            Vector3 spawnPos = generator.GetSpawnWorldPosition(clientId);
            spawnPos.y = respawnHeightOffset;
            return spawnPos;
        }

        return Vector3.up * respawnHeightOffset;
    }

    [ClientRpc]
    private void RespawnPlayerClientRpc(Vector3 spawnPos, ClientRpcParams clientRpcParams = default)
    {
        if (NetworkManager.Singleton == null ||
            NetworkManager.Singleton.LocalClient == null ||
            NetworkManager.Singleton.LocalClient.PlayerObject == null)
        {
            return;
        }

        Transform playerTransform = NetworkManager.Singleton.LocalClient.PlayerObject.transform;
        CharacterController characterController = playerTransform.GetComponent<CharacterController>();

        if (characterController != null)
            characterController.enabled = false;

        playerTransform.position = spawnPos;

        if (characterController != null)
            characterController.enabled = true;
    }

    public void SetActive(bool active)
    {
        if (!IsServer) return;
        isActive.Value = active;
    }

    public void Disable()
    {
        SetActive(false);
    }

    public void Enable()
    {
        SetActive(true);
    }

    public void Toggle()
    {
        SetActive(!isActive.Value);
    }

    private void OnActiveChanged(bool oldValue, bool newValue)
    {
        ApplyActiveState(newValue);
    }

    private void ApplyActiveState(bool active)
    {
        if (lineRenderer == null) return;

        lineRenderer.startColor = active ? activeColor : disabledColor;
        lineRenderer.endColor = active ? activeColor : disabledColor;
        lineRenderer.enabled = active;
    }
}
