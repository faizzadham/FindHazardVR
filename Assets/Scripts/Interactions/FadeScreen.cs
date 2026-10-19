using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class FadeScreen : MonoBehaviour
{
    public static FadeScreen Instance { get; private set; }

    [Header("Fade Settings")]
    [Tooltip("If true, the screen starts solid black and fades in when the scene loads")]
    public bool fadeOnStart = true;

    [Tooltip("Duration of the fade in seconds")]
    public float fadeDuration = 0.8f;

    [Tooltip("Color to fade through (usually solid black)")]
    public Color fadeColor = Color.black;

    private Renderer rend;
    private Material mat;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Do not destroy if multiple instances exist across different cameras, just reassign active
        }
        Instance = this;

        rend = GetComponent<Renderer>();
        if (rend != null)
        {
            mat = rend.material;
        }
    }

    private void Start()
    {
        if (fadeOnStart)
        {
            FadeIn();
        }
        else
        {
            SetColorAlpha(0f);
        }
    }

    /// <summary>
    /// Fades the screen from black to transparent.
    /// </summary>
    public void FadeIn()
    {
        Fade(1f, 0f);
    }

    /// <summary>
    /// Fades the screen from transparent to solid black.
    /// </summary>
    public void FadeOut()
    {
        Fade(0f, 1f);
    }

    public void Fade(float alphaIn, float alphaOut)
    {
        StopAllCoroutines();
        StartCoroutine(FadeRoutine(alphaIn, alphaOut));
    }

    /// <summary>
    /// Resolves CS1061 for BriefingToWarehouse, SceneTransitionManager, and VRMenuFlowController.
    /// </summary>
    public void FadeToScene(string sceneName)
    {
        FadeOutAndLoadScene(sceneName);
    }

    /// <summary>
    /// Overload for callers passing a scene build index.
    /// </summary>
    public void FadeToScene(int sceneIndex)
    {
        StopAllCoroutines();
        StartCoroutine(FadeOutAndLoadIndexRoutine(sceneIndex));
    }

    /// <summary>
    /// Fades to black and then loads the target scene by name.
    /// </summary>
    public void FadeOutAndLoadScene(string sceneName)
    {
        StopAllCoroutines();
        StartCoroutine(FadeOutAndLoadRoutine(sceneName));
    }

    private IEnumerator FadeRoutine(float alphaIn, float alphaOut)
    {
        float timer = 0f;
        while (timer < fadeDuration)
        {
            timer += Time.deltaTime;
            float alpha = Mathf.Lerp(alphaIn, alphaOut, timer / fadeDuration);
            SetColorAlpha(alpha);
            yield return null;
        }

        SetColorAlpha(alphaOut);
    }

    private IEnumerator FadeOutAndLoadRoutine(string sceneName)
    {
        yield return StartCoroutine(FadeRoutine(0f, 1f));
        SceneManager.LoadScene(sceneName);
    }

    private IEnumerator FadeOutAndLoadIndexRoutine(int sceneIndex)
    {
        yield return StartCoroutine(FadeRoutine(0f, 1f));
        SceneManager.LoadScene(sceneIndex);
    }

    private void SetColorAlpha(float alpha)
    {
        if (mat == null) return;

        Color c = fadeColor;
        c.a = alpha;

        if (mat.HasProperty(BaseColorId))
        {
            mat.SetColor(BaseColorId, c);
        }
        if (mat.HasProperty(ColorId))
        {
            mat.SetColor(ColorId, c);
        }
    }
}
