using UnityEngine;

public enum Sfx
{
    UiClick,
    UiHover,
    UiError,
    PlayerJoined,
    DoorOpen,
    LaserSwitch,
    LaserHit,
    PlatformShake,
    PlatformFall,
    MutatorIntro,
    WindWarning,
    LevelComplete,
    PressurePlate,
    FallRespawn,
    GameOver,
    SpikesOut,
    SpikeHit
}

/// <summary>
/// Every music track and sound effect of the game. Loaded by AudioManager from Resources/AudioLibrary,
/// so a sound is replaced by dragging another clip into this asset, without touching the code.
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Maze/Audio Library")]
public class AudioLibrary : ScriptableObject
{
    [System.Serializable]
    public class SfxEntry
    {
        public Sfx id;
        public AudioClip clip;
        [Range(0f, 1f)] public float volume = 1f;
    }

    [Header("Music")]
    [SerializeField] private AudioClip menuMusic;
    [Tooltip("One track per level, repeated when there are more levels than tracks.")]
    [SerializeField] private AudioClip[] levelMusic;
    [Tooltip("Plays instead of the level track while the Fog & Darkness mutator is active.")]
    [SerializeField] private AudioClip darknessMusic;
    [Range(0f, 1f)] [SerializeField] private float musicVolume = 0.45f;

    [Header("Sound effects")]
    [SerializeField] private SfxEntry[] sounds;

    public AudioClip MenuMusic => menuMusic;
    public AudioClip DarknessMusic => darknessMusic;
    public float MusicVolume => musicVolume;

    public AudioClip GetLevelMusic(int level)
    {
        if (levelMusic == null || levelMusic.Length == 0)
            return menuMusic;

        int index = Mathf.Max(0, level - 1) % levelMusic.Length;
        return levelMusic[index];
    }

    public bool TryGetSound(Sfx id, out AudioClip clip, out float volume)
    {
        if (sounds != null)
        {
            foreach (SfxEntry entry in sounds)
            {
                if (entry.id == id && entry.clip != null)
                {
                    clip = entry.clip;
                    volume = entry.volume;
                    return true;
                }
            }
        }

        clip = null;
        volume = 0f;
        return false;
    }
}
