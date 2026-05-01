using Unity.Services.Vivox;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using System.Threading.Tasks;

public class VoiceManager : MonoBehaviour
{
    [SerializeField] private Toggle microphoneToggle;
    [SerializeField] private Key toggleMicrophoneKey = Key.T;

    private bool isStarting;
    private bool isStarted;
    private bool isMicrophoneMuted;

    public async Task StartVoice(string playerId, string roomId)
    {
        if (isStarting || isStarted)
            return;

        isStarting = true;

        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();

        try
        {
            await InitializeVivox();
            await Login(playerId);

            VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
            VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;

            await JoinChannel(roomId);
            isStarted = true;
            SetMicrophoneMuted(isMicrophoneMuted);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Voice chat failed: {e.Message}");
        }
        finally
        {
            isStarting = false;
        }
    }

    void Awake()
    {
        if (microphoneToggle != null)
        {
            microphoneToggle.SetIsOnWithoutNotify(!isMicrophoneMuted);
            microphoneToggle.onValueChanged.AddListener(OnMicrophoneToggleChanged);
        }
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current[toggleMicrophoneKey].wasPressedThisFrame)
            ToggleMicrophone();
    }

    async Task InitializeVivox()
    {
        await VivoxService.Instance.InitializeAsync();
    }

    async Task Login(string playerName)
    {
        await VivoxService.Instance.LoginAsync(new LoginOptions
        {
            DisplayName = playerName
        });
    }

    async Task JoinChannel(string channelName)
    {
        await VivoxService.Instance.JoinGroupChannelAsync(channelName, ChatCapability.AudioOnly);
    }

    public void ToggleMicrophone()
    {
        SetMicrophoneMuted(!isMicrophoneMuted);
    }

    public void SetMicrophoneEnabled(bool isEnabled)
    {
        SetMicrophoneMuted(!isEnabled);
    }

    public void SetMicrophoneMuted(bool muted)
    {
        isMicrophoneMuted = muted;

        if (microphoneToggle != null)
            microphoneToggle.SetIsOnWithoutNotify(!muted);

        if (!isStarted)
            return;

        if (muted)
            VivoxService.Instance.MuteInputDevice();
        else
            VivoxService.Instance.UnmuteInputDevice();
    }

    void OnMicrophoneToggleChanged(bool isEnabled)
    {
        SetMicrophoneEnabled(isEnabled);
    }

    void OnParticipantAdded(VivoxParticipant participant)
    {
        Debug.Log($"{participant.DisplayName} joined");
    }

    void OnDestroy()
    {
        if (microphoneToggle != null)
            microphoneToggle.onValueChanged.RemoveListener(OnMicrophoneToggleChanged);

        VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
    }
}
