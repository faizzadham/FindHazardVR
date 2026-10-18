using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class EvaluationResultUI : MonoBehaviour
{
    [Header("Main Panel Root")]
    public GameObject resultCanvasRoot;

    [Header("Left Card: Score Ring & Tier Text")]
    [Tooltip("Drag Ring_Fill here")]
    public Image scoreRingFill;
    [Tooltip("Drag Text_Percentage here")]
    public TextMeshProUGUI percentageText;
    [Tooltip("Drag Text_SlangTitle here")]
    public TextMeshProUGUI tierTitleText;
    [Tooltip("Drag Text_Description here")]
    public TextMeshProUGUI tierDescriptionText;

    [Header("Right Cards: 4 Telemetry Metrics")]
    public TextMeshProUGUI hazardFoundValueText;
    public TextMeshProUGUI hazardMissedValueText;
    public TextMeshProUGUI missClickValueText;
    public TextMeshProUGUI timeTakenValueText;

    [Header("Action Buttons")]
    public Button replayButton;
    public Button exitButton;
    public string startingSceneName = "StartingPoint";

    [Header("Audio Feedback")]
    [Tooltip("Audio source to play results completion sound")]
    public AudioSource resultAudioSource;
    [Tooltip("Sound effect played when the evaluation results appear")]
    public AudioClip completionSoundClip;

    [Header("Tier Colors (Matches Web Dashboard)")]
    public Color lockedInColor = new Color(0.125f, 0.788f, 0.47f, 1f);   // Emerald (#20C978)
    public Color greatColor = new Color(0.22f, 0.74f, 0.97f, 1f);        // Sky Blue (#38BDF8)
    public Color validEffortColor = new Color(0.96f, 0.62f, 0.04f, 1f);  // Amber (#F59E0B)
    public Color needsReviewColor = new Color(0.94f, 0.27f, 0.27f, 1f);  // Rose Red (#EF4444)

    [Header("Ring Animation")]
    public float ringAnimationDuration = 1.0f;

    private void Awake()
    {
        if (resultAudioSource == null)
        {
            resultAudioSource = GetComponent<AudioSource>();
        }

        if (replayButton != null)
        {
            replayButton.onClick.RemoveListener(OnReplayClicked);
            replayButton.onClick.AddListener(OnReplayClicked);
        }

        if (exitButton != null)
        {
            exitButton.onClick.RemoveListener(OnExitClicked);
            exitButton.onClick.AddListener(OnExitClicked);
        }

        if (resultCanvasRoot != null)
        {
            resultCanvasRoot.SetActive(false);
        }
    }

    /// <summary>
    /// Displays the evaluation screen using the 4-tier FindHazard Admin formula.
    /// </summary>
    public void ShowResults(int found, int total, int wrongClicks, float timeTaken)
    {
        if (resultCanvasRoot != null)
        {
            resultCanvasRoot.SetActive(true);
        }
        else
        {
            gameObject.SetActive(true);
        }

        // Play completion/time finished sound effect
        if (resultAudioSource != null && completionSoundClip != null)
        {
            resultAudioSource.PlayOneShot(completionSoundClip);
        }

        int missed = Mathf.Max(0, total - found);

        // 1. Calculate Clamped Score Percentage: Base Score - (Wrong Clicks * 5%)
        float baseScore = total > 0 ? ((float)found / total) * 100f : 0f;
        float penalty = wrongClicks * 5f;
        float finalPercentage = Mathf.Clamp(baseScore - penalty, 0f, 100f);
        int displayPercentage = Mathf.RoundToInt(finalPercentage);

        // 2. Determine 4-Tier Rating, Short Statement & Theme Color
        string tierTitle;
        string tierStatement;
        Color activeTierColor;

        if (finalPercentage >= 90f)
        {
            tierTitle = "Locked In";
            tierStatement = "Master Inspector — Flawless hazard recognition and trigger discipline. Qualified for Autonomous Airside Duty.";
            activeTierColor = lockedInColor;
        }
        else if (finalPercentage >= 80f)
        {
            tierTitle = "Great";
            tierStatement = "Proficient Inspector — Meets the safety benchmark with controlled trigger discipline. Qualified for Standard Duty.";
            activeTierColor = greatColor;
        }
        else if (finalPercentage >= 60f)
        {
            tierTitle = "Valid Effort";
            tierStatement = "Developing Inspector — Spotted general hazards, but missed key threats or lost points to misclicks. Refresher required.";
            activeTierColor = validEffortColor;
        }
        else
        {
            tierTitle = "Needs Review";
            tierStatement = "Critical Retest — Critical hazard oversights or excessive false clicking. Mandatory simulator retraining required.";
            activeTierColor = needsReviewColor;
        }

        // 3. Apply Left Card UI
        if (percentageText != null)
        {
            percentageText.text = $"{displayPercentage}%";
            percentageText.color = Color.white;
        }

        if (tierTitleText != null)
        {
            tierTitleText.text = tierTitle;
            tierTitleText.color = activeTierColor;
        }

        if (tierDescriptionText != null)
        {
            tierDescriptionText.text = tierStatement;
        }

        if (scoreRingFill != null)
        {
            scoreRingFill.color = activeTierColor;
            StopAllCoroutines();
            StartCoroutine(AnimateScoreRing(finalPercentage / 100f));
        }

        // 4. Apply Right Stat Cards UI
        if (hazardFoundValueText != null)
        {
            hazardFoundValueText.text = $"{found}/{total}";
            hazardFoundValueText.color = lockedInColor;
        }

        if (hazardMissedValueText != null)
        {
            hazardMissedValueText.text = missed.ToString();
            hazardMissedValueText.color = missed == 0 ? lockedInColor : needsReviewColor;
        }

        if (missClickValueText != null)
        {
            missClickValueText.text = wrongClicks.ToString();
            missClickValueText.color = wrongClicks == 0 ? lockedInColor : needsReviewColor;
        }

        if (timeTakenValueText != null)
        {
            int minutes = Mathf.FloorToInt(timeTaken / 60f);
            int seconds = Mathf.FloorToInt(timeTaken % 60f);
            timeTakenValueText.text = string.Format("{0}:{1:00}", minutes, seconds);
        }
    }

    /// <summary>
    /// Backwards-compatible overload if called without wrongClicks.
    /// </summary>
    public void ShowResults(int found, int total, float timeTaken)
    {
        ShowResults(found, total, 0, timeTaken);
    }

    private IEnumerator AnimateScoreRing(float targetFill)
    {
        float elapsed = 0f;
        scoreRingFill.fillAmount = 0f;

        while (elapsed < ringAnimationDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / ringAnimationDuration);
            float easedT = 1f - Mathf.Pow(1f - t, 3f);
            scoreRingFill.fillAmount = Mathf.Lerp(0f, targetFill, easedT);
            yield return null;
        }

        scoreRingFill.fillAmount = targetFill;
    }

    public void OnReplayClicked()
    {
        Scene currentScene = SceneManager.GetActiveScene();
        SceneManager.LoadScene(currentScene.name);
    }

    public void OnExitClicked()
    {
        SceneManager.LoadScene(startingSceneName);
    }
}