using UnityEngine;

[RequireComponent(typeof(CanvasGroup))]
public class UIPulseAlpha : MonoBehaviour
{
    [Header("Pulse Settings")]
    [Tooltip("Speed of the breathing animation.")]
    public float pulseSpeed = 2.5f;

    [Tooltip("Minimum transparency level (0 = fully invisible).")]
    [Range(0f, 1f)]
    public float minAlpha = 0.15f;

    [Tooltip("Maximum transparency level (1 = fully visible).")]
    [Range(0f, 1f)]
    public float maxAlpha = 1.0f;

    private CanvasGroup canvasGroup;

    private void Awake()
    {
        canvasGroup = GetComponent<CanvasGroup>();
    }

    private void Update()
    {
        if (canvasGroup == null) return;

        // Smooth sinusoidal oscillation between minAlpha and maxAlpha
        float wave = (Mathf.Sin(Time.time * pulseSpeed) + 1.0f) * 0.5f;
        canvasGroup.alpha = Mathf.Lerp(minAlpha, maxAlpha, wave);
    }
}
