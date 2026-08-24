using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class SceneTransitionManager : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("The TMP Input Field where the trainee types their username.")]
    public TMP_InputField usernameInputField;

    [Tooltip("The Canvas Group on your FadeCanvas in front of the Main Camera.")]
    public CanvasGroup fadeCanvasGroup;

    [Header("Transition Settings")]
    [Tooltip("Duration of the fade in seconds.")]
    public float fadeDuration = 1.0f;

    [Tooltip("Name of the scene to load.")]
    public string targetSceneName = "Warehouse_Environment"; // or your next scene name

    private bool isTransitioning = false;

    private void Start()
    {
        // Smoothly fade in from black at the start of the scene
        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.alpha = 1f;
            StartCoroutine(FadeRoutine(1f, 0f, null));
        }
    }

    /// <summary>
    /// Hook this public method to your Submit / Continue Button OnClick() event.
    /// </summary>
    public void SubmitUsernameAndTransition()
    {
        if (isTransitioning) return;

        // Check if the username is entered
        string traineeName = usernameInputField != null ? usernameInputField.text.Trim() : "";

        if (string.IsNullOrEmpty(traineeName))
        {
            Debug.LogWarning("[SceneTransition] Please enter a valid username before proceeding!");
            return;
        }

        // Store the username across scenes
        PlayerPrefs.SetString("CurrentTrainee", traineeName);
        PlayerPrefs.Save();
        Debug.Log($"[SceneTransition] Registered Trainee: {traineeName}. Starting fade transition...");

        isTransitioning = true;
        StartCoroutine(FadeRoutine(0f, 1f, targetSceneName));
    }

    private IEnumerator FadeRoutine(float startAlpha, float endAlpha, string sceneToLoad)
    {
        float elapsed = 0f;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, endAlpha, elapsed / fadeDuration);
            yield return null;
        }

        fadeCanvasGroup.alpha = endAlpha;

        // Once fully faded to black, load the next scene asynchronously
        if (!string.IsNullOrEmpty(sceneToLoad))
        {
            SceneManager.LoadSceneAsync(sceneToLoad);
        }
    }
}
