using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.Windows;
using MFAAvalonia.Views.Mobile;
using SukiUI.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using MFAAvalonia.Extensions.MaaFW;

namespace MFAAvalonia.ViewModels.Windows;

public class AnnouncementItem
{
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public string Content { get; set; } = string.Empty;
}

public partial class AnnouncementViewModel : ViewModelBase
{
    public static readonly string AnnouncementFolder = "announcement";
    private static readonly string LegacyAnnouncementFolder = "Announcement";
    private static readonly object PublicAnnouncementLock = new();
    private static List<AnnouncementItem> _publicAnnouncementItems = new();

    [ObservableProperty] private AvaloniaList<AnnouncementItem> _announcementItems = new();
    [ObservableProperty] private AnnouncementItem? _selectedAnnouncement;
    [ObservableProperty] private string _announcementContent = string.Empty;
    [ObservableProperty] private bool _doNotRemindThisAnnouncementAgain = Convert.ToBoolean(
        GlobalConfiguration.GetValue(ConfigurationKeys.DoNotShowAnnouncementAgain, bool.FalseString));
    [ObservableProperty] private bool _isLoading = true;

    private static string GetAnnouncementDirectory()
    {
        var resourcePath = AppPaths.ResourceDirectory;
        var standardPath = Path.Combine(resourcePath, AnnouncementFolder);
        if (Directory.Exists(standardPath))
        {
            return standardPath;
        }

        var legacyPath = Path.Combine(resourcePath, LegacyAnnouncementFolder);
        return Directory.Exists(legacyPath) ? legacyPath : standardPath;
    }

    private static bool HasPublicAnnouncements()
    {
        lock (PublicAnnouncementLock)
        {
            return _publicAnnouncementItems.Count > 0;
        }
    }

    private CancellationTokenSource? _loadCts; // 加载取消令牌

