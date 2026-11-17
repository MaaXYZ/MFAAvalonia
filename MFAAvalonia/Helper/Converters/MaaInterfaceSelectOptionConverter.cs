using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace MFAAvalonia.Helper.Converters;

public class MaaInterfaceSelectOptionConverter(bool serializeAsStringArray) : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(List<MaaInterface.MaaInterfaceSelectOption>);
    }


    public override object ReadJson(JsonReader reader,
        Type objectType,
        object existingValue,
        JsonSerializer serializer)
    {
        JToken token = JToken.Load(reader);
        switch (token.Type)
        {
            case JTokenType.Array:
                var firstElement = token.First;

                // 处理空数组的情况
                if (firstElement == null)
                {
                    return new List<MaaInterface.MaaInterfaceSelectOption>();
                }

                if (firstElement.Type == JTokenType.String)
                {
                    var list = new List<MaaInterface.MaaInterfaceSelectOption>();
                    foreach (var item in token)
                    {
                        list.Add(new MaaInterface.MaaInterfaceSelectOption
                        {
                            Name = item.ToString()
                        });
                    }

                    return list;
                }

                if (firstElement.Type == JTokenType.Object)
                {
                    // 不传 serializer 以避免 converter 无限递归
                    return token.ToObject<List<MaaInterface.MaaInterfaceSelectOption>>();
                }

                // 处理其他数组元素类型（如 null 元素）
                LoggerHelper.Warning($"MaaInterfaceSelectOptionConverter: Unexpected array element type {firstElement.Type}, returning empty list.");
                return new List<MaaInterface.MaaInterfaceSelectOption>();
            case JTokenType.String:
                var oName = token.ToObject<string>(serializer);
                return new List<MaaInterface.MaaInterfaceSelectOption>
                {
                    new()
                    {
                        Name = oName ?? ""
                    }
                };
            case JTokenType.None:
            case JTokenType.Null:
                return new List<MaaInterface.MaaInterfaceSelectOption>();
        }

        LoggerHelper.Warning($"MaaInterfaceSelectOptionConverter: Unexpected token type {token.Type}, returning empty list.");
        return new List<MaaInterface.MaaInterfaceSelectOption>();
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        var array = new JArray();

        if (value is List<MaaInterface.MaaInterfaceSelectOption> selectOptions)
        {
            if (serializeAsStringArray)
            {
                foreach (var option in selectOptions)
                {
                    array.Add(option.Name);
                }
            }
            else
            {
                foreach (var option in selectOptions)
                {
                    // The model owns client fields and encrypted input persistence at every depth.
                    array.Add(JObject.FromObject(option, serializer));
                }
            }

            array.WriteTo(writer);
        }
    }

}
