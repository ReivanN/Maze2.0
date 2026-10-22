using UnityEngine;
using Cinemachine;
using Unity.Netcode;

public class CameraFollow : NetworkBehaviour
{
    private CinemachineVirtualCamera vcam;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
            return;

        vcam = FindAnyObjectByType<CinemachineVirtualCamera>();

        if (vcam == null)
        {
            Debug.LogError("CinemachineVirtualCamera не найдена в сцене");
            return;
        }

        vcam.Follow = transform;
        vcam.LookAt = transform;

        // Third-person view (default) or the scene's top-down view, switched with V.
        CameraViewMode viewMode = GetComponent<CameraViewMode>();
        if (viewMode == null)
            viewMode = gameObject.AddComponent<CameraViewMode>();

        viewMode.Configure(vcam);
    }
}
