using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Textures and materials of the maze floor, drawn in code so no art asset is needed:
///   - pebble paving: round light-grey stones set in darker mortar, with a normal map so the stones catch the light;
///   - ice: a thin see-through bluish layer with frost and cracks, laid over the tiles while the Ice mutator is on.
/// Materials are copies of the palette tile material (URP Lit), so the shader is the one already used in the game;
/// the transparent variant for ice is the one the laser material uses.
/// </summary>
public static class FloorSurfaces
{
    /// <summary>Metres of floor covered by one repeat of the textures.</summary>
    public const float TextureWorldSize = 2f;

    private const int PebbleTextureSize = 512;
    private const int PebbleCells = 9;          // stones across one texture: about 22 cm each
    private const int IceTextureSize = 256;

    private static readonly Color[] StoneColors =
    {
        new Color(0.74f, 0.76f, 0.71f),
        new Color(0.66f, 0.70f, 0.64f),
        new Color(0.80f, 0.79f, 0.74f),
        new Color(0.60f, 0.64f, 0.60f),
        new Color(0.70f, 0.71f, 0.69f)
    };

    private static readonly Color MortarColor = new Color(0.40f, 0.42f, 0.41f);

    private static Texture2D pebbleAlbedo;
    private static Texture2D pebbleNormal;
    private static Texture2D iceTexture;
    private static Material pebbleLight;
    private static Material pebbleDark;
    private static Material ice;

    /// <summary>Pebble floor material based on the palette tile material; the dark one is a touch darker for the checker.</summary>
    public static Material GetPebbleMaterial(Material template, bool light)
    {
        if (template == null)
            return null;

        ref Material cached = ref light ? ref pebbleLight : ref pebbleDark;
        if (cached != null)
            return cached;

        EnsurePebbleTextures();

        cached = new Material(template) { name = light ? "Pebble Floor Light" : "Pebble Floor Dark" };
        float tint = light ? 1f : 0.9f;
        SetColor(cached, new Color(tint, tint, tint, 1f));
        SetTexture(cached, "_BaseMap", pebbleAlbedo);
        cached.mainTexture = pebbleAlbedo;
        SetFloat(cached, "_Smoothness", 0.2f);
        SetFloat(cached, "_Metallic", 0f);

        if (cached.HasProperty("_BumpMap"))
        {
            cached.SetTexture("_BumpMap", pebbleNormal);
            SetFloat(cached, "_BumpScale", 1f);
            cached.EnableKeyword("_NORMALMAP");
        }

        return cached;
    }

