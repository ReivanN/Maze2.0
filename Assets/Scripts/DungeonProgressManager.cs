using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using System.Linq;
using TMPro;
using System.Collections;

public class DungeonProgressManager : NetworkBehaviour
{
    [Header("Dungeon Settings")]
    [SerializeField] private CaveDungeonGenerator dungeonGenerator;
    [SerializeField] private float checkInterval = 0.5f;
    [SerializeField] private float nextLevelDelay = 3f;
    [SerializeField] private int minPlayersToComplete = 2;
    [SerializeField] private float endZoneRadius = 1f;

    [Header("Level Progression")]
    [SerializeField] private int widthIncreasePerLevel = 4;
    [SerializeField] private int heightIncreasePerLevel = 4;
    [SerializeField] private int maxWidth = 0;
    [SerializeField] private int maxHeight = 0;
    
    [Header("Feedback")]
    [SerializeField] private GameObject victoryEffectPrefab;
    [SerializeField] private AudioClip victorySound;
    [SerializeField] private Material endPointActiveMaterial;
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI progressText;
    
    
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

    private NetworkVariable<int> currentLevel = new NetworkVariable<int>(
        1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<float> lastCompletionTime = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    
    private List<ulong> playersInEndZone = new List<ulong>();
    private GameObject endZoneObject;
    private float checkTimer;
    private Coroutine nextLevelCoroutine;
    
    public System.Action<int, int> OnProgressUpdated;
    public System.Action OnLevelCompleted;
    public System.Action<int> OnLevelChanged;
    
    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            Invoke(nameof(FindEndZone), 0.5f);
            
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
        
        playersAtEnd.OnValueChanged += OnPlayersAtEndChanged;
        levelCompleted.OnValueChanged += OnLevelCompletedChanged;
        currentLevel.OnValueChanged += OnCurrentLevelChanged;
        lastCompletionTime.OnValueChanged += OnLastCompletionTimeChanged;
        
        UpdateUI();
        OnLevelChanged?.Invoke(currentLevel.Value);
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
        currentLevel.OnValueChanged -= OnCurrentLevelChanged;
        lastCompletionTime.OnValueChanged -= OnLastCompletionTimeChanged;
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
        endZoneObject = GameObject.FindGameObjectWithTag("EndPoint");
        if (endZoneObject == null)
        {
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
            Debug.Log($"End point found: {endZoneObject.name}");
        }
        else
        {
            Debug.LogWarning("End point was not found!");
        }
    }
    
    void CheckPlayersAtEnd()
    {
        if (endZoneObject == null || levelCompleted.Value) return;
        
        var currentPlayersInZone = new List<ulong>();
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            if (client.PlayerObject == null) continue;

            if (IsPlayerInsideEndZone(client.PlayerObject.transform.position))
            {
                currentPlayersInZone.Add(client.ClientId);
            }
        }
        
        playersInEndZone = currentPlayersInZone;
        
        playersAtEnd.Value = playersInEndZone.Count;
        
        int totalPlayers = NetworkManager.Singleton.ConnectedClientsList.Count;
        int requiredPlayers = Mathf.Max(minPlayersToComplete, totalPlayers);

        if (totalPlayers >= minPlayersToComplete &&
            playersInEndZone.Count >= requiredPlayers)
        {
            CompleteLevel();
        }
    }

    bool IsPlayerInsideEndZone(Vector3 playerPosition)
    {
        Vector3 endPosition = endZoneObject.transform.position;
        Vector2 playerXZ = new Vector2(playerPosition.x, playerPosition.z);
        Vector2 endXZ = new Vector2(endPosition.x, endPosition.z);

        return Vector2.Distance(playerXZ, endXZ) <= endZoneRadius;
    }
    
    void CompleteLevel()
    {
        if (levelCompleted.Value) return;
        
        Debug.Log("Level completed! All players reached the finish.");
        RelayManager relayManager = FindObjectOfType<RelayManager>();
        if (relayManager != null)
            lastCompletionTime.Value = relayManager.GetElapsedGameTime();

        levelCompleted.Value = true;
        
        PlayVictoryEffectsClientRpc(endZoneObject.transform.position);
        
        if (nextLevelCoroutine != null)
        {
            StopCoroutine(nextLevelCoroutine);
        }

        nextLevelCoroutine = StartCoroutine(GenerateNewLevelAfterDelay());
    }
    
    IEnumerator GenerateNewLevelAfterDelay()
    {
        yield return new WaitForSeconds(nextLevelDelay);
        nextLevelCoroutine = null;
        GenerateNewLevel();
    }

    async void GenerateNewLevel()
    {
        if (!IsServer) return;
        
        Debug.Log("Generating a new level...");
        
        playersInEndZone.Clear();
        playersAtEnd.Value = 0;
        lastCompletionTime.Value = 0f;
        currentLevel.Value++;
        
        if (dungeonGenerator != null)
        {
            await dungeonGenerator.RegenerateDungeonForLevelAsync(
                currentLevel.Value,
                widthIncreasePerLevel,
                heightIncreasePerLevel,
                maxWidth,
                maxHeight);
        }
        else
        {
            RegenerateDungeonManually();
        }

        FindEndZone();
        levelCompleted.Value = false;
        
        UpdateLevelStartedClientRpc(currentLevel.Value);

        RelayManager relayManager = FindObjectOfType<RelayManager>();
        if (relayManager != null)
            relayManager.StartNewLevelTimerForAll();
    }
    
    void RegenerateDungeonManually()
    {
        if (dungeonGenerator == null)
        {
            dungeonGenerator = FindObjectOfType<CaveDungeonGenerator>();
        }
        
        if (dungeonGenerator != null)
        {
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
        if (victoryEffectPrefab != null)
        {
            Instantiate(victoryEffectPrefab, position, Quaternion.identity);
        }
        
        if (victorySound != null)
        {
            AudioSource.PlayClipAtPoint(victorySound, position);
        }
        
        OnLevelCompleted?.Invoke();
    }
    
    [ClientRpc]
    void UpdateLevelCompleteClientRpc()
    {
        UpdateUI();
    }

    [ClientRpc]
    void UpdateLevelStartedClientRpc(int newLevel)
    {
        UpdateUI();
        OnLevelChanged?.Invoke(newLevel);
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
            Debug.Log("Level completed!");
        }

        UpdateUI();
    }

    void OnCurrentLevelChanged(int oldValue, int newValue)
    {
        UpdateUI();
        OnLevelChanged?.Invoke(newValue);
    }

    void OnLastCompletionTimeChanged(float oldValue, float newValue)
    {
        UpdateUI();
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
            progressText.text = $"Level {currentLevel.Value}. Players at finish: {playersAtEnd.Value}/{totalPlayers}";
            
            if (levelCompleted.Value)
            {
                progressText.text = $"Level completed in {FormatTime(lastCompletionTime.Value)}. Generating next level...";
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
    
    void OnDrawGizmos()
    {
        if (endZoneObject != null && IsServer)
        {
            Gizmos.color = new Color(0, 1, 0, 0.3f);
            Gizmos.DrawSphere(endZoneObject.transform.position, endZoneRadius);
            
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
    
    public int GetPlayersAtEnd() => playersAtEnd.Value;
    public bool IsLevelCompleted() => levelCompleted.Value;
    public int GetTotalPlayersCount() => GetTotalPlayers();
    public int GetCurrentLevel() => currentLevel.Value;
    public float GetLastCompletionTime() => lastCompletionTime.Value;

    string FormatTime(float totalSeconds)
    {
        System.TimeSpan time = System.TimeSpan.FromSeconds(Mathf.Max(0f, totalSeconds));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }
}
