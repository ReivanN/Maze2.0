using System.Collections.Generic;
using StarterAssets;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// A knife lying on a random cell of the maze (one per half, spawned by CaveDungeonGenerator). A player who walks over it
/// picks it up: from then on both players see it in that player's right hand, and the left mouse button swings it.
/// A robot hit in front of the player is knocked down for a few seconds (RobotEnemy.ServerStun).
/// While the player carries an item (the cube) the left mouse button throws the item instead, as before.
///
/// The networked object stays where it was spawned; only its model is moved into the holder's hand on every machine,
/// so nothing but the holder id is synced. The model is built in code, so no art asset is needed.
/// </summary>
public class KnifePickup : NetworkBehaviour
{
    public const ulong NoHolder = ulong.MaxValue;

    [Header("Pick up")]
    [SerializeField] private float pickUpRadius = 1f;

    [Header("Attack")]
    [SerializeField] private float attackRange = 2.4f;
    [Tooltip("Robots within this angle in front of the player (or of where the camera looks) are hit.")]
    [SerializeField] private float attackAngle = 120f;
    [SerializeField] private float attackCooldown = 0.6f;
    [SerializeField] private float swingDuration = 0.3f;

    [Header("Visual")]
    [SerializeField] private Material bladeMaterial;
    [SerializeField] private Material handleMaterial;
    [SerializeField] private float lyingScale = 1.8f;
    [SerializeField] private float lyingHeight = 0.35f;
    [SerializeField] private float spinSpeed = 120f;
    [SerializeField] private float bobHeight = 0.08f;
    [Tooltip("Knife position next to the hand bone, in the player's space (x right, y up, z forward).")]
    [SerializeField] private Vector3 handOffset = new Vector3(0f, 0f, 0.04f);
    [Tooltip("Knife rotation in the player's space while held: the blade points forward and a little up.")]
    [SerializeField] private Vector3 holdRotation = new Vector3(-25f, 0f, 0f);

    [Header("Sound")]
    [SerializeField] private float soundMaxDistance = 12f;

    private static readonly List<KnifePickup> instances = new List<KnifePickup>();

    private readonly NetworkVariable<ulong> holderId = new NetworkVariable<ulong>(
        NoHolder,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Transform visual;
    private ulong cachedHolder = NoHolder;
    private Transform holderTransform;
    private Transform handBone;
    private float swingStartTime = -10f;
    private float nextAttackTime;
    private float serverNextAttackTime;
    private bool carriedItemLastFrame;

    private ThirdPersonController localController;
    private RelayManager relayManager;
    private GameObject hintCanvas;
    private TextMeshProUGUI hintText;

    public bool IsHeld => holderId.Value != NoHolder;
    private bool IsHeldByLocalPlayer => IsSpawned && NetworkManager != null && holderId.Value == NetworkManager.LocalClientId;

    /// <summary>A player holds at most one knife.</summary>
    public static bool IsHeldBy(ulong clientId)
    {
        foreach (KnifePickup knife in instances)
        {
            if (knife != null && knife.holderId.Value == clientId)
                return true;
        }

        return false;
    }

    private void Awake()
    {
        CreateVisual();
    }

    public override void OnNetworkSpawn()
    {
        instances.Add(this);
        holderId.OnValueChanged += OnHolderChanged;
        UpdateHint();
    }

    public override void OnNetworkDespawn()
    {
        instances.Remove(this);
        holderId.OnValueChanged -= OnHolderChanged;
        DestroyHint();
    }

    public override void OnDestroy()
    {
        DestroyHint();
        base.OnDestroy();
    }

    private void Update()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
            ServerUpdate();

        if (IsHeldByLocalPlayer)
            UpdateLocalHolder();
    }

    private void LateUpdate()
    {
        if (!IsSpawned || visual == null)
            return;

        if (!IsHeld)
        {
            ShowLying();
            return;
        }

        ShowInHand();
    }

    // ================= SERVER =================

