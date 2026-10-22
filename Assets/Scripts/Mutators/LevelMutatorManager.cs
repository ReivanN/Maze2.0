using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Tunable numbers for every mutator. Edit them in the Inspector of LevelMutatorManager.
/// </summary>
[System.Serializable]
public class LevelMutatorSettings
{
    [Header("Low Gravity")]
    [Tooltip("Multiplier for the controller's Gravity. 0.4 = 40% of normal gravity.")]
    [Range(0.1f, 1f)] public float lowGravityMultiplier = 0.4f;

    [Header("Heavy World")]
    [Tooltip("Multiplier for the controller's Gravity. 2 = twice as strong.")]
    [Range(1f, 4f)] public float heavyGravityMultiplier = 2f;
    [Tooltip("Multiplier for jump height. Lower it if the level feels unfair.")]
    [Range(0.3f, 1.5f)] public float heavyJumpHeightMultiplier = 0.8f;

    [Header("Ice")]
    [Tooltip("ThirdPersonController.SpeedChangeRate while on ice. Normal value is 10, lower = more slippery.")]
    [Range(0.2f, 10f)] public float iceSpeedChangeRate = 1.2f;

    [Header("Mirror Controls")]
    public bool mirrorHorizontal = true;
    public bool mirrorVertical = true;

    [Header("Wind")]
    [Tooltip("Meters per second of push. Walk speed is about 2, sprint about 5.3.")]
    [Range(0.2f, 4f)] public float windStrength = 1.4f;
    [Tooltip("How often the wind direction changes, in seconds.")]
    public float windChangeInterval = 12f;
    [Tooltip("How many seconds before the change the warning is shown.")]
    public float windWarningTime = 2.5f;

    [Header("Fog and Darkness")]
    [Tooltip("Exponential-squared fog density. Fog is measured from the CAMERA (about 10 m above the players), " +
             "so it dims the whole screen. 0.085 keeps about 50% of the colour at the players and about 20% at the screen edges.")]
    [Range(0.01f, 0.5f)] public float darkFogDensity = 0.085f;
    public Color darkFogColor = Color.black;
    public Color darkAmbientColor = Color.black;
    [Tooltip("Directional lights are multiplied by this. Even 0.1 is clearly visible (linear light), so the default is 0.")]
    [Range(0f, 1f)] public float darkSunMultiplier = 0f;
    [Tooltip("Skybox reflections also light the level. 0 turns them off.")]
    [Range(0f, 1f)] public float darkReflectionIntensity = 0f;
    [Tooltip("Radius of the light every player carries. Smaller = darker level.")]
    public float darkLanternRange = 6.5f;
    public float darkLanternIntensity = 4f;
    public Color darkLanternColor = new Color(1f, 0.85f, 0.6f, 1f);
    public float darkLanternHeight = 2.2f;
}

/// <summary>
/// Picks a random level mutator for EVERY level on the server and applies it on every client, so it affects both players.
///
/// Flow on each client:
///   1. When the game starts (or a new level starts) a full-screen intro shows the mutator.
///   2. The intro shrinks into a small square at the top of the screen.
///   3. Hovering the square with the mouse shows which mutator is active.
///
/// Setup: add this component to the same GameObject as DungeonProgressManager (it already has a NetworkObject).
/// DungeonProgressManager calls PickMutatorForLevel(...) when a new level is ready.
/// </summary>
public class LevelMutatorManager : NetworkBehaviour
{
    private const string LanternName = "MutatorLantern";

    public static LevelMutatorManager Instance { get; private set; }

    [Header("Selection")]
    [Tooltip("Anything except None forces this mutator on every level. Useful for testing, leave None for random.")]
    [SerializeField] private LevelMutatorType forcedMutator = LevelMutatorType.None;
    [SerializeField] private bool avoidRepeatingPrevious = true;

    [Header("Intro")]
    [Tooltip("Seconds the full-screen intro stays before it starts to shrink.")]
    [SerializeField] private float introHoldDuration = 3.2f;
    [Tooltip("Seconds the intro takes to shrink into the small square.")]
    [SerializeField] private float introShrinkDuration = 0.8f;

    [Header("Mutator values")]
    [SerializeField] private LevelMutatorSettings settings = new LevelMutatorSettings();

