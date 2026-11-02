using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ConvaiChatHUDToggle : MonoBehaviour
{
    [Header("Target UI & AI Objects")]
    [Tooltip("The visual Convai Chat Canvas/Panel")]
    public GameObject convaiChatUI;

    [Tooltip("Drag AI_Chatbox here")]
    public GameObject convaiNPCBrain;

    [Header("Audio Output")]
    [Tooltip("Drag the AudioSource that plays Chatbot's voice (on AI_Chatbox)")]
    public AudioSource missAnisAudioSource;

    [Header("Button References")]
    public Button toggleButton;
    public TextMeshProUGUI buttonLabel;

    [Header("Label Configuration")]
    public string openText = "CLOSE";

    private string originalLabel;
    private bool isChatOpen = false;
    private MonoBehaviour convaiNPCComponent;

    private void Awake()
    {
        if (toggleButton == null)
            toggleButton = GetComponent<Button>();

        if (buttonLabel == null && toggleButton != null)
            buttonLabel = toggleButton.GetComponentInChildren<TextMeshProUGUI>();

        if (buttonLabel != null)
            originalLabel = buttonLabel.text;

        if (convaiNPCBrain != null)
        {
            // Cache the AudioSource on the character if not manually dragged
            if (missAnisAudioSource == null)
                missAnisAudioSource = convaiNPCBrain.GetComponentInChildren<AudioSource>();

            // Find the ConvaiNPC script dynamically
            foreach (var comp in convaiNPCBrain.GetComponents<MonoBehaviour>())
            {
                if (comp.GetType().Name.Contains("ConvaiNPC"))
                {
                    convaiNPCComponent = comp;
                    break;
                }
            }
        }

        if (toggleButton != null)
        {
            toggleButton.onClick.RemoveListener(ToggleChatVisibility);
            toggleButton.onClick.AddListener(ToggleChatVisibility);
        }
    }

    private void Start()
    {
        // Enforce silent lock on scene start
        SetChatVisibility(false);
    }

    public void ToggleChatVisibility()
    {
        SetChatVisibility(!isChatOpen);
    }

    public void SetChatVisibility(bool visible)
    {
        isChatOpen = visible;

        // 1. Visual UI Window
        if (convaiChatUI != null)
            convaiChatUI.SetActive(isChatOpen);

        // 2. Hard Mute & Audio Cut
        if (missAnisAudioSource != null)
        {
            if (!isChatOpen)
            {
                missAnisAudioSource.Stop();
                missAnisAudioSource.mute = true;
            }
            else
            {
                missAnisAudioSource.mute = false;
            }
        }

        // 3. Freeze/Unfreeze Convai Processing Loop
        if (convaiNPCComponent != null)
        {
            convaiNPCComponent.enabled = isChatOpen;
        }

        // 4. Update Button Label
        if (buttonLabel != null)
        {
            buttonLabel.text = isChatOpen ? openText : originalLabel;
        }

        Debug.Log($"<color=cyan>[Convai Gate] Comms {(isChatOpen ? "ACTIVE & UNMUTED" : "LOCKED & HARD-MUTED")}</color>");
    }
}