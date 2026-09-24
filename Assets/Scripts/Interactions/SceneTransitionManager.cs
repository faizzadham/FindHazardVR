using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class SceneTransitionManager : MonoBehaviour
{
    [Header("Input Reference")]
    public TMP_InputField traineeNameInput;

    [Header("Fade Reference")]
    [Tooltip("Drag FadeCanvas here to ensure the fade executes smoothly.")]
    public FadeScreen fadeScreen;

    [Header("Target Scene")]
    public string briefingSceneName = "SafetyBriefingRoom";

    public void StartBriefing()
    {
        string traineeName = "Anonymous_Worker";
        if (traineeNameInput != null && !string.IsNullOrWhiteSpace(traineeNameInput.text))
        {
            traineeName = traineeNameInput.text.Trim();
        }

        PlayerPrefs.SetString("CurrentTrainee", traineeName); 
        PlayerPrefs.Save(); 
        Debug.Log($"[SceneTransitionManager] Trainee '{traineeName}' saved. Starting fade to {briefingSceneName}...");

        // 1. Try direct inspector reference first
        if (fadeScreen != null)
        {
            fadeScreen.FadeToScene(briefingSceneName);
            return;
        }

        // 2. Try singleton fallback
        if (FadeScreen.Instance != null)
        {
            FadeScreen.Instance.FadeToScene(briefingSceneName);
            return;
        }

        // 3. Fallback if no fade screen exists in scene
        Debug.LogWarning("[SceneTransitionManager] No FadeScreen found! Loading scene immediately.");
        SceneManager.LoadScene(briefingSceneName);
    }
}
