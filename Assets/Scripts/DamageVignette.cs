using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Red gradient flashing in from both sides of the screen when the local player gets hurt (spikes call Flash).
/// Built in code: one gradient texture, two images on an overlay canvas, so no art asset is needed.
/// </summary>
public class DamageVignette : MonoBehaviour
{
    private const float FadeInTime = 0.08f;
    private const float FadeOutTime = 0.9f;
    private const float MaxAlpha = 0.65f;
    private const float EdgeWidth = 0.32f; // part of the screen width each side covers

    private static readonly Color Red = new Color(0.85f, 0.02f, 0.04f, 1f);

    private static DamageVignette instance;

    private Canvas canvas;
    private Image left;
    private Image right;
    private float flashTime = -10f;

    /// <summary>Shows the red edges on this player's screen.</summary>
    public static void Flash()
    {
        if (instance == null)
            instance = new GameObject("Damage Vignette").AddComponent<DamageVignette>();

        instance.flashTime = Time.unscaledTime;
        instance.canvas.enabled = true;
    }

    private void Awake()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30; // under the hearts (40), the hints (45) and the menus (100)
        canvas.enabled = false;

        Sprite gradient = CreateGradientSprite();
        left = CreateEdge("Left Edge", gradient, 0f);
        right = CreateEdge("Right Edge", gradient, 1f);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    private void Update()
    {
        float t = Time.unscaledTime - flashTime;
        float alpha;

        if (t < FadeInTime)
        {
            alpha = t / FadeInTime;
        }
        else
        {
            float k = (t - FadeInTime) / FadeOutTime;
            alpha = 1f - k * k * (3f - 2f * Mathf.Clamp01(k)); // smooth fade out
        }

        alpha = Mathf.Clamp01(alpha) * MaxAlpha;
        Color color = new Color(Red.r, Red.g, Red.b, alpha);
        left.color = color;
        right.color = color;

        if (t > FadeInTime + FadeOutTime)
            canvas.enabled = false;
    }

    // One edge stretched over the full height; the right one is the left one mirrored.
    private Image CreateEdge(string edgeName, Sprite sprite, float side)
    {
        GameObject edge = new GameObject(edgeName, typeof(RectTransform), typeof(Image));
        edge.transform.SetParent(transform, false);

        RectTransform rect = (RectTransform)edge.transform;
        rect.anchorMin = new Vector2(side == 0f ? 0f : 1f - EdgeWidth, 0f);
        rect.anchorMax = new Vector2(side == 0f ? EdgeWidth : 1f, 1f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = new Vector3(side == 0f ? 1f : -1f, 1f, 1f);

        Image image = edge.GetComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false;
        image.color = Color.clear;
        return image;
    }

    // White, fully opaque at the screen edge and fading to nothing towards the middle; Image.color makes it red.
    private static Sprite CreateGradientSprite()
    {
        const int width = 128;
        Texture2D texture = new Texture2D(width, 1, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;

        for (int x = 0; x < width; x++)
        {
            float k = 1f - x / (width - 1f);
            texture.SetPixel(x, 0, new Color(1f, 1f, 1f, k * k));
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, width, 1), new Vector2(0.5f, 0.5f), 100f);
    }
}
