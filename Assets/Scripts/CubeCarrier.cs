using StarterAssets;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Lets the local player pick up, carry, put down and throw PushableCube:
///   E near a cube - pick it up (it floats above the head), sprint is off while carrying;
///   E again       - put it down in front;
///   left mouse    - throw it where the camera looks.
/// Shows the matching hint on screen. Added at runtime to the local player, so the player prefab is not edited.
/// </summary>
public class CubeCarrier : MonoBehaviour
{
    [Tooltip("How close the player has to be to a cube to pick it up.")]
    [SerializeField] private float interactDistance = 1.9f;

    private const float NoRoomHintTime = 1.2f;

    private ThirdPersonController controller;
    private float baseSprintSpeed;
    private bool sprintLimited;
    private RelayManager relayManager;

    private GameObject promptCanvas;
    private RectTransform promptRoot;
    private RectTransform promptPanel;
    private TextMeshProUGUI promptText;
    private float noRoomHintUntil;

    /// <summary>Returns false while the local player is not spawned yet.</summary>
    public static bool EnsureOnLocalPlayer()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.LocalClient == null || networkManager.LocalClient.PlayerObject == null)
            return false;

        ThirdPersonController playerController =
            networkManager.LocalClient.PlayerObject.GetComponentInChildren<ThirdPersonController>(true);
        if (playerController == null)
            return false;

        if (playerController.GetComponent<CubeCarrier>() == null)
            playerController.gameObject.AddComponent<CubeCarrier>();

        return true;
    }

    private void Awake()
    {
        controller = GetComponent<ThirdPersonController>();
        baseSprintSpeed = controller.SprintSpeed;
        CreatePrompt();
    }

    private void OnDestroy()
    {
        SetSprintLimited(false);

        if (promptCanvas != null)
            Destroy(promptCanvas);
    }

    private void Update()
    {
        PushableCube carried = FindCarriedCube();
        SetSprintLimited(carried != null);

        bool inputAllowed = IsInputAllowed();
        bool ePressed = inputAllowed && Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;
        bool throwPressed = inputAllowed && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

        if (carried != null)
        {
            if (ePressed && !carried.TryPutDown(transform))
                noRoomHintUntil = Time.time + NoRoomHintTime;
            else if (throwPressed)
                carried.Throw(GetThrowDirection());

            if (inputAllowed)
            {
                string text = Time.time < noRoomHintUntil ? "No room to put it down" : "[E] Put down     [LMB] Throw";
                ShowPromptAtBottom(text);
            }
            else
            {
                HidePrompt();
            }

            return;
        }

        PushableCube nearest = inputAllowed ? FindNearestCube() : null;
        if (nearest == null)
        {
            HidePrompt();
            return;
        }

        ShowPromptAt(nearest.transform.position + Vector3.up * 0.9f, "[E] Pick up");

        if (ePressed)
            nearest.RequestPickUp();
    }

    private bool IsInputAllowed()
    {
        if (controller == null || !controller.enabled || LaserCutscene.IsPlaying)
            return false;

        if (relayManager == null)
            relayManager = FindAnyObjectByType<RelayManager>();

        return relayManager == null || !relayManager.IsMenuOpen;
    }

    private static PushableCube FindCarriedCube()
    {
        foreach (PushableCube cube in PushableCube.Instances)
        {
            if (cube != null && cube.IsCarriedByLocalPlayer)
                return cube;
        }

        return null;
    }

    private PushableCube FindNearestCube()
    {
        PushableCube nearest = null;
        float nearestDistance = interactDistance;
        Vector3 position = transform.position;

        foreach (PushableCube cube in PushableCube.Instances)
        {
            if (cube == null || !cube.CanBePickedUp)
                continue;

            Vector3 offset = cube.transform.position - position;
            if (Mathf.Abs(offset.y) > 1.5f)
                continue;

            float distance = new Vector2(offset.x, offset.z).magnitude;
            if (distance <= nearestDistance)
            {
                nearest = cube;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private Vector3 GetThrowDirection()
    {
        Camera cam = Camera.main;
        Vector3 direction = cam != null ? cam.transform.forward : transform.forward;
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
    }

    // No running with the cube: sprint speed is the walk speed while carrying.
    private void SetSprintLimited(bool limited)
    {
        if (controller == null || sprintLimited == limited)
            return;

        sprintLimited = limited;
        controller.SprintSpeed = limited ? controller.MoveSpeed : baseSprintSpeed;
    }

    // ================= HINT =================

    private void CreatePrompt()
    {
        promptCanvas = new GameObject("Cube Prompt", typeof(Canvas), typeof(CanvasScaler));
        Canvas canvas = promptCanvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 45; // above the hearts, below the mutator HUD and the menus

        CanvasScaler scaler = promptCanvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        promptRoot = (RectTransform)promptCanvas.transform;

        GameObject panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panelObject.transform.SetParent(promptRoot, false);
        promptPanel = (RectTransform)panelObject.transform;
        promptPanel.sizeDelta = new Vector2(420f, 64f);

        Image panelImage = panelObject.GetComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.75f);
        panelImage.raycastTarget = false;
        UiTheme theme = UiTheme.Current;
        if (theme != null && theme.TooltipSprite != null)
        {
            panelImage.color = new Color(1f, 1f, 1f, 0.9f);
            UiTheme.ApplySlicedSprite(panelImage, theme.TooltipSprite, promptPanel.sizeDelta.y);
        }

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(promptPanel, false);
        promptText = textObject.GetComponent<TextMeshProUGUI>();
        UiTheme.ApplyFont(promptText);
        promptText.fontSize = 26f;
        promptText.alignment = TextAlignmentOptions.Center;
        promptText.color = Color.white;
        promptText.raycastTarget = false;

        RectTransform textRect = promptText.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16f, 0f);
        textRect.offsetMax = new Vector2(-16f, 0f);

        promptCanvas.SetActive(false);
    }

    // Hint floating over the cube on screen.
    private void ShowPromptAt(Vector3 worldPosition, string text)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            HidePrompt();
            return;
        }

        Vector3 screenPoint = cam.WorldToScreenPoint(worldPosition);
        if (screenPoint.z <= 0f ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(promptRoot, screenPoint, null, out Vector2 localPoint))
        {
            HidePrompt();
            return;
        }

        SetPrompt(text, new Vector2(0.5f, 0.5f), localPoint, 260f);
    }

    // Fixed hint at the bottom of the screen while carrying.
    private void ShowPromptAtBottom(string text)
    {
        SetPrompt(text, new Vector2(0.5f, 0f), new Vector2(0f, 150f), 520f);
    }

    private void SetPrompt(string text, Vector2 anchor, Vector2 position, float width)
    {
        promptCanvas.SetActive(true);
        promptPanel.anchorMin = anchor;
        promptPanel.anchorMax = anchor;
        promptPanel.pivot = new Vector2(0.5f, 0.5f);
        promptPanel.anchoredPosition = position;
        promptPanel.sizeDelta = new Vector2(width, promptPanel.sizeDelta.y);
        promptText.text = text;
    }

    private void HidePrompt()
    {
        if (promptCanvas != null && promptCanvas.activeSelf)
            promptCanvas.SetActive(false);
    }
}
