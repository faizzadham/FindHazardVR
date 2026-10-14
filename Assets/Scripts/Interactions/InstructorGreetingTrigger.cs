using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using TMPro;

[RequireComponent(typeof(XRSimpleInteractable))]
public class InstructorGreetingTrigger : MonoBehaviour
{
    [Header("Greeting Prompt")]
    [TextArea(2, 4)]
    [Tooltip("Prompt sent to Convai AI so Miss Anis generates and speaks her induction briefing")]
    public string greetingPrompt = "Hello Miss Anis, please introduce yourself and give the safety induction briefing.";

    [Header("Click Settings")]
    [Tooltip("Cooldown in seconds to prevent spamming while she is speaking")]
    public float clickCooldown = 5.0f;
    private float lastClickTime = -99f;

    private XRSimpleInteractable simpleInteractable;

    private void Awake()
    {
        simpleInteractable = GetComponent<XRSimpleInteractable>();
    }

    private void OnEnable()
    {
        if (simpleInteractable != null)
        {
            // Fires when the trainee aims the ray and pulls the controller index trigger
            simpleInteractable.activated.AddListener(OnCharacterClicked);
            simpleInteractable.selectEntered.AddListener(OnCharacterSelected);
        }
    }

    private void OnDisable()
    {
        if (simpleInteractable != null)
        {
            simpleInteractable.activated.RemoveListener(OnCharacterClicked);
            simpleInteractable.selectEntered.RemoveListener(OnCharacterSelected);
        }
    }

    private void OnCharacterClicked(ActivateEventArgs args)
    {
        TriggerConvaiGreeting();
    }

    private void OnCharacterSelected(SelectEnterEventArgs args)
    {
        TriggerConvaiGreeting();
    }

    public void TriggerConvaiGreeting()
    {
        // Enforce cooldown so multiple rapid clicks don't restart speech
        if (Time.time - lastClickTime < clickCooldown) return;
        lastClickTime = Time.time;

        Debug.Log("<color=cyan>[Instructor] Trainee clicked Miss Anis. Requesting greeting from Convai AI...</color>");

        bool sent = DispatchMessageToConvai(greetingPrompt);
        if (!sent)
        {
            Debug.LogWarning("[Instructor] Convai messaging handler not found. Ensure [Convai Manager] is active and connected.");
        }
    }

    /// <summary>
    /// Dispatches the prompt into Convai's pipeline so it appears in the transcript UI and generates live speech.
    /// </summary>
    private bool DispatchMessageToConvai(string message)
    {
        // 1. Check for Convai Chat UI Handlers or Room Managers in the active scene
        MonoBehaviour[] allComponents = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var component in allComponents)
        {
            if (component == null) continue;
            string typeName = component.GetType().Name;

            // Target Convai Chat UI Handler
            if (typeName.Contains("ChatUI") || typeName.Contains("ConvaiChat"))
            {
                MethodInfo sendMethod = component.GetType().GetMethod("SendTextMessage", BindingFlags.Public | BindingFlags.Instance)
                                     ?? component.GetType().GetMethod("SendUserText", BindingFlags.Public | BindingFlags.Instance)
                                     ?? component.GetType().GetMethod("Send", BindingFlags.Public | BindingFlags.Instance);

                if (sendMethod != null)
                {
                    var parameters = sendMethod.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        sendMethod.Invoke(component, new object[] { message });
                        Debug.Log($"<color=green>[Instructor] Dispatched prompt through {typeName}.{sendMethod.Name}</color>");
                        return true;
                    }
                }
            }

            // Target Convai Room Manager
            if (typeName.Contains("ConvaiRoomManager") || typeName.Contains("RoomManager"))
            {
                MethodInfo sendMethod = component.GetType().GetMethod("SendTextMessage", BindingFlags.Public | BindingFlags.Instance)
                                     ?? component.GetType().GetMethod("SendText", BindingFlags.Public | BindingFlags.Instance)
                                     ?? component.GetType().GetMethod("SendUserText", BindingFlags.Public | BindingFlags.Instance);

                if (sendMethod != null)
                {
                    var parameters = sendMethod.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        sendMethod.Invoke(component, new object[] { message });
                        Debug.Log($"<color=green>[Instructor] Dispatched prompt through {typeName}.{sendMethod.Name}</color>");
                        return true;
                    }
                    else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string))
                    {
                        string charId = "9c395133-6c51-492d-b513-17351f679143";
                        sendMethod.Invoke(component, new object[] { message, charId });
                        Debug.Log($"<color=green>[Instructor] Dispatched prompt through {typeName}.{sendMethod.Name} with Character ID</color>");
                        return true;
                    }
                }
            }
        }

        // 2. Direct fallback: Submit through Convai's TMP InputField if active in the scene
        TMP_InputField[] inputFields = FindObjectsByType<TMP_InputField>(FindObjectsSortMode.None);
        foreach (var input in inputFields)
        {
            if (input.gameObject.name.ToLower().Contains("convai") || input.transform.root.name.ToLower().Contains("convai"))
            {
                input.text = message;
                input.onSubmit?.Invoke(message);
                Debug.Log("<color=green>[Instructor] Dispatched prompt through Convai Input Field onSubmit.</color>");
                return true;
            }
        }

        return false;
    }
}