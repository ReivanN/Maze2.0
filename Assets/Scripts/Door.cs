using UnityEngine;
using Unity.Netcode;

public class Door : NetworkBehaviour
{
    public DoorColor doorColor;
    public Renderer doorRenderer;

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
        if (newValue)
            Open();
    }

    void Open()
    {
        gameObject.SetActive(false); // или анимация
    }

    [ServerRpc(RequireOwnership = false)]
    public void OpenDoorServerRpc()
    {
        isOpen.Value = true;
    }
}
public enum DoorColor
{
    Red,
    Blue
}