    private readonly NetworkVariable<int> activeMutator = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Increases on every pick, so clients show the intro even when the same mutator is forced twice in a row.
    private readonly NetworkVariable<int> mutatorSerial = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> windAngle = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> windWarning = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public LevelMutatorType ActiveMutator => (LevelMutatorType)activeMutator.Value;
    public float WindAngleDegrees => windAngle.Value;
    public LevelMutatorSettings Settings => settings;

    // server only
    private float windTimer;

    // client side
    private LevelMutatorType appliedMutator = LevelMutatorType.None;
    private PlayerMutatorApplier localApplier;
    private RelayManager relayManager;
    private bool wasGameplayActive;
    private int lastSeenSerial = -1;
    private bool introPending;
    private float introStartTime = -1000f;

    // fog / darkness
    private struct SunState
    {
        public Light light;
        public float intensity;
    }

    private bool renderSnapshotTaken;
    private bool savedFog;
    private FogMode savedFogMode;
    private Color savedFogColor;
    private float savedFogDensity;
    private AmbientMode savedAmbientMode;
    private Color savedAmbientLight;
    private float savedReflectionIntensity;
    private readonly List<SunState> savedSuns = new List<SunState>();
    private readonly List<GameObject> lanterns = new List<GameObject>();
    private float lanternRefreshTimer;

    // HUD
    private Canvas hudCanvas;
    private Image introBackdrop;
    private RectTransform badgeRect;
    private Image badgeImage;
    private TextMeshProUGUI badgeLabel;
    private TextMeshProUGUI introTitle;
    private TextMeshProUGUI introDescription;
    private RectTransform tooltipPanel;
    private TextMeshProUGUI tooltipText;
    private TextMeshProUGUI windWarningLabel;
    private RectTransform windWidget;
    private RectTransform windArrow;

    // ================= NETWORK =================

    public override void OnNetworkSpawn()
    {
        Instance = this;

        if (IsServer)
            SelectForLevel();
    }

    public override void OnNetworkDespawn()
    {
        CleanupClientState();

        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Server only. Call when a new level has been generated and is about to start.
    /// </summary>
    public void PickMutatorForLevel(int level)
    {
        if (!IsServer)
            return;

        SelectForLevel();
    }

    private void SelectForLevel()
    {
        LevelMutatorType next = ChooseMutator();

        activeMutator.Value = (int)next;
        mutatorSerial.Value++;

        windWarning.Value = false;

        if (next == LevelMutatorType.Wind)
        {
            windAngle.Value = Random.Range(0f, 360f);
            windTimer = Mathf.Max(settings.windChangeInterval, settings.windWarningTime + 1f);
        }
    }

    private LevelMutatorType ChooseMutator()
    {
        if (forcedMutator != LevelMutatorType.None)
            return forcedMutator;

        List<LevelMutatorType> pool = new List<LevelMutatorType>(LevelMutatorInfo.Selectable);

        if (avoidRepeatingPrevious && pool.Count > 1)
            pool.Remove(ActiveMutator);

        return pool[Random.Range(0, pool.Count)];
    }

    // ================= UPDATE =================

    private void Update()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
            UpdateWindOnServer();

        // Effects start when the game starts (not in the lobby) and stop when the player leaves the room.
        bool gameplayActive = IsGameplayActive();
        LevelMutatorType desired = gameplayActive ? ActiveMutator : LevelMutatorType.None;

        if (gameplayActive && !wasGameplayActive)
            introPending = true; // the game just started

        wasGameplayActive = gameplayActive;

        int serial = mutatorSerial.Value;
        if (serial != lastSeenSerial)
        {
            lastSeenSerial = serial;

            if (gameplayActive)
                introPending = true; // a new level got a new mutator
        }

        if (desired != appliedMutator)
            ApplyMutatorLocally(desired);

        if (introPending && desired != LevelMutatorType.None)
        {
            introPending = false;
            StartIntro(desired);
        }

        EnsureLocalPlayerApplier();

        if (appliedMutator == LevelMutatorType.FogAndDarkness)
        {
            // Players can join or respawn while the level is dark, so keep lanterns up to date.
            lanternRefreshTimer -= Time.deltaTime;
            if (lanternRefreshTimer <= 0f)
            {
                lanternRefreshTimer = 1f;
                RefreshLanterns();
            }
        }

        UpdateHud();
    }

