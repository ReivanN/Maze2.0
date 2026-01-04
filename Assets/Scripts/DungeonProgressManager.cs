using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using System.Linq;
using TMPro;

public class DungeonProgressManager : NetworkBehaviour
{
    [Header("Настройки лабиринта")]
    [SerializeField] private CaveDungeonGenerator dungeonGenerator;
    [SerializeField] private float checkInterval = 0.5f;
    
    [Header("Визуальная обратная связь")]
    [SerializeField] private GameObject victoryEffectPrefab;
    [SerializeField] private AudioClip victorySound;
    [SerializeField] private Material endPointActiveMaterial;
    
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI progressText;
    
    
    // Сетевая синхронизация
    private NetworkVariable<int> playersAtEnd = new NetworkVariable<int>(
        0, 
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    
    private NetworkVariable<bool> levelCompleted = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    
    private List<ulong> playersInEndZone = new List<ulong>();
    private GameObject endZoneObject;
    private float checkTimer;
    
    // События для UI
    public System.Action<int, int> OnProgressUpdated;
    public System.Action OnLevelCompleted;
    
    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Находим конечную точку после генерации
            Invoke(nameof(FindEndZone), 0.5f);
            
            // Подписываемся на события подключения игроков
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
        
        // Подписываемся на изменения сетевых переменных
        playersAtEnd.OnValueChanged += OnPlayersAtEndChanged;
        levelCompleted.OnValueChanged += OnLevelCompletedChanged;
        
        UpdateUI();
    }
    
    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            }
        }
        
        playersAtEnd.OnValueChanged -= OnPlayersAtEndChanged;
        levelCompleted.OnValueChanged -= OnLevelCompletedChanged;
    }
    
    void Update()
    {
        if (!IsServer) return;
        
        checkTimer += Time.deltaTime;
        if (checkTimer >= checkInterval)
        {
            CheckPlayersAtEnd();
            checkTimer = 0f;
        }
    }
    
    void FindEndZone()
    {
        // Ищем объект конечной точки по тегу или имени
        endZoneObject = GameObject.FindGameObjectWithTag("EndPoint");
        if (endZoneObject == null)
        {
            // Альтернативный поиск
            var endObjects = FindObjectsOfType<GameObject>()
                .Where(go => go.name.Contains("End") || go.name.Contains("Finish"))
                .ToList();
            
            if (endObjects.Count > 0)
            {
                endZoneObject = endObjects[0];
            }
        }
        
        if (endZoneObject != null)
        {
            Debug.Log($"Конечная точка найдена: {endZoneObject.name}");
        }
        else
        {
            Debug.LogWarning("Конечная точка не найдена!");
        }
    }
    
    void CheckPlayersAtEnd()
    {
        if (endZoneObject == null || levelCompleted.Value) return;
        
        var currentPlayersInZone = new List<ulong>();
        float endZoneRadius = 3f; // Радиус зоны финиша
        
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            if (client.PlayerObject == null) continue;
            
            float distance = Vector3.Distance(
                client.PlayerObject.transform.position,
                endZoneObject.transform.position
            );
            
            if (distance <= endZoneRadius)
            {
                currentPlayersInZone.Add(client.ClientId);
            }
        }
        
        // Обновляем список игроков в зоне
        playersInEndZone = currentPlayersInZone;
        
        // Синхронизируем с клиентами
        playersAtEnd.Value = playersInEndZone.Count;
        
        // Проверяем условие победы
        int totalPlayers = NetworkManager.Singleton.ConnectedClientsList.Count;
        if (playersInEndZone.Count >= 2 && playersInEndZone.Count == totalPlayers)
        {
            CompleteLevel();
        }
    }
    
    void CompleteLevel()
    {
        if (levelCompleted.Value) return;
        
        Debug.Log("Уровень пройден! Все игроки на финише.");
        levelCompleted.Value = true;
        
        // Запускаем визуальные эффекты
        PlayVictoryEffectsClientRpc(endZoneObject.transform.position);
        
        // Ждем немного перед генерацией нового уровня
        Invoke(nameof(GenerateNewLevel), 2f);
    }
    
    void GenerateNewLevel()
    {
        if (!IsServer) return;
        
        Debug.Log("Генерируем новый уровень...");
        
        // Сбрасываем состояние
        levelCompleted.Value = false;
        playersInEndZone.Clear();
        playersAtEnd.Value = 0;
        
        // Генерируем новый лабиринт
        if (dungeonGenerator != null)
        {
            dungeonGenerator.RegenerateDungeonServerRpc();
        }
        else
        {
            RegenerateDungeonManually();
        }
        
        // Обновляем UI
        UpdateLevelCompleteClientRpc();
    }
    
    void RegenerateDungeonManually()
    {
        // Альтернативный способ если генератор не работает через RPC
        if (dungeonGenerator == null)
        {
            dungeonGenerator = FindObjectOfType<CaveDungeonGenerator>();
        }
        
        if (dungeonGenerator != null)
        {
            // Вызываем методы генерации напрямую
            var method = typeof(CaveDungeonGenerator).GetMethod("Generate", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(dungeonGenerator, null);
            
            var buildMethod = typeof(CaveDungeonGenerator).GetMethod("Build", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            buildMethod?.Invoke(dungeonGenerator, null);
            
            var positionMethod = typeof(CaveDungeonGenerator).GetMethod("PositionAllPlayers", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            positionMethod?.Invoke(dungeonGenerator, null);
        }
    }
    
    [ClientRpc]
    void PlayVictoryEffectsClientRpc(Vector3 position)
    {
        // Визуальные эффекты на клиентах
        if (victoryEffectPrefab != null)
        {
            Instantiate(victoryEffectPrefab, position, Quaternion.identity);
        }
        
        if (victorySound != null)
        {
            AudioSource.PlayClipAtPoint(victorySound, position);
        }
        
        // Можно добавить UI анимации
        OnLevelCompleted?.Invoke();
    }
    
    [ClientRpc]
    void UpdateLevelCompleteClientRpc()
    {
        UpdateUI();
    }
    
    void OnPlayersAtEndChanged(int oldValue, int newValue)
    {
        UpdateUI();
        OnProgressUpdated?.Invoke(newValue, GetTotalPlayers());
    }
    
    void OnLevelCompletedChanged(bool oldValue, bool newValue)
    {
        if (newValue)
        {
            Debug.Log("Уровень завершен!");
        }
    }
    
    void OnClientConnected(ulong clientId)
    {
        UpdateUI();
    }
    
    void OnClientDisconnected(ulong clientId)
    {
        UpdateUI();
    }
    
    void UpdateUI()
    {
        if (progressText != null)
        {
            int totalPlayers = GetTotalPlayers();
            progressText.text = $"Игроков на финише: {playersAtEnd.Value}/{totalPlayers}";
            
            if (levelCompleted.Value)
            {
                progressText.text = "Уровень пройден! Генерация нового...";
            }
        }
    }
    
    int GetTotalPlayers()
    {
        if (NetworkManager.Singleton != null)
        {
            return NetworkManager.Singleton.ConnectedClientsList.Count;
        }
        return 0;
    }
    
    // Методы для визуализации зоны финиша (для отладки)
    void OnDrawGizmos()
    {
        if (endZoneObject != null && IsServer)
        {
            Gizmos.color = new Color(0, 1, 0, 0.3f);
            Gizmos.DrawSphere(endZoneObject.transform.position, 3f);
            
            Gizmos.color = Color.green;
            foreach (var playerId in playersInEndZone)
            {
                if (NetworkManager.Singleton.ConnectedClients.TryGetValue(playerId, out var client))
                {
                    if (client.PlayerObject != null)
                    {
                        Gizmos.DrawLine(endZoneObject.transform.position, 
                            client.PlayerObject.transform.position);
                    }
                }
            }
        }
    }
    
    // Public методы для UI
    public int GetPlayersAtEnd() => playersAtEnd.Value;
    public bool IsLevelCompleted() => levelCompleted.Value;
    public int GetTotalPlayersCount() => GetTotalPlayers();
}