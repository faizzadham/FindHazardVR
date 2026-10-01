using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class EvaluationResultUI : MonoBehaviour
{
    [Header("Panel Root")]
    [Tooltip("Drag ResultSummaryRoot here")]
    public GameObject resultSummaryRoot;

    [Header("UI Text Displays")]
    public TextMeshProUGUI percentageText;
    public TextMeshProUGUI badgeTitleText;
    public TextMeshProUGUI badgeSubtitleText;
    public TextMeshProUGUI hazardsFoundText;
    public TextMeshProUGUI missedHazardsText;
    public TextMeshProUGUI timeTakenText;

    [Header("Audio Feedback")]
    public AudioSource audioSource;
    public AudioClip perfectScoreSound;
    public AudioClip standardFinishSound;

    [Header("Scene Navigation")]
    public string mainMenuSceneName = "StartingPoint";

    public void ShowResults(int found, int total, float timeTaken)
    {
        Debug.Log($"<color=cyan>[EvaluationResultUI] Displaying results: {found}/{total}</color>");

        // 1. Ensure the parent canvas GameObject is active
        gameObject.SetActive(true);

        // 2. Turn ON ResultSummaryRoot so the card becomes visible
        if (resultSummaryRoot != null)
        {
            resultSummaryRoot.SetActive(true);
        }
        else
        {
            // Fallback: auto-find and activate child named ResultSummaryRoot
            Transform child = transform.Find("ResultSummaryRoot");
            if (child != null) child.gameObject.SetActive(true);
        }

        // 3. Score calculation
        float percentage = total > 0 ? ((float)found / total) * 100f : 0f;
        int missed = Mathf.Max(0, total - found);

        // 4. Update UI text displays
        if (percentageText != null) percentageText.text = $"{Mathf.RoundToInt(percentage)}%";
        if (hazardsFoundText != null) hazardsFoundText.text = $"{found}/{total}";
        if (missedHazardsText != null) missedHazardsText.text = missed.ToString();

        if (timeTakenText != null)
        {
            int minutes = Mathf.FloorToInt(timeTaken / 60);
            int seconds = Mathf.FloorToInt(timeTaken % 60);
            timeTakenText.text = string.Format("{0}:{1:00}", minutes, seconds);
        }

        // 5. Evaluation title and sound trigger
        if (found >= total && total > 0)
        {
            if (badgeTitleText != null) badgeTitleText.text = "Locked In";
            if (badgeSubtitleText != null) badgeSubtitleText.text = "Flawless inspection! All warehouse hazards identified.";
            PlayAudio(perfectScoreSound);
        }
        else if (percentage >= 80f)
        {
            if (badgeTitleText != null) badgeTitleText.text = "Great";
            if (badgeSubtitleText != null) badgeSubtitleText.text = $"You scored higher than {Mathf.RoundToInt(percentage)}% of trainees.";
            PlayAudio(standardFinishSound);
        }
        else
        {
            if (badgeTitleText != null) badgeTitleText.text = "Needs Review";
            if (badgeSubtitleText != null) badgeSubtitleText.text = "Significant hazards were missed. Retraining recommended.";
            PlayAudio(standardFinishSound);
        }
    }

    private void PlayAudio(AudioClip clip)
    {
        if (clip == null) return;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(clip);
        }
        else
        {
            Vector3 soundPos = Camera.main != null ? Camera.main.transform.position : transform.position;
            AudioSource.PlayClipAtPoint(clip, soundPos);
        }
    }

    public void OnReplayClicked()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void OnExitClicked()
    {
        SceneManager.LoadScene(mainMenuSceneName);
    }
}
