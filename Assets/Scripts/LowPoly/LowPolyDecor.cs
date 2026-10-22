using UnityEngine;

/// <summary>
/// Builds the small low-poly props (conifers, grass tufts, rocks) shared by floor tiles and hedge walls.
/// Props are purely visual: they have no colliders and are never networked.
/// </summary>
public static class LowPolyDecor
{
    /// <summary>Creates an unscaled child under <paramref name="parent"/> rendering <paramref name="mesh"/>.</summary>
    public static GameObject CreatePart(Transform parent, string name, Mesh mesh, Material[] materials, Vector3 worldPosition)
    {
        var part = new GameObject(name);
        part.transform.SetParent(parent, false);
        part.transform.position = worldPosition;

        // Cancel the parent's (often non-uniform) scale so meshes are built in metres.
        Vector3 s = parent.lossyScale;
        part.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);

        part.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = part.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        return part;
    }

    /// <summary>Conifer of stacked faceted cones, like the trees in the reference art. Submeshes: trunk, dark, light.</summary>
    public static Mesh Tree(System.Random random, float height)
    {
        var mesh = new LowPolyMesh(3);
        int sides = 7;
        float angle = LowPolyMesh.Range(random, 0f, Mathf.PI);

        float trunkTop = height * 0.25f;
        mesh.Loft(new[]
        {
            LowPolyMesh.Circle(5, height * 0.06f, 0f, angle),
            LowPolyMesh.Circle(5, height * 0.05f, trunkTop, angle)
        }, 0, 0);

        int layers = 3;
        for (int i = 0; i < layers; i++)
        {
            float t = i / (float)layers;
            float baseY = Mathf.Lerp(height * 0.2f, height * 0.62f, t);
            float radius = Mathf.Lerp(height * 0.34f, height * 0.17f, t);
            float apexY = baseY + Mathf.Lerp(height * 0.42f, height * 0.38f, t);
            int sub = i == layers - 1 ? 2 : 1;
            mesh.Loft(new[]
            {
                LowPolyMesh.Circle(sides, radius, baseY, angle + i * 0.4f, random, 0.12f),
                LowPolyMesh.Circle(sides, 0f, apexY)
            }, sub, sub);
        }
        return mesh.Build("Low Poly Tree");
    }

    /// <summary>A tuft of thin leaning blades. Single submesh.</summary>
    public static Mesh Grass(System.Random random, float height)
    {
        var mesh = new LowPolyMesh(1);
        int blades = random.Next(4, 8);
        for (int i = 0; i < blades; i++)
        {
            float a = LowPolyMesh.Range(random, 0f, Mathf.PI * 2f);
            float r = LowPolyMesh.Range(random, 0f, 0.09f);
            Vector3 root = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            Vector3 side = Quaternion.Euler(0f, LowPolyMesh.Range(random, 0f, 180f), 0f) * Vector3.right * 0.025f;
            float h = height * LowPolyMesh.Range(random, 0.55f, 1f);
            Vector3 lean = new Vector3(LowPolyMesh.Range(random, -0.08f, 0.08f), 0f, LowPolyMesh.Range(random, -0.08f, 0.08f));
            mesh.DoubleTriangle(0, root - side, root + side, root + Vector3.up * h + lean);
        }
        return mesh.Build("Low Poly Grass");
    }

    /// <summary>A lumpy faceted stone (or bush, with a green material). Single submesh.</summary>
    public static Mesh Rock(System.Random random, float radius, float height)
    {
        var mesh = new LowPolyMesh(1);
        int sides = random.Next(5, 8);
        float angle = LowPolyMesh.Range(random, 0f, Mathf.PI);
        mesh.Loft(new[]
        {
            LowPolyMesh.Circle(sides, radius, 0f, angle, random, 0.15f),
            LowPolyMesh.Circle(sides, radius * 0.85f, height * 0.55f, angle + 0.3f, random, 0.2f),
            LowPolyMesh.Circle(sides, radius * 0.4f, height, angle + 0.6f, random, 0.25f)
        }, 0, 0);
        return mesh.Build("Low Poly Rock");
    }
}
