using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(Selectable))]
public class UIButtonAudioFeedback : MonoBehaviour, IPointerEnterHandler, IPointerClickHandler
{
    [Header("Audio Clips")]
    [Tooltip("Sound played when the controller laser pointer hovers over the button")]
    public AudioClip hoverSound;

    [Tooltip("Sound played when the button is clicked / trigger is pulled")]
    public AudioClip clickSound;

    [Header("Volume Controls")]
    [Range(0f, 1f)] public float hoverVolume = 0.45f;
    [Range(0f, 1f)] public float clickVolume = 0.85f;

    [Header("Audio Source")]
    [Tooltip("Leave empty to automatically use a persistent 2D UI AudioSource that won't cut off during scene loads")]
    public AudioSource audioSource;

    private static AudioSource sharedUIAudioSource;
    private Selectable selectable;

    private void Awake()
    {
        selectable = GetComponent<Selectable>();

        // Auto-create a persistent 2D audio host so transition sounds play across scene loads
        if (audioSource == null)
        {
            if (sharedUIAudioSource == null)
            {
                GameObject audioHost = new GameObject("UI_Shared_AudioSource");
                sharedUIAudioSource = audioHost.AddComponent<AudioSource>();
                sharedUIAudioSource.playOnAwake = false;
                sharedUIAudioSource.spatialBlend = 0f; // 2D Stereo sound directly into headset ears
                DontDestroyOnLoad(audioHost);
            }
            audioSource = sharedUIAudioSource;
        }
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        // Ignore hover if button is disabled/inactive
        if (selectable != null && !selectable.interactable) return;

        if (hoverSound != null && audioSource != null)
        {
            audioSource.PlayOneShot(hoverSound, hoverVolume);
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (selectable != null && !selectable.interactable) return;

        if (clickSound != null && audioSource != null)
        {
            audioSource.PlayOneShot(clickSound, clickVolume);
        }
    }
}
