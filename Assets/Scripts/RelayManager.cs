using System;
using System.Collections.Generic;
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
    private const string FullscreenPrefKey = "Maze.Fullscreen";
    private const string ResolutionPrefKey = "Maze.ResolutionIndex";
    private const int RequiredPlayersToStart = 2;

    [Header("Legacy scene references")]
    [SerializeField] private TextMeshProUGUI textMeshProUGUI;
    [SerializeField] private TMP_InputField _inputField;

    [Header("Relay")]
    [SerializeField] private int maxPlayers = 2;
    //[SerializeField] private VoiceManager voiceManager;

    [Header("Menu")]
    [SerializeField] private string gameTitle = "Maze Together";

    private Canvas menuCanvas;
    private RectTransform screenRoot;
    private GameObject mainMenuPanel;
    private GameObject joinPanel;
    private GameObject lobbyPanel;
    private GameObject pausePanel;
    private GameObject gameOverPanel;
    private TextMeshProUGUI gameOverInfoLabel;
    private UnityEngine.UI.Button gameOverRestartButton;
    private TextMeshProUGUI gameOverRestartLabel;
    private bool gameOverOpen;
private GameObject displaySettingsPanel;
    private TMP_InputField joinCodeInput;
    private TextMeshProUGUI statusLabel;
    private TextMeshProUGUI lobbyCodeLabel;
    private TextMeshProUGUI lobbyPlayersLabel;
    private TextMeshProUGUI timerLabel;
    private UnityEngine.UI.Button startGameButton;
    private TextMeshProUGUI fullscreenButtonLabel;
    private TextMeshProUGUI resolutionButtonLabel;
    private readonly List<TextMeshProUGUI> musicVolumeLabels = new List<TextMeshProUGUI>();
    private readonly List<TextMeshProUGUI> sfxVolumeLabels = new List<TextMeshProUGUI>();
    private static readonly float[] VolumeSteps = { 0f, 0.25f, 0.5f, 0.75f, 1f };
    private int lastLobbyPlayers;
    private readonly List<RectTransform> colorSwatches = new List<RectTransform>();
    private TextMeshProUGUI colorPickerLabel;
    private Canvas legacyCanvas;
    private GameObject menuBackground;
    private readonly Vector2Int[] resolutionOptions =
    {
        new Vector2Int(1920, 1080),
        new Vector2Int(1600, 900),
        new Vector2Int(1366, 768),
        new Vector2Int(1280, 720),
        new Vector2Int(1024, 768),
        new Vector2Int(800, 600)
    };

    private string currentJoinCode;
    private bool servicesReady;
    private bool flowBusy;
    private bool gameplayStarted;
    private bool inRoom;
    private string voiceWarning = string.Empty;

    /// <summary>True from the moment the host presses Start until the player leaves the room.</summary>
    public bool IsGameplayStarted => gameplayStarted;
    private bool gameplayReady;
    private bool timerStarted;
    private bool pauseMenuOpen;
    private double timerStartNetworkTime;
    private int currentResolutionIndex;

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
        NetworkManager.Singleton.OnClientStopped -= OnClientStopped;
        NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;

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
            currentJoinCode = await StartHostWithRelay(Mathf.Max(1, maxPlayers - 1));

            if (string.IsNullOrEmpty(currentJoinCode))
            {
                ShowMainMenu("Could not start host");
                return;
            }

            if (textMeshProUGUI != null)
                textMeshProUGUI.text = currentJoinCode;

            RegisterNetworkCallbacks();
            ShowLobby(true, "Room created. Waiting for another player");
            StartVoiceChatInBackground(currentJoinCode);
        }
        catch (Exception e) when (e is RelayServiceException || e is RequestFailedException)
        {
            Debug.LogError($"Relay room creation failed: {e.Message}");
            ShowMainMenu("Room creation failed");
            AudioManager.Play(Sfx.UiError);
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

        try
        {
            // DTLS encrypts the traffic between the players and the Relay server.
            transport.SetRelayServerData(allocation.ToRelayServerData("dtls"));
        }
        catch (ArgumentException e)
        {
            Debug.LogWarning($"DTLS endpoint is not available, falling back to UDP: {e.Message}");
            transport.SetHostRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData
            );
        }

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

        try
        {
            // Must match the host: DTLS (encrypted) if the Relay server offers it.
            transport.SetRelayServerData(allocation.ToRelayServerData("dtls"));
        }
        catch (ArgumentException e)
        {
            Debug.LogWarning($"DTLS endpoint is not available, falling back to UDP: {e.Message}");
            transport.SetClientRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData,
                allocation.HostConnectionData
            );
        }

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
        StartGameplay();
    }

    public void LeaveRoom()
    {
        inRoom = false; // a deliberate exit, not a lost connection

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();

        ResetSessionState();
        ShowMainMenu("You left the room");
    }

    private void ResetSessionState()
    {
        currentJoinCode = string.Empty;
        gameplayStarted = false;
        gameplayReady = false;
        timerStarted = false;
        pauseMenuOpen = false;

        StopVoiceChat();
        SetGameplayInput(false);
    }

    private void OnClientStopped(bool wasHost)
    {
        if (!inRoom)
            return; // we left on purpose

        HandleConnectionLost(wasHost ? "The room was closed" : "Lost connection to the host");
    }

    private void OnTransportFailure()
    {
        if (!inRoom)
            return;

        HandleConnectionLost("Network connection failed");
    }

    /// <summary>
    /// The host closed the game, the network dropped or the connection could not be established:
    /// shut networking down and bring the player back to the main menu.
    /// </summary>
    private void HandleConnectionLost(string message)
    {
        inRoom = false;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();

        ResetSessionState();
        ShowMainMenu(message);
    }

    public void ResumeGame()
    {
        SetPauseMenuVisible(false);
    }

    public void ExitGame()
    {
        inRoom = false;

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
            AudioManager.Play(Sfx.UiError);
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
                AudioManager.Play(Sfx.UiError);
                return;
            }

            currentJoinCode = joinCode;
            RegisterNetworkCallbacks();
            ShowLobby(false, "Connecting to the host...");
            StartVoiceChatInBackground(joinCode);
        }
        catch (Exception e) when (e is RelayServiceException || e is RequestFailedException)
        {
            Debug.LogError($"Relay connection failed: {e.Message}");
            SetStatus("Invalid code or connection failed");
            AudioManager.Play(Sfx.UiError);
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

    /// <summary>
    /// Voice login takes seconds, so it must never block the menu: the lobby is shown first and the voice
    /// chat connects in the background.
    /// </summary>
    private async void StartVoiceChatInBackground(string joinCode)
    {
        try
        {
            await StartVoiceChat(joinCode);

            if (!inRoom)
            {
                // The player left while voice was still connecting.
                //if (voiceManager != null)
                //    await voiceManager.StopVoice();
                voiceWarning = string.Empty;
                return;
            }

            if (!gameplayStarted && !string.IsNullOrEmpty(voiceWarning))
                SetStatus(lastStatusMessage);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Voice chat failed to start: {e.Message}");
        }
    }

    private async Task StartVoiceChat(string joinCode)
    {
        //if (voiceManager == null)
        //    voiceManager = FindAnyObjectByType<VoiceManager>();

        //if (voiceManager == null)
        {
            // LevelScene has no VoiceManager object, so create one at runtime.
            //voiceManager = new GameObject("VoiceManager").AddComponent<VoiceManager>();
        }

        string authPlayerId = AuthenticationService.Instance.PlayerId;
        string shortPlayerId = authPlayerId.Length > 8 ? authPlayerId[..8] : authPlayerId;
        string playerName = $"Player_{shortPlayerId}";
        string roomName = $"Maze_{joinCode}";

        //bool started = await voiceManager.StartVoice(playerName, roomName);

        // Show the problem to the player instead of failing silently.
        //voiceWarning = started
        //    ? string.Empty
        //    : "Voice chat is unavailable (check the microphone permission and your connection)";
    }

    private async void StopVoiceChat()
    {
        voiceWarning = string.Empty;

        //if (voiceManager != null)
        //    await voiceManager.StopVoice();
    }

    private void RegisterNetworkCallbacks()
    {
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientListChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientListChanged;
        NetworkManager.Singleton.OnClientStopped -= OnClientStopped;
        NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientListChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientListChanged;
        NetworkManager.Singleton.OnClientStopped += OnClientStopped;
        NetworkManager.Singleton.OnTransportFailure += OnTransportFailure;
        inRoom = true;

        NetworkManager.Singleton.CustomMessagingManager.UnregisterNamedMessageHandler(StartGameMessageName);
        NetworkManager.Singleton.CustomMessagingManager.UnregisterNamedMessageHandler(StartTimerMessageName);
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(StartGameMessageName, OnStartGameMessage);
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(StartTimerMessageName, OnStartTimerMessage);
        UpdateLobbyPlayers();
        ApplyGameplayInputState();
    }

    private void OnClientListChanged(ulong clientId)
    {
        // The client is really connected to the host only now (StartClient returns before that).
        if (!NetworkManager.Singleton.IsServer && !gameplayStarted &&
            clientId == NetworkManager.Singleton.LocalClientId && NetworkManager.Singleton.IsConnectedClient)
        {
            SetStatus("Connected. Waiting for the host to start");
        }

        UpdateLobbyPlayers();
        ApplyGameplayInputState();

        if (NetworkManager.Singleton.IsServer && gameplayReady && !timerStarted && GetConnectedPlayersCount() >= RequiredPlayersToStart)
            BroadcastGameTimerStart();
    }

    private void OnStartGameMessage(ulong senderClientId, FastBufferReader reader)
    {
        StartGameplay();
    }

    private void StartGameplay()
    {
        if (gameplayStarted)
            return;

        gameplayStarted = true;
        gameplayReady = true;
        HideMenuForGameplay();
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
        bool canMove = gameplayReady && timerStarted && !gameOverOpen;
        SetGameplayInput(canMove);

        if (canMove)
            SetStatus(string.Empty);
    }

    private void HandlePauseInput()
    {
        if (!gameplayStarted || gameOverOpen || Keyboard.current == null)
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
        SetStatus(string.Empty);
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
        lastLobbyPlayers = 0;
        gameOverOpen = false;
        gameOverPanel?.SetActive(false);
AudioManager.EnterMenu();
    }

    private void ShowLobby(bool isHost, string message)
    {
        // The game may already have started (the host can press Start before this code runs):
        // never bring the connection UI back over the gameplay.
        if (gameplayStarted)
            return;

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
        displaySettingsPanel?.SetActive(panel == mainMenuPanel);

        if (panel != pausePanel)
            pausePanel?.SetActive(false);
    }

    private void UpdateLobbyPlayers()
    {
        if (lobbyPlayersLabel == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return;

        int connectedPlayers = GetConnectedPlayersCount();

        lobbyPlayersLabel.text = $"Players: {connectedPlayers}/{maxPlayers}";

        // The host hears it when the friend joins, the friend when they get into the room.
        if (!gameplayStarted && connectedPlayers >= RequiredPlayersToStart && connectedPlayers > lastLobbyPlayers)
            AudioManager.Play(Sfx.PlayerJoined);
        lastLobbyPlayers = connectedPlayers;

        if (startGameButton != null)
            startGameButton.interactable = connectedPlayers >= RequiredPlayersToStart;

        if (!gameplayStarted && NetworkManager.Singleton.IsHost)
        {
            SetStatus(connectedPlayers >= RequiredPlayersToStart
                ? "Ready to start"
                : "Waiting for a second player");
        }
    }

    private string lastStatusMessage = string.Empty;

    private void SetStatus(string message)
    {
        lastStatusMessage = message;

        if (statusLabel == null)
            return;

        // Keep the voice problem visible in the menus and lobby.
        bool addVoiceWarning = !string.IsNullOrEmpty(voiceWarning) &&
                               !string.IsNullOrEmpty(message) &&
                               !gameplayStarted;

        statusLabel.text = addVoiceWarning ? $"{message}\n{voiceWarning}" : message;
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
        pausePanel = CreatePanel("Pause Menu");
        gameOverPanel = CreatePanel("Game Over");

        BuildMainMenu();
        BuildJoinPanel();
        BuildLobbyPanel();
        BuildPausePanel();
        BuildGameOverPanel();
BuildDisplaySettingsPanel();
        BuildStatusLabel();
        BuildTimerLabel();
    }

    private void BuildMainMenu()
    {
        TextMeshProUGUI title = CreateText(mainMenuPanel.transform, gameTitle, 72, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 100);

        TextMeshProUGUI subtitle = CreateText(mainMenuPanel.transform, "Gather one friend and escape the maze together", 28, FontStyles.Normal, TextAlignmentOptions.Center);
        subtitle.rectTransform.sizeDelta = new Vector2(860, 56);

        BuildColorPicker(mainMenuPanel.transform);

        CreateButton(mainMenuPanel.transform, "Create Room", StartRelay);
        CreateButton(mainMenuPanel.transform, "Join Room", OpenJoinPanel, UiButtonStyle.Secondary);
    }

    private void BuildColorPicker(Transform parent)
    {
        colorPickerLabel = CreateText(parent, string.Empty, 26, FontStyles.Normal, TextAlignmentOptions.Center);
        colorPickerLabel.rectTransform.sizeDelta = new Vector2(760, 40);

        GameObject row = new GameObject("Color Picker", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        ((RectTransform)row.transform).sizeDelta = new Vector2(820, 80);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 16;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        for (int i = 0; i < PlayerColorChoice.Count; i++)
            colorSwatches.Add(CreateColorSwatch(row.transform, i));

        UpdateColorPicker();
    }

    private RectTransform CreateColorSwatch(Transform parent, int index)
    {
        GameObject swatchObject = new GameObject(PlayerColorChoice.GetName(index), typeof(RectTransform), typeof(Image), typeof(UnityEngine.UI.Button));
        swatchObject.transform.SetParent(parent, false);

        RectTransform rectTransform = swatchObject.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(56, 56);

        // The light round Layer Lab button tinted with the palette color.
        Image image = swatchObject.GetComponent<Image>();
        image.color = PlayerColorChoice.GetSwatchColor(index);
        UiTheme theme = UiTheme.Current;
        if (theme != null && theme.BadgeSprite != null)
        {
            image.sprite = theme.BadgeSprite;
            image.preserveAspect = true;
        }

        UnityEngine.UI.Button button = swatchObject.GetComponent<UnityEngine.UI.Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => SelectPlayerColor(index));
        swatchObject.AddComponent<UiButtonSounds>();

        if (index == PlayerColorChoice.AutoIndex)
        {
            TextMeshProUGUI autoLabel = CreateText(swatchObject.transform, "A", 26, FontStyles.Bold, TextAlignmentOptions.Center);
            autoLabel.color = new Color(0.2f, 0.22f, 0.26f, 1f);
            autoLabel.raycastTarget = false;
            autoLabel.rectTransform.anchorMin = Vector2.zero;
            autoLabel.rectTransform.anchorMax = Vector2.one;
            autoLabel.rectTransform.offsetMin = Vector2.zero;
            autoLabel.rectTransform.offsetMax = Vector2.zero;
        }

        return rectTransform;
    }

    private void SelectPlayerColor(int index)
    {
        PlayerColorChoice.SelectedIndex = index;
        UpdateColorPicker();
    }

    private void UpdateColorPicker()
    {
        int selected = PlayerColorChoice.SelectedIndex;

        // The selected swatch is drawn bigger; scale does not affect the layout, so the row does not jump.
        for (int i = 0; i < colorSwatches.Count; i++)
            colorSwatches[i].localScale = i == selected ? Vector3.one * 1.3f : Vector3.one;

        if (colorPickerLabel != null)
        {
            colorPickerLabel.text = selected == PlayerColorChoice.AutoIndex
                ? "Character color: Auto (chosen by the game)"
                : $"Character color: {PlayerColorChoice.GetName(selected)}";
        }
    }

    private void BuildJoinPanel()
    {
        TextMeshProUGUI title = CreateText(joinPanel.transform, "Join Room", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        joinCodeInput = CreateInput(joinPanel.transform, "ROOM CODE");
        CreateButton(joinPanel.transform, "Connect", JoinRelay);
        CreateButton(joinPanel.transform, "Back", () => ShowMainMenu("Ready to connect"), UiButtonStyle.Neutral);
    }

    private void BuildLobbyPanel()
    {
        TextMeshProUGUI title = CreateText(lobbyPanel.transform, "Lobby", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        TextMeshProUGUI label = CreateText(lobbyPanel.transform, "Room Code", 24, FontStyles.Normal, TextAlignmentOptions.Center);
        label.rectTransform.sizeDelta = new Vector2(760, 42);

        lobbyCodeLabel = CreateText(lobbyPanel.transform, "------", 64, FontStyles.Bold, TextAlignmentOptions.Center);
        lobbyCodeLabel.rectTransform.sizeDelta = new Vector2(760, 90);

        lobbyPlayersLabel = CreateText(lobbyPanel.transform, $"Players: 1/{maxPlayers}", 28, FontStyles.Normal, TextAlignmentOptions.Center);
        lobbyPlayersLabel.rectTransform.sizeDelta = new Vector2(760, 48);

        startGameButton = CreateButton(lobbyPanel.transform, "Start Game", StartGameAsHost);
        CreateButton(lobbyPanel.transform, "Leave", LeaveRoom, UiButtonStyle.Danger);
    }

    private void BuildPausePanel()
    {
        TextMeshProUGUI title = CreateText(pausePanel.transform, "Paused", 54, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 80);

        CreateButton(pausePanel.transform, "Resume", ResumeGame);
        CreateVolumeButtons(pausePanel.transform, new Vector2(420, 80), 28);
        CreateButton(pausePanel.transform, "Exit Game", ExitGame, UiButtonStyle.Danger);

        pausePanel.SetActive(false);
    }

    private void BuildGameOverPanel()
    {
        TextMeshProUGUI title = CreateText(gameOverPanel.transform, "Game Over", 72, FontStyles.Bold, TextAlignmentOptions.Center);
        title.rectTransform.sizeDelta = new Vector2(760, 100);
        title.color = new Color(1f, 0.35f, 0.35f, 1f);

        gameOverInfoLabel = CreateText(gameOverPanel.transform, string.Empty, 30, FontStyles.Normal, TextAlignmentOptions.Center);
        gameOverInfoLabel.rectTransform.sizeDelta = new Vector2(760, 100);

        gameOverRestartButton = CreateButton(gameOverPanel.transform, "Restart", RestartFromGameOver);
        gameOverRestartLabel = gameOverRestartButton.GetComponentInChildren<TextMeshProUGUI>();
        CreateButton(gameOverPanel.transform, "Leave", LeaveRoom, UiButtonStyle.Danger);

        gameOverPanel.SetActive(false);
    }

    /// <summary>Called on both players by DungeonProgressManager when a player has no hearts left.</summary>
    public void ShowGameOver(bool localPlayerLost, int level)
    {
        gameOverOpen = true;
        pauseMenuOpen = false;
        pausePanel?.SetActive(false);

        if (menuCanvas != null)
            menuCanvas.gameObject.SetActive(true);

        if (menuBackground != null)
            menuBackground.SetActive(true);

        gameOverPanel?.SetActive(true);

        if (gameOverInfoLabel != null)
        {
            string who = localPlayerLost ? "You ran out of hearts" : "Your friend ran out of hearts";
            gameOverInfoLabel.text = $"{who}\nReached level {level}";
        }

        if (gameOverRestartButton != null)
            gameOverRestartButton.interactable = true;

        if (gameOverRestartLabel != null)
            gameOverRestartLabel.text = "Restart";

        SetGameplayInput(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    /// <summary>The server restarted the game from level 1.</summary>
    public void HideGameOver()
    {
        if (!gameOverOpen)
            return;

        gameOverOpen = false;
        gameOverPanel?.SetActive(false);

        if (menuBackground != null)
            menuBackground.SetActive(false);

        ApplyGameplayInputState();
    }

    // Either player can restart; the server restarts the game for both.
    private void RestartFromGameOver()
    {
        if (DungeonProgressManager.Instance == null)
            return;

        if (gameOverRestartButton != null)
            gameOverRestartButton.interactable = false;

        if (gameOverRestartLabel != null)
            gameOverRestartLabel.text = "Restarting...";

        DungeonProgressManager.Instance.RequestRestart();
    }

    private void BuildDisplaySettingsPanel()
    {
        displaySettingsPanel = new GameObject("Display Settings", typeof(RectTransform), typeof(VerticalLayoutGroup));
        displaySettingsPanel.transform.SetParent(screenRoot, false);

        RectTransform rectTransform = displaySettingsPanel.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(1f, 1f);
        rectTransform.anchorMax = new Vector2(1f, 1f);
        rectTransform.pivot = new Vector2(1f, 1f);
        rectTransform.anchoredPosition = new Vector2(-36f, -28f);
        rectTransform.sizeDelta = new Vector2(320, 300);

        VerticalLayoutGroup layout = displaySettingsPanel.GetComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperRight;
        layout.spacing = 10;
        layout.padding = new RectOffset(0, 0, 0, 0);
        UseChildSizes(layout);

        TextMeshProUGUI title = CreateText(displaySettingsPanel.transform, "Display", 22, FontStyles.Bold, TextAlignmentOptions.Right);
        title.rectTransform.sizeDelta = new Vector2(300, 34);

        UnityEngine.UI.Button fullscreenButton = CreateButton(
            displaySettingsPanel.transform,
            string.Empty,
            ToggleFullscreen,
            new Vector2(300, 56),
            20,
            UiButtonStyle.Settings);
        fullscreenButtonLabel = fullscreenButton.GetComponentInChildren<TextMeshProUGUI>();

        UnityEngine.UI.Button resolutionButton = CreateButton(
            displaySettingsPanel.transform,
            string.Empty,
            CycleResolution,
            new Vector2(300, 56),
            20,
            UiButtonStyle.Settings);
        resolutionButtonLabel = resolutionButton.GetComponentInChildren<TextMeshProUGUI>();

        CreateVolumeButtons(displaySettingsPanel.transform, new Vector2(300, 56), 20);

        currentResolutionIndex = GetClosestResolutionIndex(Screen.width, Screen.height);

        // Restore the display settings the player chose in a previous session.
        if (PlayerPrefs.HasKey(FullscreenPrefKey) && PlayerPrefs.HasKey(ResolutionPrefKey))
        {
            ApplyDisplaySettings(
                PlayerPrefs.GetInt(FullscreenPrefKey) == 1,
                PlayerPrefs.GetInt(ResolutionPrefKey),
                save: false);
        }

        UpdateDisplaySettingsLabels();
    }

    private void CreateVolumeButtons(Transform parent, Vector2 size, float fontSize)
    {
        UnityEngine.UI.Button musicButton = CreateButton(parent, string.Empty, CycleMusicVolume, size, fontSize, UiButtonStyle.Settings);
        musicVolumeLabels.Add(musicButton.GetComponentInChildren<TextMeshProUGUI>());

        UnityEngine.UI.Button sfxButton = CreateButton(parent, string.Empty, CycleSfxVolume, size, fontSize, UiButtonStyle.Settings);
        sfxVolumeLabels.Add(sfxButton.GetComponentInChildren<TextMeshProUGUI>());

        UpdateVolumeLabels();
    }

    private void CycleMusicVolume()
    {
        AudioManager.MusicVolume = GetNextVolumeStep(AudioManager.MusicVolume);
        UpdateVolumeLabels();
    }

    private void CycleSfxVolume()
    {
        AudioManager.SfxVolume = GetNextVolumeStep(AudioManager.SfxVolume);
        UpdateVolumeLabels();
    }

    private static float GetNextVolumeStep(float volume)
    {
        foreach (float step in VolumeSteps)
        {
            if (step > volume + 0.01f)
                return step;
        }

        return VolumeSteps[0];
    }

    // Both the Display panel and the pause menu have volume buttons; keep their labels in sync.
    private void UpdateVolumeLabels()
    {
        string music = $"Music: {Mathf.RoundToInt(AudioManager.MusicVolume * 100f)}%";
        string sounds = $"Sounds: {Mathf.RoundToInt(AudioManager.SfxVolume * 100f)}%";

        foreach (TextMeshProUGUI label in musicVolumeLabels)
            label.text = music;

        foreach (TextMeshProUGUI label in sfxVolumeLabels)
            label.text = sounds;
    }

    private void ToggleFullscreen()
    {
        ApplyDisplaySettings(!Screen.fullScreen, currentResolutionIndex);
    }

    private void CycleResolution()
    {
        int nextIndex = (currentResolutionIndex + 1) % resolutionOptions.Length;
        ApplyDisplaySettings(Screen.fullScreen, nextIndex);
    }

    private void ApplyDisplaySettings(bool fullscreen, int resolutionIndex, bool save = true)
    {
        currentResolutionIndex = Mathf.Clamp(resolutionIndex, 0, resolutionOptions.Length - 1);
        Vector2Int resolution = resolutionOptions[currentResolutionIndex];
        Screen.SetResolution(resolution.x, resolution.y, fullscreen);

        if (save)
        {
            PlayerPrefs.SetInt(FullscreenPrefKey, fullscreen ? 1 : 0);
            PlayerPrefs.SetInt(ResolutionPrefKey, currentResolutionIndex);
            PlayerPrefs.Save();
        }

        UpdateDisplaySettingsLabels();
    }

    private int GetClosestResolutionIndex(int width, int height)
    {
        int bestIndex = 0;
        int bestDifference = int.MaxValue;

        for (int i = 0; i < resolutionOptions.Length; i++)
        {
            Vector2Int option = resolutionOptions[i];
            int difference = Mathf.Abs(option.x - width) + Mathf.Abs(option.y - height);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void UpdateDisplaySettingsLabels()
    {
        if (fullscreenButtonLabel != null)
            fullscreenButtonLabel.text = Screen.fullScreen ? "Fullscreen: On" : "Fullscreen: Off";

        if (resolutionButtonLabel != null)
        {
            Vector2Int resolution = resolutionOptions[currentResolutionIndex];
            resolutionButtonLabel.text = $"Resolution: {resolution.x}x{resolution.y}";
        }
    }

    private void BuildStatusLabel()
    {
        statusLabel = CreateText(screenRoot, string.Empty, 24, FontStyles.Normal, TextAlignmentOptions.Center);
        statusLabel.rectTransform.anchorMin = new Vector2(0.5f, 0f);
        statusLabel.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        statusLabel.rectTransform.pivot = new Vector2(0.5f, 0f);
        statusLabel.rectTransform.anchoredPosition = new Vector2(0f, 72f);
        statusLabel.rectTransform.sizeDelta = new Vector2(900, 84);
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
        UseChildSizes(layout);

        return panel;
    }

    // Children keep the sizes set in code instead of being stretched by the layout group.
    private static void UseChildSizes(VerticalLayoutGroup layout)
    {
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
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
        pausePanel?.SetActive(false);
        displaySettingsPanel?.SetActive(false);
        pauseMenuOpen = false;

        if (menuBackground != null)
            menuBackground.SetActive(false);

        if (timerLabel != null)
            timerLabel.gameObject.SetActive(true);

        SetStatus(string.Empty);
        AudioManager.EnterGameplay();
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
        UiTheme.ApplyFont(label);
        label.text = text;
        label.fontSize = fontSize;
        label.fontStyle = style;
        label.alignment = alignment;
        label.color = Color.white;
        label.enableWordWrapping = true;

        return label;
    }

    private UnityEngine.UI.Button CreateButton(
        Transform parent,
        string label,
        UnityEngine.Events.UnityAction action,
        UiButtonStyle style = UiButtonStyle.Primary)
    {
        return CreateButton(parent, label, action, new Vector2(420, 80), 28, style);
    }

    private UnityEngine.UI.Button CreateButton(
        Transform parent,
        string label,
        UnityEngine.Events.UnityAction action,
        Vector2 size,
        float fontSize,
        UiButtonStyle style)
    {
        UiTheme theme = UiTheme.Current;
        UnityEngine.UI.Button themedButton = theme != null ? theme.CreateButton(parent, style, label, size, fontSize) : null;
        if (themedButton != null)
        {
            themedButton.onClick.AddListener(action);
            return themedButton;
        }

        GameObject buttonObject = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(UnityEngine.UI.Button));
        buttonObject.transform.SetParent(parent, false);

        RectTransform rectTransform = buttonObject.GetComponent<RectTransform>();
        rectTransform.sizeDelta = size;

        Image image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.17f, 0.34f, 0.28f, 1f);

        UnityEngine.UI.Button button = buttonObject.GetComponent<UnityEngine.UI.Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(action);
        buttonObject.AddComponent<UiButtonSounds>();

        TextMeshProUGUI text = CreateText(buttonObject.transform, label, fontSize, FontStyles.Bold, TextAlignmentOptions.Center);
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
        rectTransform.sizeDelta = new Vector2(420, 80);

        UiTheme theme = UiTheme.Current;
        bool themed = theme != null && theme.InputSprite != null;
        Color textColor = themed ? new Color(0.13f, 0.15f, 0.18f, 1f) : Color.white;

        Image image = inputObject.GetComponent<Image>();
        image.color = new Color(0.09f, 0.11f, 0.12f, 1f);
        if (themed)
        {
            image.color = Color.white;
            UiTheme.ApplySlicedSprite(image, theme.InputSprite, rectTransform.sizeDelta.y);
        }

        TextMeshProUGUI text = CreateText(inputObject.transform, string.Empty, 28, FontStyles.Bold, TextAlignmentOptions.Center);
        text.color = textColor;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(18, 0);
        text.rectTransform.offsetMax = new Vector2(-18, 0);

        TextMeshProUGUI placeholderText = CreateText(inputObject.transform, placeholder, 24, FontStyles.Normal, TextAlignmentOptions.Center);
        placeholderText.color = new Color(textColor.r, textColor.g, textColor.b, 0.45f);
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
