using UnityEngine;

/// <summary>
/// Level rules ("mutators") that change how the level plays for BOTH players.
/// The numeric values are sent over the network (NetworkVariable&lt;int&gt;), so do not reorder them.
/// </summary>
public enum LevelMutatorType
{
    None = 0,
    LowGravity = 1,
    HeavyWorld = 2,
    Ice = 3,
    MirrorControls = 4,
    Wind = 5,
    FogAndDarkness = 6
}

/// <summary>
/// Display names, descriptions and badge looks. Text is English on purpose: the default TMP font
/// asset has no Cyrillic glyphs, and the rest of the game UI is English as well.
/// </summary>
public static class LevelMutatorInfo
{
    /// <summary>Mutators that can be picked randomly (everything except None).</summary>
    public static readonly LevelMutatorType[] Selectable =
    {
        LevelMutatorType.LowGravity,
        LevelMutatorType.HeavyWorld,
        LevelMutatorType.Ice,
        LevelMutatorType.MirrorControls,
        LevelMutatorType.Wind,
        LevelMutatorType.FogAndDarkness
    };

    public static string GetName(LevelMutatorType type)
    {
        switch (type)
        {
            case LevelMutatorType.LowGravity: return "Low Gravity";
            case LevelMutatorType.HeavyWorld: return "Heavy World";
            case LevelMutatorType.Ice: return "Ice";
            case LevelMutatorType.MirrorControls: return "Mirror Controls";
            case LevelMutatorType.Wind: return "Wind";
            case LevelMutatorType.FogAndDarkness: return "Fog & Darkness";
            default: return string.Empty;
        }
    }

    public static string GetDescription(LevelMutatorType type)
    {
        switch (type)
        {
            case LevelMutatorType.LowGravity: return "You float: jumps are longer and falls are slow";
            case LevelMutatorType.HeavyWorld: return "Strong gravity: short jumps and fast falls";
            case LevelMutatorType.Ice: return "Slippery floor: you speed up and stop slowly";
            case LevelMutatorType.MirrorControls: return "Movement is inverted for both players";
            case LevelMutatorType.Wind: return "A steady wind pushes you and changes direction";
            case LevelMutatorType.FogAndDarkness: return "Almost no light: stay close to each other's lantern";
            default: return string.Empty;
        }
    }

    /// <summary>Short text shown inside the small square at the top of the screen.</summary>
    public static string GetShortCode(LevelMutatorType type)
    {
        switch (type)
        {
            case LevelMutatorType.LowGravity: return "LOW\nG";
            case LevelMutatorType.HeavyWorld: return "HEAVY";
            case LevelMutatorType.Ice: return "ICE";
            case LevelMutatorType.MirrorControls: return "MIRROR";
            case LevelMutatorType.Wind: return "WIND";
            case LevelMutatorType.FogAndDarkness: return "DARK";
            default: return string.Empty;
        }
    }

    public static Color GetColor(LevelMutatorType type)
    {
        switch (type)
        {
            case LevelMutatorType.LowGravity: return new Color(0.35f, 0.55f, 0.95f, 1f);
            case LevelMutatorType.HeavyWorld: return new Color(0.80f, 0.45f, 0.20f, 1f);
            case LevelMutatorType.Ice: return new Color(0.25f, 0.75f, 0.85f, 1f);
            case LevelMutatorType.MirrorControls: return new Color(0.65f, 0.40f, 0.85f, 1f);
            case LevelMutatorType.Wind: return new Color(0.45f, 0.65f, 0.70f, 1f);
            case LevelMutatorType.FogAndDarkness: return new Color(0.25f, 0.27f, 0.35f, 1f);
            default: return Color.gray;
        }
    }
}
