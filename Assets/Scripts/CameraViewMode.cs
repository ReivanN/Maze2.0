using Cinemachine;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person or top-down view for the local player, switched with V (the choice is remembered).
///
/// The scene has one virtual camera set up for the top-down view (CM_TopDownCamera, Framing Transposer).
/// For the third-person view its Body is swapped at runtime for a Cinemachine3rdPersonFollow that follows the
/// player's PlayerCameraRoot; ThirdPersonController already turns that point with the mouse, so the camera orbits
/// the player and movement is relative to where the camera looks. The scene file is not changed.
/// </summary>
public class CameraViewMode : MonoBehaviour
{
    private const string PrefKey = "Maze.ThirdPersonView";
    private static readonly Quaternion TopDownRotation = Quaternion.Euler(90f, 0f, 0f);

    [Header("Third person")]
    [SerializeField] private float cameraDistance = 4f;
    [SerializeField] private Vector3 shoulderOffset = new Vector3(0.6f, 0.2f, 0f);
    [SerializeField] private Vector3 damping = new Vector3(0.1f, 0.4f, 0.3f);
    [Tooltip("The camera stays this far from walls, so it does not look through them.")]
    [SerializeField] private float cameraRadius = 0.25f;

    [Header("Top down")]
    [SerializeField] private float topDownDistance = 10f;

    private CinemachineVirtualCamera virtualCamera;
    private ThirdPersonController controller;
    private Transform cameraTarget;
    private RelayManager relayManager;
    private bool thirdPerson;
    private bool cursorLocked;

    public static bool IsThirdPerson { get; private set; }

    /// <summary>Called by CameraFollow on the local player once the virtual camera is bound to it.</summary>
    public void Configure(CinemachineVirtualCamera vcam)
    {
        virtualCamera = vcam;
        controller = GetComponentInChildren<ThirdPersonController>(true);
        cameraTarget = controller != null && controller.CinemachineCameraTarget != null
            ? controller.CinemachineCameraTarget.transform
            : transform;

        SetThirdPerson(PlayerPrefs.GetInt(PrefKey, 1) == 1);
    }

    private void Update()
    {
        if (virtualCamera == null)
            return;

        bool menuOpen = IsMenuOpen();
        bool playing = controller != null && controller.enabled && !menuOpen;

        if (playing && !CameraCutscene.IsPlaying && Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame)
        {
            SetThirdPerson(!thirdPerson);
            PlayerPrefs.SetInt(PrefKey, thirdPerson ? 1 : 0);
            PlayerPrefs.Save();
        }

        // Mouse look needs a locked cursor; menus (pause, game over, lobby) need a free one.
        SetCursorLocked(thirdPerson && playing);
    }

    private void OnDestroy()
    {
        SetCursorLocked(false);
        IsThirdPerson = false;
    }

    private void SetThirdPerson(bool enabled)
    {
        thirdPerson = enabled;
        IsThirdPerson = enabled;

        if (enabled)
        {
            Cinemachine3rdPersonFollow follow = virtualCamera.AddCinemachineComponent<Cinemachine3rdPersonFollow>();
            follow.CameraDistance = cameraDistance;
            follow.ShoulderOffset = shoulderOffset;
            follow.VerticalArmLength = 0.4f;
            follow.CameraSide = 1f;
            follow.Damping = damping;
            follow.CameraCollisionFilter = LayerMask.GetMask("Default");
            follow.IgnoreTag = "Player";
            follow.CameraRadius = cameraRadius;

            virtualCamera.Follow = cameraTarget;
            virtualCamera.LookAt = null; // the camera looks where the follow point faces
        }
        else
        {
            CinemachineFramingTransposer framing = virtualCamera.AddCinemachineComponent<CinemachineFramingTransposer>();
            framing.m_CameraDistance = topDownDistance;
            framing.m_XDamping = 1f;
            framing.m_YDamping = 1f;
            framing.m_ZDamping = 1f;

            virtualCamera.transform.rotation = TopDownRotation;
            virtualCamera.Follow = transform;
            virtualCamera.LookAt = transform;
        }

        virtualCamera.PreviousStateIsValid = false; // cut to the new view instead of sliding into it
    }

    private bool IsMenuOpen()
    {
        if (relayManager == null)
            relayManager = FindAnyObjectByType<RelayManager>();

        return relayManager != null && relayManager.IsMenuOpen;
    }

    private void SetCursorLocked(bool locked)
    {
        if (cursorLocked == locked && Cursor.lockState == (locked ? CursorLockMode.Locked : CursorLockMode.None))
            return;

        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
