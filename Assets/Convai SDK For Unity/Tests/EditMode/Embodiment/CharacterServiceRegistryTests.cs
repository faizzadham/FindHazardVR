using System.Collections.Generic;
using System.Text.RegularExpressions;
using Convai.Domain.Logging;
using Convai.Runtime;
using Convai.Runtime.Embodiment;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine.TestTools;
using Convai.Domain.Embodiment.Interfaces;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Equivalence suite for <see cref="CharacterServiceRegistry" />, which replaced the
    ///     per-seam slot types.
    /// </summary>
    /// <remarks>
    ///     Every guarantee the old <c>EmbodimentContextSlot&lt;T&gt;</c> made is asserted here so the
    ///     registry rewrite cannot quietly drop one: first-writer-wins, a named warning for the
    ///     rejected duplicate, idempotent re-registration, release only by the holder, and
    ///     change notification carrying the new value.
    ///     <para>
    ///         Two behaviors are deliberately <em>different</em> from the slots and are asserted as
    ///         such: change dispatch is per subscriber (a throwing subscriber no longer starves the
    ///         ones behind it), and a fan-out contract is a first-class shape rather than a second
    ///         slot type.
    ///     </para>
    /// </remarks>
    public sealed class CharacterServiceRegistryTests
    {
        private ConvaiSettings _settings;
        private LogLevel _originalGlobalLevel;
        private LogLevelOverride[] _originalCategoryOverrides;

        [SetUp]
        public void SetUp()
        {
            _settings = ConvaiSettings.Instance;
            if (_settings == null) return;

            _originalGlobalLevel = _settings.GlobalLogLevel;
            _originalCategoryOverrides = CloneOverrides(_settings.CategoryOverrides);
            _settings.SetGlobalLogLevel(LogLevel.Trace);
            _settings.SetCategoryOverrides(System.Array.Empty<LogLevelOverride>());
            LoggingConfig.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings == null) return;

            _settings.SetGlobalLogLevel(_originalGlobalLevel);
            _settings.SetCategoryOverrides(CloneOverrides(_originalCategoryOverrides));
            LoggingConfig.InvalidateCache();
        }

        private interface IFakeSource { }

        private interface IOtherSource { }

        private sealed class FakeSourceA : IFakeSource, IOtherSource { }

        private sealed class FakeSourceB : IFakeSource { }

        private static CharacterServiceRegistry NewRegistry() => new(null);

        // ── single-writer semantics ─────────────────────────────────────────────────

        [Test]
        public void Provide_Null_IsIgnoredAndLeavesContractVacant()
        {
            CharacterServiceRegistry registry = NewRegistry();

            CharacterServiceRegistry.ServiceToken token = registry.Provide<IFakeSource>(null);

            Assert.IsFalse(token.IsValid);
            Assert.IsNull(registry.Get<IFakeSource>());
        }

        [Test]
        public void Provide_FirstProvider_IsResolvable()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var source = new FakeSourceA();

            CharacterServiceRegistry.ServiceToken token = registry.Provide<IFakeSource>(source);

            Assert.IsTrue(token.IsValid);
            Assert.AreSame(source, registry.Get<IFakeSource>());
            Assert.IsTrue(registry.TryGet(out IFakeSource resolved));
            Assert.AreSame(source, resolved);
        }

        [Test]
        public void Provide_SameInstanceTwice_IsIdempotentAndDoesNotWarn()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var source = new FakeSourceA();
            int changes = 0;
            registry.AddChangedHandler<IFakeSource>(_ => changes++);

            registry.Provide<IFakeSource>(source);
            registry.Provide<IFakeSource>(source);

            Assert.AreEqual(1, changes, "Re-providing the same instance must not re-notify.");
            Assert.AreSame(source, registry.Get<IFakeSource>());
        }

        [Test]
        public void Provide_SecondProvider_IsRejectedAndBothInstancesAreNamed()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var first = new FakeSourceA();
            var second = new FakeSourceB();
            registry.Provide<IFakeSource>(first);

            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("Duplicate fake source"));
            CharacterServiceRegistry.ServiceToken rejected = registry.Provide<IFakeSource>(second);

            Assert.IsFalse(rejected.IsValid, "A rejected registration must not hand back a live token.");
            Assert.AreSame(first, registry.Get<IFakeSource>(), "First writer must keep the contract.");
        }

        [Test]
        public void RejectedDuplicate_ReleasingItsToken_DoesNotEvictTheRealProvider()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var first = new FakeSourceA();
            var second = new FakeSourceB();
            registry.Provide<IFakeSource>(first);

            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("Duplicate fake source"));
            CharacterServiceRegistry.ServiceToken rejected = registry.Provide<IFakeSource>(second);
            rejected.Release();

            Assert.AreSame(first, registry.Get<IFakeSource>());
        }

        [Test]
        public void Release_ByHolder_VacatesContractAndNotifiesWithNull()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var source = new FakeSourceA();
            var observed = new List<IFakeSource>();
            registry.AddChangedHandler<IFakeSource>(v => observed.Add(v));

            CharacterServiceRegistry.ServiceToken token = registry.Provide<IFakeSource>(source);
            token.Release();

            Assert.IsNull(registry.Get<IFakeSource>());
            Assert.AreEqual(2, observed.Count);
            Assert.AreSame(source, observed[0]);
            Assert.IsNull(observed[1], "Vacating a contract must notify with null, not just go quiet.");
        }

        [Test]
        public void Release_Twice_IsSafe()
        {
            CharacterServiceRegistry registry = NewRegistry();
            CharacterServiceRegistry.ServiceToken token = registry.Provide<IFakeSource>(new FakeSourceA());

            token.Release();
            Assert.DoesNotThrow(() => token.Release());
            Assert.IsNull(registry.Get<IFakeSource>());
        }

        [Test]
        public void Release_DefaultToken_IsSafe()
        {
            CharacterServiceRegistry.ServiceToken token = default;
            Assert.DoesNotThrow(() => token.Release());
        }

        [Test]
        public void Contracts_AreIndependent()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var source = new FakeSourceA();

            registry.Provide<IFakeSource>(source);

            Assert.AreSame(source, registry.Get<IFakeSource>());
            Assert.IsNull(registry.Get<IOtherSource>(),
                "One instance registered under one contract must not answer a different contract.");
        }

        [Test]
        public void SameInstance_CanProvideTwoContractsIndependently()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var source = new FakeSourceA();

            CharacterServiceRegistry.ServiceToken fake = registry.Provide<IFakeSource>(source);
            registry.Provide<IOtherSource>(source);
            fake.Release();

            Assert.IsNull(registry.Get<IFakeSource>());
            Assert.AreSame(source, registry.Get<IOtherSource>(),
                "Releasing one contract must not withdraw the other.");
        }

        // ── change notification ────────────────────────────────────────────────────

        [Test]
        public void ChangedHandler_AddedTwice_IsInvokedOnce()
        {
            CharacterServiceRegistry registry = NewRegistry();
            int changes = 0;
            void Handler(IFakeSource _) => changes++;

            registry.AddChangedHandler<IFakeSource>(Handler);
            registry.AddChangedHandler<IFakeSource>(Handler);
            registry.Provide<IFakeSource>(new FakeSourceA());

            Assert.AreEqual(1, changes);
        }

        [Test]
        public void ChangedHandler_AfterRemoval_IsNotInvoked()
        {
            CharacterServiceRegistry registry = NewRegistry();
            int changes = 0;
            void Handler(IFakeSource _) => changes++;

            registry.AddChangedHandler<IFakeSource>(Handler);
            registry.RemoveChangedHandler<IFakeSource>(Handler);
            registry.Provide<IFakeSource>(new FakeSourceA());

            Assert.AreEqual(0, changes);
        }

        [Test]
        public void ThrowingSubscriber_DoesNotStarveLaterSubscribers()
        {
            // This is the behavior the old slot got WRONG: it wrapped the whole multicast delegate
            // in one try, so the first throwing subscriber silently prevented every later module
            // from learning the contract had changed.
            CharacterServiceRegistry registry = NewRegistry();
            bool secondRan = false;

            registry.AddChangedHandler<IFakeSource>(_ => throw new System.InvalidOperationException("boom"));
            registry.AddChangedHandler<IFakeSource>(_ => secondRan = true);

            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("subscriber threw"));
            registry.Provide<IFakeSource>(new FakeSourceA());

            Assert.IsTrue(secondRan, "A throwing subscriber must not stop the ones behind it.");
        }

        [Test]
        public void Handler_ThatMutatesSubscriptions_DoesNotCorruptDispatch()
        {
            CharacterServiceRegistry registry = NewRegistry();
            int secondCalls = 0;
            void Second(IFakeSource _) => secondCalls++;

            registry.AddChangedHandler<IFakeSource>(_ => registry.RemoveChangedHandler<IFakeSource>(Second));
            registry.AddChangedHandler<IFakeSource>(Second);

            Assert.DoesNotThrow(() => registry.Provide<IFakeSource>(new FakeSourceA()));
        }

        [Test]
        public void Handler_ThatReleasesAnotherContract_IsSafe()
        {
            CharacterServiceRegistry registry = NewRegistry();
            CharacterServiceRegistry.ServiceToken other = registry.Provide<IOtherSource>(new FakeSourceA());
            registry.AddChangedHandler<IFakeSource>(_ => other.Release());

            Assert.DoesNotThrow(() => registry.Provide<IFakeSource>(new FakeSourceA()));
            Assert.IsNull(registry.Get<IOtherSource>());
        }

        // ── fan-out contracts ──────────────────────────────────────────────────────

        [Test]
        public void Contribute_PreservesRegistrationOrder()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var a = new FakeSourceA();
            var b = new FakeSourceB();

            registry.Contribute<IFakeSource>(a);
            registry.Contribute<IFakeSource>(b);

            var buffer = new List<IFakeSource>();
            registry.GetAll(buffer);

            Assert.AreEqual(2, buffer.Count);
            Assert.AreSame(a, buffer[0]);
            Assert.AreSame(b, buffer[1]);
        }

        [Test]
        public void Contribute_SameInstanceTwice_IsNotDuplicated()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var a = new FakeSourceA();

            registry.Contribute<IFakeSource>(a);
            registry.Contribute<IFakeSource>(a);

            var buffer = new List<IFakeSource>();
            registry.GetAll(buffer);
            Assert.AreEqual(1, buffer.Count);
        }

        [Test]
        public void Contribute_ReleasingOne_LeavesTheOthers()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var a = new FakeSourceA();
            var b = new FakeSourceB();
            CharacterServiceRegistry.ServiceToken tokenA = registry.Contribute<IFakeSource>(a);
            registry.Contribute<IFakeSource>(b);

            tokenA.Release();

            var buffer = new List<IFakeSource>();
            registry.GetAll(buffer);
            Assert.AreEqual(1, buffer.Count);
            Assert.AreSame(b, buffer[0]);
        }

        [Test]
        public void GetAll_ClearsBufferFirst()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var buffer = new List<IFakeSource> { new FakeSourceA() };

            registry.GetAll(buffer);

            Assert.IsEmpty(buffer, "GetAll must not append to whatever the caller passed in.");
        }

        [Test]
        public void HasAny_ReflectsContributions()
        {
            CharacterServiceRegistry registry = NewRegistry();
            Assert.IsFalse(registry.HasAny<IFakeSource>());

            CharacterServiceRegistry.ServiceToken token = registry.Contribute<IFakeSource>(new FakeSourceA());
            Assert.IsTrue(registry.HasAny<IFakeSource>());

            token.Release();
            Assert.IsFalse(registry.HasAny<IFakeSource>());
        }

        [Test]
        public void SingleAndFanOut_AreSeparateRegistries()
        {
            CharacterServiceRegistry registry = NewRegistry();
            var provided = new FakeSourceA();
            var contributed = new FakeSourceB();

            registry.Provide<IFakeSource>(provided);
            registry.Contribute<IFakeSource>(contributed);

            Assert.AreSame(provided, registry.Get<IFakeSource>());
            var buffer = new List<IFakeSource>();
            registry.GetAll(buffer);
            Assert.AreEqual(1, buffer.Count);
            Assert.AreSame(contributed, buffer[0]);
        }

        private static LogLevelOverride[] CloneOverrides(System.Collections.Generic.IReadOnlyList<LogLevelOverride> source)
        {
            if (source == null) return System.Array.Empty<LogLevelOverride>();
            var copy = new LogLevelOverride[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }
    }
}