    private bool IsGameplayActive()
    {
        if (relayManager == null)
            relayManager = FindAnyObjectByType<RelayManager>();

        // Without a menu (for example a test scene) the effects start immediately.
        return relayManager == null || relayManager.IsGameplayStarted;
    }

    private void UpdateWindOnServer()
    {
        if (ActiveMutator != LevelMutatorType.Wind)
            return;

        windTimer -= Time.deltaTime;

        if (!windWarning.Value && windTimer <= settings.windWarningTime)
            windWarning.Value = true;

        if (windTimer <= 0f)
        {
            // Turn the wind by 90-270 degrees so the change is always noticeable.
            windAngle.Value = Mathf.Repeat(windAngle.Value + Random.Range(90f, 270f), 360f);
            windWarning.Value = false;
            windTimer = Mathf.Max(settings.windChangeInterval, settings.windWarningTime + 1f);
        }
    }

    // ================= APPLY (every client) =================

    private void ApplyMutatorLocally(LevelMutatorType type)
    {
        if (type == appliedMutator)
            return;

        if (appliedMutator == LevelMutatorType.FogAndDarkness)
            ExitDarkness();

        appliedMutator = type;

        if (type == LevelMutatorType.FogAndDarkness)
            EnterDarkness();

        if (localApplier != null)
            localApplier.Apply(type);
    }

    private void EnsureLocalPlayerApplier()
    {
        NetworkManager networkManager = NetworkManager.Singleton;

        if (networkManager == null ||
            networkManager.LocalClient == null ||
            networkManager.LocalClient.PlayerObject == null)
        {
            localApplier = null;
            return;
        }

        GameObject playerObject = networkManager.LocalClient.PlayerObject.gameObject;

        if (localApplier != null && localApplier.gameObject == playerObject)
            return;

        localApplier = playerObject.GetComponent<PlayerMutatorApplier>();
        if (localApplier == null)
            localApplier = playerObject.AddComponent<PlayerMutatorApplier>();

        localApplier.Configure(this);
        localApplier.Apply(appliedMutator);
    }

    private void CleanupClientState()
    {
        if (appliedMutator == LevelMutatorType.FogAndDarkness)
            ExitDarkness();

        appliedMutator = LevelMutatorType.None;

        if (localApplier != null)
        {
            Destroy(localApplier); // its OnDestroy restores the controller values
            localApplier = null;
        }

        if (hudCanvas != null)
        {
            Destroy(hudCanvas.gameObject);
            hudCanvas = null;
        }
    }

    // ================= FOG AND DARKNESS =================

    private void EnterDarkness()
    {
        if (!renderSnapshotTaken)
        {
            savedFog = RenderSettings.fog;
            savedFogMode = RenderSettings.fogMode;
            savedFogColor = RenderSettings.fogColor;
            savedFogDensity = RenderSettings.fogDensity;
            savedAmbientMode = RenderSettings.ambientMode;
            savedAmbientLight = RenderSettings.ambientLight;
            savedReflectionIntensity = RenderSettings.reflectionIntensity;

            savedSuns.Clear();
            foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                    savedSuns.Add(new SunState { light = light, intensity = light.intensity });
            }

            renderSnapshotTaken = true;
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = settings.darkFogColor;
        RenderSettings.fogDensity = settings.darkFogDensity;

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = settings.darkAmbientColor;
        RenderSettings.reflectionIntensity = settings.darkReflectionIntensity;

        foreach (SunState sun in savedSuns)
        {
            if (sun.light != null)
                sun.light.intensity = sun.intensity * settings.darkSunMultiplier;
        }

        lanternRefreshTimer = 0f;
        RefreshLanterns();
    }

    private void ExitDarkness()
    {
        if (renderSnapshotTaken)
        {
            RenderSettings.fog = savedFog;
            RenderSettings.fogMode = savedFogMode;
            RenderSettings.fogColor = savedFogColor;
            RenderSettings.fogDensity = savedFogDensity;
            RenderSettings.ambientMode = savedAmbientMode;
            RenderSettings.ambientLight = savedAmbientLight;
            RenderSettings.reflectionIntensity = savedReflectionIntensity;

            foreach (SunState sun in savedSuns)
            {
                if (sun.light != null)
                    sun.light.intensity = sun.intensity;
            }

            savedSuns.Clear();
            renderSnapshotTaken = false;
        }

        foreach (GameObject lantern in lanterns)
        {
            if (lantern != null)
                Destroy(lantern);
        }

        lanterns.Clear();
    }

