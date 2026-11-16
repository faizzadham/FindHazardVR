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
        if (audioSource != null)
        {
            audioSource.loop = true; // Ensure background ambience continuously loops without stopping
            audioSource.spatialBlend = 0f; // Force 2D stereo so background audio is not attenuated by 3D distance
            if (targetVolume <= 0f) targetVolume = 0.35f;
            audioSource.volume = targetVolume; // Start with audible volume immediately
        }
    }

    private void Start()
    {
        if (audioSource != null)
        {
            audioSource.spatialBlend = 0f;
            audioSource.volume = targetVolume;
            if (!audioSource.isPlaying)
            {
                audioSource.Play();
            }
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
