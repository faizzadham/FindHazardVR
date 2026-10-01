using System.Collections;
using UnityEngine;
using TMPro;

public class TrainingGameManager : MonoBehaviour
{
    public static TrainingGameManager Instance { get; private set; }

    [Header("UI Displays")]
    public GameObject playerHUDCanvas;
    public TextMeshProUGUI timerText;
    public TextMeshProUGUI scoreText;

    [Header("Countdown Screen")]
    public GameObject countdownRoot;
    public TextMeshProUGUI countdownText;
    public int countdownSeconds = 5;

    [Header("Countdown Visuals")]
    [Tooltip("Color applied to the START! text")]
    public Color startTextColor = new Color(0.18f, 0.85f, 0.35f, 1f); // Vibrant safety green

    [Header("Countdown Audio Synchronization")]
    [Tooltip("AudioSource component used to play countdown audio")]
    public AudioSource countdownAudioSource;

    [Tooltip("Drag tickkk.WAV here (5.068s duration)")]
    public AudioClip countdownAudioTrack;

    [Tooltip("Drag Start.wav here (plays right when START! text appears)")]
    public AudioClip countdownStartClip;

    [Tooltip("Interval between ticks: 5.068s / 5 beats ≈ 1.014s")]
    public float beatInterval = 1.014f;

    [Tooltip("Pre-roll offset if tickkk.WAV has initial silence before beat 1")]
    public float initialDelay = 0.0f;

    [Tooltip("Fallback display duration if no start audio clip is assigned")]
    public float defaultStartDisplayDuration = 1.25f;

    [Header("Player Restrictions During Countdown")]
    public GameObject locomotionSystem;
    public GameObject[] rayInteractors;

    [Header("Evaluation Screen")]
    public EvaluationResultUI evaluationResultUI;

    [Header("Session Settings")]
    public float sessionDuration = 180f;
    public int totalHazards = 8;

    private bool isSessionActive = false;
    private float timeRemaining;
    private int currentScore = 0;
    private Color originalCountdownColor = Color.yellow;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Cache the default yellow color of your countdown digits
        if (countdownText != null)
        {
            originalCountdownColor = countdownText.color;
        }

        if (countdownAudioSource == null)
        {
            countdownAudioSource = GetComponent<AudioSource>();
        }
    }

    private void Start()
    {
        StartCoroutine(StartCountdownRoutine());
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
            UpdateTimerDisplay();
            EndTrainingSession();
        }
    }

    private IEnumerator StartCountdownRoutine()
    {
        isSessionActive = false;

        // 1. Lock locomotion and ray interactors while counting down
        SetPlayerInputState(false);

        // 2. Setup initial UI states & ensure original yellow color
        if (countdownRoot != null) countdownRoot.SetActive(true);
        if (playerHUDCanvas != null) playerHUDCanvas.SetActive(true);

        if (countdownText != null)
        {
            countdownText.color = originalCountdownColor;
        }

        // 3. Play tickkk.WAV track once from the beginning (5.068s)
        if (countdownAudioSource != null && countdownAudioTrack != null)
        {
            countdownAudioSource.PlayOneShot(countdownAudioTrack);
        }

        if (initialDelay > 0f)
        {
            yield return new WaitForSeconds(initialDelay);
        }

        int currentCount = countdownSeconds;

        // 4. Update the numbers in sync with the beat interval (5, 4, 3, 2, 1 in Yellow)
        while (currentCount > 0)
        {
            if (countdownText != null)
            {
                countdownText.text = currentCount.ToString();
            }

            yield return new WaitForSeconds(beatInterval);
            currentCount--;
        }

        // 5. On the 6th beat: Turn text GREEN and display START!
        if (countdownText != null)
        {
            countdownText.color = startTextColor;
            countdownText.text = "START!";
        }

        float displayDuration = defaultStartDisplayDuration;

        // 6. Play Start.wav audio
        if (countdownAudioSource != null && countdownStartClip != null)
        {
            countdownAudioSource.PlayOneShot(countdownStartClip);
            displayDuration = Mathf.Max(countdownStartClip.length, defaultStartDisplayDuration);
        }

        yield return new WaitForSeconds(displayDuration);

        // 7. Hide countdown screen, reset color for replays, and unlock player
        if (countdownRoot != null)
        {
            countdownRoot.SetActive(false);
        }

        if (countdownText != null)
        {
            countdownText.color = originalCountdownColor;
        }

        SetPlayerInputState(true);
        BeginSession();
    }

    private void BeginSession()
    {
        isSessionActive = true;
        timeRemaining = sessionDuration;
        currentScore = 0;
        UpdateHUDDisplays();
    }

    /// <summary>
    /// Called by HazardFeedback.cs whenever a trainee identifies a hazard
    /// </summary>
    public void AddHazardFound()
    {
        if (!isSessionActive) return;

        currentScore++;
        UpdateHUDDisplays();

        Debug.Log($"<color=green>[TrainingGameManager] Hazard Found! Current score: {currentScore}/{totalHazards}</color>");

        if (currentScore >= totalHazards)
        {
            EndTrainingSession();
        }
    }

    public void EndTrainingSession()
    {
        if (!isSessionActive && timeRemaining > 0 && currentScore < totalHazards) return;
        isSessionActive = false;

        float timeTaken = sessionDuration - timeRemaining;
        int missed = Mathf.Max(0, totalHazards - currentScore);

        Debug.Log($"<color=yellow>[TrainingGameManager] Session Finished! Found: {currentScore}/{totalHazards}, Missed: {missed}, Time: {timeTaken:F1}s</color>");

        // 1. Hide active in-game HUD
        if (playerHUDCanvas != null)
        {
            playerHUDCanvas.SetActive(false);
        }

        // 2. Display the In-VR Evaluation Results Screen
        if (evaluationResultUI != null)
        {
            evaluationResultUI.ShowResults(currentScore, totalHazards, timeTaken);
        }

        // 3. Optional: Transmit to Laravel API Bridge if present
        LaravelApiBridge bridge = GetComponent<LaravelApiBridge>();
        if (bridge != null)
        {
            bridge.SendSessionResult(currentScore * 10, currentScore, missed, timeTaken);
        }
    }

    private void SetPlayerInputState(bool isEnabled)
    {
        if (locomotionSystem != null)
        {
            locomotionSystem.SetActive(isEnabled);
        }

        if (rayInteractors != null)
        {
            foreach (GameObject interactor in rayInteractors)
            {
                if (interactor != null) interactor.SetActive(isEnabled);
            }
        }
    }

    private void UpdateHUDDisplays()
    {
        if (scoreText != null)
        {
            scoreText.text = $"{currentScore}/{totalHazards}";
        }
        UpdateTimerDisplay();
    }

    private void UpdateTimerDisplay()
    {
        if (timerText != null)
        {
            int minutes = Mathf.FloorToInt(timeRemaining / 60);
            int seconds = Mathf.FloorToInt(timeRemaining % 60);
            timerText.text = string.Format("{0}:{1:00}", minutes, seconds);
        }
    }
}