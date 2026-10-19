using System.Collections;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class AmbientAudioController : MonoBehaviour
{
    [Header("Fade Settings")]
    [Tooltip("Target volume to reach after fade-in")]
    [Range(0f, 1f)] public float targetVolume = 0.25f;

    [Tooltip("Duration in seconds for the audio to fade in")]
    public float fadeInDuration = 2.0f;

    private AudioSource audioSource;
    private Coroutine fadeRoutine;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        audioSource.volume = 0f; // Start silent for fade-in
    }

    private void Start()
    {
        if (audioSource.clip != null)
        {
            if (!audioSource.isPlaying) audioSource.Play();
            fadeRoutine = StartCoroutine(FadeVolume(0f, targetVolume, fadeInDuration));
        }
    }

    public void FadeOutAndStop(float duration)
    {
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        StartCoroutine(FadeVolume(audioSource.volume, 0f, duration, true));
    }

    private IEnumerator FadeVolume(float startVol, float endVol, float duration, bool stopOnComplete = false)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            audioSource.volume = Mathf.Lerp(startVol, endVol, elapsed / duration);
            yield return null;
        }

        audioSource.volume = endVol;
        if (stopOnComplete) audioSource.Stop();
    }
}
