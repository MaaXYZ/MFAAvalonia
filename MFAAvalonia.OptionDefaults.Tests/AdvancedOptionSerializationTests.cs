using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class AdvancedOptionSerializationTests
{
    [Theory]
    [InlineData("[]", false)]
    [InlineData("[]", true)]
    [InlineData("null", false)]
    [InlineData("null", true)]
    public void EmptyAndNullAdvancedOptionsRoundTripWithoutChangingShape(string value, bool protocolMode)
    {
        var converter = new MaaInterfaceSelectAdvancedConverter(protocolMode);
        var task = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceTask>(
            "{\"name\":\"task\",\"entry\":\"entry\",\"advanced\":" + value + "}", converter)!;
        if (value == "[]") Assert.Empty(Assert.IsType<System.Collections.Generic.List<MaaInterface.MaaInterfaceSelectAdvanced>>(task.Advanced));
        else Assert.Null(task.Advanced);

        var serialized = JObject.Parse(JsonConvert.SerializeObject(task, converter));
        Assert.True(JToken.DeepEquals(JToken.Parse(value), serialized["advanced"]));
    }

    [Fact]
    public void DirectNullWriteProducesAJsonNullValue()
    {
        using var output = new StringWriter();
        using var writer = new JsonTextWriter(output);
        new MaaInterfaceSelectAdvancedConverter(false).WriteJson(writer, null!, new JsonSerializer());
        writer.Flush();
        Assert.Equal("null", output.ToString());
    }
}