    private void ServerUpdate()
    {
        if (IsHeld)
        {
            // The holder left the game: the knife lies where it was spawned again.
            if (!NetworkManager.ConnectedClients.ContainsKey(holderId.Value))
                holderId.Value = NoHolder;

            return;
        }

        foreach (NetworkClient client in NetworkManager.ConnectedClientsList)
        {
            NetworkObject player = client.PlayerObject;
            if (player == null || IsHeldBy(client.ClientId))
                continue;

            Vector3 offset = player.transform.position - transform.position;
            if (Mathf.Abs(offset.y) > 1.5f || new Vector2(offset.x, offset.z).magnitude > pickUpRadius)
                continue;

            holderId.Value = client.ClientId;
            PickedUpRpc(RpcTarget.Single(client.ClientId, RpcTargetUse.Temp));
            return;
        }
    }

    [Rpc(SendTo.Server)]
    private void AttackRpc(Vector3 aimDirection, RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        if (senderId != holderId.Value || Time.time < serverNextAttackTime)
            return;

        serverNextAttackTime = Time.time + attackCooldown * 0.8f;

        if (!NetworkManager.ConnectedClients.TryGetValue(senderId, out NetworkClient client) || client.PlayerObject == null)
            return;

        Transform player = client.PlayerObject.transform;
        Vector3 facing = Flatten(player.forward);
        Vector3 aim = Flatten(aimDirection);

        foreach (RobotEnemy robot in RobotEnemy.Instances)
        {
            if (robot == null)
                continue;

            Vector3 offset = robot.transform.position - player.position;
            if (Mathf.Abs(offset.y) > 2f)
                continue;

            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance > attackRange)
                continue;

            // Right next to the robot any swing hits; farther away it has to be in front of the player or the camera.
            bool inFront = distance < 0.8f ||
                           Vector3.Angle(facing, offset) <= attackAngle * 0.5f ||
                           aim.sqrMagnitude > 0f && Vector3.Angle(aim, offset) <= attackAngle * 0.5f;

            if (inFront)
                robot.ServerStun();
        }

        SwingRpc(senderId);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void PickedUpRpc(RpcParams rpcParams)
    {
        AudioManager.Play(Sfx.KeyPickup);
    }

    // The attacker already swung locally when they clicked.
    [Rpc(SendTo.Everyone)]
    private void SwingRpc(ulong attackerId)
    {
        if (NetworkManager.LocalClientId != attackerId)
            StartSwing();
    }

    // ================= LOCAL HOLDER =================

    private void UpdateLocalHolder()
    {
        // ItemCarrier throws a carried item with the same button, and may have thrown it earlier this frame.
        bool carryingItem = IsLocalPlayerCarryingItem();
        bool itemBusy = carryingItem || carriedItemLastFrame;
        carriedItemLastFrame = carryingItem;

        bool inputAllowed = IsInputAllowed();
        SetHintVisible(inputAllowed);
        if (hintText != null)
            hintText.text = carryingItem ? "Put the item down to use the knife" : "[LMB] Knife: knock a robot out for 5 s";

        if (!inputAllowed || itemBusy || Time.time < nextAttackTime)
            return;

        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
            return;

        nextAttackTime = Time.time + attackCooldown;
        StartSwing();
        AttackRpc(GetAimDirection());
    }

    private bool IsInputAllowed()
    {
        if (CameraCutscene.IsPlaying)
            return false;

        if (localController == null)
        {
            NetworkObject player = NetworkManager.LocalClient != null ? NetworkManager.LocalClient.PlayerObject : null;
            if (player != null)
                localController = player.GetComponentInChildren<ThirdPersonController>(true);
        }

        if (localController == null || !localController.enabled)
            return false;

        if (relayManager == null)
            relayManager = FindAnyObjectByType<RelayManager>();

        return relayManager == null || !relayManager.IsMenuOpen;
    }

    private static bool IsLocalPlayerCarryingItem()
    {
        foreach (CarryableItem item in CarryableItem.Instances)
        {
            if (item != null && item.IsCarriedByLocalPlayer)
                return true;
        }

        return false;
    }

