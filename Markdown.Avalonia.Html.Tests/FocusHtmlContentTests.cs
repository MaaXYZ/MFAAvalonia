using Avalonia.Media;
using ColorTextBlock.Avalonia;
using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Markdown.Avalonia.Html.Tests;

public class FocusHtmlContentTests
{
    [Theory]
    [InlineData("inline", "DeepSkyBlue", "火影忍者，启动！")]
    [InlineData("file", "DeepSkyBlue", "火影忍者，启动！")]
    [InlineData("url", "DeepSkyBlue", "火影忍者，启动！")]
    [InlineData("inline", "darkgray", "尝试返回主页面")]
    [InlineData("file", "darkgray", "尝试返回主页面")]
    [InlineData("url", "darkgray", "尝试返回主页面")]
    public async Task ResolvedHtmlRendersTextAndFontColor(string source, string color, string text)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mfa-focus-html-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var html = $"<font color=\"{color}\">{text}</font>";
            await File.WriteAllTextAsync(Path.Combine(root, "focus.html"), html, TestContext.Current.CancellationToken);
            var input = source switch
            {
                "file" => "focus.html",
                "url" => "https://example.com/focus.html",
                _ => html
            };
            var missingFiles = new List<string>();
            var resolved = await ContentResolver.ResolveAsync(input, root, _ => Task.FromResult(html), missingFiles.Add);
            Assert.Equal(html, resolved);
            Assert.Empty(missingFiles);

            var engine = new global::Markdown.Avalonia.Markdown
            {
                Plugins = new global::Markdown.Avalonia.Full.MdAvPlugins()
            };
            var inlines = engine.ParseGamutInline(resolved).ToArray();
            Assert.Equal(text, string.Concat(inlines.Select(inline => inline.AsString())));
            var font = Assert.Single(inlines.OfType<CSpan>(), span => span.Classes.Contains("Font"));
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(font.Foreground);
            Assert.Equal(Color.Parse(color), brush.Color);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
