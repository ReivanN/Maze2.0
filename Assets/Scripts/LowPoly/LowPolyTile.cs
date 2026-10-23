using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns a flat floor cell into a chunky low-poly tile: a slightly inset slab with a bevelled top and dark sides,
/// paved with round pebbles (FloorSurfaces) in a light / dark checker, and the odd grass tuft or stone. The original
/// renderer is hidden, the collider is left untouched, so gameplay does not change.
/// The slab takes the size of the stretched tile (wide corridor cells, short passages between them), reaches down to
/// the ground under raised floors so they look solid, and follows the tilt of a ramp (ramps get no props).
/// While the Ice mutator is on (LevelMutatorManager calls SetIce) every tile is covered by a thin see-through ice layer.
/// </summary>
public class LowPolyTile : MonoBehaviour
{
    [SerializeField] private LowPolyPalette palette;
    [SerializeField] private float gap = 0.14f;
    [SerializeField] private float thickness = 0.4f;
    [SerializeField, Range(0f, 1f)] private float grassChance = 0.45f;
    [SerializeField, Range(0f, 1f)] private float rockChance = 0.08f;
    [Tooltip("How far the ice layer lies above the tile top.")]
    [SerializeField] private float iceHeight = 0.015f;

    // Checker step: half of one wall + corridor period of CaveDungeonGenerator (2 m + 4 m), so neighbouring cells alternate.
    private const float CheckerStep = 3f;
    private const float Bevel = 0.05f;

    private static readonly Dictionary<Vector3, Mesh> slabCache = new Dictionary<Vector3, Mesh>();
    private static readonly Dictionary<Vector2, Mesh> iceCache = new Dictionary<Vector2, Mesh>();
    private static readonly List<LowPolyTile> tiles = new List<LowPolyTile>();
    private static bool iceOn;

    private Transform visual;
    private GameObject iceLayer;
    private float topHalfX;
    private float topHalfZ;

    /// <summary>Shows or hides the ice layer on every floor tile, now and on tiles built later.</summary>
    public static void SetIce(bool on)
    {
        iceOn = on;

        foreach (LowPolyTile tile in tiles)
        {
            if (tile != null)
                tile.ApplyIce();
        }
    }

    private void OnDestroy()
    {
        tiles.Remove(this);
    }

    private void Start()
    {
        if (palette == null)
            return;

        var ownRenderer = GetComponent<MeshRenderer>();
        if (ownRenderer != null)
            ownRenderer.enabled = false;

        Vector3 origin = transform.position;
        int cellX = Mathf.FloorToInt(origin.x / CheckerStep);
        int cellZ = Mathf.FloorToInt(origin.z / CheckerStep);
        bool light = ((cellX + cellZ) & 1) == 0;

        // Tile size in metres: the mesh (a 10 m plane on the floor prefab) times the scale the generator gave it.
        MeshFilter filter = GetComponent<MeshFilter>();
        Vector3 meshSize = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds.size : new Vector3(10f, 0f, 10f);
        Vector3 scale = transform.localScale;
        float halfX = meshSize.x * scale.x * 0.5f;
        float halfZ = meshSize.z * scale.z * 0.5f;
        topHalfX = halfX - gap * 0.5f - Bevel;
        topHalfZ = halfZ - gap * 0.5f - Bevel;

        bool tilted = transform.up.y < 0.999f;
        float depth = thickness + Mathf.Max(0f, origin.y) + (tilted ? 1f : 0f);

        visual = new GameObject("Low Poly Tile").transform;
        visual.SetParent(transform, false);

        Material template = light ? palette.tileLight : palette.tileDark;
        Material top = FloorSurfaces.GetPebbleMaterial(palette.tileLight, light) ?? template;
        LowPolyDecor.CreatePart(visual, "Slab", Slab(halfX, halfZ, depth), new[] { top, palette.tileSide }, origin);

        tiles.Add(this);
        ApplyIce();

        // Props are placed flat in world space, they would float over or sink into a slope.
        if (tilted)
            return;

        System.Random random = LowPolyMesh.RandomFor(origin, 1);
        float edge = Mathf.Min(halfX, halfZ) - gap - 0.15f;

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

    private void ApplyIce()
    {
        if (visual == null)
            return;

        if (iceOn && iceLayer == null)
        {
            Material iceMaterial = FloorSurfaces.GetIceMaterial(palette.tileLight);
            if (iceMaterial == null)
                return;

            // The layer is a child of the tile, so it follows the tilt of a ramp.
            iceLayer = LowPolyDecor.CreatePart(visual, "Ice", IceLayer(topHalfX, topHalfZ), new[] { iceMaterial },
                transform.position + transform.up * iceHeight);
        }

        if (iceLayer != null && iceLayer.activeSelf != iceOn)
            iceLayer.SetActive(iceOn);
    }

    private static Vector3 EdgePoint(System.Random random, float edge)
    {
        float along = LowPolyMesh.Range(random, -edge, edge);
        float across = random.NextDouble() < 0.5 ? -edge : edge;
        return random.NextDouble() < 0.5 ? new Vector3(along, 0f, across) : new Vector3(across, 0f, along);
    }

    /// <summary>Slab mesh shared by all tiles of one size. Submeshes: top (pebbles), sides.</summary>
    private Mesh Slab(float tileHalfX, float tileHalfZ, float depth)
    {
        float halfX = tileHalfX - gap * 0.5f;
        float halfZ = tileHalfZ - gap * 0.5f;
        Vector3 key = new Vector3(halfX, halfZ, depth);
        if (slabCache.TryGetValue(key, out Mesh cached) && cached != null)
            return cached;

        var mesh = new LowPolyMesh(2);
        mesh.Loft(new[]
        {
            Rect(halfX, halfZ, -depth),
            Rect(halfX, halfZ, -Bevel),
            Rect(halfX - Bevel, halfZ - Bevel, 0f)
        }, 1, 0);

        Mesh built = mesh.Build("Low Poly Tile");
        SetPlanarUVs(built);
        slabCache[key] = built;
        return built;
    }

    /// <summary>A flat quad over the tile top for the ice, with the same texture mapping as the floor.</summary>
    private static Mesh IceLayer(float halfX, float halfZ)
    {
        Vector2 key = new Vector2(halfX, halfZ);
        if (iceCache.TryGetValue(key, out Mesh cached) && cached != null)
            return cached;

        Mesh mesh = new Mesh { name = "Ice Layer" };
        mesh.SetVertices(Rect(halfX, halfZ, 0f));
        mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
        mesh.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
        SetPlanarUVs(mesh);
        mesh.RecalculateBounds();

        iceCache[key] = mesh;
        return mesh;
    }

    // The textures are mapped from above: one repeat per FloorSurfaces.TextureWorldSize metres. The sides get UVs
    // too but use an untextured material. Tangents are needed for the pebble normal map.
    private static void SetPlanarUVs(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector2[] uvs = new Vector2[vertices.Length];

        for (int i = 0; i < vertices.Length; i++)
            uvs[i] = new Vector2(vertices[i].x, vertices[i].z) / FloorSurfaces.TextureWorldSize;

        mesh.uv = uvs;
        mesh.RecalculateTangents();
    }

    private static Vector3[] Rect(float halfX, float halfZ, float y)
    {
        return new[]
        {
            new Vector3(-halfX, y, -halfZ), new Vector3(halfX, y, -halfZ),
            new Vector3(halfX, y, halfZ), new Vector3(-halfX, y, halfZ)
        };
    }
}
