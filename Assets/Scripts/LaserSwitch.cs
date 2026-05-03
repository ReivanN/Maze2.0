using Unity.Netcode;
using UnityEngine;
using System.Collections;

public class LaserSwitch : NetworkBehaviour
{
    [SerializeField] private LaserTrap targetLaser;
    [SerializeField] private bool disableLaser = true;
    [SerializeField] private bool oneTimeUse = false;
    [SerializeField] private Transform visualRoot;
    [SerializeField] private float pressDepth = 0.12f;
    [SerializeField] private float pressDuration = 0.12f;

    private readonly NetworkVariable<bool> isPressed = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private bool used;
    private Vector3 releasedLocalPosition;
    private Coroutine pressAnimation;

    private void Awake()
    {
        if (visualRoot == null)
            visualRoot = transform;

        releasedLocalPosition = visualRoot.localPosition;
    }

    public override void OnNetworkSpawn()
    {
        isPressed.OnValueChanged += OnPressedChanged;
        ApplyPressedState(isPressed.Value, true);
    }

    public override void OnNetworkDespawn()
    {
        isPressed.OnValueChanged -= OnPressedChanged;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (used && oneTimeUse) return;
        if (!other.CompareTag("Player")) return;
        if (targetLaser == null) return;

        isPressed.Value = true;

        if (disableLaser)
            targetLaser.Disable();
        else
            targetLaser.Toggle();

        used = true;
    }

    public void SetTargetLaser(LaserTrap laserTrap)
    {
        targetLaser = laserTrap;
    }

    private void OnPressedChanged(bool oldValue, bool newValue)
    {
        ApplyPressedState(newValue, false);
    }

    private void ApplyPressedState(bool pressed, bool immediate)
    {
        if (visualRoot == null)
            return;

        Vector3 targetPosition = releasedLocalPosition + Vector3.down * (pressed ? pressDepth : 0f);

        if (pressAnimation != null)
            StopCoroutine(pressAnimation);

        if (immediate)
        {
            visualRoot.localPosition = targetPosition;
            return;
        }

        pressAnimation = StartCoroutine(AnimatePress(targetPosition));
    }

    private IEnumerator AnimatePress(Vector3 targetPosition)
    {
        Vector3 startPosition = visualRoot.localPosition;
        float elapsed = 0f;

        while (elapsed < pressDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / pressDuration);
            t = 1f - Mathf.Pow(1f - t, 3f);
            visualRoot.localPosition = Vector3.Lerp(startPosition, targetPosition, t);
            yield return null;
        }

        visualRoot.localPosition = targetPosition;
        pressAnimation = null;
    }
}
