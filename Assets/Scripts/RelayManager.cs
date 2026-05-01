using TMPro;
using Unity.Services.Core;
using Unity.Services.Authentication;
using UnityEngine;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

public class RelayManager : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI textMeshProUGUI;
    [SerializeField] private TMP_InputField _inputField;
    
    [SerializeField] private int maxPlayers = 4;
    [SerializeField] private VoiceManager voiceManager;

    private async void Start()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();

        await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    public async void StartRelay()
    {
        try
        {
            string joinCode = await StartHostWithRelay(maxPlayers);
            textMeshProUGUI.text = $"{joinCode}";

            if (!string.IsNullOrEmpty(joinCode))
                await StartVoiceChat(joinCode);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"Ошибка создания Relay: {e.Message}");
            textMeshProUGUI.text = "Ошибка создания комнаты";
        }
    }

    public async void JoinRelay()
    {
        try
        {
            string joinCode = _inputField.text.Trim().ToUpper();
            bool success = await StartClientWithRelay(joinCode);
            textMeshProUGUI.text = success ? "Подключение..." : "Ошибка подключения";

            if (success)
                await StartVoiceChat(joinCode);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"Ошибка подключения к Relay: {e.Message}");
            textMeshProUGUI.text = "Неверный код или ошибка подключения";
        }
    }

    public async Task<string> StartHostWithRelay(int maxConnections)
    {
        // Создание выделения (allocation) на сервере Relay
        Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
        
        // Получение кода для присоединения
        string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
        
        // Настройка транспорта Unity для использования Relay
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetHostRelayData(
            allocation.RelayServer.IpV4,
            (ushort)allocation.RelayServer.Port,
            allocation.AllocationIdBytes,
            allocation.Key,
            allocation.ConnectionData
        );
        
        // Запуск хоста
        bool started = NetworkManager.Singleton.StartHost();
        return started ? joinCode : null;
    }

    public async Task<bool> StartClientWithRelay(string joinCode)
    {
        if (string.IsNullOrEmpty(joinCode))
        {
            Debug.LogError("Join code is empty");
            return false;
        }

        // Присоединение к выделению по коду
        JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
        
        // Настройка транспорта Unity для клиента
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetClientRelayData(
            allocation.RelayServer.IpV4,
            (ushort)allocation.RelayServer.Port,
            allocation.AllocationIdBytes,
            allocation.Key,
            allocation.ConnectionData,
            allocation.HostConnectionData
        );
        
        // Запуск клиента
        return NetworkManager.Singleton.StartClient();
    }

    private async Task StartVoiceChat(string joinCode)
    {
        if (voiceManager == null)
            voiceManager = FindAnyObjectByType<VoiceManager>();

        if (voiceManager == null)
        {
            Debug.LogWarning("VoiceManager не найден в сцене");
            return;
        }

        string authPlayerId = AuthenticationService.Instance.PlayerId;
        string shortPlayerId = authPlayerId.Length > 8 ? authPlayerId[..8] : authPlayerId;
        string playerName = $"Player_{shortPlayerId}";
        string roomName = $"Maze_{joinCode}";

        await voiceManager.StartVoice(playerName, roomName);
    }
}
