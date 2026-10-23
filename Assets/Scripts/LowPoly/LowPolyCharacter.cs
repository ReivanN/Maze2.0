using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Replaces the default armature mesh with a low-poly hooded wanderer: pointed hood with a dark face and glowing eyes,
/// layered shoulder cape, split long coat, leather belt, gloves and boots, and a sheathed sword on the hip.
/// Every piece is rigidly bound to a bone of the existing rig, so all current animations keep working.
/// The cloak follows the colour chosen in the menu (via <see cref="NetworkPlayerColor"/>); white keeps the default blue-grey.
/// </summary>
public class LowPolyCharacter : MonoBehaviour
{
    [Header("Materials")]
    [SerializeField] private Material cloakMaterial;
    [SerializeField] private Material leatherMaterial;
    [SerializeField] private Material darkLeatherMaterial;
    [SerializeField] private Material pantsMaterial;
    [SerializeField] private Material faceMaterial;
    [SerializeField] private Material eyeMaterial;
    [SerializeField] private Material steelMaterial;

    [Tooltip("How much of the default cloak colour stays mixed in when a player colour is applied.")]
    [SerializeField, Range(0f, 1f)] private float cloakColorBlend = 0.35f;

    // Gear submeshes.
    private const int Leather = 0, DarkLeather = 1, Pants = 2, Face = 3, Eyes = 4, Steel = 5;

    // Extra bones created under the head for the face (see AddFaceBone).
    public const string LeftEyeBone = "Left_Eye", RightEyeBone = "Right_Eye", MouthBone = "Mouth";

    private readonly Dictionary<string, Transform> boneByName = new Dictionary<string, Transform>();
    private readonly List<Transform> bones = new List<Transform>();
    private Transform holder;
    private LowPolyMesh cloak;
    private LowPolyMesh gear;
    private Material cloakInstance;
    private Color defaultCloakColor;
    private NetworkPlayerColor playerColor;

