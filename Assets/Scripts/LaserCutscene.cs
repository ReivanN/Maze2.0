using System.Collections;
using Cinemachine;
using StarterAssets;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Short camera shot of a laser turning off, played on both players at the same time (started by LaserSwitch).
/// A temporary virtual camera with a high priority takes over, the CinemachineBrain blends to it and back.
/// The local player cannot move or look around while the shot plays.
/// </summary>
[DefaultExecutionOrder(-200)] // clears the input before PlayerMutatorApplier (-100) and ThirdPersonController read it
public class LaserCutscene : MonoBehaviour
{
    public const float BlendTime = 1f;
    /// <summary>Seconds from the start of the shot until the server turns the laser off.</summary>
    public const float LaserOffDelay = 1.5f;

    private const float HoldAfterLaserOff = 1.4f;
    private const float CameraHeight = 7f;      // above the 6 m walls, so the maze does not block the view
    private const float CameraBackOffset = 3.5f;
    private const int CameraPriority = 100;

    private static LaserCutscene instance;

    private Coroutine shot;
    private GameObject shotCamera;
    private CinemachineBrain brain;
    private CinemachineBlendDefinition savedBlend;
    private StarterAssetsInputs inputs;

    public static bool IsPlaying => instance != null && instance.shot != null;

    /// <param name="focus">Point the camera looks at (the laser).</param>
    /// <param name="viewDirection">Direction the camera looks along, horizontally.</param>
    public static void Play(Vector3 focus, Vector3 viewDirection)
    {
        if (instance == null)
            instance = new GameObject("Laser Cutscene").AddComponent<LaserCutscene>();

        instance.StartShot(focus, viewDirection);
    }

    private void StartShot(Vector3 focus, Vector3 viewDirection)
    {
        if (shot != null)
        {
            StopCoroutine(shot);
            FinishShot();
        }

        shot = StartCoroutine(PlayShot(focus, viewDirection));
    }

    private IEnumerator PlayShot(Vector3 focus, Vector3 viewDirection)
    {
        inputs = FindLocalInputs();

        Camera mainCamera = Camera.main;
        brain = mainCamera != null ? mainCamera.GetComponent<CinemachineBrain>() : null;
        if (brain != null)
        {
            savedBlend = brain.m_DefaultBlend;
            brain.m_DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, BlendTime);
        }

        Vector3 flatDirection = new Vector3(viewDirection.x, 0f, viewDirection.z);
        if (flatDirection.sqrMagnitude < 0.01f)
            flatDirection = Vector3.forward;

        shotCamera = new GameObject("Laser Cutscene Camera");
        shotCamera.transform.position = focus - flatDirection.normalized * CameraBackOffset + Vector3.up * CameraHeight;
        shotCamera.transform.LookAt(focus);

        // No Body / Aim components: the virtual camera simply uses its own transform.
        CinemachineVirtualCamera virtualCamera = shotCamera.AddComponent<CinemachineVirtualCamera>();
        virtualCamera.m_Priority = CameraPriority;
        virtualCamera.m_Lens.FieldOfView = 60f;

        yield return new WaitForSeconds(LaserOffDelay + HoldAfterLaserOff);

        // Removing the camera makes the brain blend back to the player camera.
        Destroy(shotCamera);
        shotCamera = null;

        yield return new WaitForSeconds(BlendTime);

        FinishShot();
    }

    private void FinishShot()
    {
        if (shotCamera != null)
            Destroy(shotCamera);
        shotCamera = null;

        if (brain != null)
            brain.m_DefaultBlend = savedBlend;
        brain = null;

        inputs = null;
        shot = null;
    }

    private void Update()
    {
        if (shot == null || inputs == null)
            return;

        // Input events arrive before Update, so clearing them here keeps the character still for the whole shot.
        inputs.move = Vector2.zero;
        inputs.look = Vector2.zero;
        inputs.jump = false;
        inputs.sprint = false;
    }

    private static StarterAssetsInputs FindLocalInputs()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.LocalClient == null || networkManager.LocalClient.PlayerObject == null)
            return null;

        return networkManager.LocalClient.PlayerObject.GetComponentInChildren<StarterAssetsInputs>(true);
    }
}
