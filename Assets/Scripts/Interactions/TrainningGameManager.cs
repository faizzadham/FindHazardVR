using System.Collections;
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

    [Header("Countdown Screen")]
    [Tooltip("Parent panel of the countdown display")]
    public GameObject countdownRoot;

    [Tooltip("Large center countdown text component")]
    public TextMeshProUGUI countdownText;

    [Tooltip("Countdown duration in seconds before game starts")]
    public int countdownSeconds = 5;

    [Header("Player Restrictions During Countdown")]
    [Tooltip("Drag the Locomotion GameObject under XR Origin here to freeze movement")]
    public GameObject locomotionSystem;

    [Tooltip("Drag Left and Right Controller Ray Interactor GameObjects here to disable clicking")]
    public GameObject[] rayInteractors;

    [Header("Evaluation Screen")]
    public EvaluationResultUI evaluationResultUI;

    [Header("Session Settings")]
    [Tooltip("Total training duration in seconds (180s = 3:00)")]
    public float sessionDuration = 180f;

    [Tooltip("Total number of hazards placed in the warehouse")]
    public int totalHazards = 10;

    private float timeRemaining;
    private int currentScore = 0;
    private bool isSessionActive = false; // Remains false until countdown finishes

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

        // Lock player movement and ray clicking, then begin 5s countdown
        StartCoroutine(StartCountdownRoutine());
    }

    private IEnumerator StartCountdownRoutine()
    {
        // 1. Lock locomotion and raycasts
        SetPlayerInputState(false);

        if (countdownRoot != null)
        {
            countdownRoot.SetActive(true);
        }

        // 2. Count down from 5 to 1
        int count = countdownSeconds;
        while (count > 0)
        {
            if (countdownText != null)
            {
                countdownText.text = count.ToString();
            }
            yield return new WaitForSeconds(1f);
            count--;
        }

        // 3. Display Start cue
        if (countdownText != null)
        {
            countdownText.text = "<color=#2ECC71>START!</color>";
        }
        yield return new WaitForSeconds(0.6f);

        // 4. Hide countdown panel and unlock player capabilities
        if (countdownRoot != null)
        {
            countdownRoot.SetActive(false);
        }

        SetPlayerInputState(true);
        isSessionActive = true;
        Debug.Log("[TrainingGameManager] Countdown complete. Session timer and player controls unlocked.");
    }

    private void SetPlayerInputState(bool isEnabled)
    {
        // Freeze/unfreeze continuous movement and snap turn
        if (locomotionSystem != null)
        {
            locomotionSystem.SetActive(isEnabled);
        }

        // Disable/enable laser rays so hazards and non-hazards cannot be hovered or clicked
        if (rayInteractors != null)
        {
            foreach (GameObject ray in rayInteractors)
            {
                if (ray != null)
                {
                    ray.SetActive(isEnabled);
                }
            }
        }
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

        if (playerHUDCanvas != null)
        {
            playerHUDCanvas.SetActive(false);
        }

        if (evaluationResultUI != null)
        {
            evaluationResultUI.ShowResults(currentScore, totalHazards, timeTaken);
        }
    }
}