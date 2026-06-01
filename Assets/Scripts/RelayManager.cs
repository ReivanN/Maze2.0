using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public class RelayManager : MonoBehaviour
{
    private const string StartGameMessageName = "Maze.StartGame";
    private const string StartTimerMessageName = "Maze.StartTimer";
    private const int RequiredPlayersToStart = 2;

    [Header("Legacy scene references")]
    [SerializeField] private TextMeshProUGUI textMeshProUGUI;
    [SerializeField] private TMP_InputField _inputField;

    [Header("Relay")]
    [SerializeField] private int maxPlayers = 4;
    [SerializeField] private VoiceManager voiceManager;

    [Header("Menu")]
    [SerializeField] private string gameTitle = "Maze 2.0";
    [SerializeField] private float loadingDuration = 1.5f;

    private Canvas menuCanvas;
    private RectTransform screenRoot;
    private GameObject mainMenuPanel;
    private GameObject joinPanel;
    private GameObject lobbyPanel;
    private GameObject loadingPanel;
    private GameObject pausePanel;
    private TMP_InputField joinCodeInput;
    private TextMeshProUGUI statusLabel;
    private TextMeshProUGUI lobbyCodeLabel;
    private TextMeshProUGUI lobbyPlayersLabel;
    private TextMeshProUGUI loadingLabel;
    private TextMeshProUGUI timerLabel;
    private UnityEngine.UI.Button startGameButton;
    private Canvas legacyCanvas;
    private GameObject menuBackground;

    private string currentJoinCode;
    private bool servicesReady;
    private bool flowBusy;
    private bool gameplayStarted;
    private bool gameplayReady;
    private bool timerStarted;
    private bool pauseMenuOpen;
    private double timerStartNetworkTime;

    private async void Start()
    {
        EnsureEventSystem();
        CacheLegacyCanvas();
        BuildMenu();
        SetLegacyCanvasVisible(false);
        SetGameplayInput(false);
        ShowMainMenu("Ready to connect");

        await EnsureServicesReady();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton == null)
            return;

        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientListChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientListChanged;

        if (NetworkManager.Singleton.CustomMessagingManager != null)
        {
            NetworkManager.Singleton.CustomMessagingManager.UnregisterNamedMessageHandler(StartGameMessageName);
            NetworkManager.Singleton.CustomMessagingManager.UnregisterNamedMessageHandler(StartTimerMessageName);
        }
    }

    private void Update()
    {
        UpdateGameTimer();
        HandlePauseInput();
    }

    public async void StartRelay()
    {
        if (flowBusy)
            return;

        flowBusy = true;
        SetStatus("Creating room...");

        try
        {
            currentJoinCode = await StartHostWithRelay(maxPlayers);

            if (string.IsNullOrEmpty(currentJoinCode))
            {
                ShowMainMenu("Could not start host");
                return;
            }

            if (textMeshProUGUI != null)
                textMeshProUGUI.text = currentJoinCode;

            RegisterNetworkCallbacks();
            await StartVoiceChat(currentJoinCode);
            ShowLobby(true, "Room created. Waiting for another player");
        }
        catch (Exception e) when (e is RelayServiceException || e is RequestFailedException)
        {
            Debug.LogError($"Relay room creation failed: {e.Message}");
            ShowMainMenu("Room creation failed");
        }
        finally
        {
            flowBusy = false;
        }
    }

    public async void JoinRelay()
    {
        string joinCode = joinCodeInput != null ? joinCodeInput.text : _inputField != null ? _inputField.text : string.Empty;
        await JoinRelayWithCode(joinCode);
    }

    public async Task<string> StartHostWithRelay(int maxConnections)
    {
        await EnsureServicesReady();

        Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
        string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetHostRelayData(
            allocation.RelayServer.IpV4,
            (ushort)allocation.RelayServer.Port,
            allocation.AllocationIdBytes,
            allocation.Key,
            allocation.ConnectionData
        );

        bool started = NetworkManager.Singleton.StartHost();
        return started ? joinCode : null;
    }

    public async Task<bool> StartClientWithRelay(string joinCode)
    {
        await EnsureServicesReady();

        if (string.IsNullOrWhiteSpace(joinCode))
        {
            Debug.LogError("Join code is empty");
            return false;
        }

        JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim().ToUpperInvariant());

        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetClientRelayData(
            allocation.RelayServer.IpV4,
            (ushort)allocation.RelayServer.Port,
            allocation.AllocationIdBytes,
            allocation.Key,
            allocation.ConnectionData,
            allocation.HostConnectionData
        );

        return NetworkManager.Singleton.StartClient();
    }

    public void OpenJoinPanel()
    {
        ShowPanel(joinPanel);
        SetStatus("Enter the room code from the host");
        joinCodeInput?.Select();
    }

    public void StartGameAsHost()
    {
        if (!NetworkManager.Singleton.IsHost || gameplayStarted)
            return;

        if (GetConnectedPlayersCount() < RequiredPlayersToStart)
        {
            SetStatus("Waiting for a second player");
            UpdateLobbyPlayers();
            return;
        }

        using FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp);
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(StartGameMessageName, writer);
        StartGameplayLoading();
    }

    public void LeaveRoom()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();

        currentJoinCode = string.Empty;
        gameplayStarted = false;
        gameplayReady = false;
        timerStarted = false;
        SetGameplayInput(false);
        ShowMainMenu("You left the room");
    }

    public void ResumeGame()
    {
        SetPauseMenuVisible(false);
    }

    public void ExitGame()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();

        Application.Quit();

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private async Task JoinRelayWithCode(string joinCode)
    {
        if (flowBusy)
            return;

        joinCode = joinCode.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(joinCode))
        {
            SetStatus("Enter a room code");
            return;
        }

        flowBusy = true;
        SetStatus("Connecting...");

        try
        {
            bool success = await StartClientWithRelay(joinCode);

            if (textMeshProUGUI != null)
                textMeshProUGUI.text = success ? "Connecting..." : "Connection failed";

            if (!success)
            {
                SetStatus("Connection failed");
                return;
            }

            currentJoinCode = joinCode;
            RegisterNetworkCallbacks();
            await StartVoiceChat(joinCode);
            ShowLobby(false, "Connected. Waiting for the host to start");
        }
        catch (Exception e) when (e is RelayServiceException || e is RequestFailedException)
        {
            Debug.LogError($"Relay connection failed: {e.Message}");
            SetStatus("Invalid code or connection failed");
        }
        finally
        {
            flowBusy = false;
        }
    }

    private async Task EnsureServicesReady()
    {
        if (servicesReady)
            return;

        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        servicesReady = true;
    }

    private async Task StartVoiceChat(string joinCode)
    {
        if (voiceManager == null)
            voiceManager = FindAnyObjectByType<VoiceManager>();

        if (voiceManager == null)
        {
            Debug.LogWarning("VoiceManager was not found in the scene");
            return;
        }

        string authPlayerId = AuthenticationService.Instance.PlayerId;
        string shortPlayerId = authPlayerId.Length > 8 ? authPlayerId[..8] : authPlayerId;
        string playerName = $"Player_{shortPlayerId}";
        string roomName = $"Maze_{joinCode}";

        await voiceManager.StartVoice(playerName, roomName);
    }

    private void RegisterNetworkCallbacks()
    {
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientListChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientListChanged;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientListChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientListChanged;

        NetworkManager.Singleton.CustomMessagingManager.UnregisterNamedMessageHandler(StartGameMessageName);
        NetworkManager.Singleton.CustomMessagingManager.UnregisterNamedMessageHandler(StartTimerMessageName);
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(StartGameMessageName, OnStartGameMessage);
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(StartTimerMessageName, OnStartTimerMessage);
        UpdateLobbyPlayers();
        ApplyGameplayInputState();
    }

    private void OnClientListChanged(ulong clientId)
    {
        UpdateLobbyPlayers();
        ApplyGameplayInputState();

        if (NetworkManager.Singleton.IsServer && gameplayReady && !timerStarted && GetConnectedPlayersCount() >= RequiredPlayersToStart)
            BroadcastGameTimerStart();
    }

    private void OnStartGameMessage(ulong senderClientId, FastBufferReader reader)
    {
        StartGameplayLoading();
    }

    private void StartGameplayLoading()
    {
        if (gameplayStarted)
            return;

        gameplayStarted = true;
        gameplayReady = false;
        SetStatus("Loading...");
        StartCoroutine(LoadingRoutine());
    }

    private IEnumerator LoadingRoutine()
    {
        ShowPanel(loadingPanel);
        SetGameplayInput(false);

        float elapsed = 0f;
        while (elapsed < loadingDuration)
        {
            elapsed += Time.deltaTime;
            float progress = Mathf.Clamp01(elapsed / loadingDuration);
            if (loadingLabel != null)
                loadingLabel.text = $"Loading maze... {Mathf.RoundToInt(progress * 100f)}%";
            yield return null;
        }

        HideMenuForGameplay();

        gameplayReady = true;
        SetLegacyCanvasVisible(false);
        ApplyGameplayInputState();

        if (NetworkManager.Singleton.IsServer && GetConnectedPlayersCount() >= RequiredPlayersToStart)
            BroadcastGameTimerStart();
    }

    private void SetGameplayInput(bool enabled)
    {
        if (NetworkManager.Singleton == null ||
            NetworkManager.Singleton.LocalClient == null ||
            NetworkManager.Singleton.LocalClient.PlayerObject == null)
        {
            return;
        }

        GameObject playerObject = NetworkManager.Singleton.LocalClient.PlayerObject.gameObject;
        foreach (PlayerInput playerInput in playerObject.GetComponentsInChildren<PlayerInput>(true))
            playerInput.enabled = enabled;

        foreach (Behaviour behaviour in playerObject.GetComponentsInChildren<Behaviour>(true))
        {
            string typeName = behaviour.GetType().Name;
            if (typeName == "StarterAssetsInputs" || typeName == "ThirdPersonController")
                behaviour.enabled = enabled;
        }
    }

    private void ApplyGameplayInputState()
    {
        bool canMove = gameplayReady && timerStarted;
        SetGameplayInput(canMove);

        if (gameplayReady && !canMove)
            SetStatus("Waiting for a second player");
    }

    private void HandlePauseInput()
    {
        if (!gameplayStarted || Keyboard.current == null)
            return;

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
            SetPauseMenuVisible(!pauseMenuOpen);
    }

    private void SetPauseMenuVisible(bool visible)
    {
        pauseMenuOpen = visible;

        if (menuCanvas != null)
            menuCanvas.gameObject.SetActive(true);

        if (pausePanel != null)
            pausePanel.SetActive(visible);

        if (menuBackground != null)
            menuBackground.SetActive(false);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void BroadcastGameTimerStart()
    {
        double startTime = NetworkManager.Singleton.ServerTime.Time;
        using FastBufferWriter writer = new FastBufferWriter(sizeof(double), Allocator.Temp);
        writer.WriteValueSafe(startTime);
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(StartTimerMessageName, writer);
        StartGameTimer(startTime);
    }

    private void OnStartTimerMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out double startTime);
        StartGameTimer(startTime);
    }

    private void StartGameTimer(double startTime)
    {
        timerStartNetworkTime = startTime;
        timerStarted = true;
        UpdateGameTimer();
        ApplyGameplayInputState();
    }

    private void UpdateGameTimer()
    {
        if (timerLabel == null || !timerStarted || NetworkManager.Singleton == null)
            return;

        double elapsedSeconds = Math.Max(0d, NetworkManager.Singleton.LocalTime.Time - timerStartNetworkTime);
        timerLabel.text = $"Time: {FormatTime(elapsedSeconds)}";
    }

    private string FormatTime(double totalSeconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }

    public float GetElapsedGameTime()
    {
        if (!timerStarted || NetworkManager.Singleton == null)
            return 0f;

        return (float)Math.Max(0d, NetworkManager.Singleton.LocalTime.Time - timerStartNetworkTime);
    }

    public string GetFormattedElapsedGameTime()
    {
        return FormatTime(GetElapsedGameTime());
    }

    public void StartNewLevelTimerForAll()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        BroadcastGameTimerStart();
    }

    private void ShowMainMenu(string message)
    {
        if (menuCanvas != null)
            menuCanvas.gameObject.SetActive(true);

        if (menuBackground != null)
            menuBackground.SetActive(true);

        if (timerLabel != null)
            timerLabel.gameObject.SetActive(false);

        if (pausePanel != null)
            pausePanel.SetActive(false);

        ShowPanel(mainMenuPanel);
        SetStatus(message);
    }

    private void ShowLobby(bool isHost, string message)
    {
        ShowPanel(lobbyPanel);
        SetStatus(message);

        if (lobbyCodeLabel != null)
            lobbyCodeLabel.text = string.IsNullOrEmpty(currentJoinCode) ? "Code appears here" : currentJoinCode;

        if (startGameButton != null)
            startGameButton.gameObject.SetActive(isHost);

        UpdateLobbyPlayers();
    }

    private void ShowPanel(GameObject panel)
    {
        mainMenuPanel?.SetActive(panel == mainMenuPanel);
        joinPanel?.SetActive(panel == joinPanel);
        lobbyPanel?.SetActive(panel == lobbyPanel);
        loadingPanel?.SetActive(panel == loadingPanel);

        if (panel != pausePanel)
            pausePanel?.SetActive(false);
    }

    private void UpdateLobbyPlayers()
    {
        if (lobbyPlayersLabel == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return;

        int connectedPlayers = GetConnectedPlayersCount();

        lobbyPlayersLabel.text = $"Players: {connectedPlayers}/{maxPlayers + 1}";

        if (startGameButton != null)
            startGameButton.interactable = connectedPlayers >= RequiredPlayersToStart;

        if (!gameplayStarted && NetworkManager.Singleton.IsHost)
        {
            SetStatus(connectedPlayers >= RequiredPlayersToStart
                ? "Ready to start"
                : "Waiting for a second player");
        }
    }

    private void SetStatus(string message)
    {
        if (statusLabel != null)
            statusLabel.text = message;
    }

    private void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null)
            return;

        GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        DontDestroyOnLoad(eventSystemObject);
    }

    private void BuildMenu()
    {
        menuCanvas = new GameObject("Main Menu Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)).GetComponent<Canvas>();
        menuCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        menuCanvas.sortingOrder = 100;

        CanvasScaler scaler = menuCanvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        screenRoot = menuCanvas.GetComponent<RectTransform>();
        AddBackground(screenRoot);

        mainMenuPanel = CreatePanel("Main Menu");
        joinPanel = CreatePanel("Join Room");
        lobbyPanel = CreatePanel("Lobby");
        loadingPanel = CreatePanel("Loading");
        pausePanel = CreatePanel("Pause Menu");

        BuildMainMenu();
        BuildJoinPanel();
        BuildLobbyPanel();
        BuildLoadingPanel();
        BuildPausePanel();
        BuildStatusLabel();
        BuildTimerLabel();
    }

    private void BuildMainMenu()
    {
        TextMeshProUGUI title = CreateText(mainMenuPanel.transform, gameTitle, 72, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 100);

        TextMeshProUGUI subtitle = CreateText(mainMenuPanel.transform, "Gather your friends and escape the maze together", 28, FontStyles.Normal, TextAlignmentOptions.Center);
        subtitle.rectTransform.sizeDelta = new Vector2(860, 56);

        CreateButton(mainMenuPanel.transform, "Create Room", StartRelay);
        CreateButton(mainMenuPanel.transform, "Join Room", OpenJoinPanel);
    }

    private void BuildJoinPanel()
    {
        TextMeshProUGUI title = CreateText(joinPanel.transform, "Join Room", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        joinCodeInput = CreateInput(joinPanel.transform, "ROOM CODE");
        CreateButton(joinPanel.transform, "Connect", JoinRelay);
        CreateButton(joinPanel.transform, "Back", () => ShowMainMenu("Ready to connect"));
    }

    private void BuildLobbyPanel()
    {
        TextMeshProUGUI title = CreateText(lobbyPanel.transform, "Lobby", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        TextMeshProUGUI label = CreateText(lobbyPanel.transform, "Room Code", 24, FontStyles.Normal, TextAlignmentOptions.Center);
        label.rectTransform.sizeDelta = new Vector2(760, 42);

        lobbyCodeLabel = CreateText(lobbyPanel.transform, "------", 64, FontStyles.Bold, TextAlignmentOptions.Center);
        lobbyCodeLabel.rectTransform.sizeDelta = new Vector2(760, 90);

        lobbyPlayersLabel = CreateText(lobbyPanel.transform, $"Players: 1/{maxPlayers + 1}", 28, FontStyles.Normal, TextAlignmentOptions.Center);
        lobbyPlayersLabel.rectTransform.sizeDelta = new Vector2(760, 48);

        startGameButton = CreateButton(lobbyPanel.transform, "Start Game", StartGameAsHost);
        CreateButton(lobbyPanel.transform, "Leave", LeaveRoom);
    }

    private void BuildLoadingPanel()
    {
        TextMeshProUGUI title = CreateText(loadingPanel.transform, "Preparing", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        loadingLabel = CreateText(loadingPanel.transform, "Loading maze... 0%", 30, FontStyles.Normal, TextAlignmentOptions.Center);
        loadingLabel.rectTransform.sizeDelta = new Vector2(760, 64);
    }

    private void BuildPausePanel()
    {
        TextMeshProUGUI title = CreateText(pausePanel.transform, "Paused", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        CreateButton(pausePanel.transform, "Resume", ResumeGame);
        CreateButton(pausePanel.transform, "Exit Game", ExitGame);

        pausePanel.SetActive(false);
    }

    private void BuildStatusLabel()
    {
        statusLabel = CreateText(screenRoot, string.Empty, 24, FontStyles.Normal, TextAlignmentOptions.Center);
        statusLabel.rectTransform.anchorMin = new Vector2(0.5f, 0f);
        statusLabel.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        statusLabel.rectTransform.pivot = new Vector2(0.5f, 0f);
        statusLabel.rectTransform.anchoredPosition = new Vector2(0f, 72f);
        statusLabel.rectTransform.sizeDelta = new Vector2(900, 48);
        statusLabel.color = new Color(1f, 1f, 1f, 0.78f);
    }

    private void BuildTimerLabel()
    {
        timerLabel = CreateText(screenRoot, "Time: 00:00.00", 28, FontStyles.Bold, TextAlignmentOptions.Right);
        timerLabel.rectTransform.anchorMin = new Vector2(1f, 1f);
        timerLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        timerLabel.rectTransform.pivot = new Vector2(1f, 1f);
        timerLabel.rectTransform.anchoredPosition = new Vector2(-36f, -28f);
        timerLabel.rectTransform.sizeDelta = new Vector2(360, 48);
        timerLabel.gameObject.SetActive(false);
    }

    private GameObject CreatePanel(string panelName)
    {
        GameObject panel = new GameObject(panelName, typeof(RectTransform), typeof(VerticalLayoutGroup));
        panel.transform.SetParent(screenRoot, false);

        RectTransform rectTransform = panel.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(900, 620);

        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 18;
        layout.padding = new RectOffset(24, 24, 24, 24);

        return panel;
    }

    private void AddBackground(RectTransform parent)
    {
        menuBackground = new GameObject("Menu Background", typeof(RectTransform), typeof(Image));
        menuBackground.transform.SetParent(parent, false);

        RectTransform rectTransform = menuBackground.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        Image image = menuBackground.GetComponent<Image>();
        image.color = new Color(0.03f, 0.04f, 0.05f, 0.94f);
    }

    private void HideMenuForGameplay()
    {
        mainMenuPanel?.SetActive(false);
        joinPanel?.SetActive(false);
        lobbyPanel?.SetActive(false);
        loadingPanel?.SetActive(false);
        pausePanel?.SetActive(false);
        pauseMenuOpen = false;

        if (menuBackground != null)
            menuBackground.SetActive(false);

        if (timerLabel != null)
            timerLabel.gameObject.SetActive(true);

        SetStatus(string.Empty);
    }

    private int GetConnectedPlayersCount()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return 0;

        return NetworkManager.Singleton.ConnectedClientsList.Count;
    }

    private void CacheLegacyCanvas()
    {
        if (textMeshProUGUI != null)
            legacyCanvas = textMeshProUGUI.GetComponentInParent<Canvas>(true);

        if (legacyCanvas == null && _inputField != null)
            legacyCanvas = _inputField.GetComponentInParent<Canvas>(true);
    }

    private void SetLegacyCanvasVisible(bool visible)
    {
        if (legacyCanvas != null)
            legacyCanvas.gameObject.SetActive(visible);
    }

    private TextMeshProUGUI CreateText(Transform parent, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment)
    {
        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);

        TextMeshProUGUI label = textObject.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.fontStyle = style;
        label.alignment = alignment;
        label.color = Color.white;
        label.enableWordWrapping = true;

        return label;
    }

    private UnityEngine.UI.Button CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction action)
    {
        GameObject buttonObject = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(UnityEngine.UI.Button));
        buttonObject.transform.SetParent(parent, false);

        RectTransform rectTransform = buttonObject.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(420, 70);

        Image image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.17f, 0.34f, 0.28f, 1f);

        UnityEngine.UI.Button button = buttonObject.GetComponent<UnityEngine.UI.Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(action);

        TextMeshProUGUI text = CreateText(buttonObject.transform, label, 26, FontStyles.Bold, TextAlignmentOptions.Center);
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;

        return button;
    }

    private TMP_InputField CreateInput(Transform parent, string placeholder)
    {
        GameObject inputObject = new GameObject("Join Code Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        inputObject.transform.SetParent(parent, false);

        RectTransform rectTransform = inputObject.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(420, 70);

        Image image = inputObject.GetComponent<Image>();
        image.color = new Color(0.09f, 0.11f, 0.12f, 1f);

        TextMeshProUGUI text = CreateText(inputObject.transform, string.Empty, 28, FontStyles.Bold, TextAlignmentOptions.Center);
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(18, 0);
        text.rectTransform.offsetMax = new Vector2(-18, 0);

        TextMeshProUGUI placeholderText = CreateText(inputObject.transform, placeholder, 24, FontStyles.Normal, TextAlignmentOptions.Center);
        placeholderText.color = new Color(1f, 1f, 1f, 0.45f);
        placeholderText.rectTransform.anchorMin = Vector2.zero;
        placeholderText.rectTransform.anchorMax = Vector2.one;
        placeholderText.rectTransform.offsetMin = new Vector2(18, 0);
        placeholderText.rectTransform.offsetMax = new Vector2(-18, 0);

        TMP_InputField input = inputObject.GetComponent<TMP_InputField>();
        input.textComponent = text;
        input.placeholder = placeholderText;
        input.characterLimit = 8;
        input.contentType = TMP_InputField.ContentType.Alphanumeric;
        input.onValueChanged.AddListener(value => input.SetTextWithoutNotify(value.ToUpperInvariant()));

        return input;
    }
}
