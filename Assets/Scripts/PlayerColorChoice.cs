using UnityEngine;

/// <summary>
/// Character colors the player can pick in the main menu. The choice is saved in PlayerPrefs and sent to the
/// server by NetworkPlayerColor when the player spawns. Index 0 is "Auto": the game picks the color by player
/// number, as it did before the picker existed.
/// </summary>
public static class PlayerColorChoice
{
    private const string PrefKey = "Maze.PlayerColor";
    public const int AutoIndex = 0;

    private static readonly string[] Names =
    {
        "Auto", "White", "Red", "Orange", "Yellow", "Green", "Teal", "Sky", "Blue", "Purple", "Pink"
    };

    private static readonly Color[] Colors =
    {
        new Color(0.75f, 0.75f, 0.78f, 1f), // only the swatch color, Auto sends nothing
        Color.white,
        new Color(0.9f, 0.18f, 0.18f, 1f),
        new Color(1f, 0.55f, 0.12f, 1f),
        new Color(1f, 0.85f, 0.2f, 1f),
        new Color(0.3f, 0.8f, 0.3f, 1f),
        new Color(0.15f, 0.75f, 0.7f, 1f),
        new Color(0.35f, 0.7f, 1f, 1f),
        new Color(0.2f, 0.35f, 0.9f, 1f),
        new Color(0.6f, 0.3f, 0.85f, 1f),
        new Color(1f, 0.45f, 0.7f, 1f)
    };

    public static int Count => Colors.Length;

    public static int SelectedIndex
    {
        get
        {
            int index = PlayerPrefs.GetInt(PrefKey, AutoIndex);
            return index >= 0 && index < Count ? index : AutoIndex;
        }
        set
        {
            PlayerPrefs.SetInt(PrefKey, Mathf.Clamp(value, 0, Count - 1));
            PlayerPrefs.Save();
        }
    }

    public static string GetName(int index) => Names[Mathf.Clamp(index, 0, Count - 1)];

    public static Color GetSwatchColor(int index) => Colors[Mathf.Clamp(index, 0, Count - 1)];

    /// <summary>False for Auto and for indexes outside the palette (for example sent by a modified client).</summary>
    public static bool TryGetColor(int index, out Color color)
    {
        if (index <= AutoIndex || index >= Count)
        {
            color = Color.white;
            return false;
        }

        color = Colors[index];
        return true;
    }
}
