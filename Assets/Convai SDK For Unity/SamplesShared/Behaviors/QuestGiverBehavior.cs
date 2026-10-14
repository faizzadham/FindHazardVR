using Convai.Runtime.Behaviors;
using UnityEngine;

namespace Convai.Sample.Behaviors
{
    /// <summary>
    ///     Demonstrates sequencing quest dialogue using the behaviour extension system.
    /// </summary>
    public class QuestGiverBehavior : ConvaiCharacterBehaviorBase
    {
        [SerializeField] private string[] questSteps =
        {
            "Welcome, adventurer!", "We need herbs from the forest.", "Return once you have gathered three bundles."
        };

        private int _stepIndex;

        /// <inheritdoc />
        public override void OnCharacterInitialized(IConvaiCharacterAgent agent) => _stepIndex = 0;

        /// <inheritdoc />
        public override void OnCharacterReady(IConvaiCharacterAgent agent)
        {
            if (questSteps.Length == 0) return;

            agent.SendNarrativeSpeech(questSteps[_stepIndex]);
            _stepIndex = Mathf.Clamp(_stepIndex + 1, 0, questSteps.Length - 1);
        }
    }
}
