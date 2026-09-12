using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public class EvaluationResultUI : MonoBehaviour
{
    [Header("UI Root")]
    [Tooltip("The parent GameObject of the entire Result Summary Canvas/Panel")]
    public GameObject resultsRootPanel;

    [Header("Left Card References")]
    public TextMeshProUGUI percentageText;
    public TextMeshProUGUI slangTitleText;
    public TextMeshProUGUI feedbackDescText;
    public Button exitButton;
    public Button replayButton;

    [Header("Right Column References")]
    public TextMeshProUGUI hazardFoundText;
    public TextMeshProUGUI missedHazardText;
    public TextMeshProUGUI timeTakenText;

    [Header("Scene Navigation")]
    public string startingPointSceneName = "StartingPoint";

    private void Awake()
    {
        // Ensure panel starts hidden during gameplay
        if (resultsRootPanel != null)
            resultsRootPanel.SetActive(false);

        // Assign button click listeners
        if (exitButton != null)
            exitButton.onClick.AddListener(OnExitClicked);

        if (replayButton != null)
            replayButton.onClick.AddListener(OnReplayClicked);
    }

    public void ShowResults(int hazardsFound, int totalHazards, float timeTakenSeconds)
    {
        if (resultsRootPanel != null)
            resultsRootPanel.SetActive(true);

        // Calculations
        int missed = Mathf.Max(0, totalHazards - hazardsFound);
        int percentage = totalHazards > 0 ? Mathf.RoundToInt(((float)hazardsFound / totalHazards) * 100f) : 0;

        // Format Time (mm:ss)
        int minutes = Mathf.FloorToInt(timeTakenSeconds / 60);
        int seconds = Mathf.FloorToInt(timeTakenSeconds % 60);
        string formattedTime = string.Format("{0}:{1:00}", minutes, seconds);

        // Populate Right Column
        if (hazardFoundText != null)
            hazardFoundText.text = $"{hazardsFound}/{totalHazards}";

        if (missedHazardText != null)
            missedHazardText.text = missed.ToString();

        if (timeTakenText != null)
            timeTakenText.text = formattedTime;

        // Populate Left Column
        if (percentageText != null)
            percentageText.text = $"{percentage}%";

        SetSlangStatement(percentage, hazardsFound, totalHazards);
    }

    private void SetSlangStatement(int percentage, int found, int total)
    {
        if (percentage >= 100)
        {
            slangTitleText.text = "Locked In";
            feedbackDescText.text = "Absolute W. Zero violations bypassed your radar. You're the safety GOAT.";
        }
        else if (percentage >= 80)
        {
            slangTitleText.text = "Great";
            feedbackDescText.text = "You're definitely cooking! You scored higher than 80% of trainees on this floor.";
        }
        else if (percentage >= 50)
        {
            slangTitleText.text = "Valid Effort";
            feedbackDescText.text = "Not bad, but a few hazards caught you lacking. Run it back to stay sharp.";
        }
        else
        {
            slangTitleText.text = "Cooked";
            feedbackDescText.text = "Major skill issue. Safety hazards running wild on your shift. Hit replay immediately.";
        }
    }

    public void OnReplayClicked()
    {
        // Reload current warehouse scene to reset the session
        Scene activeScene = SceneManager.GetActiveScene();
        SceneManager.LoadScene(activeScene.name);
    }

    public void OnExitClicked()
    {
        // Return to username registration/starting point scene
        SceneManager.LoadScene(startingPointSceneName);
    }
}
