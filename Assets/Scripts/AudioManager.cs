using UnityEngine;

/// <summary>
/// Plays the music and the sound effects from Resources/AudioLibrary.
/// Created on first use and kept between scenes. Every client plays its own sounds:
/// gameplay scripts call it from code that already runs on all clients (NetworkVariable callbacks, ClientRpcs).
/// </summary>
public class AudioManager : MonoBehaviour
{
    private const string LibraryResourcePath = "AudioLibrary";
    private const string MusicVolumePrefKey = "Maze.MusicVolume";
    private const string SfxVolumePrefKey = "Maze.SfxVolume";
    private const float MusicFadeDuration = 1.2f;
    private const float SpatialMaxDistance = 40f;

    private static AudioManager instance;
    private static bool quitting;

    private AudioLibrary library;
    private readonly AudioSource[] musicSources = new AudioSource[2];
    private readonly float[] musicFades = new float[2];
    private int activeMusicSource;
    private AudioSource sfxSource;

    private float musicVolume = 0.75f;
    private float sfxVolume = 0.8f;

    private bool inGameplay;
    private int currentLevel = 1;
    private bool darknessMusic;

    private static AudioManager Instance
    {
        get
        {
            if (instance == null && !quitting)
            {
                GameObject managerObject = new GameObject("Audio Manager");
                DontDestroyOnLoad(managerObject);
                instance = managerObject.AddComponent<AudioManager>();
            }

            return instance;
        }
    }

    public static float MusicVolume
    {
        get => Instance != null ? Instance.musicVolume : 0f;
        set
        {
            if (Instance == null)
                return;

            Instance.musicVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(MusicVolumePrefKey, Instance.musicVolume);
            PlayerPrefs.Save();
        }
    }

    public static float SfxVolume
    {
        get => Instance != null ? Instance.sfxVolume : 0f;
        set
        {
            if (Instance == null)
                return;

            Instance.sfxVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(SfxVolumePrefKey, Instance.sfxVolume);
            PlayerPrefs.Save();
        }
    }

    /// <summary>Non-positional sound, heard the same by the local player wherever they are.</summary>
    public static void Play(Sfx id)
    {
        AudioManager manager = Instance;
        if (manager == null || !manager.TryGetSound(id, out AudioClip clip, out float volume))
            return;

        manager.sfxSource.PlayOneShot(clip, volume * manager.sfxVolume);
    }

    /// <summary>3D sound at a point in the level, quieter the farther the local player is.</summary>
    public static void PlayAt(Sfx id, Vector3 position, float maxDistance = SpatialMaxDistance)
    {
        AudioManager manager = Instance;
        if (manager == null || !manager.TryGetSound(id, out AudioClip clip, out float volume))
            return;

        PlayClipAt(clip, position, volume, maxDistance);
    }

    /// <summary>Replacement for AudioSource.PlayClipAtPoint that respects the sound volume setting.</summary>
    public static void PlayClipAt(AudioClip clip, Vector3 position, float volume, float maxDistance = SpatialMaxDistance)
    {
        AudioManager manager = Instance;
        if (manager == null || clip == null)
            return;

        GameObject soundObject = new GameObject("One Shot Audio");
        soundObject.transform.position = position;

        AudioSource source = soundObject.AddComponent<AudioSource>();
        source.clip = clip;
        source.volume = volume * manager.sfxVolume;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2f;
        source.maxDistance = maxDistance;
        source.Play();

        Destroy(soundObject, clip.length + 0.1f);
    }

    public static void EnterMenu()
    {
        AudioManager manager = Instance;
        if (manager == null)
            return;

        manager.inGameplay = false;
        manager.darknessMusic = false;
        manager.RefreshMusic();
    }

    public static void EnterGameplay()
    {
        AudioManager manager = Instance;
        if (manager == null)
            return;

        manager.inGameplay = true;
        manager.RefreshMusic();
    }

    public static void SetLevel(int level)
    {
        AudioManager manager = Instance;
        if (manager == null)
            return;

        manager.currentLevel = level;
        manager.RefreshMusic();
    }

    public static void SetDarknessMusic(bool enabled)
    {
        AudioManager manager = Instance;
        if (manager == null)
            return;

        manager.darknessMusic = enabled;
        manager.RefreshMusic();
    }

    private void Awake()
    {
        library = Resources.Load<AudioLibrary>(LibraryResourcePath);
        if (library == null)
            Debug.LogWarning($"AudioManager: Resources/{LibraryResourcePath} not found, the game will be silent.");

        musicVolume = PlayerPrefs.GetFloat(MusicVolumePrefKey, musicVolume);
        sfxVolume = PlayerPrefs.GetFloat(SfxVolumePrefKey, sfxVolume);

        for (int i = 0; i < musicSources.Length; i++)
        {
            AudioSource source = gameObject.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            musicSources[i] = source;
        }

        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        sfxSource.spatialBlend = 0f;
    }

    private void OnApplicationQuit()
    {
        quitting = true;
    }

    private void Update()
    {
        // Crossfade: the active track fades in, the previous one fades out and stops.
        // Unscaled time keeps the music fading while the game is paused.
        float step = Time.unscaledDeltaTime / MusicFadeDuration;
        float baseVolume = musicVolume * (library != null ? library.MusicVolume : 1f);

        for (int i = 0; i < musicSources.Length; i++)
        {
            AudioSource source = musicSources[i];
            float target = i == activeMusicSource && source.clip != null ? 1f : 0f;

            musicFades[i] = Mathf.MoveTowards(musicFades[i], target, step);
            source.volume = musicFades[i] * baseVolume;

            if (musicFades[i] <= 0f && source.isPlaying)
                source.Stop();
        }
    }

    private void RefreshMusic()
    {
        if (library == null)
            return;

        AudioClip clip;
        if (!inGameplay)
            clip = library.MenuMusic;
        else if (darknessMusic && library.DarknessMusic != null)
            clip = library.DarknessMusic;
        else
            clip = library.GetLevelMusic(currentLevel);

        PlayMusic(clip);
    }

    private void PlayMusic(AudioClip clip)
    {
        if (clip == null)
            return;

        AudioSource current = musicSources[activeMusicSource];
        if (current.clip == clip && current.isPlaying)
            return;

        activeMusicSource = 1 - activeMusicSource;

        AudioSource next = musicSources[activeMusicSource];
        next.clip = clip;
        next.Play();
    }

    private bool TryGetSound(Sfx id, out AudioClip clip, out float volume)
    {
        if (library != null)
            return library.TryGetSound(id, out clip, out volume);

        clip = null;
        volume = 0f;
        return false;
    }
}
