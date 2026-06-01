using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class DungeonUI : MonoBehaviour
{
    [Header("UI Elements")]
    [SerializeField] private TMP_Text progressText;
    [SerializeField] private TMP_Text levelText;
    [SerializeField] private Image progressFill;
    [SerializeField] private GameObject victoryPanel;
    [SerializeField] private GameObject countdownPanel;
    [SerializeField] private TMP_Text countdownText;
    
    [Header("Settings")]
    [SerializeField] private Color progressLowColor = Color.red;
    [SerializeField] private Color progressHighColor = Color.green;
    
    private DungeonProgressManager progressManager;
    private int currentLevel = 1;
    private float countdownTimer = 3f;
    private bool isCountingDown = false;
    
    void Start()
    {
        progressManager = FindObjectOfType<DungeonProgressManager>();
        
        if (progressManager != null)
        {
            progressManager.OnProgressUpdated += UpdateProgress;
            progressManager.OnLevelCompleted += ShowVictoryScreen;
            progressManager.OnLevelChanged += SetCurrentLevel;
            currentLevel = progressManager.GetCurrentLevel();
        }
        
        UpdateLevelDisplay();
        HideVictoryScreen();
    }
    
    void Update()
    {
        if (isCountingDown)
        {
            countdownTimer -= Time.deltaTime;
            countdownText.text = Mathf.CeilToInt(countdownTimer).ToString();
            
            if (countdownTimer <= 0f)
            {
                EndCountdown();
            }
        }
    }
    
    void UpdateProgress(int playersAtEnd, int totalPlayers)
    {
        if (progressText != null)
        {
            progressText.text = $"{playersAtEnd}/{totalPlayers}";
        }
        
        if (progressFill != null && totalPlayers > 0)
        {
            float progress = (float)playersAtEnd / totalPlayers;
            progressFill.fillAmount = progress;
            progressFill.color = Color.Lerp(progressLowColor, progressHighColor, progress);
        }
    }
    
    void ShowVictoryScreen()
    {
        if (victoryPanel != null)
        {
            victoryPanel.SetActive(true);
        }
        
        StartCountdown();
    }
    
    void HideVictoryScreen()
    {
        if (victoryPanel != null)
        {
            victoryPanel.SetActive(false);
        }
    }
    
    void StartCountdown()
    {
        isCountingDown = true;
        countdownTimer = 3f;
        
        if (countdownPanel != null)
        {
            countdownPanel.SetActive(true);
        }
    }
    
    void EndCountdown()
    {
        isCountingDown = false;
        
        if (countdownPanel != null)
        {
            countdownPanel.SetActive(false);
        }
        
        HideVictoryScreen();
        
        UpdateLevelDisplay();
    }

    void SetCurrentLevel(int level)
    {
        currentLevel = level;
        UpdateLevelDisplay();
    }
    
    void UpdateLevelDisplay()
    {
        if (levelText != null)
        {
            levelText.text = $"Level: {currentLevel}";
        }
    }
    
    void OnDestroy()
    {
        if (progressManager != null)
        {
            progressManager.OnProgressUpdated -= UpdateProgress;
            progressManager.OnLevelCompleted -= ShowVictoryScreen;
            progressManager.OnLevelChanged -= SetCurrentLevel;
        }
    }
}
