using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using System.Linq;
using TMPro;
using System.Collections;
using UnityEngine.UI;

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
    [SerializeField] private TextMeshProUGUI levelText;
    [SerializeField] private bool createLevelTextIfMissing = true;

    [Header("Health")]
    [SerializeField] private int maxHearts = 3;
    [Tooltip("After losing a heart the player cannot lose another one for this many seconds.")]
    [SerializeField] private float damageCooldown = 2f;

    /// <summary>Hearts of one player, kept by the server and synced to both players.</summary>
    public struct PlayerHearts : INetworkSerializable, System.IEquatable<PlayerHearts>
    {
        public ulong ClientId;
        public int Hearts;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref Hearts);
        }

        public bool Equals(PlayerHearts other) => ClientId == other.ClientId && Hearts == other.Hearts;
    }

    private NetworkList<PlayerHearts> playerHearts;

    private NetworkVariable<bool> gameOver = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // The player who lost the last heart.
    private NetworkVariable<ulong> gameOverPlayer = new NetworkVariable<ulong>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly Dictionary<ulong, float> lastDamageTime = new Dictionary<ulong, float>();
    private bool restarting;

    public static DungeonProgressManager Instance { get; private set; }
    public int MaxHearts => maxHearts;
    public bool IsGameOver => gameOver.Value;


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
    
    private void Awake()
    {
        // NetworkList has to exist before the object is spawned.
        playerHearts = new NetworkList<PlayerHearts>();
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;

        if (IsServer)
        {
            Invoke(nameof(FindEndZone), 0.5f);

            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
                EnsureHearts(clientId);
        }

        gameOver.OnValueChanged += OnGameOverChanged;
        HeartsHud.EnsureCreated();

        playersAtEnd.OnValueChanged += OnPlayersAtEndChanged;
        levelCompleted.OnValueChanged += OnLevelCompletedChanged;
        currentLevel.OnValueChanged += OnCurrentLevelChanged;
        lastCompletionTime.OnValueChanged += OnLastCompletionTimeChanged;
        
        UpdateUI();
        AudioManager.SetLevel(currentLevel.Value);
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
        gameOver.OnValueChanged -= OnGameOverChanged;

        if (Instance == this)
            Instance = null;
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
        if (endZoneObject == null || levelCompleted.Value || gameOver.Value) return;
        
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
        await GenerateLevelAsync(currentLevel.Value + 1);
    }

    async System.Threading.Tasks.Task GenerateLevelAsync(int level)
    {
        if (!IsServer) return;

        Debug.Log($"Generating level {level}...");

        playersInEndZone.Clear();
        playersAtEnd.Value = 0;
        lastCompletionTime.Value = 0f;
        currentLevel.Value = level;

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

        // A new random mutator for every level (the documented flow; it was not called before).
        if (LevelMutatorManager.Instance != null)
            LevelMutatorManager.Instance.PickMutatorForLevel(currentLevel.Value);

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
        
        // A clip assigned in the scene overrides the one from the audio library.
        if (victorySound != null)
            AudioManager.PlayClipAt(victorySound, position, 1f);
        else
            AudioManager.Play(Sfx.LevelComplete);
        
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
        AudioManager.SetLevel(newValue);
        OnLevelChanged?.Invoke(newValue);
    }

    void OnLastCompletionTimeChanged(float oldValue, float newValue)
    {
        UpdateUI();
    }
    
    void OnClientConnected(ulong clientId)
    {
        EnsureHearts(clientId);
        UpdateUI();
    }

    void OnClientDisconnected(ulong clientId)
    {
        int index = FindHeartsIndex(clientId);
        if (index >= 0)
            playerHearts.RemoveAt(index);

        UpdateUI();
    }

    // ================= HEALTH =================

    /// <summary>Hearts of a player; a player the server has not registered yet counts as full.</summary>
    public int GetHearts(ulong clientId)
    {
        int index = FindHeartsIndex(clientId);
        return index >= 0 ? playerHearts[index].Hearts : maxHearts;
    }

    /// <summary>The other player in the room, if there is one.</summary>
    public bool TryGetPartnerId(ulong localClientId, out ulong partnerId)
    {
        foreach (PlayerHearts entry in playerHearts)
        {
            if (entry.ClientId != localClientId)
            {
                partnerId = entry.ClientId;
                return true;
            }
        }

        partnerId = 0;
        return false;
    }

    public bool DidLocalPlayerLose()
    {
        return NetworkManager.Singleton != null && gameOverPlayer.Value == NetworkManager.Singleton.LocalClientId;
    }

    /// <summary>
    /// Server: the player was hit by a trap (laser, fall off the map). Takes one heart; when a player has no hearts
    /// left, both players lose.
    /// </summary>
    public void DamagePlayer(ulong clientId)
    {
        if (!IsServer || gameOver.Value || levelCompleted.Value || restarting)
            return;

        if (lastDamageTime.TryGetValue(clientId, out float lastTime) && Time.time - lastTime < damageCooldown)
            return;

        lastDamageTime[clientId] = Time.time;

        EnsureHearts(clientId);
        int index = FindHeartsIndex(clientId);
        PlayerHearts entry = playerHearts[index];
        entry.Hearts = Mathf.Max(0, entry.Hearts - 1);
        playerHearts[index] = entry;

        if (entry.Hearts > 0)
            return;

        gameOverPlayer.Value = clientId;
        gameOver.Value = true;

        if (nextLevelCoroutine != null)
        {
            StopCoroutine(nextLevelCoroutine);
            nextLevelCoroutine = null;
        }
    }

    /// <summary>Restart button on the game over screen (either player).</summary>
    public void RequestRestart()
    {
        RequestRestartRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestRestartRpc()
    {
        if (gameOver.Value && !restarting)
            RestartGame();
    }

    // Server: full hearts, back to level 1 with a new maze, a new mutator and a new timer.
    private async void RestartGame()
    {
        restarting = true;

        for (int i = 0; i < playerHearts.Count; i++)
        {
            PlayerHearts entry = playerHearts[i];
            entry.Hearts = maxHearts;
            playerHearts[i] = entry;
        }

        lastDamageTime.Clear();

        await GenerateLevelAsync(1);

        gameOver.Value = false;
        restarting = false;
    }

    private void EnsureHearts(ulong clientId)
    {
        if (IsServer && FindHeartsIndex(clientId) < 0)
            playerHearts.Add(new PlayerHearts { ClientId = clientId, Hearts = maxHearts });
    }

    private int FindHeartsIndex(ulong clientId)
    {
        for (int i = 0; i < playerHearts.Count; i++)
        {
            if (playerHearts[i].ClientId == clientId)
                return i;
        }

        return -1;
    }

    private void OnGameOverChanged(bool oldValue, bool newValue)
    {
        RelayManager relayManager = FindObjectOfType<RelayManager>();

        if (newValue)
        {
            AudioManager.Play(Sfx.GameOver);

            if (relayManager != null)
                relayManager.ShowGameOver(DidLocalPlayerLose(), currentLevel.Value);
        }
        else if (relayManager != null)
        {
            relayManager.HideGameOver();
        }
    }
    
    void UpdateUI()
    {
        EnsureLevelText();

        if (levelText != null)
            levelText.text = $"Level {currentLevel.Value}";

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

    void EnsureLevelText()
    {
        if (levelText != null || !createLevelTextIfMissing)
            return;

        Canvas canvas = progressText != null
            ? progressText.GetComponentInParent<Canvas>()
            : FindObjectOfType<Canvas>();

        if (canvas == null)
        {
            canvas = new GameObject("Dungeon UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }

        GameObject levelObject = new GameObject("Level Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        levelObject.transform.SetParent(canvas.transform, false);

        levelText = levelObject.GetComponent<TextMeshProUGUI>();
        UiTheme.ApplyFont(levelText);
        levelText.text = $"Level {currentLevel.Value}";
        levelText.fontSize = 30;
        levelText.fontStyle = FontStyles.Bold;
        levelText.alignment = TextAlignmentOptions.Left;
        levelText.color = Color.white;

        RectTransform rectTransform = levelText.rectTransform;
        rectTransform.anchorMin = new Vector2(0f, 1f);
        rectTransform.anchorMax = new Vector2(0f, 1f);
        rectTransform.pivot = new Vector2(0f, 1f);
        rectTransform.anchoredPosition = new Vector2(36f, -28f);
        rectTransform.sizeDelta = new Vector2(320f, 52f);
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
