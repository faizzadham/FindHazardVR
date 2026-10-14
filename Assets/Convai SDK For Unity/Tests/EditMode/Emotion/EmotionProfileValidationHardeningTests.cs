using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Pins <c>ConvaiEmotionProfile.OnValidate</c>'s NaN hardening across every float field.
    /// </summary>
    /// <remarks>
    ///     Nine fields — the smoothing speeds, the strength trim, the three reaction-kick values and
    ///     the resting-mood strength — had no NaN guard while every later field did.
    ///     <c>Mathf.Max</c> and <c>Mathf.Clamp</c> propagate NaN, because their comparisons are
    ///     false, so a NaN written by code or carried by a corrupted asset survived validation and
    ///     then poisoned the accumulator's smoothing for the whole session. This test walks every
    ///     float the profile serializes, so a field added later cannot quietly reopen the gap.
    /// </remarks>
    public sealed class EmotionProfileValidationHardeningTests
    {
        private static IEnumerable<FieldInfo> SerializedFloatFields() =>
            typeof(ConvaiEmotionProfile)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(float));

        [Test]
        public void EveryFloatField_SurvivesNaN()
        {
            var poisoned = new List<string>();

            foreach (FieldInfo field in SerializedFloatFields())
            {
                ConvaiEmotionProfile profile = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
                try
                {
                    field.SetValue(profile, float.NaN);
                    InvokeOnValidate(profile);

                    var after = (float)field.GetValue(profile);
                    if (float.IsNaN(after) || float.IsInfinity(after))
                        poisoned.Add($"{field.Name} -> {after}");
                }
                finally
                {
                    Object.DestroyImmediate(profile);
                }
            }

            Assert.That(poisoned, Is.Empty,
                "These fields let NaN through validation, which poisons the emotion pipeline for the " +
                "whole session: " + string.Join(", ", poisoned));
        }

        [Test]
        public void EveryFloatField_SurvivesInfinity()
        {
            var poisoned = new List<string>();

            foreach (FieldInfo field in SerializedFloatFields())
            {
                ConvaiEmotionProfile profile = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
                try
                {
                    field.SetValue(profile, float.PositiveInfinity);
                    InvokeOnValidate(profile);

                    var after = (float)field.GetValue(profile);
                    if (float.IsNaN(after) || float.IsInfinity(after))
                        poisoned.Add($"{field.Name} -> {after}");
                }
                finally
                {
                    Object.DestroyImmediate(profile);
                }
            }

            Assert.That(poisoned, Is.Empty, string.Join(", ", poisoned));
        }

        private static void InvokeOnValidate(ConvaiEmotionProfile profile) =>
            typeof(ConvaiEmotionProfile)
                .GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(profile, null);
    }
}
