using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small helper for building flat-shaded low-poly meshes in code. Every triangle gets its own vertices, so faces
/// stay sharp (faceted look) without any texture. Each material is its own submesh.
/// </summary>
public class LowPolyMesh
{
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Vector3> normals = new List<Vector3>();
    private readonly List<int> vertexBones = new List<int>();
    private readonly List<int>[] triangles;

    /// <summary>Bone index given to the triangles added from now on (only used by skinned meshes).</summary>
    public int Bone { get; set; }

    public LowPolyMesh(int submeshCount)
    {
        triangles = new List<int>[submeshCount];
        for (int i = 0; i < submeshCount; i++)
            triangles[i] = new List<int>();
    }

    /// <summary>Adds a triangle, flipping it if needed so its face points away from <paramref name="inside"/>.</summary>
    public void Triangle(int submesh, Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.sqrMagnitude < 1e-10f)
            return;

        normal.Normalize();
        if (Vector3.Dot(normal, (a + b + c) / 3f - inside) < 0f)
        {
            (b, c) = (c, b);
            normal = -normal;
        }

        int start = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c);
        normals.Add(normal); normals.Add(normal); normals.Add(normal);
        vertexBones.Add(Bone); vertexBones.Add(Bone); vertexBones.Add(Bone);
        triangles[submesh].Add(start);
        triangles[submesh].Add(start + 1);
        triangles[submesh].Add(start + 2);
    }

    /// <summary>A triangle visible from both sides (grass blades).</summary>
    public void DoubleTriangle(int submesh, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
        Vector3 centre = (a + b + c) / 3f;
        Triangle(submesh, a, b, c, centre - normal);
        Triangle(submesh, a, b, c, centre + normal);
    }

    /// <summary>
    /// Connects horizontal rings (same point count, bottom to top) into a closed convex solid.
    /// Faces mostly pointing up use <paramref name="topSubmesh"/>, the rest <paramref name="sideSubmesh"/>.
    /// </summary>
    public void Loft(IList<Vector3[]> rings, int sideSubmesh, int topSubmesh, bool capBottom = true)
    {
        // Each band is judged against the middle of its own two rings, so bent shapes (a hood tip) stay correct.
        for (int r = 0; r < rings.Count - 1; r++)
        {
            Vector3[] low = rings[r], high = rings[r + 1];
            Vector3 inside = (Centre(low) + Centre(high)) * 0.5f;
            for (int i = 0; i < low.Length; i++)
            {
                int j = (i + 1) % low.Length;
                Quad(low[i], low[j], high[j], high[i], inside, sideSubmesh, topSubmesh);
            }
        }

        int last = rings.Count - 1;
        Cap(rings[last], (Centre(rings[last]) + Centre(rings[last - 1])) * 0.5f, sideSubmesh, topSubmesh);
        if (capBottom)
            Cap(rings[0], (Centre(rings[0]) + Centre(rings[1])) * 0.5f, sideSubmesh, topSubmesh);
    }

    /// <summary>
    /// A flat plate (coat flap): <paramref name="outline"/> extruded by <paramref name="thickness"/>.
    /// The outline may have jagged edges as long as every point is visible from its centre.
    /// </summary>
    public void Panel(int submesh, Vector3[] outline, Vector3 thickness)
    {
        Vector3 centre = Centre(outline);
        Vector3 inside = centre + thickness * 0.5f;
        for (int i = 0; i < outline.Length; i++)
        {
            Vector3 a = outline[i], b = outline[(i + 1) % outline.Length];
            Triangle(submesh, centre, a, b, inside);
            Triangle(submesh, centre + thickness, a + thickness, b + thickness, inside);

            Vector3 edgeInside = Vector3.Lerp((a + b) * 0.5f, centre, 0.5f) + thickness * 0.5f;
            Triangle(submesh, a, b, b + thickness, edgeInside);
            Triangle(submesh, a, b + thickness, a + thickness, edgeInside);
        }
    }

    private static Vector3 Centre(Vector3[] ring)
    {
        Vector3 centre = Vector3.zero;
        foreach (Vector3 p in ring) centre += p;
        return centre / ring.Length;
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside, int side, int top)
    {
        int sub = FaceUp(a, b, c, inside) ? top : side;
        Triangle(sub, a, b, c, inside);
        Triangle(sub, a, c, d, inside);
    }

    private void Cap(Vector3[] ring, Vector3 inside, int side, int top)
    {
        Vector3 centre = Centre(ring);
        for (int i = 0; i < ring.Length; i++)
        {
            Vector3 a = ring[i], b = ring[(i + 1) % ring.Length];
            Triangle(FaceUp(centre, a, b, inside) ? top : side, centre, a, b, inside);
        }
    }

    private static bool FaceUp(Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
    {
        Vector3 n = Vector3.Cross(b - a, c - a).normalized;
        if (Vector3.Dot(n, (a + b + c) / 3f - inside) < 0f)
            n = -n;
        return n.y > 0.5f;
    }

    public Mesh Build(string name)
    {
        var mesh = new Mesh { name = name };
        if (vertices.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.subMeshCount = triangles.Length;
        for (int i = 0; i < triangles.Length; i++)
            mesh.SetTriangles(triangles[i], i);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Like <see cref="Build"/>, but every vertex is fully bound to the bone it was added with.</summary>
    public Mesh BuildSkinned(string name, Matrix4x4[] bindposes)
    {
        Mesh mesh = Build(name);
        var weights = new BoneWeight[vertexBones.Count];
        for (int i = 0; i < weights.Length; i++)
            weights[i] = new BoneWeight { boneIndex0 = vertexBones[i], weight0 = 1f };
        mesh.boneWeights = weights;
        mesh.bindposes = bindposes;
        return mesh;
    }

    // ---- shape helpers --------------------------------------------------------------------------------------------

    /// <summary>Square ring of half-size <paramref name="half"/> at height <paramref name="y"/>.</summary>
    public static Vector3[] Square(float half, float y)
    {
        return new[]
        {
            new Vector3(-half, y, -half), new Vector3(half, y, -half),
            new Vector3(half, y, half), new Vector3(-half, y, half)
        };
    }

    /// <summary>Regular polygon ring, optionally with per-point radius jitter (rocks, bushes).</summary>
    public static Vector3[] Circle(int sides, float radius, float y, float angle = 0f, System.Random jitter = null, float amount = 0f)
    {
        var ring = new Vector3[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = angle + i * Mathf.PI * 2f / sides;
            float r = radius;
            if (jitter != null)
                r *= 1f + ((float)jitter.NextDouble() * 2f - 1f) * amount;
            ring[i] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
        }
        return ring;
    }

    /// <summary>Same ring moved by <paramref name="offset"/>.</summary>
    public static Vector3[] Offset(Vector3[] ring, Vector3 offset)
    {
        var result = new Vector3[ring.Length];
        for (int i = 0; i < ring.Length; i++)
            result[i] = ring[i] + offset;
        return result;
    }

    /// <summary>Stable per-cell random generator, so every client decorates the maze the same way.</summary>
    public static System.Random RandomFor(Vector3 position, int salt)
    {
        int x = Mathf.RoundToInt(position.x * 10f);
        int z = Mathf.RoundToInt(position.z * 10f);
        unchecked
        {
            int hash = x * 73856093 ^ z * 19349663 ^ salt * 83492791;
            return new System.Random(hash);
        }
    }

    public static float Range(System.Random random, float min, float max)
    {
        return min + (float)random.NextDouble() * (max - min);
    }
}
