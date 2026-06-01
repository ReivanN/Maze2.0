using Unity.Netcode;
using UnityEngine;

public class LaserTrap : NetworkBehaviour
{
    [Header("Laser")]
    [SerializeField] private Transform laserOrigin;
    [SerializeField] private Vector3 localDirection = Vector3.forward;
    [SerializeField] private float maxDistance = 8f;
    [SerializeField] private float hitRadius = 0.2f;
    [SerializeField] private float laserHeight = 3f;
    [SerializeField] private float laserThickness = 0.35f;
    [SerializeField] private LayerMask hitMask = ~0;

    [Header("Visual")]
    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private int verticalBeamCount = 6;
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

    private NetworkVariable<bool> hasConfiguredSegment = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<Vector3> configuredStart = new NetworkVariable<Vector3>(
        Vector3.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<Vector3> configuredEnd = new NetworkVariable<Vector3>(
        Vector3.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<float> configuredHeight = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<float> configuredThickness = new NetworkVariable<float>(
        0f,
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

        ResetLineRendererToSingleBeam();
    }

    private void Update()
    {
        if (hasConfiguredSegment.Value)
        {
            UpdateBarrierVisual(configuredStart.Value, configuredEnd.Value, GetConfiguredHeight());

            if (!IsServer || !isActive.Value) return;
            if (Time.time - lastHitTime < hitCooldown) return;

            TryRespawnPlayersInBarrier(configuredStart.Value, configuredEnd.Value, GetConfiguredHeight(), GetConfiguredThickness());
            return;
        }

        Vector3 origin = laserOrigin.position;
        Vector3 direction = laserOrigin.TransformDirection(localDirection.normalized);
        Vector3 forwardEnd = GetLaserEnd(origin, direction, out RaycastHit forwardHit);
        Vector3 backwardEnd = GetLaserEnd(origin, -direction, out RaycastHit backwardHit);

        UpdateVisual(backwardEnd, forwardEnd);

        if (!IsServer || !isActive.Value) return;
        if (Time.time - lastHitTime < hitCooldown) return;

        if (TryRespawnHitPlayer(forwardHit))
            return;

        TryRespawnHitPlayer(backwardHit);
    }

    public void ConfigureSegment(Vector3 start, Vector3 end)
    {
        ConfigureBarrier(start, end, laserHeight, laserThickness);
    }

    public void ConfigureBarrier(Vector3 start, Vector3 end, float height, float thickness)
    {
        if (!IsServer) return;

        configuredStart.Value = start;
        configuredEnd.Value = end;
        configuredHeight.Value = Mathf.Max(0.1f, height);
        configuredThickness.Value = Mathf.Max(0.05f, thickness);
        hasConfiguredSegment.Value = true;
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

        if (lineRenderer.positionCount != 2)
            ResetLineRendererToSingleBeam();

        lineRenderer.SetPosition(0, start);
        lineRenderer.SetPosition(1, end);
    }

    private void UpdateBarrierVisual(Vector3 start, Vector3 end, float height)
    {
        if (lineRenderer == null) return;

        lineRenderer.enabled = isActive.Value;
        if (!isActive.Value) return;

        int beamCount = Mathf.Max(2, verticalBeamCount);
        int positionCount = beamCount * 2;

        if (lineRenderer.positionCount != positionCount)
            lineRenderer.positionCount = positionCount;

        for (int i = 0; i < beamCount; i++)
        {
            float t = beamCount == 1 ? 0f : (float)i / (beamCount - 1);
            Vector3 heightOffset = Vector3.up * (height * t);
            int index = i * 2;

            if (i % 2 == 0)
            {
                lineRenderer.SetPosition(index, start + heightOffset);
                lineRenderer.SetPosition(index + 1, end + heightOffset);
            }
            else
            {
                lineRenderer.SetPosition(index, end + heightOffset);
                lineRenderer.SetPosition(index + 1, start + heightOffset);
            }
        }
    }

    private void TryRespawnPlayersInBarrier(Vector3 start, Vector3 end, float height, float thickness)
    {
        Vector3 segment = end - start;
        float length = segment.magnitude;

        if (length <= 0.01f)
            return;

        Vector3 direction = segment / length;
        Vector3 center = (start + end) * 0.5f + Vector3.up * (height * 0.5f);
        Vector3 halfExtents = new Vector3(thickness * 0.5f, height * 0.5f, length * 0.5f);
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        Collider[] hits = Physics.OverlapBox(center, halfExtents, rotation, hitMask, QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            if (TryRespawnHitPlayer(hit))
                return;
        }
    }

    private bool TryRespawnHitPlayer(Collider hitCollider)
    {
        if (!TryGetPlayerNetworkObject(hitCollider, out NetworkObject playerNetworkObject))
            return false;

        lastHitTime = Time.time;
        RespawnPlayer(playerNetworkObject.OwnerClientId);
        return true;
    }

    private bool TryRespawnHitPlayer(RaycastHit hit)
    {
        if (!TryGetPlayerNetworkObject(hit.collider, out NetworkObject playerNetworkObject))
            return false;

        lastHitTime = Time.time;
        RespawnPlayer(playerNetworkObject.OwnerClientId);
        return true;
    }

    private bool TryGetPlayerNetworkObject(Collider collider, out NetworkObject playerNetworkObject)
    {
        playerNetworkObject = null;

        if (collider == null)
            return false;

        playerNetworkObject = collider.GetComponentInParent<NetworkObject>();
        return playerNetworkObject != null && playerNetworkObject.CompareTag("Player");
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
        Color color = active ? activeColor : disabledColor;

        if (lineRenderer != null)
        {
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
            lineRenderer.enabled = active && !hasConfiguredSegment.Value;
        }

    }

    private float GetConfiguredHeight()
    {
        return configuredHeight.Value > 0f ? configuredHeight.Value : laserHeight;
    }

    private float GetConfiguredThickness()
    {
        return configuredThickness.Value > 0f ? configuredThickness.Value : laserThickness;
    }

    private void ResetLineRendererToSingleBeam()
    {
        if (lineRenderer != null)
            lineRenderer.positionCount = 2;
    }
}
