using Convai.Domain.Emotion;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Emotion;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Covers the client-authoritative emotion connect path: the shared
    ///     <see cref="EmotionConfigResolver" /> mapping and the character's
    ///     effective mode discovery from an attached <see cref="IEmotionDetectionModeSource" />.
    /// </summary>
    [TestFixture]
    public sealed class EmotionConnectConfigTests
    {
        // EmotionConfigResolver (the request payload built for both transports)

        [Test]
        public void Resolve_Off_ProducesNoEmotionConfig()
        {
            Assert.That(EmotionConfigResolver.Resolve(EmotionDetectionMode.Off), Is.Null);
        }

        [Test]
        public void Resolve_Nrclex_RequestsNrclexProvider()
        {
            Assert.That(EmotionConfigResolver.Resolve(EmotionDetectionMode.Nrclex)?.Provider,
                Is.EqualTo("nrclex"));
        }

        [Test]
        public void Resolve_Llm_RequestsLlmProvider()
        {
            Assert.That(EmotionConfigResolver.Resolve(EmotionDetectionMode.Llm)?.Provider,
                Is.EqualTo("llm"));
        }

        // Character provider discovery (drives the room-connect resolver)

        [Test]
        public void CharacterProvider_NoSource_ResolvesOff()
        {
            var go = new GameObject(nameof(CharacterProvider_NoSource_ResolvesOff));
            go.SetActive(false);
            try
            {
                var character = go.AddComponent<ConvaiCharacter>();
                character.Configure("char-1", "Char 1");

                Assert.That(((IConvaiCharacterAgent)character).EmotionDetectionMode,
                    Is.EqualTo(EmotionDetectionMode.Off));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void CharacterProvider_WithChildSource_ResolvesSourceMode()
        {
            var go = new GameObject(nameof(CharacterProvider_WithChildSource_ResolvesSourceMode));
            go.SetActive(false);
            try
            {
                var character = go.AddComponent<ConvaiCharacter>();
                character.Configure("char-1", "Char 1");

                var child = new GameObject("avatar");
                child.transform.SetParent(go.transform);
                child.AddComponent<StubModeSource>().Mode = EmotionDetectionMode.Llm;

                Assert.That(((IConvaiCharacterAgent)character).EmotionDetectionMode,
                    Is.EqualTo(EmotionDetectionMode.Llm));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void CharacterProvider_IgnoresNestedCharacterSource()
        {
            var parentGo = new GameObject(nameof(CharacterProvider_IgnoresNestedCharacterSource));
            parentGo.SetActive(false);
            try
            {
                var parentCharacter = parentGo.AddComponent<ConvaiCharacter>();
                parentCharacter.Configure("parent-char", "Parent");

                var childGo = new GameObject("nested-character");
                childGo.transform.SetParent(parentGo.transform);

                var childCharacter = childGo.AddComponent<ConvaiCharacter>();
                childCharacter.Configure("child-char", "Child");
                childGo.AddComponent<StubModeSource>().Mode = EmotionDetectionMode.Llm;

                Assert.That(((IConvaiCharacterAgent)parentCharacter).EmotionDetectionMode,
                    Is.EqualTo(EmotionDetectionMode.Off));
                Assert.That(((IConvaiCharacterAgent)childCharacter).EmotionDetectionMode,
                    Is.EqualTo(EmotionDetectionMode.Llm));
            }
            finally
            {
                Object.DestroyImmediate(parentGo);
            }
        }

        private sealed class StubModeSource : MonoBehaviour, IEmotionDetectionModeSource
        {
            public EmotionDetectionMode Mode = EmotionDetectionMode.Off;
            public EmotionDetectionMode EmotionDetectionMode => Mode;
        }
    }
}
