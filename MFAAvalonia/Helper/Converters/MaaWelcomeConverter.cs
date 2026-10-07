using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace MFAAvalonia.Helper.Converters;

/// <summary>
/// Parses the PI welcome field's string, string-array, and legacy object-array forms.
/// </summary>
public sealed class MaaWelcomeConverter : JsonConverter
{
    public override bool CanWrite => false;

    public override bool CanConvert(Type objectType) =>
        objectType == typeof(List<MaaInterface.MaaInterfaceWelcome>);

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (token.Type == JTokenType.Null)
            return null;

        if (token.Type == JTokenType.String)
        {
            return new List<MaaInterface.MaaInterfaceWelcome>
            {
                new MaaInterface.MaaInterfaceWelcome
                {
                    Content = token.Value<string>(),
                    IsLegacyString = true,
                }
            };
        }

        if (token is JArray array)
        {
            if (array.Count == 0)
                throw new JsonSerializationException("welcome announcement array cannot be empty.");

            var result = new List<MaaInterface.MaaInterfaceWelcome>(array.Count);
            foreach (var item in array)
            {
                if (item.Type == JTokenType.String)
                {
                    var stringContent = item.Value<string>();
                    if (string.IsNullOrWhiteSpace(stringContent))
                        throw new JsonSerializationException("welcome announcement content is required.");

                    result.Add(new MaaInterface.MaaInterfaceWelcome
                    {
                        Content = stringContent,
                        IsLegacyString = true,
                    });
                    continue;
                }

                if (item is not JObject obj)
                    throw new JsonSerializationException(
                        "welcome announcement entries must be strings or objects.");

                var content = obj["content"]?.Type == JTokenType.String
                    ? obj["content"]!.Value<string>()
                    : null;
                if (string.IsNullOrWhiteSpace(content))
                    throw new JsonSerializationException("welcome announcement content is required.");

                var labelToken = obj["label"];
                if (labelToken != null && labelToken.Type != JTokenType.String)
                    throw new JsonSerializationException("welcome announcement label must be a string.");

                result.Add(new MaaInterface.MaaInterfaceWelcome
                {
                    Label = labelToken?.Value<string>(),
                    Content = content,
                });
            }

            return result;
        }

        throw new JsonSerializationException(
            "welcome must be a string or a non-empty array of announcement strings or objects.");
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        throw new NotSupportedException();
}
