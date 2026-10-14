using Convai.Runtime.Configuration;
using Convai.Sample.UI.Settings;
using Convai.Runtime.Components;
using UnityEngine;

namespace Convai.Samples
{
    /// <summary>
    ///     Sample input controller that opens the settings panel when F10 is pressed.
    ///     Demonstrates how to integrate with the Convai SDK settings panel controller.
    /// </summary>
    public class ConvaiSampleInteractionController : MonoBehaviour
    {
        [SerializeField] private ConvaiKeyBindings keyBindings;
        [SerializeField] private SettingsHandler settingsHandler;

        private void Awake()
        {
            if (keyBindings == null) ConvaiKeyBindings.GetBinding(out keyBindings);
            if (settingsHandler == null)
                Debug.LogWarning($"[{nameof(ConvaiSampleInteractionController)}] SettingsHandler not assigned in Inspector. Drag the SettingsHandler component into the serialized field.", this);
        }

        private void Update()
        {
            if (keyBindings == null) return;

            if (IsOpenSettingsPressed())
            {
                if (settingsHandler == null)
                {
                    Debug.LogWarning(
                        "[ConvaiSampleInteractionController] SettingsHandler is not available; cannot open settings.");
                    return;
                }

                settingsHandler.TogglePanel();
            }
        }

        /// <summary>
        ///     The shared helper answers for whichever input backend the project is configured
        ///     with, and caches its Input System lookups so polling here costs nothing per frame.
        /// </summary>
        private bool IsOpenSettingsPressed() =>
            ConvaiInputSystem.WasKeyPressedThisFrame(keyBindings.OpenSettingsKey);

    }
}
