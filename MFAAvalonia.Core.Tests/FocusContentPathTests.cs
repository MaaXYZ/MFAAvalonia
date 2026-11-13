using MFAAvalonia.Extensions.MaaFW;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class FocusContentPathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("进行周度签到")]
    [InlineData("<font color=\"DeepSkyBlue\">火影忍者，启动！</font>")]
    [InlineData("<font color=\"darkgray\">尝试返回主页面</font>")]
    [InlineData("<img src=\"images/status.png\" />")]
    [InlineData("[帮助](docs/help.md)")]
    [InlineData("![image](maa://image/123)")]
    [InlineData("[color:red]失败[/color]")]
    [InlineData("`resource/help.md`")]
    [InlineData("第一行\nresource/help.md")]
    [InlineData("第一行\rresource/help.md")]
    [InlineData("当前版本 v1.4.11")]
    [InlineData("Task completed.")]
    [InlineData("成功 / 失败")]
    [InlineData("https://example.com/help.md")]
    [InlineData("http://example.com/help.txt")]
    [InlineData("$focus.message")]
    public void InlineContentDoesNotTriggerMissingFileWarning(string content)
    {
        Assert.False(FocusContentPath.LooksLikeFilePath(content));
    }

    [Theory]
    [InlineData("help.md")]
    [InlineData("help.MARKDOWN")]
    [InlineData("help.txt")]
    [InlineData("help.text")]
    [InlineData("resource/descs/task.md")]
    [InlineData("resource\\descs\\task.md")]
    [InlineData("resource/task help.md")]
    [InlineData("./task help")]
    [InlineData(".\\task help")]
    [InlineData("../help")]
    [InlineData("..\\help")]
    [InlineData("/tmp/task help")]
    [InlineData("C:\\resource\\task help")]
    [InlineData("C:/resource/task help")]
    [InlineData("\\\\server\\share\\help")]
    [InlineData("{PROJECT_DIR}/resource/help")]
    [InlineData("resource/help")]
    [InlineData("resource\\help")]
    public void FileReferencesRetainMissingFileWarning(string content)
    {
        Assert.True(FocusContentPath.LooksLikeFilePath(content));
    }
}
