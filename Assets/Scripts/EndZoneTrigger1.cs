using UnityEngine;
using Unity.Netcode;

public class EndZoneTrigger : NetworkBehaviour
{
    [SerializeField] private float triggerRadius = 3f;
    [SerializeField] private ParticleSystem activationEffect;
    [SerializeField] private Light zoneLight;
    
    private DungeonProgressManager progressManager;
    private bool isActive = true;
    
    void Start()
    {
        progressManager = FindObjectOfType<DungeonProgressManager>();
        
        if (zoneLight != null)
        {
            zoneLight.color = Color.yellow;
        }
    }
    
    void Update()
    {
        if (!IsServer || !isActive) return;
        
        // Визуальная обратная связь при приближении игроков
        CheckNearbyPlayers();
    }
    
    void CheckNearbyPlayers()
    {
        Collider[] colliders = Physics.OverlapSphere(transform.position, triggerRadius);
        int playersInZone = 0;
        
        foreach (var collider in colliders)
        {
            if (collider.CompareTag("Player"))
            {
                playersInZone++;
                
                // Визуальный эффект для конкретного игрока
                if (collider.TryGetComponent<PlayerEndZoneFeedback>(out var feedback))
                {
                    feedback.SetInEndZone(true);
                }
            }
        }
        
        UpdateVisualFeedback(playersInZone);
    }
    
    void UpdateVisualFeedback(int playersCount)
    {
        if (zoneLight != null)
        {
            // Меняем цвет в зависимости от количества игроков
            if (playersCount == 0)
            {
                zoneLight.color = Color.yellow;
            }
            else if (playersCount == 1)
            {
                zoneLight.color = new Color(1f, 0.5f, 0f); // Оранжевый
            }
            else
            {
                zoneLight.color = Color.green;
                
                if (activationEffect != null && !activationEffect.isPlaying)
                {
                    activationEffect.Play();
                }
            }
        }
    }
    
    public void Deactivate()
    {
        isActive = false;
        
        if (zoneLight != null)
        {
            zoneLight.enabled = false;
        }
        
        if (activationEffect != null)
        {
            activationEffect.Stop();
        }
        
        DeactivateClientRpc();
    }
    
    [ClientRpc]
    void DeactivateClientRpc()
    {
        isActive = false;
        
        // Отключаем визуальные эффекты на всех клиентах
        var renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = Color.gray;
        }
    }
    
    public void Reactivate()
    {
        isActive = true;
        
        if (zoneLight != null)
        {
            zoneLight.enabled = true;
            zoneLight.color = Color.yellow;
        }
        
        ReactivateClientRpc();
    }
    
    [ClientRpc]
    void ReactivateClientRpc()
    {
        isActive = true;
        
        var renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = Color.white;
        }
    }
    
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0, 1, 0, 0.3f);
        Gizmos.DrawSphere(transform.position, triggerRadius);
    }
}

// Вспомогательный скрипт для визуальной обратной связи на игроке
public class PlayerEndZoneFeedback : NetworkBehaviour
{
    [SerializeField] private ParticleSystem endZoneParticles;
    [SerializeField] private Material endZoneMaterial;
    
    private Material originalMaterial;
    private Renderer playerRenderer;
    
    void Start()
    {
        playerRenderer = GetComponentInChildren<Renderer>();
        if (playerRenderer != null)
        {
            originalMaterial = playerRenderer.material;
        }
    }
    
    public void SetInEndZone(bool inZone)
    {
        if (!IsOwner) return;
        
        SetInEndZoneServerRpc(inZone);
    }
    
    [ServerRpc]
    void SetInEndZoneServerRpc(bool inZone)
    {
        SetInEndZoneClientRpc(inZone);
    }
    
    [ClientRpc]
    void SetInEndZoneClientRpc(bool inZone)
    {
        // Визуальная обратная связь на всех клиентах
        if (playerRenderer != null && endZoneMaterial != null)
        {
            playerRenderer.material = inZone ? endZoneMaterial : originalMaterial;
        }
        
        if (endZoneParticles != null)
        {
            if (inZone && !endZoneParticles.isPlaying)
            {
                endZoneParticles.Play();
            }
            else if (!inZone && endZoneParticles.isPlaying)
            {
                endZoneParticles.Stop();
            }
        }
    }
}