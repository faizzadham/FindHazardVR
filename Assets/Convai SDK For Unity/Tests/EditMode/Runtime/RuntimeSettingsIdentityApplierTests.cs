using System;
using System.Collections.Generic;
using Convai.Runtime.Components;
using Convai.Runtime.Settings;
using Convai.Shared.Abstractions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public class RuntimeSettingsIdentityApplierTests
    {
        [Test]
        public void AppliesPlayerDisplayName_OnInitializationAndChange()
        {
            var playerObject = new GameObject("Player");
            try
            {
                ConvaiPlayer player = playerObject.AddComponent<ConvaiPlayer>();
                player.Configure("Inspector Name");
                player.SetRuntimeDisplayName("Before Applier");
                var settings = new StubRuntimeSettingsService(CreateSnapshot("Rishav"));

                using var applier = new RuntimeSettingsIdentityApplier(settings, () => player);

                Assert.AreEqual("Rishav", player.PlayerName);

                settings.RaisePlayerDisplayNameChanged("Updated Name");

                Assert.AreEqual("Updated Name", player.PlayerName);
            }
            finally
            {
                Object.DestroyImmediate(playerObject);
            }
        }

        [Test]
        public void DefaultPlayerDisplayName_ClearsRuntimeOverride()
        {
            var playerObject = new GameObject("Player");
            try
            {
                ConvaiPlayer player = playerObject.AddComponent<ConvaiPlayer>();
                player.Configure("Inspector Name");
                var settings = new StubRuntimeSettingsService(CreateSnapshot("Rishav"));
                using var applier = new RuntimeSettingsIdentityApplier(settings, () => player);

                settings.RaisePlayerDisplayNameChanged("Player");

                Assert.AreEqual("Inspector Name", player.PlayerName);
            }
            finally
            {
                Object.DestroyImmediate(playerObject);
            }
        }

        [Test]
        public void DisplayNameCallback_ReceivesEffectiveName_OnChangeAndReset()
        {
            var playerObject = new GameObject("Player");
            try
            {
                ConvaiPlayer player = playerObject.AddComponent<ConvaiPlayer>();
                player.Configure("Inspector Name");
                var settings = new StubRuntimeSettingsService(CreateSnapshot("Rishav"));
                var appliedNames = new List<string>();
                using var applier = new RuntimeSettingsIdentityApplier(
                    settings,
                    () => player,
                    appliedNames.Add);
                appliedNames.Clear();

                settings.RaisePlayerDisplayNameChanged("Updated Name");
                settings.RaisePlayerDisplayNameChanged("Player");

                CollectionAssert.AreEqual(
                    new[] { "Updated Name", "Inspector Name" },
                    appliedNames);
            }
            finally
            {
                Object.DestroyImmediate(playerObject);
            }
        }

        [Test]
        public void ApplyCurrent_AppliesSettingToLateBoundPlayer()
        {
            var settings = new StubRuntimeSettingsService(CreateSnapshot("Rishav"));
            ConvaiPlayer player = null;
            using var applier = new RuntimeSettingsIdentityApplier(settings, () => player);
            var playerObject = new GameObject("Player");
            try
            {
                player = playerObject.AddComponent<ConvaiPlayer>();
                player.Configure("Inspector Name");

                applier.ApplyCurrent();

                Assert.AreEqual("Rishav", player.PlayerName);
            }
            finally
            {
                Object.DestroyImmediate(playerObject);
            }
        }

        private static ConvaiRuntimeSettingsSnapshot CreateSnapshot(string playerDisplayName) =>
            new(playerDisplayName, true, false, string.Empty);

        private sealed class StubRuntimeSettingsService : IConvaiRuntimeSettingsService
        {
            public StubRuntimeSettingsService(ConvaiRuntimeSettingsSnapshot current)
            {
                Current = current;
            }

            public event Action<ConvaiRuntimeSettingsChanged> Changed;

            public ConvaiRuntimeSettingsSnapshot Current { get; private set; }

            public ConvaiRuntimeSettingsApplyResult Apply(ConvaiRuntimeSettingsPatch patch) =>
                ConvaiRuntimeSettingsApplyResult.Ok(Current, ConvaiRuntimeSettingsChangeMask.None);

            public ConvaiRuntimeSettingsApplyResult ResetToDefaults() =>
                ConvaiRuntimeSettingsApplyResult.Ok(Current, ConvaiRuntimeSettingsChangeMask.None);

            public void RaisePlayerDisplayNameChanged(string playerDisplayName)
            {
                ConvaiRuntimeSettingsSnapshot previous = Current;
                Current = Current.With(playerDisplayName: playerDisplayName);
                Changed?.Invoke(new ConvaiRuntimeSettingsChanged(
                    previous,
                    Current,
                    ConvaiRuntimeSettingsChangeMask.PlayerDisplayName));
            }
        }
    }
}