    private static Vector3 GetAimDirection()
    {
        Camera cam = Camera.main;
        return cam != null ? Flatten(cam.transform.forward) : Vector3.zero;
    }

    private void StartSwing()
    {
        swingStartTime = Time.time;
        AudioManager.PlayAt(Sfx.KnifeSwing, visual != null ? visual.position : transform.position, soundMaxDistance);
    }

    private void OnHolderChanged(ulong oldValue, ulong newValue)
    {
        UpdateHint();
    }

    // ================= MODEL =================

    private void ShowLying()
    {
        visual.localScale = Vector3.one * lyingScale;
        visual.localPosition = Vector3.up * (lyingHeight + Mathf.Sin(Time.time * 2.5f) * bobHeight);
        visual.localRotation = Quaternion.Euler(0f, Time.time * spinSpeed, 0f) * Quaternion.Euler(-30f, 0f, 0f);
        SetVisualActive(true);
    }

    private void ShowInHand()
    {
        if (cachedHolder != holderId.Value || holderTransform == null)
        {
            cachedHolder = holderId.Value;
            NetworkObject player = FindPlayerObject(cachedHolder);
            holderTransform = player != null ? player.transform : null;
            handBone = holderTransform != null ? FindRightHand(holderTransform) : null;
        }

        if (holderTransform == null)
        {
            SetVisualActive(false);
            return;
        }

        SetVisualActive(true);

        Quaternion playerRotation = holderTransform.rotation;
        Vector3 handPosition = handBone != null
            ? handBone.position
            : holderTransform.position + playerRotation * new Vector3(0.3f, 1f, 0.3f);

        // A quick horizontal slash from right to left, reaching a little forward in the middle.
        float t = Mathf.Clamp01((Time.time - swingStartTime) / swingDuration);
        bool swinging = t < 1f;
        float reach = swinging ? Mathf.Sin(t * Mathf.PI) : 0f;
        Quaternion swing = swinging
            ? Quaternion.Euler(25f * reach, Mathf.Lerp(70f, -70f, 1f - (1f - t) * (1f - t)), 0f)
            : Quaternion.identity;

        visual.localScale = Vector3.one;
        visual.position = handPosition + playerRotation * (handOffset + Vector3.forward * (0.25f * reach));
        visual.rotation = playerRotation * swing * Quaternion.Euler(holdRotation);
    }

    private void SetVisualActive(bool active)
    {
        if (visual.gameObject.activeSelf != active)
            visual.gameObject.SetActive(active);
    }

    private NetworkObject FindPlayerObject(ulong clientId)
    {
        if (NetworkManager.LocalClientId == clientId && NetworkManager.LocalClient != null && NetworkManager.LocalClient.PlayerObject != null)
            return NetworkManager.LocalClient.PlayerObject;

        foreach (NetworkObject obj in NetworkManager.SpawnManager.SpawnedObjectsList)
        {
            if (obj != null && obj.IsPlayerObject && obj.OwnerClientId == clientId)
                return obj;
        }

        return null;
    }

    private static Transform FindRightHand(Transform player)
    {
        Animator animator = player.GetComponentInChildren<Animator>();
        if (animator != null && animator.isHuman)
        {
            Transform bone = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (bone != null)
                return bone;
        }

        // A generic rig: guess by the usual bone names (RightHand, Hand_R, hand.R, r_hand...).
        foreach (Transform child in player.GetComponentsInChildren<Transform>())
        {
            string name = child.name.ToLowerInvariant();
            if (!name.Contains("hand") || name.Contains("index") || name.Contains("thumb") || name.Contains("finger"))
                continue;

            if (name.Contains("right") || name.EndsWith("_r") || name.EndsWith(".r") || name.StartsWith("r_") || name.Contains("hand_r"))
                return child;
        }

        return null;
    }

