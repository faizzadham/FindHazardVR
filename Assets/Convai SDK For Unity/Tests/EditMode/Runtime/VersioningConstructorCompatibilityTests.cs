using System;
using System.Reflection;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Services;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class VersioningConstructorCompatibilityTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ConnectionContext_OriginalClrSignature_RemainsCallable(bool disconnected)
        {
            // Optional arguments are baked into callers. Precompiled consumers still bind this exact signature.
            ConstructorInfo constructor = typeof(ConnectionContext).GetConstructor(new[]
            {
                typeof(string), typeof(string), typeof(string), typeof(string), typeof(DateTime), typeof(DateTime?)
            });
            Assert.That(constructor, Is.Not.Null);
            Assert.That(constructor.GetParameters()[5].IsOptional, Is.True);
            Assert.That(constructor.GetParameters()[5].DefaultValue, Is.Null);
            DateTime connectedAt = new(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);
            DateTime? disconnectedAt = disconnected ? connectedAt.AddMinutes(1) : null;

            var context = (ConnectionContext)constructor.Invoke(new object[]
                { "room", "character-session", "session", "character", connectedAt, disconnectedAt });

            Assert.That(context.RoomName, Is.EqualTo("room"));
            Assert.That(context.CharacterSessionId, Is.EqualTo("character-session"));
            Assert.That(context.SessionId, Is.EqualTo("session"));
            Assert.That(context.CharacterId, Is.EqualTo("character"));
            Assert.That(context.ConnectedAtUtc, Is.EqualTo(connectedAt));
            Assert.That(context.DisconnectedAtUtc, Is.EqualTo(disconnectedAt));
            Assert.That(context.CharacterReference, Is.Null);
        }

        [Test]
        public void ConnectionContext_SourceDefaultAndVersionAwareOverload_BothRemainUsable()
        {
            DateTime connectedAt = DateTime.UtcNow;
            var legacy = new ConnectionContext("room", "character-session", "session", "character", connectedAt);
            var versioned = new ConnectionContext("room", "character-session", "session", "character", connectedAt,
                null, "exact-api-reference");

            Assert.That(legacy.DisconnectedAtUtc, Is.Null);
            Assert.That(legacy.CharacterReference, Is.Null);
            Assert.That(versioned.CharacterReference, Is.EqualTo("exact-api-reference"));
            Assert.That(versioned.WithDisconnection(connectedAt.AddMinutes(1)).CharacterReference,
                Is.EqualTo("exact-api-reference"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NarrativeService_OriginalClrSignature_PreservesAuthoringFlag(bool useDraft)
        {
            ConstructorInfo constructor = typeof(NarrativeDesignDataService).GetConstructor(new[]
                { typeof(Func<string>), typeof(bool) });
            Assert.That(constructor, Is.Not.Null);
            Assert.That(constructor.GetParameters()[1].IsOptional, Is.True);
            Assert.That(constructor.GetParameters()[1].DefaultValue, Is.False);
            Func<string> provider = () => "test-key";

            var service = (NarrativeDesignDataService)constructor.Invoke(new object[] { provider, useDraft });

            Assert.That(ReadField(service, "_apiKeyProvider"), Is.SameAs(provider));
            Assert.That(ReadField(service, "_useDraft"), Is.EqualTo(useDraft));
            Assert.That(ReadField(service, "_preserveSelectedReference"), Is.False);
        }

        [Test]
        public void NarrativeService_SourceDefaultAndSelectedReferenceOverload_BothRemainUsable()
        {
            var legacy = new NarrativeDesignDataService(() => "test-key");
            var selected = new NarrativeDesignDataService(() => "test-key", useDraft: true,
                preserveSelectedReference: true);

            Assert.That(ReadField(legacy, "_useDraft"), Is.False);
            Assert.That(ReadField(legacy, "_preserveSelectedReference"), Is.False);
            Assert.That(ReadField(selected, "_useDraft"), Is.True);
            Assert.That(ReadField(selected, "_preserveSelectedReference"), Is.True);
        }

        [Test]
        public void NarrativeService_AllOverloadsStillRejectMissingCredentialProvider()
        {
            Assert.Throws<ArgumentNullException>(() => new NarrativeDesignDataService(null));
            Assert.Throws<ArgumentNullException>(() => new NarrativeDesignDataService(null, true));
            Assert.Throws<ArgumentNullException>(() => new NarrativeDesignDataService(null, true, true));
        }

        private static object ReadField(object value, string name)
        {
            FieldInfo field = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            return field.GetValue(value);
        }
    }
}