    private void Awake()
    {
        if (cloakMaterial == null)
            return;

        foreach (Transform t in GetComponentsInChildren<Transform>(true))
            if (!boneByName.ContainsKey(t.name))
                boneByName.Add(t.name, t);

        if (!boneByName.ContainsKey("Hips") || !boneByName.ContainsKey("Head"))
        {
            Debug.LogWarning($"{name}: LowPolyCharacter could not find the humanoid bones, keeping the default mesh.");
            return;
        }

        foreach (SkinnedMeshRenderer original in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            original.enabled = false;

        // The Animator culls by the visibility of the renderers it knew at start-up. With the original mesh hidden it
        // would think the character is off screen and stop moving the bones, freezing the new model.
        foreach (Animator animator in GetComponentsInChildren<Animator>(true))
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        holder = new GameObject("Low Poly Character").transform;
        holder.SetParent(transform, false);

        cloak = new LowPolyMesh(1);
        gear = new LowPolyMesh(6);
        BuildBody();
        BuildHead();
        BuildArm("Left");
        BuildArm("Right");
        BuildLeg("Left");
        BuildLeg("Right");
        BuildCoat();
        BuildSword();

        Transform[] boneArray = bones.ToArray();
        var bindposes = new Matrix4x4[boneArray.Length];
        for (int i = 0; i < boneArray.Length; i++)
            bindposes[i] = boneArray[i].worldToLocalMatrix * holder.localToWorldMatrix;

        SkinnedMeshRenderer cloakRenderer = CreateRenderer("Cloak", cloak.BuildSkinned("Low Poly Cloak", bindposes), boneArray,
            new[] { cloakMaterial });
        CreateRenderer("Gear", gear.BuildSkinned("Low Poly Gear", bindposes), boneArray,
            new[] { leatherMaterial, darkLeatherMaterial, pantsMaterial, faceMaterial, eyeMaterial, steelMaterial });

        cloakInstance = cloakRenderer.material;
        defaultCloakColor = cloakMaterial.GetColor("_BaseColor");

        playerColor = GetComponent<NetworkPlayerColor>();
        if (playerColor != null)
            playerColor.ColorApplied += ApplyPlayerColor;

        // Blinking and faces, and the arms going up when the knight carries an item.
        KnightAnimations animations = gameObject.AddComponent<KnightAnimations>();
        animations.Setup(boneByName[LeftEyeBone], boneByName[RightEyeBone], boneByName[MouthBone],
            boneByName["Left_UpperArm"], boneByName["Left_LowerArm"], boneByName["Left_Hand"],
            boneByName["Right_UpperArm"], boneByName["Right_LowerArm"], boneByName["Right_Hand"]);
    }

    private void OnDestroy()
    {
        if (playerColor != null)
            playerColor.ColorApplied -= ApplyPlayerColor;
        if (cloakInstance != null)
            Destroy(cloakInstance);
    }

    private void ApplyPlayerColor(Color color)
    {
        bool white = color.r > 0.95f && color.g > 0.95f && color.b > 0.95f;
        Color cloakColor = white ? defaultCloakColor : Color.Lerp(color, defaultCloakColor, cloakColorBlend);
        cloakInstance.SetColor("_BaseColor", cloakColor);
    }

    private SkinnedMeshRenderer CreateRenderer(string partName, Mesh mesh, Transform[] boneArray, Material[] materials)
    {
        var part = new GameObject(partName);
        part.transform.SetParent(holder, false);
        var renderer = part.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.bones = boneArray;
        renderer.rootBone = boneByName["Hips"];
        renderer.sharedMaterials = materials;
        renderer.updateWhenOffscreen = true;
        return renderer;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>Bone position in the character's local space (x right, y up, z forward).</summary>
    private Vector3 P(string bone) => holder.InverseTransformPoint(boneByName[bone].position);

    private bool Has(string bone) => boneByName.ContainsKey(bone);

    /// <summary>Makes following geometry move with <paramref name="bone"/>.</summary>
    private void Bind(string bone)
    {
        Transform t = boneByName[bone];
        int index = bones.IndexOf(t);
        if (index < 0)
        {
            index = bones.Count;
            bones.Add(t);
        }
        cloak.Bone = index;
        gear.Bone = index;
    }

    /// <summary>Ring around <paramref name="axis"/> at <paramref name="centre"/>; 4 sides make a box section.</summary>
    private static Vector3[] Ring(Vector3 centre, Vector3 axis, Vector2 halfSize, int sides)
    {
        Vector3 up = axis.normalized;
        Vector3 reference = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) > 0.9f ? Vector3.up : Vector3.forward;
        Vector3 right = Vector3.Cross(up, reference).normalized;
        Vector3 forward = Vector3.Cross(right, up);

        float offset = sides == 4 ? Mathf.PI * 0.25f : 0f;
        float scale = sides == 4 ? Mathf.Sqrt(2f) : 1f;
        var ring = new Vector3[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = offset + i * Mathf.PI * 2f / sides;
            ring[i] = centre + right * (Mathf.Cos(a) * halfSize.x * scale) + forward * (Mathf.Sin(a) * halfSize.y * scale);
        }
        return ring;
    }

    /// <summary>Tapered limb piece from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static void Segment(LowPolyMesh mesh, int sub, Vector3 a, Vector3 b, Vector2 sizeA, Vector2 sizeB, int sides = 6)
    {
        Vector3 axis = b - a;
        mesh.Loft(new[] { Ring(a, axis, sizeA, sides), Ring(b, axis, sizeB, sides) }, sub, sub);
    }

    /// <summary>Horizontal ellipse ring; <paramref name="zigzag"/> moves every other point up/down for a ragged hem.</summary>
    private static Vector3[] Ellipse(Vector3 centre, float rx, float rz, int sides, float zigzag = 0f)
    {
        var ring = new Vector3[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = Mathf.PI * 0.5f + i * Mathf.PI * 2f / sides;
            float y = (i % 2 == 0 ? -1f : 1f) * zigzag;
            ring[i] = centre + new Vector3(Mathf.Cos(a) * rx, y, Mathf.Sin(a) * rz);
        }
        return ring;
    }

    // ---- body parts ----------------------------------------------------------------------------------------------

    private void BuildBody()
    {
        Vector3 hips = P("Hips");
        Vector3 chest = Has("Chest") ? P("Chest") : Vector3.Lerp(hips, P("Neck"), 0.5f);
        Vector3 neck = P("Neck");
        float shoulderY = P("Left_UpperArm").y;

        Bind("Spine");
        Segment(cloak, 0, hips + Vector3.up * 0.02f, chest, new Vector2(0.15f, 0.1f), new Vector2(0.165f, 0.11f));

        Bind(Has("UpperChest") ? "UpperChest" : "Chest");
        Segment(cloak, 0, chest, neck - Vector3.up * 0.03f, new Vector2(0.165f, 0.11f), new Vector2(0.18f, 0.12f));

        // Layered shoulder cape with a ragged hem, like the reference.
        Vector3 collar = neck;
        cloak.Loft(new[]
        {
            Ellipse(new Vector3(neck.x, shoulderY - 0.17f, neck.z), 0.31f, 0.2f, 10, 0.04f),
            Ellipse(new Vector3(neck.x, shoulderY + 0.04f, neck.z), 0.27f, 0.17f, 10),
            Ellipse(collar, 0.1f, 0.085f, 10)
        }, 0, 0);
        cloak.Loft(new[]
        {
            Ellipse(new Vector3(neck.x, shoulderY - 0.05f, neck.z), 0.24f, 0.16f, 8, 0.03f),
            Ellipse(new Vector3(neck.x, shoulderY + 0.08f, neck.z), 0.2f, 0.14f, 8),
            Ellipse(collar + Vector3.up * 0.02f, 0.09f, 0.08f, 8)
        }, 0, 0);

        // Little gem clasp on the chest.
        Vector3 gem = new Vector3(neck.x, shoulderY - 0.14f, chest.z + 0.135f);
        gear.Loft(new[]
        {
            Ring(gem + Vector3.down * 0.05f, Vector3.up, Vector2.zero, 4),
            Ring(gem, Vector3.up, new Vector2(0.025f, 0.012f), 4),
            Ring(gem + Vector3.up * 0.05f, Vector3.up, Vector2.zero, 4)
        }, Steel, Steel);

        Bind("Hips");
        // Belt with buckle and a pouch.
        gear.Loft(new[]
        {
            Ellipse(hips + Vector3.down * 0.03f, 0.185f, 0.13f, 8),
            Ellipse(hips + Vector3.up * 0.05f, 0.18f, 0.125f, 8)
        }, Leather, Leather);
        Vector3 buckle = hips + new Vector3(0f, 0.01f, 0.13f);
        Segment(gear, Steel, buckle + Vector3.down * 0.035f, buckle + Vector3.up * 0.035f,
            new Vector2(0.04f, 0.012f), new Vector2(0.04f, 0.012f), 4);
        Vector3 pouch = hips + new Vector3(0.16f, -0.06f, 0.07f);
        Segment(gear, DarkLeather, pouch + Vector3.down * 0.05f, pouch + Vector3.up * 0.05f,
            new Vector2(0.05f, 0.035f), new Vector2(0.045f, 0.03f), 4);
    }

    private void BuildHead()
    {
        Bind("Head");
        Vector3 c = P("Head") + Vector3.up * 0.09f;

        // Pointed hood whose tip folds backwards.
        cloak.Loft(new[]
        {
            Ellipse(c + new Vector3(0f, -0.16f, -0.01f), 0.16f, 0.15f, 8),
            Ellipse(c + new Vector3(0f, 0f, 0f), 0.15f, 0.16f, 8),
            Ellipse(c + new Vector3(0f, 0.12f, -0.02f), 0.13f, 0.13f, 8),
            Ellipse(c + new Vector3(0f, 0.22f, -0.06f), 0.08f, 0.08f, 8),
            Ellipse(c + new Vector3(0f, 0.28f, -0.14f), 0.04f, 0.04f, 8),
            Ellipse(c + new Vector3(0f, 0.29f, -0.29f), 0f, 0f, 8)
        }, 0, 0);

        // Dark face in the hood opening with two narrow glowing eyes.
        Segment(gear, Face, c + new Vector3(0f, -0.1f, 0.12f), c + new Vector3(0f, 0.07f, 0.12f),
            new Vector2(0.08f, 0.05f), new Vector2(0.07f, 0.05f), 4);
        float z = 0.12f + 0.05f + 0.004f;

        // Each eye and the mouth hang on their own small bone under the head, so KnightAnimations can blink and
        // make faces by scaling and tilting them.
        Vector3 leftEye = c + new Vector3(-0.035f, 0.005f, z);
        Vector3 rightEye = c + new Vector3(0.035f, 0.005f, z);
        Vector3 mouth = c + new Vector3(0f, -0.055f, z);
        AddFaceBone(LeftEyeBone, leftEye);
        AddFaceBone(RightEyeBone, rightEye);
        AddFaceBone(MouthBone, mouth);

        Bind(LeftEyeBone);
        Segment(gear, Eyes, c + new Vector3(-0.055f, 0.01f, z), c + new Vector3(-0.015f, 0f, z),
            new Vector2(0.008f, 0.003f), new Vector2(0.006f, 0.003f), 4);
        Bind(RightEyeBone);
        Segment(gear, Eyes, c + new Vector3(0.055f, 0.01f, z), c + new Vector3(0.015f, 0f, z),
            new Vector2(0.008f, 0.003f), new Vector2(0.006f, 0.003f), 4);

        // A thin glowing mouth, hidden (scaled to zero) until an expression needs it.
        Bind(MouthBone);
        Segment(gear, Eyes, mouth + new Vector3(-0.022f, 0f, 0f), mouth + new Vector3(0.022f, 0f, 0f),
            new Vector2(0.004f, 0.003f), new Vector2(0.004f, 0.003f), 4);
    }

    /// <summary>A helper bone under the head at <paramref name="position"/> (character space), axes like the character.</summary>
    private void AddFaceBone(string boneName, Vector3 position)
    {
        Transform bone = new GameObject(boneName).transform;
        bone.SetParent(boneByName["Head"], false);
        bone.SetPositionAndRotation(holder.TransformPoint(position), holder.rotation);
        boneByName[boneName] = bone;
    }

    private void BuildArm(string side)
    {
        Vector3 upper = P(side + "_UpperArm");
        Vector3 lower = P(side + "_LowerArm");
        Vector3 hand = P(side + "_Hand");

        Bind(side + "_UpperArm");
        Segment(cloak, 0, upper, lower, new Vector2(0.065f, 0.06f), new Vector2(0.055f, 0.05f));

        // Big leather gauntlet flaring towards the elbow.
        Bind(side + "_LowerArm");
        Segment(gear, Leather, lower + (hand - lower) * 0.1f, hand, new Vector2(0.075f, 0.07f), new Vector2(0.055f, 0.05f));

        Bind(side + "_Hand");
        Vector3 direction = (hand - lower).normalized;
        Segment(gear, DarkLeather, hand, hand + direction * 0.13f, new Vector2(0.05f, 0.03f), new Vector2(0.045f, 0.025f), 4);
    }

    private void BuildLeg(string side)
    {
        Vector3 upper = P(side + "_UpperLeg");
        Vector3 lower = P(side + "_LowerLeg");
        Vector3 foot = P(side + "_Foot");
        Vector3 toes = Has(side + "_Toes") ? P(side + "_Toes") : foot + Vector3.forward * 0.12f;

        Bind(side + "_UpperLeg");
        Segment(gear, Pants, upper, lower, new Vector2(0.085f, 0.085f), new Vector2(0.06f, 0.06f));

        Bind(side + "_LowerLeg");
        Segment(gear, Leather, lower, foot, new Vector2(0.06f, 0.06f), new Vector2(0.055f, 0.055f));
        Segment(gear, Leather, Vector3.Lerp(lower, foot, 0.1f), Vector3.Lerp(lower, foot, 0.32f),
            new Vector2(0.08f, 0.08f), new Vector2(0.075f, 0.075f));

        // Boot sole sits on the ground (character root is at the feet).
        Bind(side + "_Foot");
        Vector3 heel = new Vector3(foot.x, 0.045f, foot.z - 0.06f);
        Vector3 tip = new Vector3(toes.x, 0.045f, toes.z + 0.07f);
        Segment(gear, DarkLeather, heel, tip, new Vector2(0.06f, 0.045f), new Vector2(0.055f, 0.04f), 4);
    }

    private void BuildCoat()
    {
        Vector3 hips = P("Hips");
        float top = hips.y + 0.02f;
        float knee = P("Left_LowerLeg").y;
        float bottom = knee - 0.02f;

        // Front and side flaps follow the legs, leaving the split in the middle like the reference.
        foreach (string side in new[] { "Left", "Right" })
        {
            float s = Mathf.Sign(P(side + "_UpperLeg").x - hips.x);
            Bind(side + "_UpperLeg");
            float zf = hips.z;
            cloak.Panel(0, new[]
            {
                new Vector3(hips.x + s * 0.015f, top, zf + 0.135f),
                new Vector3(hips.x + s * 0.19f, top, zf + 0.135f),
                new Vector3(hips.x + s * 0.21f, bottom, zf + 0.16f),
                new Vector3(hips.x + s * 0.12f, bottom - 0.12f, zf + 0.165f),
                new Vector3(hips.x + s * 0.03f, bottom + 0.04f, zf + 0.16f)
            }, new Vector3(0f, 0f, -0.025f));

            cloak.Panel(0, new[]
            {
                new Vector3(hips.x + s * 0.195f, top, zf - 0.1f),
                new Vector3(hips.x + s * 0.195f, top, zf + 0.12f),
                new Vector3(hips.x + s * 0.22f, bottom, zf + 0.14f),
                new Vector3(hips.x + s * 0.225f, bottom - 0.1f, zf + 0.01f),
                new Vector3(hips.x + s * 0.22f, bottom, zf - 0.13f)
            }, new Vector3(-s * 0.025f, 0f, 0f));
        }

        // Long back flap hangs from the hips with a ragged hem.
        Bind("Hips");
        float back = hips.z - 0.13f;
        float hem = knee - 0.18f;
        cloak.Panel(0, new[]
        {
            new Vector3(hips.x - 0.19f, top, back),
            new Vector3(hips.x + 0.19f, top, back),
            new Vector3(hips.x + 0.26f, hem + 0.05f, back - 0.07f),
            new Vector3(hips.x + 0.13f, hem - 0.06f, back - 0.07f),
            new Vector3(hips.x, hem + 0.03f, back - 0.07f),
            new Vector3(hips.x - 0.13f, hem - 0.07f, back - 0.07f),
            new Vector3(hips.x - 0.26f, hem + 0.04f, back - 0.07f)
        }, new Vector3(0f, 0f, 0.025f));
    }

    private void BuildSword()
    {
        Bind("Hips");
        Vector3 hips = P("Hips");
        float s = Mathf.Sign(P("Left_UpperLeg").x - hips.x);
        Vector3 hilt = hips + new Vector3(s * 0.25f, 0.02f, 0.06f);
        Vector3 down = new Vector3(s * 0.1f, -0.85f, -0.5f).normalized;
        Vector3 across = Vector3.Cross(down, Vector3.right).normalized;

        Segment(gear, DarkLeather, hilt, hilt + down * 0.75f, new Vector2(0.03f, 0.012f), new Vector2(0.026f, 0.011f), 4);
        Segment(gear, Steel, hilt + down * 0.75f, hilt + down * 0.79f, new Vector2(0.026f, 0.011f), new Vector2(0.012f, 0.008f), 4);
        Segment(gear, Steel, hilt - across * 0.07f, hilt + across * 0.07f, new Vector2(0.015f, 0.015f), new Vector2(0.015f, 0.015f), 4);
        Segment(gear, Leather, hilt, hilt - down * 0.15f, new Vector2(0.016f, 0.016f), new Vector2(0.016f, 0.016f), 6);
        Segment(gear, Steel, hilt - down * 0.15f, hilt - down * 0.19f, new Vector2(0.026f, 0.026f), new Vector2(0.02f, 0.02f), 6);
    }
}