    /// <summary>
    /// Gives every player (local and remote) a small point light, so both players can see each other in the dark.
    /// Lights are created locally on each client, nothing is networked.
    /// </summary>
    private void RefreshLanterns()
    {
        lanterns.RemoveAll(lantern => lantern == null);

        foreach (NetworkObject networkObject in FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
        {
            if (!networkObject.IsPlayerObject)
                continue;

            if (networkObject.transform.Find(LanternName) != null)
                continue;

            GameObject lanternObject = new GameObject(LanternName);
            lanternObject.transform.SetParent(networkObject.transform, false);
            lanternObject.transform.localPosition = new Vector3(0f, settings.darkLanternHeight, 0f);

            Light light = lanternObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = settings.darkLanternRange;
            light.intensity = settings.darkLanternIntensity;
            light.color = settings.darkLanternColor;
            light.shadows = LightShadows.None;

            lanterns.Add(lanternObject);
        }
    }

    // ================= HUD =================

    private void StartIntro(LevelMutatorType type)
    {
        EnsureHud();

        introTitle.text = LevelMutatorInfo.GetName(type).ToUpperInvariant();
        introDescription.text = LevelMutatorInfo.GetDescription(type);
        introStartTime = Time.unscaledTime;
    }

    private void UpdateHud()
    {
        bool hasMutator = appliedMutator != LevelMutatorType.None;

        if (!hasMutator && hudCanvas == null)
            return;

        EnsureHud();

        // 0 = full-screen intro, 1 = small square at the top.
        float elapsed = Time.unscaledTime - introStartTime;
        float shrink = Mathf.Max(0.01f, introShrinkDuration);
        float t = Mathf.Clamp01((elapsed - introHoldDuration) / shrink);
        float eased = t * t * (3f - 2f * t);
        bool introRunning = hasMutator && elapsed < introHoldDuration + shrink;

        badgeRect.gameObject.SetActive(hasMutator);

        if (hasMutator)
        {
            badgeImage.color = LevelMutatorInfo.GetColor(appliedMutator);
            badgeLabel.text = LevelMutatorInfo.GetShortCode(appliedMutator);
            ApplyBadgeLayout(eased);
        }

        introBackdrop.gameObject.SetActive(introRunning);
        introTitle.gameObject.SetActive(introRunning);
        introDescription.gameObject.SetActive(introRunning);

        if (introRunning)
        {
            introBackdrop.color = new Color(0f, 0f, 0f, 0.9f * (1f - eased));

            float textAlpha = 1f - Mathf.Clamp01(eased * 1.6f);
            introTitle.color = new Color(1f, 1f, 1f, textAlpha);
            introDescription.color = new Color(0.85f, 0.85f, 0.85f, textAlpha);
        }

        // Hover tooltip under the square (only after the intro has shrunk).
        bool hover = false;
        if (hasMutator && !introRunning && Mouse.current != null)
        {
            hover = RectTransformUtility.RectangleContainsScreenPoint(
                badgeRect, Mouse.current.position.ReadValue(), null);
        }

        tooltipPanel.gameObject.SetActive(hover);
        if (hover)
        {
            tooltipText.text =
                $"<b>{LevelMutatorInfo.GetName(appliedMutator)}</b>\n" +
                $"{LevelMutatorInfo.GetDescription(appliedMutator)}";
        }

        UpdateWindHud();
    }

    private void ApplyBadgeLayout(float eased)
    {
        Vector2 anchor = Vector2.Lerp(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f), eased);

        badgeRect.anchorMin = anchor;
        badgeRect.anchorMax = anchor;
        badgeRect.pivot = anchor;
        badgeRect.anchoredPosition = Vector2.Lerp(new Vector2(0f, 120f), new Vector2(0f, -24f), eased);
        badgeRect.sizeDelta = Vector2.Lerp(new Vector2(240f, 240f), new Vector2(80f, 80f), eased);
    }

