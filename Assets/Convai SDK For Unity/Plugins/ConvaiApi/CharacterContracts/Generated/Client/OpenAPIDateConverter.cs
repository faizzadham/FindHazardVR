using System;
using Newtonsoft.Json;

namespace Convai.CharacterApi.Contracts.Client
{
    public sealed class OpenAPIDateConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) =>
            objectType == typeof(DateTime) || objectType == typeof(DateTime?);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;
            return DateTime.Parse(
                (string)reader.Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }
            writer.WriteValue(((DateTime)value).ToString("yyyy-MM-dd"));
        }
    }
}