    private void CreateVisual()
    {
        visual = new GameObject("Knife Model").transform;
        visual.SetParent(transform, false);

        Color steel = new Color(0.82f, 0.85f, 0.9f);
        Color dark = new Color(0.18f, 0.18f, 0.2f);
        Color wood = new Color(0.36f, 0.2f, 0.1f);

        // The grip is at the model's origin, the blade points along +Z.
        CreatePart(PrimitiveType.Cube, new Vector3(0f, 0f, 0.2f), new Vector3(0.05f, 0.012f, 0.28f), bladeMaterial, steel, 0.9f);
        CreatePart(PrimitiveType.Cube, new Vector3(0f, 0f, 0.365f), new Vector3(0.03f, 0.012f, 0.06f), bladeMaterial, steel, 0.9f);
        CreatePart(PrimitiveType.Cube, new Vector3(0f, 0f, 0.055f), new Vector3(0.13f, 0.03f, 0.025f), handleMaterial, dark, 0.7f);
        CreatePart(PrimitiveType.Cylinder, new Vector3(0f, 0f, -0.02f), new Vector3(0.035f, 0.065f, 0.035f), handleMaterial, wood, 0f,
            Quaternion.Euler(90f, 0f, 0f));
        CreatePart(PrimitiveType.Sphere, new Vector3(0f, 0f, -0.095f), Vector3.one * 0.045f, handleMaterial, dark, 0.7f);
    }

    private void CreatePart(PrimitiveType type, Vector3 position, Vector3 scale, Material material, Color color, float metallic,
        Quaternion? rotation = null)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        DestroyImmediate(part.GetComponent<Collider>()); // the knife is picked up by distance, it has no collider
        part.transform.SetParent(visual, false);
        part.transform.localPosition = position;
        part.transform.localRotation = rotation ?? Quaternion.identity;
        part.transform.localScale = scale;

        Renderer partRenderer = part.GetComponent<Renderer>();
        if (material != null)
        {
            partRenderer.sharedMaterial = material;
            return;
        }

        Material instance = partRenderer.material;
        instance.color = color;
        if (instance.HasProperty("_Metallic"))
            instance.SetFloat("_Metallic", metallic);
        if (instance.HasProperty("_Smoothness"))
            instance.SetFloat("_Smoothness", metallic > 0f ? 0.8f : 0.3f);
    }

    private static Vector3 Flatten(Vector3 direction)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
    }

    // ================= HINT =================

    private void UpdateHint()
    {
        if (IsHeldByLocalPlayer)
            CreateHint();
        else
            DestroyHint();
    }

    private void SetHintVisible(bool visible)
    {
        if (hintCanvas != null && hintCanvas.activeSelf != visible)
            hintCanvas.SetActive(visible);
    }

    private void CreateHint()
    {
        if (hintCanvas != null)
            return;

        hintCanvas = new GameObject("Knife Hint", typeof(Canvas), typeof(CanvasScaler));
        Canvas canvas = hintCanvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 44; // just under the item prompt

        CanvasScaler scaler = hintCanvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panelObject.transform.SetParent(hintCanvas.transform, false);
        RectTransform panel = (RectTransform)panelObject.transform;
        panel.anchorMin = new Vector2(0.5f, 0f);
        panel.anchorMax = new Vector2(0.5f, 0f);
        panel.pivot = new Vector2(0.5f, 0f);
        panel.anchoredPosition = new Vector2(0f, 40f);
        panel.sizeDelta = new Vector2(560f, 52f);

        Image panelImage = panelObject.GetComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.6f);
        panelImage.raycastTarget = false;

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(panel, false);
        hintText = textObject.GetComponent<TextMeshProUGUI>();
        UiTheme.ApplyFont(hintText);
        hintText.fontSize = 24f;
        hintText.alignment = TextAlignmentOptions.Center;
        hintText.color = Color.white;
        hintText.raycastTarget = false;

        RectTransform textRect = hintText.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12f, 0f);
        textRect.offsetMax = new Vector2(-12f, 0f);
    }

    private void DestroyHint()
    {
        if (hintCanvas != null)
            Destroy(hintCanvas);

        hintCanvas = null;
        hintText = null;
    }
}
