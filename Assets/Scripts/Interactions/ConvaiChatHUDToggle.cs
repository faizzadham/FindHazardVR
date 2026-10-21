using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ConvaiChatHUDToggle : MonoBehaviour
{
    [Header("Target Chat UI")]
    [Tooltip("Drag the Convai chat GameObject/Canvas here")]
    public GameObject convaiChatUI;

    [Header("Button References")]
    public Button toggleButton;
    public TextMeshProUGUI buttonLabel;

    [Header("Label Configuration")]
    [Tooltip("Text displayed on the button when the chat box is open")]
    public string openText = "Close";

    private string originalLabel;
    private bool isChatOpen = false;

    private void Awake()
    {
        if (toggleButton == null)
        {
            toggleButton = GetComponent<Button>();
        }

        if (buttonLabel == null && toggleButton != null)
        {
            buttonLabel = toggleButton.GetComponentInChildren<TextMeshProUGUI>();
        }

        // Cache your original button text so it is never lost or overwritten
        if (buttonLabel != null)
        {
            originalLabel = buttonLabel.text;
        }

        if (toggleButton != null)
        {
            toggleButton.onClick.RemoveListener(ToggleChatVisibility);
            toggleButton.onClick.AddListener(ToggleChatVisibility);
        }
    }

    private void Start()
    {
        // Start closed without affecting your button background or colors
        SetChatVisibility(false);
    }

    public void ToggleChatVisibility()
    {
        SetChatVisibility(!isChatOpen);
    }

    public void SetChatVisibility(bool visible)
    {
        isChatOpen = visible;

        // Toggle chat UI visibility
        if (convaiChatUI != null)
        {
            convaiChatUI.SetActive(isChatOpen);
        }

        // Only swap the label text; leave all image backgrounds and colors untouched
        if (buttonLabel != null)
        {
            buttonLabel.text = isChatOpen ? openText : originalLabel;
        }

        Debug.Log($"<color=cyan>[Convai UI] Chat HUD is now {(isChatOpen ? "OPEN" : "CLOSED")}.</color>");
    }
}