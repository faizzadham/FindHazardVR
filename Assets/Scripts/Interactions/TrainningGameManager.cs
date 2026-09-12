using UnityEngine;
using TMPro;

public class TrainingGameManager : MonoBehaviour
{
    public static TrainingGameManager Instance { get; private set; }

    [Header("UI Displays")]
    [Tooltip("The parent GameObject or Canvas of the live gameplay HUD to hide at session end.")]
    public GameObject playerHUDCanvas;

    [Tooltip("Text component displaying the digital clock (e.g., 3:00)")]
    public TextMeshProUGUI timerText;

    [Tooltip("Text component displaying the score ratio (e.g., 0/10)")]
    public TextMeshProUGUI scoreText;

    [Header("Evaluation Screen")]
    public EvaluationResultUI evaluationResultUI;

    [Header("Session Settings")]
    [Tooltip("Total training duration in seconds (180s = 3:00)")]
    public float sessionDuration = 180f;

    [Tooltip("Total number of hazards placed in the warehouse")]
    public int totalHazards = 10;

    private float timeRemaining;
    private int currentScore = 0;
    private bool isSessionActive = true;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        timeRemaining = sessionDuration;
        UpdateScoreDisplay();
        UpdateTimerDisplay();
    }

    private void Update()
    {
        if (!isSessionActive) return;

        if (timeRemaining > 0)
        {
            timeRemaining -= Time.deltaTime;
            UpdateTimerDisplay();
        }
        else
        {
            timeRemaining = 0;
            EndTrainingSession();
        }
    }

    public void AddHazardFound()
    {
        if (!isSessionActive) return;

        currentScore++;
        UpdateScoreDisplay();

        if (currentScore >= totalHazards)
        {
            Debug.Log("<color=green>[TrainingGameManager] All hazards identified!</color>");
            EndTrainingSession();
        }
    }

    private void UpdateTimerDisplay()
    {
        if (timerText == null) return;
        int minutes = Mathf.FloorToInt(timeRemaining / 60);
        int seconds = Mathf.FloorToInt(timeRemaining % 60);
        timerText.text = string.Format("{0}:{1:00}", minutes, seconds);
    }

    private void UpdateScoreDisplay()
    {
        if (scoreText == null) return;
        scoreText.text = $"{currentScore}/{totalHazards}";
    }

    private void EndTrainingSession()
    {
        if (!isSessionActive && currentScore < totalHazards && timeRemaining > 0) return;
        isSessionActive = false;

        float timeTaken = sessionDuration - timeRemaining;
        Debug.Log($"[TrainingGameManager] Session ended. Score: {currentScore}/{totalHazards}, Time: {timeTaken:F1}s");

        // Safely hide ONLY the HUD, leaving Main Camera and XR Origin running
        if (playerHUDCanvas != null)
        {
            playerHUDCanvas.SetActive(false);
        }

        // Trigger Evaluation Results Panel
        if (evaluationResultUI != null)
        {
            evaluationResultUI.ShowResults(currentScore, totalHazards, timeTaken);
        }
        else
        {
            Debug.LogError("[TrainingGameManager] 'Evaluation Result UI' reference is missing in the Inspector!");
        }
    }
}
