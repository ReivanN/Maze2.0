using UnityEngine;
using Unity.Netcode;

public class Button: NetworkBehaviour
{
    public DoorColor buttonColor;
    public Renderer buttonRenderer;

    private Door targetDoor;

    private void Start()
    {
        ApplyColor();
    }

    public void SetTargetDoor(Door door)
    {
        targetDoor = door;
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

        if (other.CompareTag("Player") && targetDoor != null)
        {
            targetDoor.OpenDoorServerRpc();
            Destroy(this.gameObject);
        }
    }
}