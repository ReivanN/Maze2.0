using Unity.Services.Vivox;
using Unity.Services.Core;
using UnityEngine;
using System.Threading.Tasks;

public class VoiceManager : MonoBehaviour
{
    async void Start()
    {
        await UnityServices.InitializeAsync();
        await InitializeVivox();
        await Login("Player_" + Random.Range(0, 9999));
        VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
        await JoinChannel("GlobalChannel");
    }

    async void StartVoiceChannel()
    {
        await UnityServices.InitializeAsync();
        await InitializeVivox();
        await Login("Player_" + Random.Range(0, 9999));
        VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
        await JoinChannel("GlobalChannel");
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

    void OnParticipantAdded(VivoxParticipant participant)
    {
        Debug.Log($"{participant.DisplayName} joined");
    }

    void OnDestroy()
    {
        VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
    }
}