using System.Collections.Generic;

namespace Convai.SampleCommon.UI.Emotion
{
    /// <summary>
    ///     Pure helper that selects the top-N non-zero entries from an emotion score table,
    ///     descending by score. Extracted from <see cref="EmotionDebugPanel" /> so the ranking
    ///     logic is a plain, engine-free-input POCO method that can be unit tested without a
    ///     scene or MonoBehaviour.
    /// </summary>
    public static class EmotionScoreRanking
    {
        /// <summary>
        ///     Clears <paramref name="destination" /> and fills it with the top
        ///     <paramref name="maxRows" /> non-zero entries of <paramref name="scores" />, sorted by
        ///     score descending. Small (taxonomy-sized) input, so an O(n * maxRows) partial
        ///     insertion sort is simpler and allocation-free versus sorting a copy.
        /// </summary>
        public static void CollectTopScores(
            IReadOnlyDictionary<string, float> scores,
            int maxRows,
            List<KeyValuePair<string, float>> destination)
        {
            destination.Clear();
            if (scores == null || maxRows <= 0) return;

            foreach (KeyValuePair<string, float> kvp in scores)
            {
                if (kvp.Value <= 0f) continue;

                int insertAt = destination.Count;
                for (int i = 0; i < destination.Count; i++)
                {
                    if (kvp.Value > destination[i].Value)
                    {
                        insertAt = i;
                        break;
                    }
                }

                if (insertAt >= maxRows) continue;

                destination.Insert(insertAt, kvp);
                if (destination.Count > maxRows)
                    destination.RemoveAt(destination.Count - 1);
            }
        }
    }
}