    private void UpdateWindHud()
    {
        bool windActive = appliedMutator == LevelMutatorType.Wind;

        windWidget.gameObject.SetActive(windActive);
        windWarningLabel.gameObject.SetActive(windActive && windWarning.Value);

        if (windActive)
        {
            // The arrow shows the wind direction relative to where the camera looks.
            Camera cam = Camera.main;
            float cameraYaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            float relativeAngle = windAngle.Value - cameraYaw;
            windArrow.localRotation = Quaternion.Euler(0f, 0f, -relativeAngle);
        }
    }

    private void EnsureHud()
    {
        if (hudCanvas != null)
            return;

        GameObject canvasObject = new GameObject("Level Mutator HUD", typeof(Canvas), typeof(CanvasScaler));
        hudCanvas = canvasObject.GetComponent<Canvas>();
        hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        hudCanvas.sortingOrder = 50; // below the main menu / pause menu (100)

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        RectTransform root = hudCanvas.GetComponent<RectTransform>();

        // Full-screen dark backdrop of the intro.
        GameObject backdropObject = new GameObject("Intro Backdrop", typeof(RectTransform), typeof(Image));
        backdropObject.transform.SetParent(root, false);
        introBackdrop = backdropObject.GetComponent<Image>();
        introBackdrop.color = new Color(0f, 0f, 0f, 0.9f);
        introBackdrop.raycastTarget = false;

        RectTransform backdropRect = introBackdrop.rectTransform;
        backdropRect.anchorMin = Vector2.zero;
        backdropRect.anchorMax = Vector2.one;
        backdropRect.offsetMin = Vector2.zero;
        backdropRect.offsetMax = Vector2.zero;

        // The badge: large in the middle during the intro, then a small square at the top.
        GameObject badgeObject = new GameObject("Mutator Badge", typeof(RectTransform), typeof(Image));
        badgeObject.transform.SetParent(root, false);
        badgeRect = badgeObject.GetComponent<RectTransform>();
        badgeImage = badgeObject.GetComponent<Image>();
        badgeImage.raycastTarget = false;

        UiTheme theme = UiTheme.Current;
        bool roundBadge = theme != null && theme.BadgeSprite != null;
        if (roundBadge)
        {
            // Light round Layer Lab button, tinted with the mutator color in UpdateHud.
            badgeImage.sprite = theme.BadgeSprite;
            badgeImage.preserveAspect = true;
        }

        badgeLabel = CreateText(badgeRect, "Badge Label", 64f, FontStyles.Bold, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        badgeLabel.color = Color.white;
        RectTransform labelRect = badgeLabel.rectTransform;
        // The round sprite needs a relative inset so the code stays inside the circle at every badge size.
        labelRect.anchorMin = roundBadge ? new Vector2(0.18f, 0.2f) : Vector2.zero;
        labelRect.anchorMax = roundBadge ? new Vector2(0.82f, 0.84f) : Vector2.one;
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.offsetMin = roundBadge ? Vector2.zero : new Vector2(4f, 4f);
        labelRect.offsetMax = roundBadge ? Vector2.zero : new Vector2(-4f, -4f);
        // Auto size keeps the short code inside the badge while the badge shrinks from large to small.
        badgeLabel.enableAutoSizing = true;
        badgeLabel.fontSizeMin = 8f;
        badgeLabel.fontSizeMax = 64f;

        // Intro texts under the big badge.
        introTitle = CreateText(root, "Intro Title", 96f, FontStyles.Bold, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(1500f, 130f));
        introTitle.color = Color.white;

        introDescription = CreateText(root, "Intro Description", 40f, FontStyles.Normal, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), new Vector2(0f, -185f), new Vector2(1500f, 80f));
        introDescription.color = new Color(0.85f, 0.85f, 0.85f, 1f);

        // Hover tooltip, directly under the small square.
        GameObject tooltipObject = new GameObject("Mutator Tooltip", typeof(RectTransform), typeof(Image));
        tooltipObject.transform.SetParent(root, false);
        tooltipPanel = tooltipObject.GetComponent<RectTransform>();
        tooltipPanel.anchorMin = new Vector2(0.5f, 1f);
        tooltipPanel.anchorMax = new Vector2(0.5f, 1f);
        tooltipPanel.pivot = new Vector2(0.5f, 1f);
        tooltipPanel.anchoredPosition = new Vector2(0f, -112f);
        tooltipPanel.sizeDelta = new Vector2(640f, 110f);

        Image tooltipImage = tooltipObject.GetComponent<Image>();
        tooltipImage.color = new Color(0f, 0f, 0f, 0.85f);
        tooltipImage.raycastTarget = false;
        if (theme != null && theme.TooltipSprite != null)
        {
            tooltipImage.color = new Color(1f, 1f, 1f, 0.92f);
            UiTheme.ApplySlicedSprite(tooltipImage, theme.TooltipSprite, tooltipPanel.sizeDelta.y);
        }

        tooltipText = CreateText(tooltipPanel, "Tooltip Text", 26f, FontStyles.Normal, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        tooltipText.color = Color.white;
        RectTransform tooltipTextRect = tooltipText.rectTransform;
        tooltipTextRect.anchorMin = Vector2.zero;
        tooltipTextRect.anchorMax = Vector2.one;
        tooltipTextRect.pivot = new Vector2(0.5f, 0.5f);
        tooltipTextRect.offsetMin = new Vector2(16f, 10f);
        tooltipTextRect.offsetMax = new Vector2(-16f, -10f);

        // Wind warning and wind widget.
        windWarningLabel = CreateText(root, "Wind Warning", 34f, FontStyles.Bold, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0f, -250f), new Vector2(900f, 60f));
        windWarningLabel.text = "The wind is about to change!";
        windWarningLabel.color = new Color(1f, 0.6f, 0.2f, 1f);
        windWarningLabel.gameObject.SetActive(false);

        CreateWindArrow(root);

        // Keep the tooltip on top of everything else we created.
        tooltipPanel.SetAsLastSibling();
        tooltipPanel.gameObject.SetActive(false);
    }