    /// <summary>See-through glossy ice layer.</summary>
    public static Material GetIceMaterial(Material template)
    {
        if (ice != null || template == null)
            return ice;

        if (iceTexture == null)
            iceTexture = CreateIceTexture();

        ice = new Material(template) { name = "Ice Floor" };
        SetColor(ice, Color.white);
        SetTexture(ice, "_BaseMap", iceTexture);
        ice.mainTexture = iceTexture;
        SetFloat(ice, "_Smoothness", 0.95f);
        SetFloat(ice, "_Metallic", 0f);
        ice.DisableKeyword("_NORMALMAP");

        // URP Lit: transparent surface, alpha blended, no depth write, no shadow.
        SetFloat(ice, "_Surface", 1f);
        SetFloat(ice, "_Blend", 0f);
        SetFloat(ice, "_SrcBlend", (float)BlendMode.SrcAlpha);
        SetFloat(ice, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        SetFloat(ice, "_SrcBlendAlpha", (float)BlendMode.One);
        SetFloat(ice, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        SetFloat(ice, "_ZWrite", 0f);
        ice.SetOverrideTag("RenderType", "Transparent");
        ice.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        ice.DisableKeyword("_ALPHATEST_ON");
        ice.renderQueue = (int)RenderQueue.Transparent;
        ice.SetShaderPassEnabled("ShadowCaster", false);
        ice.SetShaderPassEnabled("DepthOnly", false);

        return ice;
    }

    // ================= PEBBLES =================

    private static void EnsurePebbleTextures()
    {
        if (pebbleAlbedo != null && pebbleNormal != null)
            return;

        int size = PebbleTextureSize;
        System.Random random = new System.Random(1234);

        // One stone per grid cell, jittered; the pattern wraps around, so the texture tiles without seams.
        Vector2[,] points = new Vector2[PebbleCells, PebbleCells];
        float[,] radii = new float[PebbleCells, PebbleCells];
        Color[,] colors = new Color[PebbleCells, PebbleCells];

        for (int x = 0; x < PebbleCells; x++)
        {
            for (int y = 0; y < PebbleCells; y++)
            {
                points[x, y] = new Vector2(0.25f + (float)random.NextDouble() * 0.5f, 0.25f + (float)random.NextDouble() * 0.5f);
                radii[x, y] = 0.42f + (float)random.NextDouble() * 0.16f;

                Color baseColor = StoneColors[random.Next(StoneColors.Length)];
                float shade = 0.92f + (float)random.NextDouble() * 0.14f;
                colors[x, y] = baseColor * shade;
            }
        }

        float[] heights = new float[size * size];
        Color[] albedo = new Color[size * size];

        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                float u = (px + 0.5f) / size * PebbleCells;
                float v = (py + 0.5f) / size * PebbleCells;
                int cx = Mathf.FloorToInt(u);
                int cy = Mathf.FloorToInt(v);

                float f1 = float.MaxValue;
                float f2 = float.MaxValue;
                int id1x = 0, id1y = 0;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int gx = cx + dx;
                        int gy = cy + dy;
                        int wx = (gx % PebbleCells + PebbleCells) % PebbleCells;
                        int wy = (gy % PebbleCells + PebbleCells) % PebbleCells;
                        Vector2 point = new Vector2(gx, gy) + points[wx, wy];
                        float distance = Vector2.Distance(new Vector2(u, v), point);

                        if (distance < f1)
                        {
                            f2 = f1;
                            f1 = distance;
                            id1x = wx;
                            id1y = wy;
                        }
                        else if (distance < f2)
                        {
                            f2 = distance;
                        }
                    }
                }

                // Inside a stone: away from the border with the next stone and within the stone's own radius,
                // so stones come out rounded with mortar between them.
                float border = Mathf.InverseLerp(0.06f, 0.16f, f2 - f1);
                float round = Mathf.InverseLerp(0f, 0.08f, radii[id1x, id1y] - f1);
                float stone = Mathf.Min(border, round);

                // Dome: high in the middle, falling to the mortar at the edge.
                float dome = Mathf.Sqrt(Mathf.Clamp01(Mathf.Min((f2 - f1) * 2.2f, (radii[id1x, id1y] - f1) * 4f)));
                float grain = Hash(px, py) * 0.08f - 0.04f;
                int index = py * size + px;
                heights[index] = stone * (0.35f + 0.65f * dome);

                Color stoneColor = colors[id1x, id1y] * (0.8f + 0.25f * dome) + new Color(grain, grain, grain, 0f);
                Color mortar = MortarColor * (0.9f + Hash(px * 3, py * 7) * 0.2f);
                Color color = Color.Lerp(mortar, stoneColor, stone);
                color.a = 1f;
                albedo[index] = color;
            }
        }

        pebbleAlbedo = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Pebble Albedo", wrapMode = TextureWrapMode.Repeat };
        pebbleAlbedo.SetPixels(albedo);
        pebbleAlbedo.Apply(true, true);

        pebbleNormal = CreateNormalMap(heights, size, 6f);
    }

    // Normal map from a height field. Written as R = x, G = y, B = 1, A = 1, which URP's UnpackNormal reads both
    // as an RGB and as a DXT5nm (AG) normal map.
    private static Texture2D CreateNormalMap(float[] heights, int size, float strength)
    {
        Color[] pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float left = heights[y * size + (x - 1 + size) % size];
                float right = heights[y * size + (x + 1) % size];
                float down = heights[((y - 1 + size) % size) * size + x];
                float up = heights[((y + 1) % size) * size + x];

                Vector3 normal = new Vector3((left - right) * strength, (down - up) * strength, 1f).normalized;
                pixels[y * size + x] = new Color(normal.x * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, 1f, 1f);
            }
        }

        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
        {
            name = "Pebble Normal",
            wrapMode = TextureWrapMode.Repeat
        };
        texture.SetPixels(pixels);
        texture.Apply(true, true);
        return texture;
    }

    // ================= ICE =================

    private static Texture2D CreateIceTexture()
    {
        int size = IceTextureSize;
        System.Random random = new System.Random(4321);
        Color[] pixels = new Color[size * size];
        Color baseColor = new Color(0.78f, 0.92f, 1f, 0.38f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Soft frost clouds plus fine sparkle.
                float frost = SmoothNoise(x / 48f, y / 48f) * 0.6f + SmoothNoise(x / 12f, y / 12f) * 0.4f;
                float sparkle = Hash(x, y) > 0.985f ? 0.25f : 0f;
                float white = frost * 0.22f + sparkle;
                pixels[y * size + x] = new Color(
                    Mathf.Min(1f, baseColor.r + white),
                    Mathf.Min(1f, baseColor.g + white * 0.6f),
                    1f,
                    Mathf.Min(1f, baseColor.a + frost * 0.18f + sparkle));
            }
        }

        // Cracks: a few jagged white lines with short branches.
        for (int i = 0; i < 7; i++)
        {
            Vector2 position = new Vector2((float)random.NextDouble() * size, (float)random.NextDouble() * size);
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            DrawCrack(pixels, size, random, position, angle, 50 + random.Next(60), 2);
        }

        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Ice", wrapMode = TextureWrapMode.Repeat };
        texture.SetPixels(pixels);
        texture.Apply(true, true);
        return texture;
    }

    private static void DrawCrack(Color[] pixels, int size, System.Random random, Vector2 position, float angle, int length, int branches)
    {
        for (int step = 0; step < length; step++)
        {
            angle += ((float)random.NextDouble() - 0.5f) * 0.6f;
            position += new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            int x = ((int)position.x % size + size) % size;
            int y = ((int)position.y % size + size) % size;
            int index = y * size + x;
            pixels[index] = new Color(0.95f, 0.98f, 1f, 0.85f);

            if (branches > 0 && random.NextDouble() < 0.025)
            {
                float side = random.NextDouble() < 0.5 ? 1f : -1f;
                DrawCrack(pixels, size, random, position, angle + side * (0.6f + (float)random.NextDouble()), length / 3, branches - 1);
            }
        }
    }

    // ================= UTILS =================

    private static float Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float SmoothNoise(float x, float y)
    {
        int ix = Mathf.FloorToInt(x);
        int iy = Mathf.FloorToInt(y);
        float fx = x - ix;
        float fy = y - iy;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float a = Hash(ix, iy);
        float b = Hash(ix + 1, iy);
        float c = Hash(ix, iy + 1);
        float d = Hash(ix + 1, iy + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static void SetColor(Material material, Color color)
    {
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
    }

    private static void SetTexture(Material material, string property, Texture texture)
    {
        if (material.HasProperty(property))
            material.SetTexture(property, texture);
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property))
            material.SetFloat(property, value);
    }
}
