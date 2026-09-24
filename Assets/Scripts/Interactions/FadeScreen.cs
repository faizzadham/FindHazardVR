using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class FadeScreen : MonoBehaviour
{
    public static FadeScreen Instance { get; private set; }

    [Header("Fade Configuration")]
    [Tooltip("Automatically fade from black to clear when the scene boots.")]
    public bool fadeOnStart = true;
    public float fadeDuration = 1.0f;

    [Header("UI Reference")]
    [Tooltip("The CanvasGroup on your FadeCanvas.")]
    public CanvasGroup canvasGroup;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (canvasGroup == null)
        {
            canvasGroup = GetComponent<CanvasGroup>();
        }
    }

    private void Start()
    {
        if (fadeOnStart && canvasGroup != null)
        {
            // Start solid black and transition smoothly to clear
            FadeIn();
        }
    }

    public void FadeIn()
    {
        StartCoroutine(FadeRoutine(1f, 0f));
    }

    public void FadeOut()
    {
        StartCoroutine(FadeRoutine(0f, 1f));
    }

    public void FadeToScene(string sceneName)
    {
        StartCoroutine(FadeAndLoadRoutine(sceneName));
    }

    private IEnumerator FadeRoutine(float startAlpha, float targetAlpha)
    {
        if (canvasGroup == null) yield break;

        float elapsed = 0f;
        canvasGroup.alpha = startAlpha;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = targetAlpha;
    }

    private IEnumerator FadeAndLoadRoutine(string sceneName)
    {
        // 1. Fade to solid black
        yield return StartCoroutine(FadeRoutine(0f, 1f));

        // 2. Load the target scene cleanly
        SceneManager.LoadScene(sceneName);
    }
}
