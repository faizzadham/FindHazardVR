using System;
using System.Linq;
using System.Reflection;
using Convai.Domain.Errors;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    public sealed class NamingOwnershipTests
    {
        [Test]
        [Category("Architecture")]
        public void CanonicalEntrypoints_LiveInOwnedNamespaces()
        {
            Assert.That(FindType("Convai.Runtime.Components.ConvaiManager"), Is.Not.Null);
            Assert.That(FindType("Convai.Application.ConvaiSDK"), Is.Not.Null);
        }

        [Test]
        [Category("Architecture")]
        public void SessionErrorCodes_UseLowercaseDotNotation()
        {
            FieldInfo[] fields = typeof(SessionErrorCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(string))
                .ToArray();

            var invalid = fields
                .Select(field => (field.Name, Value: (string)field.GetValue(null)))
                .Where(entry => string.IsNullOrWhiteSpace(entry.Value) ||
                                !entry.Value.Contains(".") ||
                                entry.Value.Any(char.IsUpper))
                .Select(entry => $"{entry.Name}={entry.Value}")
                .ToArray();

            Assert.That(invalid, Is.Empty);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }

            return null;
        }
    }
}
