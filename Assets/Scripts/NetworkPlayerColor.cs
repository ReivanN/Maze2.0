using Unity.Netcode;
using UnityEngine;

public class NetworkPlayerColor : NetworkBehaviour
{
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private Color playerOneColor = Color.white;
    [SerializeField] private Color playerTwoColor = Color.red;

    /// <summary>Raised whenever the player's color is applied, for visuals that are not <see cref="targetRenderer"/>.</summary>
    public event System.Action<Color> ColorApplied;

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

        // The color picked in the main menu replaces the default one. Auto keeps the default.
        if (IsOwner && PlayerColorChoice.SelectedIndex != PlayerColorChoice.AutoIndex)
            SubmitColorRpc(PlayerColorChoice.SelectedIndex);
    }

    public override void OnNetworkDespawn()
    {
        syncedColor.OnValueChanged -= OnColorChanged;
    }

    // Sends the palette index, not the color itself, so the server only accepts colors from the palette.
    [Rpc(SendTo.Server)]
    private void SubmitColorRpc(int paletteIndex, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        if (PlayerColorChoice.TryGetColor(paletteIndex, out Color color))
            syncedColor.Value = color;
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

        ColorApplied?.Invoke(color);
    }
}