    partial void OnDoNotRemindThisAnnouncementAgainChanged(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowAnnouncementAgain, value.ToString());
    }

    // 选中项变更时加载内容
    partial void OnSelectedAnnouncementChanged(AnnouncementItem? oldValue, AnnouncementItem? newValue)
    {
        if (newValue is null || oldValue == newValue) return;

        // 取消之前的加载任务
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();

        // 滚动到顶部
        _view?.Viewer?.ScrollViewer?.ScrollToHome();

        // 加载新内容（Markdown 组件会自动处理缓存）
        _ = LoadContentAsync(newValue, _loadCts.Token);
    }

    /// <summary>
    /// 加载公告内容
    /// </summary>
    private async Task LoadContentAsync(AnnouncementItem item, CancellationToken cancellationToken)
    {
        try
        {
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnnouncementContent = item.Content;
            }, DispatcherPriority.Normal, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 切换选中项时取消，忽略异常
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"加载公告内容失败: {item.FilePath}, 错误: {ex.Message}");
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                AnnouncementContent = $"### 加载失败\n{ex.Message}";
            });
        }
    }

    public static async Task AddAnnouncementAsync(string announcement, string? title = null, string? projectDir = null)
    {
        var resolvedContent = await announcement.ResolveContentAsync(projectDir).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolvedContent))
        {
            return;
        }

        ParseAnnouncement(resolvedContent, out var parsedTitle, out var remainingContent);
        var item = new AnnouncementItem
        {
            Title = string.IsNullOrWhiteSpace(parsedTitle) ? (title ?? "Welcome") : parsedTitle,
            Content = TaskQueueView.ConvertCustomMarkup(remainingContent)
        };

        var normalizedContent = NormalizeAnnouncementContent(item.Content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return;
        }

        if (_publicAnnouncementItems.Any(existing =>
                NormalizeAnnouncementContent(existing.Content).Equals(normalizedContent, StringComparison.Ordinal)))
        {
            return;
        }

        lock (PublicAnnouncementLock)
        {
            _publicAnnouncementItems.Add(item);
        }
    }

    /// <summary>
    /// Replaces PI-provided announcements and records their complete ordered declaration.
    /// </summary>
    public static async Task SetWelcomeAnnouncementsAsync(
        IReadOnlyList<MaaInterface.MaaInterfaceWelcome>? welcome,
        string? projectDir = null)
    {
        var declarations = welcome ?? [];
        var snapshot = JsonConvert.SerializeObject(
            declarations.Select(item => new { item.Label, item.Content, item.IsLegacyString }).ToList(),
            Formatting.None);
        var previousSnapshot = GlobalConfiguration.GetValue(
            ConfigurationKeys.WelcomeAnnouncementSnapshot, string.Empty);
        var shouldRecordSnapshot = welcome != null || !string.IsNullOrEmpty(previousSnapshot);
        if (shouldRecordSnapshot
            && !string.Equals(previousSnapshot, snapshot, StringComparison.Ordinal))
        {
            GlobalConfiguration.SetValue(ConfigurationKeys.WelcomeAnnouncementSnapshot, snapshot);
            GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowAnnouncementAgain, bool.FalseString);
        }

        var items = new List<AnnouncementItem>();
        foreach (var declaration in declarations)
        {
            if (string.IsNullOrWhiteSpace(declaration.Content))
            {
                LoggerHelper.Warning("已跳过缺少 content 的 welcome 公告。");
                continue;
            }

            var resolvedContent = await declaration.Content.ResolveContentAsync(projectDir)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolvedContent))
                continue;

            string title;
            string content;
            if (declaration.IsLegacyString)
            {
                ParseAnnouncement(resolvedContent, out var parsedTitle, out var remainingContent);
                title = parsedTitle;
                content = remainingContent;
            }
            else
            {
                title = LanguageHelper.GetLocalizedString(declaration.Label);
                content = resolvedContent;
            }

            var item = new AnnouncementItem
            {
                Title = title,
                FilePath = declaration.Content,
                Content = TaskQueueView.ConvertCustomMarkup(content),
            };
            if (!string.IsNullOrWhiteSpace(NormalizeAnnouncementContent(item.Content)))
                items.Add(item);
        }

        lock (PublicAnnouncementLock)
        {
            _publicAnnouncementItems = items;
        }
    }

    /// <summary>
    /// 加载公告元数据（Markdown 文件列表）
    /// </summary>
    private async Task LoadAnnouncementMetadataAsync()
    {
        try
        {
            IsLoading = true;

            var announcementDir = GetAnnouncementDirectory();

            // 后台线程获取 Markdown 文件列表并读取内容
            var tempItems = await Task.Run(() =>
            {
                var items = new List<AnnouncementItem>();
                if (!Directory.Exists(announcementDir))
                    return items;

                var mdFiles = Directory.GetFiles(announcementDir, "*.md")
                    .OrderBy(Path.GetFileName)
                    .ToList();

                foreach (var mdFile in mdFiles)
                {
                    try
                    {
                        // 读取第一行作为标题（Markdown 标题可能以 # 开头）
                        var fileContent = File.ReadAllText(mdFile);
                        ParseAnnouncement(fileContent, out var title, out var content);
                        items.Add(new AnnouncementItem
                        {
                            Title = title,
                            FilePath = mdFile,
                            Content = TaskQueueView.ConvertCustomMarkup(content),
                            LastModified = File.GetLastWriteTime(mdFile)
                        });
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error($"读取公告元数据失败: {mdFile}, 错误: {ex.Message}");
                    }
                }
                return items;
            }).ConfigureAwait(false);

            // UI 线程更新公告列表
            var tempContentSet = tempItems
                .Select(item => NormalizeAnnouncementContent(item.Content))
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .ToHashSet(StringComparer.Ordinal);

            List<AnnouncementItem> publicItems;
            lock (PublicAnnouncementLock)
            {
                publicItems = _publicAnnouncementItems
                    .Where(item => !tempContentSet.Contains(NormalizeAnnouncementContent(item.Content)))
                    .ToList();
            }

            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                AnnouncementItems.Clear();
                AnnouncementItems.AddRange(tempItems);
                AnnouncementItems.AddRange(publicItems);
                LoggerHelper.Info($"公告数量：{AnnouncementItems.Count}");
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"加载公告元数据失败: {ex.Message}");
        }
        finally
        {
            await DispatcherHelper.RunOnMainThreadAsync(() => IsLoading = false);
        }
    }

    // 拆分 Markdown 第一行（标题）和剩余内容
    public static void SplitFirstLine(string content, out string firstLine, out string remainingContent)
    {
        if (string.IsNullOrEmpty(content))
        {
            firstLine = "";
            remainingContent = "";
            return;
        }

        var newLineCandidates = new[]
        {
            "\r\n",
            "\n",
            "\r"
        };
        int firstNewLineIndex = int.MaxValue;
        string? matchedNewLine = null;

        foreach (var nl in newLineCandidates)
        {
            int index = content.IndexOf(nl);
            if (index != -1 && index < firstNewLineIndex)
            {
                firstNewLineIndex = index;
                matchedNewLine = nl;
            }
        }

        if (matchedNewLine == null)
        {
            firstLine = content;
            remainingContent = "";
        }
        else
        {
            firstLine = content.Substring(0, firstNewLineIndex);
            // 保留剩余内容的 Markdown 格式（包含换行符）
            remainingContent = content.Substring(firstNewLineIndex + matchedNewLine.Length);
        }
    }

    private static void ParseAnnouncement(string content, out string title, out string body)
    {
        SplitFirstLine(content, out var firstLine, out var remainingContent);
        title = firstLine.TrimStart('#', ' ').Trim();
        body = string.IsNullOrWhiteSpace(remainingContent) ? content : remainingContent;

        // Strip YAML front matter before handing the document to Markdown.Avalonia.
        // Announcement files commonly start with `---` and end the metadata with a
        // second `---`; the title field remains the list item title.
        if (!string.Equals(firstLine.Trim(), "---", StringComparison.Ordinal))
            return;

        var lines = remainingContent.Replace("\r\n", "\n").Split('\n');
        var closingIndex = Array.FindIndex(lines, line => line.Trim() == "---");
        if (closingIndex < 0)
            return;

        var titleLine = lines.FirstOrDefault(line =>
            line.TrimStart().StartsWith("title:", StringComparison.OrdinalIgnoreCase));
        if (titleLine != null)
            title = titleLine[(titleLine.IndexOf(':') + 1)..].Trim().Trim('"', '\'');

        body = string.Join("\n", lines.Skip(closingIndex + 1)).TrimStart('\n');
    }

    private static string NormalizeAnnouncementContent(string? content)
    {
        return string.IsNullOrWhiteSpace(content)
            ? string.Empty
            : content.Replace("\r\n", "\n").Trim();
    }

    private AnnouncementView? _view;

    public void SetView(AnnouncementView? view)
    {
        _view = view;
    }

    public static async Task CheckAnnouncement(bool forceShow = false)
    {
        try
        {
            var viewModel = new AnnouncementViewModel();

            // 如果不是强制显示且用户选择了不再提醒，直接返回
            if (!forceShow && viewModel.DoNotRemindThisAnnouncementAgain)
            {
                return;
            }

            var announcementDir = GetAnnouncementDirectory();

            var scanResult = await Task.Run(() =>
            {
                if (!Directory.Exists(announcementDir))
                {
                    return (exists: false, hasAnnouncements: HasPublicAnnouncements());
                }

                var hasAnnouncements = Directory.EnumerateFiles(announcementDir, "*.md").Any()
                                       || HasPublicAnnouncements();
                return (exists: true, hasAnnouncements);
            }).ConfigureAwait(false);

            if (!scanResult.exists && !scanResult.hasAnnouncements)
            {
                LoggerHelper.Warning($"公告文件夹不存在: {announcementDir}");
                return;
            }

            if (!scanResult.hasAnnouncements)
            {
                await DispatcherHelper.RunOnMainThreadAsync(() =>
                    ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.AnnouncementEmpty.ToLocalization()));
                return;
            }

            if (OperatingSystem.IsAndroid())
            {
                await viewModel.LoadAnnouncementMetadataAsync();

                if (!viewModel.AnnouncementItems.Any())
                {
                    await DispatcherHelper.RunOnMainThreadAsync(() =>
                        ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.AnnouncementEmpty.ToLocalization()));
                    return;
                }

                DispatcherHelper.PostOnMainThread(() =>
                {
                    var announcementContent = new MobileAnnouncementView(viewModel);
                    viewModel.SelectedAnnouncement = viewModel.AnnouncementItems[0];
                    Instances.DialogManager.CreateDialog()
                        .WithTitle(LangKeys.Announcement.ToLocalization())
                        .WithContent(announcementContent)
                        .WithActionButton(LangKeys.ShowDisclaimerNoMore.ToLocalization(), _ =>
                        {
                            GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowAnnouncementAgain, bool.TrueString);
                        })
                        .WithActionButton(LangKeys.Ok.ToLocalization(), _ => { }, true)
                        .TryShow();
                });

                return;
            }

            var announcementView = await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                var view = new AnnouncementView
                {
                    DataContext = viewModel
                };
                viewModel.SetView(view);
                view.Show();
                return view;
            });

            // 异步加载公告元数据
            await viewModel.LoadAnnouncementMetadataAsync();

            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                if (!viewModel.AnnouncementItems.Any())
                {
                    if (forceShow)
                    {
                        ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.AnnouncementEmpty.ToLocalization());
                    }

                    announcementView.Close();
                    return;
                }

                // 选中第一个公告
                viewModel.SelectedAnnouncement = viewModel.AnnouncementItems[0];
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"显示公告窗口失败: {ex.Message}");
        }
    }

    // 窗口关闭时清理资源
    public void Cleanup()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        // 清理视图引用
        _view = null;
        
        // 清理公告内容
        AnnouncementContent = string.Empty;
        SelectedAnnouncement = null;
        AnnouncementItems.Clear();
        _loadCts = null;
    }
}
