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
    public Color startTextColor = new Color(0.18f, 0.85f, 0.35f, 1f);

    [Header("Countdown Audio Synchronization")]
    public AudioSource countdownAudioSource;
    public AudioClip countdownAudioTrack;
    public AudioClip countdownStartClip;
    public float beatInterval = 1.014f;
    public float initialDelay = 0.0f;
    public float defaultStartDisplayDuration = 1.25f;

    [Header("Player Restrictions")]
    public GameObject locomotionSystem;
    public GameObject[] rayInteractors;

    [Header("Evaluation Screen")]
    public EvaluationResultUI evaluationResultUI;

    [Header("Session Settings")]
    public float sessionDuration = 180f;
    public int totalHazards = 10;

    // Lists tracking found and all scene hazards
    private List<string> allSceneHazards = new List<string>();
    private List<string> foundHazards = new List<string>();

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

        // Auto-register all active hazards in the warehouse
        HazardFeedback[] hazards = FindObjectsByType<HazardFeedback>(FindObjectsSortMode.None);
        foreach (var h in hazards)
        {
            if (h.isHazard)
            {
                allSceneHazards.Add(string.IsNullOrEmpty(h.hazardDisplayName) ? h.gameObject.name : h.hazardDisplayName);
            }
        }
        totalHazards = allSceneHazards.Count;
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
        SetPlayerInputState(false);

        if (countdownRoot != null) countdownRoot.SetActive(true);
        if (playerHUDCanvas != null) playerHUDCanvas.SetActive(true);
        if (countdownText != null) countdownText.color = originalCountdownColor;

        if (countdownAudioSource != null && countdownAudioTrack != null)
        {
            countdownAudioSource.PlayOneShot(countdownAudioTrack);
        }

        if (initialDelay > 0f) yield return new WaitForSeconds(initialDelay);

        int currentCount = countdownSeconds;
        while (currentCount > 0)
        {
            if (countdownText != null) countdownText.text = currentCount.ToString();
            yield return new WaitForSeconds(beatInterval);
            currentCount--;
        }

        if (countdownText != null)
        {
            countdownText.color = startTextColor;
            countdownText.text = "START!";
        }

        float displayDuration = defaultStartDisplayDuration;
        if (countdownAudioSource != null && countdownStartClip != null)
        {
            countdownAudioSource.PlayOneShot(countdownStartClip);
            displayDuration = Mathf.Max(countdownStartClip.length, defaultStartDisplayDuration);
        }

        yield return new WaitForSeconds(displayDuration);

        if (countdownRoot != null) countdownRoot.SetActive(false);
        if (countdownText != null) countdownText.color = originalCountdownColor;

        SetPlayerInputState(true);
        BeginSession();
    }

    private void BeginSession()
    {
        isSessionActive = true;
        timeRemaining = sessionDuration;
        currentScore = 0;
        foundHazards.Clear();
        UpdateHUDDisplays();
    }

    public void AddHazardFound(string hazardName)
    {
        if (!isSessionActive) return;

        if (!foundHazards.Contains(hazardName))
        {
            foundHazards.Add(hazardName);
            currentScore = foundHazards.Count;
            UpdateHUDDisplays();

            if (currentScore >= totalHazards)
            {
                EndTrainingSession();
            }
        }
    }

    public void EndTrainingSession()
    {
        if (!isSessionActive && timeRemaining > 0 && currentScore < totalHazards) return;
        isSessionActive = false;

        float timeTaken = sessionDuration - timeRemaining;

        // Compute missed hazards list
        List<string> missedHazards = new List<string>();
        foreach (string h in allSceneHazards)
        {
            if (!foundHazards.Contains(h))
            {
                missedHazards.Add(h);
            }
        }

        if (playerHUDCanvas != null) playerHUDCanvas.SetActive(false);
        if (evaluationResultUI != null) evaluationResultUI.ShowResults(currentScore, totalHazards, timeTaken);

        // Send results with found and missed hazard lists to Laravel
        LaravelApiBridge bridge = GetComponent<LaravelApiBridge>();
        if (bridge != null)
        {
            bridge.SendSessionResult(currentScore * 10, currentScore, missedHazards.Count, timeTaken, foundHazards, missedHazards);
        }
    }

    private void SetPlayerInputState(bool isEnabled)
    {
        if (locomotionSystem != null) locomotionSystem.SetActive(isEnabled);
        if (rayInteractors != null)
        {
            foreach (var r in rayInteractors) if (r != null) r.SetActive(isEnabled);
        }
    }

    private void UpdateHUDDisplays()
    {
        if (scoreText != null) scoreText.text = $"{currentScore}/{totalHazards}";
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