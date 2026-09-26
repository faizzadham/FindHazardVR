using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class VRMenuFlowController : MonoBehaviour
{
    [Header("UI Panels Under VR_MenuCanvas")]
    [Tooltip("The initial title/intro panel")]
    public GameObject startMenuPanel;

    [Tooltip("The main menu panel (Start Training / Exit)")]
    public GameObject mainMenuPanel;

    [Tooltip("The registration panel with the username input")]
    public GameObject registrationPanel;

    [Header("Keyboard")]
    [Tooltip("The floating XRI Keyboard object")]
    public GameObject xriKeyboard;

    [Header("Registration Input")]
    [Tooltip("Drag InputField_Username here")]
    public TMP_InputField usernameInput;

    [Header("Scene Destination")]
    [Tooltip("Exact name of the scene file in Build Settings")]
    public string briefingSceneName = "SafetyBriefingRoom";

    private void Awake()
    {
        // Listen for when the user laser-clicks/focuses the input field
        if (usernameInput != null)
        {
            usernameInput.onSelect.AddListener(OnInputFieldSelected);
        }
    }

    private void OnDestroy()
    {
        if (usernameInput != null)
        {
            usernameInput.onSelect.RemoveListener(OnInputFieldSelected);
        }
    }

    private void Start()
    {
        // Boot directly into the Start Menu with keyboard hidden
        ShowStartMenu();
    }

    /// <summary>
    /// Call from StartMenu's Start button
    /// </summary>
    public void ShowMainMenu()
    {
        SetPanelState(start: true, main: true, reg: false, keyboard: false);
        SetPanelState(start: false, main: true, reg: false, keyboard: false);
    }

    /// <summary>
    /// Call from MainMenu's Exit button
    /// </summary>
    public void ShowStartMenu()
    {
        SetPanelState(start: true, main: false, reg: false, keyboard: false);
    }

    /// <summary>
    /// Call from MainMenu's Start Training button
    /// </summary>
    public void ShowRegistration()
    {
        // Open the registration panel, but keep the keyboard HIDDEN until clicked
        SetPanelState(start: false, main: false, reg: true, keyboard: false);
    }

    /// <summary>
    /// Automatically called when the user laser-clicks inside InputField_Username
    /// </summary>
    private void OnInputFieldSelected(string currentText)
    {
        ShowKeyboard();
    }

    public void ShowKeyboard()
    {
        if (xriKeyboard != null)
        {
            xriKeyboard.SetActive(true);
        }
    }

    public void HideKeyboard()
    {
        if (xriKeyboard != null)
        {
            xriKeyboard.SetActive(false);
        }
    }

    private void SetPanelState(bool start, bool main, bool reg, bool keyboard)
    {
        if (startMenuPanel != null) startMenuPanel.SetActive(start);
        if (mainMenuPanel != null) mainMenuPanel.SetActive(main);
        if (registrationPanel != null) registrationPanel.SetActive(reg);
        if (xriKeyboard != null) xriKeyboard.SetActive(keyboard);
    }

    /// <summary>
    /// Call from Button_StartBriefing
    /// </summary>
    public void StartBriefing()
    {
        // Hide keyboard when submitting
        HideKeyboard();

        // 1. Capture and save the trainee name
        string traineeName = "Anonymous_Worker";
        if (usernameInput != null && !string.IsNullOrWhiteSpace(usernameInput.text))
        {
            traineeName = usernameInput.text.Trim();
        }

        PlayerPrefs.SetString("CurrentTrainee", traineeName);
        PlayerPrefs.Save();
        Debug.Log($"[VRMenuFlow] Trainee '{traineeName}' registered. Loading {briefingSceneName}...");

        // 2. Teleport to SafetyBriefingRoom
        if (FadeScreen.Instance != null)
        {
            FadeScreen.Instance.FadeToScene(briefingSceneName);
        }
        else
        {
            SceneManager.LoadScene(briefingSceneName);
        }
    }
}
