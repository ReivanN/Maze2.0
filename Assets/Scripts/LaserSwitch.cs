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
    [SerializeField] private Vector3 minimumTriggerSize = new Vector3(1.5f, 1.2f, 1.5f);

    [Header("Cube")]
    [Tooltip("When set, only this cube put or thrown onto the plate presses it; players walking over it do nothing.")]
    [SerializeField] private PushableCube requiredCube;
    [Tooltip("How far from the plate center (on X and Z) the cube still counts as standing on the plate. " +
             "1 = anywhere in the plate's maze cell (cells are 2 m), so a cube that visibly touches the plate counts.")]
    [SerializeField] private float cubeDetectionHalfSize = 1f;
    [SerializeField] private float cubeRestHeight = 0.5f;

    [Header("Camera shot")]
    [Tooltip("Direction the laser-off camera looks along. Set by the generator: from the corridor into the blocked dead end.")]
    [SerializeField] private Vector3 cutsceneViewDirection = Vector3.forward;

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

        EnsureUsableTrigger();
    }

    public override void OnNetworkSpawn()
    {
        // Not in Awake: on clients NGO instantiates the prefab at (0, 0, 0) and moves it to its real place only
        // after Awake. Caching the position there put the plate into the maze corner (inside a wall) on the client,
        // so the client pushed the cube to the wrong place and the laser never turned off.
        releasedLocalPosition = visualRoot.localPosition;

        // A client that joins after the plate was pressed receives the lowered position.
        if (isPressed.Value)
            releasedLocalPosition += Vector3.up * pressDepth;

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
        if (requiredCube != null) return; // this plate is pressed by the cube, not by players
        if (used && oneTimeUse) return;
        if (!IsPlayerCollider(other)) return;

        Activate();
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned || used || requiredCube == null || !requiredCube.IsSpawned)
            return;

        // A cube carried over the plate (or flying over it) does not count: it has to be put down or land on it.
        if (requiredCube.IsCarried)
            return;

        Vector3 offset = requiredCube.transform.position - transform.position;
        bool onPlate = Mathf.Abs(offset.x) <= cubeDetectionHalfSize &&
                       Mathf.Abs(offset.z) <= cubeDetectionHalfSize &&
                       offset.y <= 0.9f;

        if (onPlate)
        {
            Debug.Log("[LaserSwitch] The cube is on the plate, turning the laser off.");
            requiredCube.LockOnPlate(transform.position + Vector3.up * cubeRestHeight);
            Activate();
        }
    }

    public void SetTargetLaser(LaserTrap laserTrap)
    {
        targetLaser = laserTrap;
    }

    public void SetTargetLaser(LaserTrap laserTrap, Vector3 viewDirection)
    {
        targetLaser = laserTrap;
        cutsceneViewDirection = viewDirection;
    }

    public void SetRequiredCube(PushableCube cube)
    {
        requiredCube = cube;
    }

    private void Activate()
    {
        if (targetLaser == null)
            return;

        isPressed.Value = true;
        used = true;

        if (disableLaser)
            StartCoroutine(DisableLaserWithCameraShot());
        else
            targetLaser.Toggle();
    }

    // Both players watch the laser turn off: the shot starts now, the laser goes off while the camera looks at it.
    private IEnumerator DisableLaserWithCameraShot()
    {
        PlayCameraCutsceneRpc(targetLaser.BarrierCenter, cutsceneViewDirection);

        yield return new WaitForSeconds(CameraCutscene.ActionDelay);

        if (targetLaser != null)
            targetLaser.Disable();
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayCameraCutsceneRpc(Vector3 focus, Vector3 viewDirection)
    {
        CameraCutscene.Play(focus, viewDirection);
    }

    private bool IsPlayerCollider(Collider other)
    {
        if (other == null)
            return false;

        if (other.CompareTag("Player"))
            return true;

        NetworkObject networkObject = other.GetComponentInParent<NetworkObject>();
        return networkObject != null && networkObject.CompareTag("Player");
    }

    private void EnsureUsableTrigger()
    {
        BoxCollider boxCollider = GetComponent<BoxCollider>();
        if (boxCollider == null)
            boxCollider = gameObject.AddComponent<BoxCollider>();

        boxCollider.isTrigger = true;
        boxCollider.enabled = true;
        boxCollider.size = new Vector3(
            Mathf.Max(boxCollider.size.x, minimumTriggerSize.x),
            Mathf.Max(boxCollider.size.y, minimumTriggerSize.y),
            Mathf.Max(boxCollider.size.z, minimumTriggerSize.z)
        );
        boxCollider.center = new Vector3(boxCollider.center.x, Mathf.Max(boxCollider.center.y, minimumTriggerSize.y * 0.5f), boxCollider.center.z);
    }

    private void OnPressedChanged(bool oldValue, bool newValue)
    {
        if (newValue && !oldValue)
            AudioManager.PlayAt(Sfx.PressurePlate, transform.position);

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
