using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Hearts of both players in the top-left corner, under the level number. Reads the synced hearts from
/// DungeonProgressManager; created on every client by DungeonProgressManager.
/// The heart sprite is drawn in code, so the HUD does not depend on any art asset.
/// </summary>
public class HeartsHud : MonoBehaviour
{
    private const float LocalHeartSize = 44f;
    private const float PartnerHeartSize = 30f;
    private const float LostPulseDuration = 0.45f;

    private static readonly Color FullColor = new Color(0.92f, 0.16f, 0.22f, 1f);
    private static readonly Color EmptyColor = new Color(0.15f, 0.15f, 0.18f, 0.55f);

    private static HeartsHud instance;
    private static Sprite heartSprite;

    private Canvas canvas;
    private Image[] localHearts;
    private Image[] partnerHearts;
    private GameObject partnerRow;
    private RelayManager relayManager;

    private int shownLocalHearts = -1;
    private int pulseIndex = -1;
    private float pulseStartTime;

    public static void EnsureCreated()
    {
        if (instance == null)
            instance = new GameObject("Hearts HUD").AddComponent<HeartsHud>();
    }

    private void Awake()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40; // under the mutator HUD (50) and the menus (100)

        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        RectTransform root = (RectTransform)transform;
        localHearts = CreateRow(root, "You", new Vector2(36f, -88f), LocalHeartSize, out _);
        partnerHearts = CreateRow(root, "Friend", new Vector2(36f, -146f), PartnerHeartSize, out partnerRow);

        canvas.enabled = false;
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    private void Update()
    {
        DungeonProgressManager manager = DungeonProgressManager.Instance;
        NetworkManager networkManager = NetworkManager.Singleton;

        if (relayManager == null)
            relayManager = FindAnyObjectByType<RelayManager>();

        bool visible = manager != null && networkManager != null && networkManager.IsConnectedClient &&
                       relayManager != null && relayManager.IsGameplayStarted;
        canvas.enabled = visible;

        if (!visible)
        {
            shownLocalHearts = -1;
            return;
        }

        ulong localId = networkManager.LocalClientId;
        int localCount = manager.GetHearts(localId);

        // A heart was just lost: that heart pulses so the player notices it.
        if (shownLocalHearts > localCount)
        {
            pulseIndex = localCount;
            pulseStartTime = Time.unscaledTime;
        }

        shownLocalHearts = localCount;
        ApplyHearts(localHearts, localCount, manager.MaxHearts);

        bool hasPartner = manager.TryGetPartnerId(localId, out ulong partnerId);
        partnerRow.SetActive(hasPartner);
        if (hasPartner)
            ApplyHearts(partnerHearts, manager.GetHearts(partnerId), manager.MaxHearts);

        UpdatePulse();
    }

    private static void ApplyHearts(Image[] hearts, int count, int max)
    {
        for (int i = 0; i < hearts.Length; i++)
        {
            hearts[i].gameObject.SetActive(i < max);
            hearts[i].color = i < count ? FullColor : EmptyColor;
        }
    }

    private void UpdatePulse()
    {
        for (int i = 0; i < localHearts.Length; i++)
            localHearts[i].rectTransform.localScale = Vector3.one;

        if (pulseIndex < 0 || pulseIndex >= localHearts.Length)
            return;

        float t = (Time.unscaledTime - pulseStartTime) / LostPulseDuration;
        if (t >= 1f)
        {
            pulseIndex = -1;
            return;
        }

        float scale = 1f + 0.6f * Mathf.Sin(t * Mathf.PI) * (1f - t);
        localHearts[pulseIndex].rectTransform.localScale = Vector3.one * scale;
    }

    private static Image[] CreateRow(RectTransform root, string caption, Vector2 position, float heartSize, out GameObject rowObject)
    {
        rowObject = new GameObject(caption + " Hearts", typeof(RectTransform));
        RectTransform row = (RectTransform)rowObject.transform;
        row.SetParent(root, false);
        row.anchorMin = new Vector2(0f, 1f);
        row.anchorMax = new Vector2(0f, 1f);
        row.pivot = new Vector2(0f, 1f);
        row.anchoredPosition = position;
        row.sizeDelta = new Vector2(400f, heartSize);

        GameObject labelObject = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(row, false);
        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        UiTheme.ApplyFont(label);
        label.text = caption;
        label.fontSize = heartSize * 0.55f;
        label.alignment = TextAlignmentOptions.Left;
        label.color = Color.white;
        label.raycastTarget = false;

        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0.5f);
        labelRect.anchorMax = new Vector2(0f, 0.5f);
        labelRect.pivot = new Vector2(0f, 0.5f);
        labelRect.anchoredPosition = Vector2.zero;
        labelRect.sizeDelta = new Vector2(110f, heartSize);

        // Room for more hearts than the default 3, in case maxHearts is raised in the inspector.
        const int slots = 6;
        Image[] hearts = new Image[slots];

        for (int i = 0; i < slots; i++)
        {
            GameObject heartObject = new GameObject("Heart " + (i + 1), typeof(RectTransform), typeof(Image));
            heartObject.transform.SetParent(row, false);

            Image image = heartObject.GetComponent<Image>();
            image.sprite = GetHeartSprite();
            image.raycastTarget = false;

            RectTransform rect = image.rectTransform;
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(110f + heartSize * 0.5f + i * (heartSize + 8f), 0f);
            rect.sizeDelta = new Vector2(heartSize, heartSize);

            hearts[i] = image;
        }

        return hearts;
    }

    /// <summary>White heart with a dark outline; Image.color tints the white part (full / empty heart).</summary>
    private static Sprite GetHeartSprite()
    {
        if (heartSprite != null)
            return heartSprite;

        const int size = 64;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;

        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Heart curve (x^2 + y^2 - 1)^3 - x^2 y^3 <= 0, mapped to the texture with a small margin.
                float px = (x + 0.5f) / size * 2.6f - 1.3f;
                float py = (y + 0.5f) / size * 2.65f - 1.2f;
                float inner = HeartDistance(px, py, 1f);
                float outer = HeartDistance(px, py, 1.12f);

                Color32 color = new Color32(0, 0, 0, 0);
                if (inner <= 0f)
                    color = new Color32(255, 255, 255, 255);
                else if (outer <= 0f)
                    color = new Color32(40, 10, 14, 255);

                pixels[y * size + x] = color;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();

        heartSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        return heartSprite;
    }

    private static float HeartDistance(float x, float y, float scale)
    {
        x /= scale;
        y /= scale;
        float a = x * x + y * y - 1f;
        return a * a * a - x * x * y * y * y;
    }
}
