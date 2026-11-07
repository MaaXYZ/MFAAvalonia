using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.ViewModels.Windows;
using SukiUI.Dialogs;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MFAAvalonia.OptionDefaults.Tests;

public class OptionControlsTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<OptionControlsTestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Installs only the UI state and dialog services needed by the real option controls.
/// Skipping view-model constructors avoids processor creation, device discovery, and live-view timers.
/// </summary>
internal sealed class OptionControlsTestContext : IDisposable
{
    private readonly ConcurrentDictionary<Type, Lazy<object>> _serviceCache;
    private readonly Lazy<object>? _previousRoot;
    private readonly Lazy<object>? _previousDialogs;
    private readonly Lazy<object>? _previousVersionSettings;
    private bool _processorActive;

    public RootViewModel Root { get; }
    public TaskQueueViewModel TaskQueue { get; }
    public SukiDialogManager DialogManager { get; } = new();
    public ISukiDialog? Dialog { get; private set; }
    public int Saves { get; private set; }
    public TaskOptionGenerator Generator { get; }
    public Window Window { get; } = new() { Width = 900, Height = 600 };

    public OptionControlsTestContext()
    {
        Root = (RootViewModel)RuntimeHelpers.GetUninitializedObject(typeof(RootViewModel));
        Root.Idle = true;
        TaskQueue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
        _serviceCache = (ConcurrentDictionary<Type, Lazy<object>>)typeof(Instances)
            .GetField("ServiceCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _serviceCache.TryGetValue(typeof(RootViewModel), out _previousRoot);
        _serviceCache.TryGetValue(typeof(ISukiDialogManager), out _previousDialogs);
        _serviceCache.TryGetValue(typeof(VersionUpdateSettingsUserControlModel), out _previousVersionSettings);
        _serviceCache[typeof(RootViewModel)] = new Lazy<object>(() => Root);
        _serviceCache[typeof(ISukiDialogManager)] = new Lazy<object>(() => DialogManager);
        _serviceCache[typeof(VersionUpdateSettingsUserControlModel)] = new Lazy<object>(() =>
            RuntimeHelpers.GetUninitializedObject(typeof(VersionUpdateSettingsUserControlModel)));
        DialogManager.OnDialogShown += (_, args) => Dialog = args.Dialog;
        DialogManager.OnDialogDismissed += (_, _) => Dialog = null;
        Generator = (TaskOptionGenerator)typeof(TaskOptionGenerator)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(TaskQueueViewModel), typeof(Action), typeof(Func<bool>)], null)!
            .Invoke([TaskQueue, (Action)(() => Saves++), (Func<bool>)(() => _processorActive)]);
    }

    public void ResetSaveCount() => Saves = 0;

    public void SetProcessorActiveBeforeIdleNotification(bool active)
    {
        _processorActive = active;
    }

    public void Show(Control content)
    {
        Window.Content = content;
        Window.Show();
        Window.UpdateLayout();
    }

    public void Dispose()
    {
        Window.Close();
        DialogManager.DismissDialog();
        RestoreService(typeof(RootViewModel), _previousRoot);
        RestoreService(typeof(ISukiDialogManager), _previousDialogs);
        RestoreService(typeof(VersionUpdateSettingsUserControlModel), _previousVersionSettings);
    }

    private void RestoreService(Type type, Lazy<object>? previous)
    {
        if (previous == null) _serviceCache.TryRemove(type, out _);
        else _serviceCache[type] = previous;
    }
}
