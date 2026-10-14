using Convai.Runtime.Actions;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Convai.Runtime
{
    /// <summary>
    ///     Reusable character identity and default interaction settings.
    /// </summary>
    [MovedFrom(true, "Convai.Runtime", "Convai.Runtime",
        "ConvaiAgentDefinition")]
    [CreateAssetMenu(fileName = "ConvaiCharacterProfile", menuName = "Convai/Character Profile")]
    public sealed class ConvaiCharacterProfile : ScriptableObject
    {
        [SerializeField] [ConvaiInspectorSection("Identity")] [Tooltip("The unique character id from the Convai dashboard.")]
        private string _characterId = string.Empty;

        [SerializeField] [ConvaiInspectorSection("Identity")]
        [Tooltip("Use the live character, or pin a released version returned by the API.")]
        private ConvaiCharacterVersionMode _characterVersionMode = ConvaiCharacterVersionMode.Live;

        [SerializeField] [ConvaiInspectorSection("Identity")]
        [Tooltip("Released version used in Specific mode, such as 1.0 or 1.0.2.")]
        private string _characterVersion = string.Empty;

        [SerializeField] [HideInInspector]
        private string _characterVersionReference = string.Empty;

        [SerializeField] [ConvaiInspectorSection("Identity")] [Tooltip("Display name used in transcripts and debug output.")]
        private string _characterName = string.Empty;

        [SerializeField] [ConvaiInspectorSection("Identity")] [Tooltip("Default nametag color for this character.")]
        private Color _nameTagColor = Color.white;

        [SerializeField] [ConvaiInspectorSection("Defaults")] [Tooltip("Whether remote audio should start enabled for this character.")]
        private bool _enableRemoteAudioOnStart = true;

        [SerializeField] [ConvaiInspectorSection("Defaults")] [Tooltip("Whether this character should attempt session resume on reconnect.")]
        private bool _enableSessionResume = true;

        public string CharacterId => _characterId ?? string.Empty;
        public ConvaiCharacterVersionMode CharacterVersionMode => _characterVersionMode;
        public string CharacterVersion => _characterVersion ?? string.Empty;
        public string SelectedCharacterReference => _characterVersionReference ?? string.Empty;
        public string CharacterReference =>
            ConvaiCharacterVersionReference.Create(CharacterId, CharacterVersionMode, CharacterVersion,
                SelectedCharacterReference);
        public string CharacterName => _characterName ?? string.Empty;
        public Color NameTagColor => _nameTagColor;
        public bool EnableRemoteAudioOnStart => _enableRemoteAudioOnStart;
        public bool EnableSessionResume => _enableSessionResume;
    }
}
