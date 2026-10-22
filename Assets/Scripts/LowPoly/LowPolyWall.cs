using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Dresses a wall block as a trimmed low-poly hedge: dark green sides, a bevelled lighter top with a few faceted
/// bush lumps, and now and then a conifer growing out of it. Only the look changes; the collider stays as it is.
/// </summary>
public class LowPolyWall : MonoBehaviour
{
    [SerializeField] private LowPolyPalette palette;
    [Tooltip("How far the hedge reaches below the floor, so tile gaps never show a seam at the wall foot.")]
    [SerializeField] private float sinkBelowFloor = 0.5f;
    [SerializeField, Range(0f, 1f)] private float treeChance = 0.12f;
    [SerializeField, Range(0f, 1f)] private float bushChance = 0.6f;

    private static readonly Dictionary<Vector3, Mesh> blockCache = new Dictionary<Vector3, Mesh>();

    private void Start()
    {
        if (palette == null)
            return;

        var ownRenderer = GetComponent<MeshRenderer>();
        if (ownRenderer != null)
            ownRenderer.enabled = false;

        // The wall is a scaled unit cube; its size in metres is the scale.
        Vector3 size = transform.lossyScale;
        Vector3 centre = transform.position;
        Vector3 top = centre + Vector3.up * (size.y * 0.5f);

        var visual = new GameObject("Low Poly Hedge").transform;
        visual.SetParent(transform, false);

        LowPolyDecor.CreatePart(visual, "Block", Block(size), new[] { palette.hedgeSide, palette.hedgeTop }, top);

        System.Random random = LowPolyMesh.RandomFor(centre, 2);
        float reach = Mathf.Min(size.x, size.z) * 0.5f - 0.35f;

        if (random.NextDouble() < bushChance)
        {
            int lumps = random.Next(1, 3);
            for (int i = 0; i < lumps; i++)
            {
                float radius = LowPolyMesh.Range(random, 0.4f, 0.65f);
                Vector3 offset = new Vector3(LowPolyMesh.Range(random, -reach, reach), -0.1f, LowPolyMesh.Range(random, -reach, reach));
                LowPolyDecor.CreatePart(visual, "Bush", LowPolyDecor.Rock(random, radius, radius * 0.9f),
                    new[] { random.NextDouble() < 0.5 ? palette.hedgeTop : palette.treeLight }, top + offset);
            }
        }

        if (random.NextDouble() < treeChance)
        {
            Vector3 offset = new Vector3(LowPolyMesh.Range(random, -0.3f, 0.3f), -0.1f, LowPolyMesh.Range(random, -0.3f, 0.3f));
            LowPolyDecor.CreatePart(visual, "Tree", LowPolyDecor.Tree(random, LowPolyMesh.Range(random, 2.5f, 4f)),
                new[] { palette.trunk, palette.treeDark, palette.treeLight }, top + offset);
        }
    }

    /// <summary>Hedge block with its origin at the top centre. Submeshes: sides, top.</summary>
    private Mesh Block(Vector3 size)
    {
        if (blockCache.TryGetValue(size, out Mesh cached) && cached != null)
            return cached;

        const float bevel = 0.18f;
        float halfX = size.x * 0.5f, halfZ = size.z * 0.5f;
        float bottom = -size.y - sinkBelowFloor;

        Vector3[] Rect(float inset, float y) => new[]
        {
            new Vector3(-halfX + inset, y, -halfZ + inset), new Vector3(halfX - inset, y, -halfZ + inset),
            new Vector3(halfX - inset, y, halfZ - inset), new Vector3(-halfX + inset, y, halfZ - inset)
        };

        var mesh = new LowPolyMesh(2);
        mesh.Loft(new[] { Rect(0f, bottom), Rect(0f, -bevel), Rect(bevel, 0f) }, 0, 1);

        Mesh built = mesh.Build("Low Poly Hedge");
        blockCache[size] = built;
        return built;
    }
}
