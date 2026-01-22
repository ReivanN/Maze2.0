using Unity.Netcode;
using UnityEngine;

public class NetworkPlayerColor : NetworkBehaviour
{
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private Color playerOneColor = Color.white;
    [SerializeField] private Color playerTwoColor = Color.red;

    private NetworkVariable<Color> syncedColor =
        new NetworkVariable<Color>(
            Color.white,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            if (OwnerClientId == 0)
                syncedColor.Value = playerOneColor;
            else
                syncedColor.Value = playerTwoColor;
        }

        ApplyColor(syncedColor.Value);
        syncedColor.OnValueChanged += OnColorChanged;
    }

    private void OnColorChanged(Color oldColor, Color newColor)
    {
        ApplyColor(newColor);
    }

    private void ApplyColor(Color color)
    {
        if (targetRenderer != null)
        {
            // Пробегаем по всем материалам и меняем цвет
            Material[] mats = targetRenderer.materials; // инстанс материалов
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i].color = color;
            }
        }
    }
}