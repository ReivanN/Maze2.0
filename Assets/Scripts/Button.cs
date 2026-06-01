using UnityEngine;
using Unity.Netcode;

public class Button: NetworkBehaviour
{
    public DoorColor buttonColor;
    public Renderer buttonRenderer;

    private Door targetDoor;
    private NetworkVariable<DoorColor> syncedButtonColor = new NetworkVariable<DoorColor>(
        DoorColor.Red,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public override void OnNetworkSpawn()
    {
        syncedButtonColor.OnValueChanged += OnButtonColorChanged;
        buttonColor = syncedButtonColor.Value;
        ApplyColor();
    }

    public override void OnNetworkDespawn()
    {
        syncedButtonColor.OnValueChanged -= OnButtonColorChanged;
    }

    public void SetTargetDoor(Door door)
    {
        targetDoor = door;
    }

    public void SetButtonColor(DoorColor color)
    {
        buttonColor = color;

        if (IsServer)
            syncedButtonColor.Value = color;

        ApplyColor();
    }

    void OnButtonColorChanged(DoorColor oldValue, DoorColor newValue)
    {
        buttonColor = newValue;
        ApplyColor();
    }

    void ApplyColor()
    {
        if (!buttonRenderer) return;

        switch (buttonColor)
        {
            case DoorColor.Red:
                buttonRenderer.material.color = Color.red;
                break;

            case DoorColor.Blue:
                buttonRenderer.material.color = Color.blue;
                break;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        if (IsPlayerCollider(other) && targetDoor != null)
        {
            targetDoor.OpenDoorServerRpc();

            if (TryGetComponent<NetworkObject>(out var networkObject) && networkObject.IsSpawned)
                networkObject.Despawn();
            else
                Destroy(gameObject);
        }
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
}
