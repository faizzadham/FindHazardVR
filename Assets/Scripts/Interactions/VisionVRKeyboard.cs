using UnityEngine;
using TMPro;

public class VisionVRKeyboard : MonoBehaviour
{
    [Header("Input Target")]
    [Tooltip("The Input Field on the registration card")]
    public TMP_InputField targetInputField;

    [Tooltip("The preview text label sitting on the keyboard's top ribbon")]
    public TextMeshProUGUI previewTextLabel;

    [Header("Keyboard State")]
    public bool isShiftActive = false;

    private void Start()
    {
        UpdatePreviewText();
    }

    /// <summary>
    /// Call this from any standard letter button's OnClick event.
    /// Pass the lowercase character as a string parameter (e.g. "q", "w", "e").
    /// </summary>
    public void TypeKey(string character)
    {
        if (targetInputField == null) return;

        string charToAdd = isShiftActive ? character.ToUpper() : character.ToLower();
        targetInputField.text += charToAdd;

        // Auto-disable shift after one letter (standard mobile behavior)
        if (isShiftActive)
        {
            isShiftActive = false;
        }

        UpdatePreviewText();
    }

    /// <summary>
    /// Call this from the Space button
    /// </summary>
    public void TypeSpace()
    {
        if (targetInputField == null) return;
        targetInputField.text += " ";
        UpdatePreviewText();
    }

    /// <summary>
    /// Call this from the Backspace (⌫) button
    /// </summary>
    public void Backspace()
    {
        if (targetInputField == null || targetInputField.text.Length == 0) return;

        targetInputField.text = targetInputField.text.Substring(0, targetInputField.text.Length - 1);
        UpdatePreviewText();
    }

    /// <summary>
    /// Call this from the Shift (⇧) button
    /// </summary>
    public void ToggleShift()
    {
        isShiftActive = !isShiftActive;
    }

    /// <summary>
    /// Call this from the Return (↵) button
    /// </summary>
    public void PressReturn()
    {
        if (targetInputField != null)
        {
            Debug.Log($"[Keyboard] Trainee Registered: '{targetInputField.text}'");
        }
    }

    private void UpdatePreviewText()
    {
        if (previewTextLabel == null) return;

        if (targetInputField != null && !string.IsNullOrEmpty(targetInputField.text))
        {
            previewTextLabel.text = targetInputField.text;
            previewTextLabel.color = Color.white;
        }
        else
        {
            previewTextLabel.text = "Trainee Name Preview";
            previewTextLabel.color = new Color(0.6f, 0.6f, 0.6f, 0.8f);
        }
    }
}
