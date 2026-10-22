using System.Collections;
using UnityEngine;
using Unity.Netcode;

public class Door : NetworkBehaviour
{
    public DoorColor doorColor;
    public Renderer doorRenderer;

    [Tooltip("Direction the door-opening camera shot looks along. Set by the generator.")]
    [SerializeField] private Vector3 cutsceneViewDirection = Vector3.right;

    private bool opening;

    private NetworkVariable<bool> isOpen = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<DoorColor> syncedDoorColor = new NetworkVariable<DoorColor>(
        DoorColor.Red,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public override void OnNetworkSpawn()
    {
        isOpen.OnValueChanged += OnDoorStateChanged;
        syncedDoorColor.OnValueChanged += OnDoorColorChanged;

        ApplyColor(syncedDoorColor.Value);

        if (isOpen.Value)
            Open();
    }

    public override void OnNetworkDespawn()
    {
        isOpen.OnValueChanged -= OnDoorStateChanged;
        syncedDoorColor.OnValueChanged -= OnDoorColorChanged;
    }

    public void SetDoorColor(DoorColor color)
    {
        doorColor = color;

        if (IsServer)
            syncedDoorColor.Value = color;

        ApplyColor(color);
    }

    void ApplyColor(DoorColor color)
    {
        if (!doorRenderer) return;

        switch (color)
        {
            case DoorColor.Red:
                doorRenderer.material.color = Color.red;
                break;

            case DoorColor.Blue:
                doorRenderer.material.color = Color.blue;
                break;
        }
    }

    void OnDoorColorChanged(DoorColor oldValue, DoorColor newValue)
    {
        doorColor = newValue;
        ApplyColor(newValue);
    }

    void OnDoorStateChanged(bool oldValue, bool newValue)
    {
        if (newValue && !oldValue)
            AudioManager.Play(Sfx.DoorOpen); // both players hear it: one pressed the button, the other gets the way open

        if (newValue)
            Open();
    }

    void Open()
    {
        gameObject.SetActive(false); // или анимация
    }

    /// <summary>Server: direction the door camera shot looks along. Set by the generator: from the maze half towards the exit.</summary>
    public void SetCutsceneViewDirection(Vector3 viewDirection)
    {
        cutsceneViewDirection = viewDirection;
    }

    /// <summary>
    /// Server only. This used to be a ServerRpc with RequireOwnership = false, which would have let any client
    /// open any door. The only caller is Button.OnTriggerEnter, which already runs on the server.
    /// Both players watch the door open: the camera shot starts now, the door opens while the camera looks at it.
    /// </summary>
    public void OpenDoor()
    {
        if (!IsServer || isOpen.Value || opening)
            return;

        opening = true;
        StartCoroutine(OpenWithCameraShot());
    }

    private IEnumerator OpenWithCameraShot()
    {
        PlayDoorCutsceneRpc(transform.position, cutsceneViewDirection);

        yield return new WaitForSeconds(CameraCutscene.ActionDelay);

        isOpen.Value = true;
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayDoorCutsceneRpc(Vector3 focus, Vector3 viewDirection)
    {
        // The key was just picked up; the door-open sound follows when the door opens in the shot.
        AudioManager.Play(Sfx.KeyPickup);
        CameraCutscene.Play(focus, viewDirection);
    }
}
public enum DoorColor
{
    Red,
    Blue
}
