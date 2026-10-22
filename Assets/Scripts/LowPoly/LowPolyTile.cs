using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns a flat floor cell into a chunky low-poly tile: a slightly inset slab with a bevelled top and dark sides,
/// light and dark tiles in a checker pattern, and the odd grass tuft or stone. The original renderer is hidden,
/// the collider is left untouched, so gameplay does not change.
/// </summary>
public class LowPolyTile : MonoBehaviour
{
    [SerializeField] private LowPolyPalette palette;
    [SerializeField] private float cellSize = 2f;
    [SerializeField] private float gap = 0.14f;
    [SerializeField] private float thickness = 0.4f;
    [SerializeField, Range(0f, 1f)] private float grassChance = 0.45f;
    [SerializeField, Range(0f, 1f)] private float rockChance = 0.08f;

    private static readonly Dictionary<float, Mesh> slabCache = new Dictionary<float, Mesh>();

    private void Start()
    {
        if (palette == null)
            return;

        var ownRenderer = GetComponent<MeshRenderer>();
        if (ownRenderer != null)
            ownRenderer.enabled = false;

        Vector3 origin = transform.position;
        int cellX = Mathf.RoundToInt(origin.x / cellSize);
        int cellZ = Mathf.RoundToInt(origin.z / cellSize);
        bool light = ((cellX + cellZ) & 1) == 0;

        var visual = new GameObject("Low Poly Tile").transform;
        visual.SetParent(transform, false);

        LowPolyDecor.CreatePart(visual, "Slab", Slab(), new[] { light ? palette.tileLight : palette.tileDark, palette.tileSide }, origin);

        System.Random random = LowPolyMesh.RandomFor(origin, 1);
        float edge = cellSize * 0.5f - gap - 0.15f;

        // Grass grows along the tile edges, out of the way of the walking line.
        if (random.NextDouble() < (light ? grassChance : grassChance * 0.5f))
        {
            int tufts = random.Next(1, 4);
            for (int i = 0; i < tufts; i++)
            {
                Vector3 offset = EdgePoint(random, edge);
                LowPolyDecor.CreatePart(visual, "Grass", LowPolyDecor.Grass(random, LowPolyMesh.Range(random, 0.25f, 0.5f)),
                    new[] { palette.grass }, origin + offset);
            }
        }

        if (random.NextDouble() < rockChance)
        {
            float radius = LowPolyMesh.Range(random, 0.12f, 0.22f);
            LowPolyDecor.CreatePart(visual, "Rock", LowPolyDecor.Rock(random, radius, radius * 1.1f),
                new[] { palette.rock }, origin + EdgePoint(random, edge - 0.1f));
        }
    }

    private static Vector3 EdgePoint(System.Random random, float edge)
    {
        float along = LowPolyMesh.Range(random, -edge, edge);
        float across = random.NextDouble() < 0.5 ? -edge : edge;
        return random.NextDouble() < 0.5 ? new Vector3(along, 0f, across) : new Vector3(across, 0f, along);
    }

    /// <summary>Slab mesh shared by all tiles. Submeshes: top (tile colour), sides.</summary>
    private Mesh Slab()
    {
        float half = cellSize * 0.5f - gap * 0.5f;
        if (slabCache.TryGetValue(half, out Mesh cached) && cached != null)
            return cached;

        const float bevel = 0.05f;
        var mesh = new LowPolyMesh(2);
        mesh.Loft(new[]
        {
            LowPolyMesh.Square(half, -thickness),
            LowPolyMesh.Square(half, -bevel),
            LowPolyMesh.Square(half - bevel, 0f)
        }, 1, 0);

        Mesh built = mesh.Build("Low Poly Tile");
        slabCache[half] = built;
        return built;
    }
}
