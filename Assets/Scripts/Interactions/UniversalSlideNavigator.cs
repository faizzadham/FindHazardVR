using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class UniversalSlideNavigator : MonoBehaviour
{
    [Header("Slide Content")]
    [Tooltip("Add child slide/stage GameObjects in sequential order")]
    public GameObject[] slides;

    [Header("Navigation Buttons")]
    public Button nextButton;
    public Button prevButton;

    [Header("Page Indicator (Optional)")]
    public TextMeshProUGUI pageCounterText;
    [Tooltip("Prefix format for counter, e.g. 'PAGE ' or 'STAGE '")]
    public string counterPrefix = "PAGE ";

    [Header("Audio Feedback (Optional)")]
    public AudioSource audioSource;
    public AudioClip slideTurnSound;

    [Header("Behavior Settings")]
    [Tooltip("If true, pressing next on the last slide loops back to slide 1")]
    public bool loopSlides = false;

    private int currentIndex = 0;

    private void Awake()
    {
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }
    }

    private void Start()
    {
        if (nextButton != null)
        {
            nextButton.onClick.RemoveListener(NextSlide);
            nextButton.onClick.AddListener(NextSlide);
        }

        if (prevButton != null)
        {
            prevButton.onClick.RemoveListener(PreviousSlide);
            prevButton.onClick.AddListener(PreviousSlide);
        }

        UpdateDisplay();
    }

    public void NextSlide()
    {
        if (slides == null || slides.Length == 0) return;

        if (currentIndex < slides.Length - 1)
        {
            currentIndex++;
            PlaySound();
            UpdateDisplay();
        }
        else if (loopSlides)
        {
            currentIndex = 0;
            PlaySound();
            UpdateDisplay();
        }
    }

    public void PreviousSlide()
    {
        if (slides == null || slides.Length == 0) return;

        if (currentIndex > 0)
        {
            currentIndex--;
            PlaySound();
            UpdateDisplay();
        }
        else if (loopSlides)
        {
            currentIndex = slides.Length - 1;
            PlaySound();
            UpdateDisplay();
        }
    }

    public void JumpToSlide(int index)
    {
        if (slides == null || slides.Length == 0) return;

        if (index >= 0 && index < slides.Length)
        {
            currentIndex = index;
            PlaySound();
            UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        if (slides == null || slides.Length == 0) return;

        // 1. Toggle visibility of slide bodies
        for (int i = 0; i < slides.Length; i++)
        {
            if (slides[i] != null)
            {
                slides[i].SetActive(i == currentIndex);
            }
        }

        // 2. Update page text: "PAGE 1 / 6"
        if (pageCounterText != null)
        {
            pageCounterText.text = $"{counterPrefix}{currentIndex + 1} / {slides.Length}";
        }

        // 3. Hide or show boundary navigation buttons
        if (!loopSlides)
        {
            if (prevButton != null)
            {
                // Hidden on Page 1, visible on later pages
                bool showPrev = currentIndex > 0;
                prevButton.gameObject.SetActive(showPrev);
            }

            if (nextButton != null)
            {
                // Visible on early pages, hidden on the final page
                bool showNext = currentIndex < slides.Length - 1;
                nextButton.gameObject.SetActive(showNext);
            }
        }
        else
        {
            // If looping is enabled, both buttons always remain visible
            if (prevButton != null) prevButton.gameObject.SetActive(true);
            if (nextButton != null) nextButton.gameObject.SetActive(true);
        }
    }

    private void PlaySound()
    {
        if (audioSource != null && slideTurnSound != null)
        {
            audioSource.PlayOneShot(slideTurnSound, 0.6f);
        }
    }
}
