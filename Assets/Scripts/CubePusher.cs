using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lets the local player push PushableCube by walking into it. Added at runtime to the object with the
/// CharacterController (OnControllerColliderHit is only sent there), so the player prefab does not need to be edited.
/// </summary>
public class CubePusher : MonoBehaviour
{
    [Tooltip("Speed of the pushed cube, m/s. A bit below walking speed (2), so the player stays pressed against the cube.")]
    [SerializeField] private float pushSpeed = 1.8f;

    /// <summary>Returns false while the local player is not spawned yet.</summary>
    public static bool EnsureOnLocalPlayer()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.LocalClient == null || networkManager.LocalClient.PlayerObject == null)
            return false;

        CharacterController controller = networkManager.LocalClient.PlayerObject.GetComponentInChildren<CharacterController>(true);
        if (controller == null)
            return false;

        if (controller.GetComponent<CubePusher>() == null)
            controller.gameObject.AddComponent<CubePusher>();

        return true;
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        PushableCube cube = hit.collider.GetComponentInParent<PushableCube>();
        if (cube == null || cube.IsLocked)
            return;

        // Standing on top of the cube is not pushing it.
        if (hit.moveDirection.y < -0.3f)
            return;

        if (!cube.IsOwner)
        {
            // The first touch asks the server for the cube; pushing starts once it is ours.
            cube.RequestControl();
            return;
        }

        Vector3 direction = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
        if (direction.sqrMagnitude < 0.01f)
            return;

        cube.Push(direction.normalized, pushSpeed);
    }
}