    /// <summary>
    /// Top-right widget: a fixed "WIND" caption and an arrow that rotates with the wind direction.
    /// </summary>
    private void CreateWindArrow(RectTransform root)
    {
        GameObject widgetObject = new GameObject("Wind Widget", typeof(RectTransform));
        windWidget = widgetObject.GetComponent<RectTransform>();
        windWidget.SetParent(root, false);
        windWidget.anchorMin = new Vector2(1f, 1f);
        windWidget.anchorMax = new Vector2(1f, 1f);
        windWidget.pivot = new Vector2(0.5f, 0.5f);
        windWidget.anchoredPosition = new Vector2(-110f, -110f);
        windWidget.sizeDelta = new Vector2(120f, 150f);

        Color arrowColor = new Color(0.7f, 0.9f, 1f, 0.95f);

        // Caption stays upright, only the arrow below rotates.
        TextMeshProUGUI caption = CreateText(windWidget, "Wind Caption", 22f, FontStyles.Bold,
            TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(120f, 30f));
        caption.text = "WIND";
        caption.color = arrowColor;

        GameObject arrowObject = new GameObject("Wind Arrow", typeof(RectTransform));
        windArrow = arrowObject.GetComponent<RectTransform>();
        windArrow.SetParent(windWidget, false);
        windArrow.anchorMin = new Vector2(0.5f, 1f);
        windArrow.anchorMax = new Vector2(0.5f, 1f);
        windArrow.pivot = new Vector2(0.5f, 0.5f);
        windArrow.anchoredPosition = new Vector2(0f, -55f);
        windArrow.sizeDelta = new Vector2(100f, 100f);

        // Shaft (points up) and a tip made from a square rotated by 45 degrees.
        CreateImage(windArrow, "Shaft", new Vector2(0f, -4f), new Vector2(8f, 64f), arrowColor, 0f);
        CreateImage(windArrow, "Tip", new Vector2(0f, 30f), new Vector2(26f, 26f), arrowColor, 45f);
    }

    private static TextMeshProUGUI CreateText(
        RectTransform parent, string name, float fontSize, FontStyles style, TextAlignmentOptions alignment,
        Vector2 anchor, Vector2 anchoredPosition, Vector2 size)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);

        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        UiTheme.ApplyFont(text);
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.alignment = alignment;
        text.raycastTarget = false;

        RectTransform rect = text.rectTransform;
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = anchor;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        return text;
    }

    private static Image CreateImage(
        RectTransform parent, string name, Vector2 anchoredPosition, Vector2 size, Color color, float rotationZ)
    {
        GameObject imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);

        Image image = imageObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;

        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        rect.localRotation = Quaternion.Euler(0f, 0f, rotationZ);

        return image;
    }
}
