using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum UiButtonStyle
{
    Primary,
    Secondary,
    Neutral,
    Danger,
    Settings
}

/// <summary>
/// Look of the code-built UI: Layer Lab button prefabs, font and sprites.
/// Loaded from Resources/UiTheme. When the asset is missing, the UI falls back to plain colored rectangles.
/// </summary>
[CreateAssetMenu(fileName = "UiTheme", menuName = "Maze/UI Theme")]
public class UiTheme : ScriptableObject
{
    private const string ResourcePath = "UiTheme";

    [Header("Layer Lab button prefabs")]
    [SerializeField] private GameObject primaryButton;
    [SerializeField] private GameObject secondaryButton;
    [SerializeField] private GameObject neutralButton;
    [SerializeField] private GameObject dangerButton;
    [SerializeField] private GameObject settingsButton;

    [Header("Text")]
    [SerializeField] private TMP_FontAsset font;

    [Header("Sprites")]
    [SerializeField] private Sprite inputSprite;
    [SerializeField] private Sprite tooltipSprite;
    [SerializeField] private Sprite badgeSprite;

    private static UiTheme current;
    private static bool loaded;

    public static UiTheme Current
    {
        get
        {
            if (!loaded)
            {
                current = Resources.Load<UiTheme>(ResourcePath);
                loaded = true;
            }

            return current;
        }
    }

    public Sprite InputSprite => inputSprite;
    public Sprite TooltipSprite => tooltipSprite;
    public Sprite BadgeSprite => badgeSprite;

    public static void ApplyFont(TMP_Text text)
    {
        UiTheme theme = Current;
        if (theme != null && theme.font != null)
            text.font = theme.font;
    }

    /// <summary>
    /// Uses a Layer Lab sprite as a 9-sliced background. The pack sprites are sliced only horizontally,
    /// so the border is scaled to the target height to keep the rounded corners round.
    /// </summary>
    public static void ApplySlicedSprite(Image image, Sprite sprite, float height)
    {
        if (sprite == null)
            return;

        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = sprite.rect.height / Mathf.Max(1f, height);
    }

    /// <summary>
    /// Instantiates the Layer Lab button prefab for the style and makes it clickable.
    /// Returns null when the theme has no prefab for that style.
    /// </summary>
    public UnityEngine.UI.Button CreateButton(Transform parent, UiButtonStyle style, string label, Vector2 size, float fontSize)
    {
        GameObject prefab = GetButtonPrefab(style);
        if (prefab == null)
            return null;

        GameObject buttonObject = Instantiate(prefab, parent, false);
        buttonObject.name = string.IsNullOrEmpty(label) ? prefab.name : label;

        RectTransform rectTransform = (RectTransform)buttonObject.transform;
        rectTransform.sizeDelta = size;

        // The prefab is a plain root with a "Bg" image and a "Text (TMP)" label; the image is the click target.
        Image background = buttonObject.GetComponentInChildren<Image>();
        if (background != null)
            ApplySlicedSprite(background, background.sprite, size.y);

        TextMeshProUGUI text = buttonObject.GetComponentInChildren<TextMeshProUGUI>();
        if (text != null)
        {
            text.text = label;
            text.fontSize = fontSize;
            text.raycastTarget = false;
        }

        UnityEngine.UI.Button button = buttonObject.AddComponent<UnityEngine.UI.Button>();
        button.targetGraphic = background;
        buttonObject.AddComponent<UiButtonSounds>();

        return button;
    }

    private GameObject GetButtonPrefab(UiButtonStyle style)
    {
        switch (style)
        {
            case UiButtonStyle.Secondary: return secondaryButton;
            case UiButtonStyle.Neutral: return neutralButton;
            case UiButtonStyle.Danger: return dangerButton;
            case UiButtonStyle.Settings: return settingsButton;
            default: return primaryButton;
        }
    }
}
