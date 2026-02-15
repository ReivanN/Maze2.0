using UnityEngine;
using Unity.Netcode;

public class Door : NetworkBehaviour
{
    public DoorColor doorColor;
    public Renderer doorRenderer;

    private NetworkVariable<bool> isOpen = new NetworkVariable<bool>(false);

    private void Start()
    {
        ApplyColor();
        isOpen.OnValueChanged += OnDoorStateChanged;
    }

    void ApplyColor()
    {
        if (!doorRenderer) return;

        switch (doorColor)
        {
            case DoorColor.Red:
                doorRenderer.material.color = Color.red;
                break;

            case DoorColor.Blue:
                doorRenderer.material.color = Color.blue;
                break;
        }
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