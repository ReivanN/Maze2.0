using UnityEngine;

public class PlatformTrigger : MonoBehaviour
{
    private NetworkFallingPlatform parentPlatform;

    private void Awake()
    {
        // Находим родительский скрипт платформы
        parentPlatform = GetComponentInParent<NetworkFallingPlatform>();
        
        if (parentPlatform == null)
        {
            Debug.LogError("PlatformTrigger не может найти NetworkFallingPlatform в родительских объектах!");
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (parentPlatform != null)
        {
            parentPlatform.OnTriggerEntered(other);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (parentPlatform != null)
        {
            parentPlatform.OnTriggerExited(other);
        }
    }
}