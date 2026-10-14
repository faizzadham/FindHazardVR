using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Fixtures
{
    internal static class RigBindingTestAssets
    {
        internal static Mesh CreateMeshWithBlendshapes(params string[] blendshapeNames)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero };
            for (int i = 0; i < blendshapeNames.Length; i++)
            {
                mesh.AddBlendShapeFrame(
                    blendshapeNames[i],
                    100f,
                    new[] { Vector3.zero },
                    new[] { Vector3.zero },
                    new[] { Vector3.zero });
            }
            return mesh;
        }

        internal static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }
    }
}
