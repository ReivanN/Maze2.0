using UnityEngine;

/// <summary>
/// All colours of the low-poly forest look in one place. Floor tiles and hedge walls read their materials from here,
/// so restyling the maze is a matter of editing these materials.
/// </summary>
[CreateAssetMenu(fileName = "LowPolyPalette", menuName = "Maze/Low Poly Palette")]
public class LowPolyPalette : ScriptableObject
{
    [Header("Floor")]
    public Material tileLight;
    public Material tileDark;
    public Material tileSide;

    [Header("Hedge walls")]
    public Material hedgeSide;
    public Material hedgeTop;

    [Header("Decoration")]
    public Material treeDark;
    public Material treeLight;
    public Material trunk;
    public Material grass;
    public Material rock;
}
