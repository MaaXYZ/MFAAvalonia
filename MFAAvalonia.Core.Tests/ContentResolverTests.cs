using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class ContentResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mfa-content-{Guid.NewGuid():N}");
    private readonly List<string> _missingFiles = [];
    private readonly List<string> _fetchedUrls = [];
    private const string Html = "<font color=\"DeepSkyBlue\">火影忍者，启动！</font>";

    public ContentResolverTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(Html)]
    [InlineData("<font color=\"darkgray\">尝试返回主页面</font>")]
    [InlineData("<div><b>{name}</b><br/><img src=\"images/task.png\"/></div>")]
    [InlineData("<p>HTML 和 **Markdown**</p>\n第二行")]
    [InlineData("[帮助](docs/help.html)")]
    [InlineData("![image](maa://image/123)")]
    [InlineData("[color:red]失败[/color]")]
    [InlineData("`resource/help.md`")]
    [InlineData("# docs/help.md")]
    [InlineData("进行周度签到")]
    [InlineData("版本 v1.4.11")]
    [InlineData("成功 / 失败")]
    [InlineData("$focus.message")]
    [InlineData("$focus.message.html")]
    public async Task InlineContentIsPreservedWithoutFileWarnings(string content)
    {
        Assert.Equal(content, await Resolve(content));
        Assert.Empty(_missingFiles);
        Assert.Empty(_fetchedUrls);
    }

    [Fact]
    public async Task InlineHtmlNeverReachesFilePathParsing()
    {
        // Any path operation on this base directory would fail. HTML must be
        // recognized as display content before filesystem probing starts.
        var resolved = await ContentResolver.ResolveAsync(Html, "\0", FetchUrl, _missingFiles.Add);
        Assert.Equal(Html, resolved);
        Assert.Empty(_missingFiles);
    }

    [Theory]
    [InlineData("notice.html")]
    [InlineData("notice.htm")]
    [InlineData("notice.HTML")]
    [InlineData("notice.md")]
    [InlineData("notice.markdown")]
    [InlineData("notice.txt")]
    [InlineData("notice.text")]
    [InlineData("notice.custom")]
    public async Task ExistingFilesAreReadAsContent(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, name), Html, TestContext.Current.CancellationToken);
        Assert.Equal(Html, await Resolve(name));
        Assert.Empty(_missingFiles);
    }

    [Theory]
    [InlineData("resource/descs/notice.html")]
    [InlineData("resource\\descs\\notice.htm")]
    [InlineData("{PROJECT_DIR}/resource/descs/notice.HTML")]
    [InlineData("{PROJECT_DIR}\\resource\\descs\\notice.HTM")]
    public async Task HtmlFilesSupportProjectPathsAndCaseFallback(string reference)
    {
        var directory = Path.Combine(_root, "Resource", "Descs");
        Directory.CreateDirectory(directory);
        var extension = reference.EndsWith("htm", StringComparison.OrdinalIgnoreCase) ? ".htm" : ".html";
        await File.WriteAllTextAsync(Path.Combine(directory, "Notice" + extension), Html, TestContext.Current.CancellationToken);
        Assert.Equal(Html, await Resolve(reference));
        Assert.Empty(_missingFiles);
    }

    [Fact]
    public async Task AbsoluteHtmlPathIsRead()
    {
        var path = Path.Combine(_root, "notice.html");
        await File.WriteAllTextAsync(path, Html, TestContext.Current.CancellationToken);
        Assert.Equal(Html, await Resolve(path));
        Assert.Empty(_missingFiles);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("project-file")]
    [InlineData("project-placeholder")]
    [InlineData("absolute-project-path")]
    public async Task WorkingDirectoryFallbackPreservesProjectPathPrecedence(string mode)
    {
        // Use a unique relative path without changing the process-wide current
        // directory, so other tests can run concurrently.
        var directoryName = $"mfa-cwd-{Guid.NewGuid():N}";
        var workingDirectory = Path.Combine(Environment.CurrentDirectory, directoryName);
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var reference = Path.Combine(directoryName, "notice.html");
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "notice.html"), Html,
                TestContext.Current.CancellationToken);

            if (mode == "project-file")
            {
                Directory.CreateDirectory(Path.Combine(_root, directoryName));
                await File.WriteAllTextAsync(Path.Combine(_root, reference), "project content",
                    TestContext.Current.CancellationToken);
            }

            var input = mode switch
            {
                "project-placeholder" => $"{{PROJECT_DIR}}/{reference}",
                "absolute-project-path" => Path.Combine(_root, reference),
                _ => reference
            };
            var expected = mode switch
            {
                "relative" => Html,
                "project-file" => "project content",
                _ => input
            };

            Assert.Equal(expected, await Resolve(input));
            Assert.Equal(mode is "project-placeholder" or "absolute-project-path" ? 1 : 0, _missingFiles.Count);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RelativeProjectDirectoryExpandsPlaceholderOnce()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "notice.html"), Html, TestContext.Current.CancellationToken);
        var relativeRoot = Path.GetRelativePath(Environment.CurrentDirectory, _root);
        Assert.Equal(Html, await ContentResolver.ResolveAsync(
            "{PROJECT_DIR}/notice.html", relativeRoot, FetchUrl, _missingFiles.Add));
        Assert.Empty(_missingFiles);
    }

    [Fact]
    public async Task FileWhoseContentEqualsItsNameIsStillSuccessfullyResolved()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "notice.html"), "notice.html", TestContext.Current.CancellationToken);
        Assert.Equal("notice.html", await Resolve("notice.html"));
        Assert.Empty(_missingFiles);
    }

    [Theory]
    [InlineData("notice.html")]
    [InlineData("resource\\notice.htm")]
    [InlineData("{PROJECT_DIR}/resource/notice.md")]
    [InlineData("./missing-content")]
    [InlineData("{PROJECT_DIR}/missing-content")]
    public async Task MissingContentFilesReportResolvedCandidate(string reference)
    {
        Assert.Equal(reference, await Resolve(reference));
        var candidate = Assert.Single(_missingFiles);
        Assert.True(Path.IsPathFullyQualified(candidate));
        Assert.StartsWith(_root, candidate);
        Assert.DoesNotContain("{PROJECT_DIR}", candidate);
        if (Path.DirectorySeparatorChar == '/')
            Assert.DoesNotContain('\\', candidate);
    }

    [Theory]
    [InlineData("https://example.com/notice.html")]
    [InlineData("https://example.com/notice.HTM?lang=zh#content")]
    [InlineData("http://example.com/notice.md")]
    public async Task ContentUrlsAreFetchedAndHtmlIsPreserved(string url)
    {
        Assert.Equal(Html, await Resolve(url));
        Assert.Equal(url, Assert.Single(_fetchedUrls));
        Assert.Empty(_missingFiles);
    }

    [Fact]
    public async Task OrdinaryUrlRemainsALink()
    {
        const string url = "https://example.com/help";
        Assert.Equal($"[{url}]({url})", await Resolve(url));
        Assert.Empty(_fetchedUrls);
        Assert.Empty(_missingFiles);
    }

    [Fact]
    public async Task CaseFallbackDoesNotReadOutsideProjectRoot()
    {
        var child = Path.Combine(_root, "project");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(_root, "Outside.html"), Html, TestContext.Current.CancellationToken);
        const string reference = "../outside.html";
        // On a case-insensitive volume, the exact lookup succeeds and is
        // intentionally allowed outside the root. Only the fallback is confined.
        var exactExists = File.Exists(Path.Combine(child, reference));
        Assert.Equal(exactExists ? Html : reference,
            await ContentResolver.ResolveAsync(reference, child, FetchUrl, _missingFiles.Add));
        Assert.Equal(exactExists ? 0 : 1, _missingFiles.Count);
    }

    [Fact]
    public async Task ReadErrorsPropagateInsteadOfBeingReportedAsMissingFiles()
    {
        var path = Path.Combine(_root, "locked.html");
        await File.WriteAllTextAsync(path, Html, TestContext.Current.CancellationToken);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => Resolve("locked.html"));
        Assert.Empty(_missingFiles);
    }

    private Task<string> Resolve(string content) =>
        ContentResolver.ResolveAsync(content, _root, FetchUrl, _missingFiles.Add);

    private Task<string> FetchUrl(string url)
    {
        _fetchedUrls.Add(url);
        return Task.FromResult(Html);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
