using System.Collections;
using System.Collections.Generic;
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
    public Color startTextColor = new Color(0.18f, 0.85f, 0.35f, 1f);

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

    [Header("Player Restrictions")]
    public GameObject locomotionSystem;
    public GameObject[] rayInteractors;

    [Header("Evaluation Screen")]
    public EvaluationResultUI evaluationResultUI;

    [Header("Session Settings")]
    public float sessionDuration = 180f;
    public int totalHazards = 10;

    // Tracking lists & counters
    private List<string> allSceneHazards = new List<string>();
    private List<string> foundHazards = new List<string>();
    private int wrongClicks = 0;

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

        if (countdownText != null)
        {
            originalCountdownColor = countdownText.color;
        }

        if (countdownAudioSource == null)
        {
            countdownAudioSource = GetComponent<AudioSource>();
        }

        // Auto-register all authentic hazards in the warehouse scene
        allSceneHazards.Clear();
        HazardFeedback[] hazards = FindObjectsByType<HazardFeedback>(FindObjectsSortMode.None);
        foreach (var h in hazards)
        {
            if (h.isHazard)
            {
                string displayName = string.IsNullOrWhiteSpace(h.hazardDisplayName) ? h.gameObject.name : h.hazardDisplayName;
                if (!allSceneHazards.Contains(displayName))
                {
                    allSceneHazards.Add(displayName);
                }
            }
        }

        if (allSceneHazards.Count > 0)
        {
            totalHazards = allSceneHazards.Count;
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

        // 1. Lock locomotion and ray interactors during countdown
        SetPlayerInputState(false);

        // 2. Setup initial UI states & ensure original yellow color
        if (countdownRoot != null) countdownRoot.SetActive(true);
        if (playerHUDCanvas != null) playerHUDCanvas.SetActive(true);

        if (countdownText != null)
        {
            countdownText.color = originalCountdownColor;
        }

        // 3. Play tickkk.WAV track once from the start (5.068s)
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

        // 6. Play Start.wav audio cue
        if (countdownAudioSource != null && countdownStartClip != null)
        {
            countdownAudioSource.PlayOneShot(countdownStartClip);
            displayDuration = Mathf.Max(countdownStartClip.length, defaultStartDisplayDuration);
        }

        yield return new WaitForSeconds(displayDuration);

        // 7. Hide countdown screen, reset color for future attempts, and unlock player
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
        wrongClicks = 0;
        foundHazards.Clear();
        UpdateHUDDisplays();
    }

    /// <summary>
    /// Called by HazardFeedback.cs whenever a trainee identifies an authentic hazard.
    /// </summary>
    public void AddHazardFound(string hazardName)
    {
        if (!isSessionActive) return;

        if (!foundHazards.Contains(hazardName))
        {
            foundHazards.Add(hazardName);
            currentScore = foundHazards.Count;
            UpdateHUDDisplays();

            Debug.Log($"<color=green>[TrainingGameManager] Hazard Identified: {hazardName} ({currentScore}/{totalHazards})</color>");

            // Automatically finish session if all hazards are found
            if (currentScore >= totalHazards)
            {
                EndTrainingSession();
            }
        }
    }

    /// <summary>
    /// Fallback overload for legacy calls without name arguments.
    /// </summary>
    public void AddHazardFound()
    {
        AddHazardFound("Hazard Object");
    }

    /// <summary>
    /// Called by HazardFeedback.cs whenever a trainee clicks a non-hazard/safe prop.
    /// </summary>
    public void AddWrongClick()
    {
        if (!isSessionActive) return;

        wrongClicks++;
        Debug.Log($"<color=orange>[TrainingGameManager] Safe Object Clicked! Total Misclicks: {wrongClicks}</color>");
    }

    public void EndTrainingSession()
    {
        if (!isSessionActive && timeRemaining > 0 && currentScore < totalHazards) return;
        isSessionActive = false;

        float timeTaken = sessionDuration - timeRemaining;

        // Compile list of missed hazards
        List<string> missedHazards = new List<string>();
        foreach (string h in allSceneHazards)
        {
            if (!foundHazards.Contains(h))
            {
                missedHazards.Add(h);
            }
        }

        Debug.Log($"<color=yellow>[TrainingGameManager] Session Finished! Found: {currentScore}/{totalHazards}, Missed: {missedHazards.Count}, Wrong Clicks: {wrongClicks}, Time: {timeTaken:F1}s</color>");

        // 1. Hide in-game HUD
        if (playerHUDCanvas != null)
        {
            playerHUDCanvas.SetActive(false);
        }

        // 2. Display in-VR evaluation results screen
        if (evaluationResultUI != null)
        {
            evaluationResultUI.ShowResults(currentScore, totalHazards, timeTaken);
        }

        // 3. Dispatch session telemetry to Laravel backend
        LaravelApiBridge bridge = GetComponent<LaravelApiBridge>();
        if (bridge == null)
        {
            bridge = LaravelApiBridge.Instance;
        }

        if (bridge != null)
        {
            bridge.SendSessionResult(
                score: currentScore * 10,
                found: currentScore,
                missed: missedHazards.Count,
                wrong: wrongClicks,
                timeTaken: timeTaken,
                foundList: foundHazards,
                missedList: missedHazards
            );
        }
        else
        {
            Debug.LogWarning("[TrainingGameManager] LaravelApiBridge component not found. Telemetry not dispatched.");
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
            foreach (var r in rayInteractors)
            {
                if (r != null) r.SetActive(isEnabled);
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