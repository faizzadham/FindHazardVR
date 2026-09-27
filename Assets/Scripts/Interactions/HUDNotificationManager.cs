using System.Collections;
using UnityEngine;
using TMPro;

public class HUDNotificationManager : MonoBehaviour
{
    public static HUDNotificationManager Instance { get; private set; }

    [Header("UI Reference")]
    [Tooltip("The TextMeshProUGUI element in your PlayerHUDCanvas")]
    public TextMeshProUGUI notificationText;

    [Tooltip("The CanvasGroup for fading the notification")]
    public CanvasGroup notificationCanvasGroup;

    [Header("Display Duration")]
    public float displayDuration = 2.5f;
    public float fadeSpeed = 4.0f;

    private Coroutine currentRoutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (notificationCanvasGroup != null)
        {
            notificationCanvasGroup.alpha = 0f;
        }
    }

    public void ShowNotification(string message, Color textColor)
    {
        if (notificationText == null) return;

        if (currentRoutine != null)
        {
            StopCoroutine(currentRoutine);
        }

        currentRoutine = StartCoroutine(DisplayRoutine(message, textColor));
    }

    private IEnumerator DisplayRoutine(string message, Color textColor)
    {
        notificationText.text = message;
        notificationText.color = textColor;

        // Fade In
        if (notificationCanvasGroup != null)
        {
            while (notificationCanvasGroup.alpha < 1f)
            {
                notificationCanvasGroup.alpha += Time.deltaTime * fadeSpeed;
                yield return null;
            }
            notificationCanvasGroup.alpha = 1f;
        }

        yield return new WaitForSeconds(displayDuration);

        // Fade Out
        if (notificationCanvasGroup != null)
        {
            while (notificationCanvasGroup.alpha > 0f)
            {
                notificationCanvasGroup.alpha -= Time.deltaTime * fadeSpeed;
                yield return null;
            }
            notificationCanvasGroup.alpha = 0f;
        }
    }
}
