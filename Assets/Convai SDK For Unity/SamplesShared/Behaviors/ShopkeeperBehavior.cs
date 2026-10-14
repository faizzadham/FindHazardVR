using System.Globalization;
using Convai.Runtime.Behaviors;
using UnityEngine;

namespace Convai.Sample.Behaviors
{
    /// <summary>
    ///     Sample NPC behaviour that watches transcripts for commerce keywords and routes them to a trigger.
    /// </summary>
    public class ShopkeeperBehavior : ConvaiCharacterBehaviorBase
    {
        private static readonly string[] Keywords = { "buy", "purchase", "shop", "merchant", "trade" };

        [SerializeField] [Tooltip("Trigger name to raise when a purchase intent is detected.")]
        private string purchaseTrigger = "shop.purchase";

        /// <inheritdoc />
        public override bool OnTranscriptReceived(IConvaiCharacterAgent agent, string transcript, bool isFinal)
        {
            if (!isFinal) return false;

            string lower = transcript.ToLower(CultureInfo.InvariantCulture);
            foreach (string keyword in Keywords)
            {
                if (lower.Contains(keyword))
                {
                    agent.SendTrigger(purchaseTrigger);
                    return true;
                }
            }

            return false;
        }
    }
}
