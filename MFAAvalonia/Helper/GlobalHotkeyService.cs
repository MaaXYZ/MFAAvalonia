using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper.HotkeyIpc;
using SharpHook;
using SharpHook.Data;
using SharpHook.Native;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MFAAvalonia.Helper;

public static class GlobalHotkeyService
{
    private static readonly ConcurrentDictionary<HotkeyIdentifier, ICommand> _commands = new();
    private static readonly ConcurrentDictionary<HotkeyIdentifier, string> _owners = new();
    private static IGlobalHook? _hook;
    private static HotkeyPrimaryElection? _election;
    private static HotkeyIpcServer? _server;
    private static HotkeyIpcClient? _client;
    private static Task? _hookTask;

    public static bool IsStopped { get; private set; }
    public static bool IsPrimary => _election?.IsPrimary == true;
    public static bool IsEnabled { get; private set; }

    public static void Initialize()
    {
        if (OperatingSystem.IsAndroid())
            return;
        if (_election != null) return;if (Design.IsDesignMode)
            return;
        IsStopped = false;
        // 使用全局互斥锁选举主进程
        // 与 Program.IsNewInstance 不同，这里对所有 MFAAvalonia 实例生效（不区分目录）
        _election = new HotkeyPrimaryElection();
        if (_election.TryBecomePrimary())
            InitializeAsPrimary();
        else
            InitializeAsSecondary();
    }

    private static void InitializeAsPrimary()
    {
        LoggerHelper.Info("全局热键服务：作为主进程初始化。");
        try
        {
            _server = new HotkeyIpcServer();
            ObserveBackgroundTask(_server.StartAsync(), "启动热键 IPC 服务");
            _hook = new SimpleGlobalHook();
            _hook.KeyPressed += HandleKeyEvent;
            _hookTask = _hook.RunAsync();
            ObserveBackgroundTask(_hookTask, "运行全局热键 Hook");
            var saved = HotkeyPrimaryElection.LoadState();
            if (saved != null) LoggerHelper.Info($"全局热键服务：已恢复 {saved.Length} 个热键。");
            IsEnabled = true;
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"初始化全局热键主进程失败：原因={e.Message}", e);
            ToastHelper.Error(LangKeys.GlobalHotkeyServiceError.ToLocalization());
        }
    }

    private static void InitializeAsSecondary()
    {
        LoggerHelper.Info("全局热键服务：作为子进程初始化。");
        _client = new HotkeyIpcClient();
        _client.HotkeyTriggered += h => ExecuteHotkey(h);
        _client.Disconnected += OnPrimaryDisconnected;
        // 在后台线程运行连接，避免阻塞 UI 线程
        _ = Task.Run(ConnectToPrimaryAsync);
    }

    private static async Task ConnectToPrimaryAsync()
    {
        LoggerHelper.Info("全局热键服务：开始连接主进程。");
        for (int i = 0; i < 10 && _client != null; i++)
        {
            try
            {
                LoggerHelper.Info($"全局热键服务：连接尝试 {i + 1}/10。");
                if (await _client.ConnectAsync())
                {
                    LoggerHelper.Info($"全局热键服务：已连接到主进程，准备注册 {_commands.Count} 个热键。");
                    IsEnabled = true;
                    foreach (var h in _commands.Keys){
                        LoggerHelper.Info($"全局热键服务：向主进程注册热键 {h}。");
                        await _client.RegisterHotkeyAsync(h);
                    }
                    return;
                }else
                {
                    LoggerHelper.Warning($"全局热键服务：第 {i + 1} 次连接返回 false。");
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"全局热键服务：第 {i + 1} 次连接异常，原因={ex.Message}");
            }
            await Task.Delay(500);
        }
        LoggerHelper.Warning("全局热键服务：无法连接主进程，准备尝试成为主进程。");
        await TryBecomePrimaryAsync();
    }

    private static async Task TryBecomePrimaryAsync()
    {
        _client?.Dispose();
        _client = null;
        
        // 在后台线程尝试成为主进程
        bool becamePrimary = await Task.Run(() => _election?.TryBecomePrimary() == true);
        
        if (becamePrimary)
        {
            InitializeAsPrimary();
        }
        else
        {
            _client = new HotkeyIpcClient();
            _client.HotkeyTriggered += h => ExecuteHotkey(h);
            _client.Disconnected += OnPrimaryDisconnected;
            await ConnectToPrimaryAsync();
        }
    }

    private static void OnPrimaryDisconnected()
    {
        IsEnabled = false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            bool primaryAlive = await Task.Run(() => HotkeyPrimaryElection.IsPrimaryAlive());
            if (!primaryAlive)
                await TryBecomePrimaryAsync();
            else
                await ConnectToPrimaryAsync();
        });
    }

    private static void ExecuteHotkey(HotkeyIdentifier h)
    {
        if (_commands.TryGetValue(h, out var cmd))
            TaskManager.RunTask(async () =>
            {
                try
                {
                    if (cmd.CanExecute(null)) await Dispatcher.UIThread.InvokeAsync(() => cmd.Execute(null), DispatcherPriority.Background);
                }
                catch (Exception ex) { LoggerHelper.Error($"执行热键命令失败：原因={ex.Message}", ex); }
            }, noMessage: true);
    }

    private static void HandleKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        var h = Normalize(e.Data.KeyCode, e.RawEvent.Mask);
        bool localRegistered = _commands.ContainsKey(h);
        bool remoteRegistered = _server?.HasSubscription(h) == true;
        
        LoggerHelper.Debug($"全局热键服务：检测到按键={h}，本地已注册={localRegistered}，远程已订阅={remoteRegistered}");
        
        if (localRegistered || remoteRegistered)
        {
            LoggerHelper.Info($"全局热键服务：已触发热键 {h}。");
            // 如果本地注册了，执行本地命令
            if (localRegistered)
                ExecuteHotkey(h);
            // 广播给所有订阅了该热键的客户端
            if (remoteRegistered)
                _ = _server?.BroadcastHotkeyTriggeredAsync(h);
        }
    }

    private static HotkeyIdentifier Normalize(KeyCode k, EventMask m)
    {
        const EventMask C = EventMask.LeftCtrl | EventMask.RightCtrl;
        const EventMask S = EventMask.LeftShift | EventMask.RightShift;
        const EventMask A = EventMask.LeftAlt | EventMask.RightAlt;
        const EventMask M = EventMask.LeftMeta | EventMask.RightMeta;
        var n = EventMask.None;
        if ((m & C) != 0) n |= C;
        if ((m & S) != 0) n |= S;
        if ((m & A) != 0) n |= A;
        if ((m & M) != 0) n |= M;
        return new HotkeyIdentifier(k, n);
    }

    public static bool Register(KeyGesture? g, ICommand c, string? ownerResourceKey, out string? occupiedByOwnerResourceKey)
    {
        occupiedByOwnerResourceKey = null;
        if (g == null || c == null) return true;
        var (k, m) = Convert(g);
        var h = new HotkeyIdentifier(k, m);
        LoggerHelper.Info($"全局热键服务：注册热键 {h}。");
        if (_commands.TryAdd(h, c))
        {
            if (!string.IsNullOrWhiteSpace(ownerResourceKey))
                _owners[h] = ownerResourceKey;
            if (_client?.IsConnected == true) _ = _client.RegisterHotkeyAsync(h);
            SaveState();
            return true;
        }
        _owners.TryGetValue(h, out occupiedByOwnerResourceKey);
        return false;
    }

    private static async void ObserveBackgroundTask(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (IsStopped)
        {
            LoggerHelper.Debug($"全局热键服务关闭时已取消：{operation}");
        }
        catch (SocketException ex) when (IsStopped || ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
            LoggerHelper.Debug($"全局热键服务关闭连接时已忽略：{operation}，原因={ex.Message}");
        }
        catch (HookException ex) when (IsStopped)
        {
            LoggerHelper.Debug($"全局热键服务关闭 Hook 时已忽略：{operation}，原因={ex.Message}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"全局热键后台任务失败：操作={operation}，原因={ex.Message}");
        }
    }

    public static void Unregister(KeyGesture? g)
    {
        if (g == null) return;
        var (k, m) = Convert(g);
        var h = new HotkeyIdentifier(k, m);
        if (_commands.TryRemove(h, out _))
        {
            _owners.TryRemove(h, out _);
            if (_client?.IsConnected == true) _ = _client.UnregisterHotkeyAsync(h);
            SaveState();
        }
    }

    private static void SaveState()
    {
        if (IsPrimary) HotkeyPrimaryElection.SaveState(_server?.GetAllRegisteredHotkeys().ToArray() ?? _commands.Keys.ToArray());
    }

    public static void Shutdown()
    {
        if (IsStopped) return;
        IsStopped = true;

        if (_hook != null)
        {
            _hook.KeyPressed -= HandleKeyEvent;
            try
            {
                _hook.Dispose();
            }
            catch (HookException ex)
            {
                LoggerHelper.Warning($"停止全局热键 Hook 失败，已继续退出：{ex.Message}");
            }
            _hook = null;
        }
        _hookTask = null;
        _client?.Dispose();
        _client = null;
        _server?.Dispose();
        _server = null;
        _election?.Dispose();
        _election = null;
        _commands.Clear();
        _owners.Clear();
        IsEnabled = false;
        HotkeyPrimaryElection.ClearState();
    }

    private static (KeyCode, EventMask) Convert(KeyGesture g)
    {
        var k = g.Key switch
        {
            Key.D0 => KeyCode.Vc0, Key.D1 => KeyCode.Vc1, Key.D2 => KeyCode.Vc2, Key.D3 => KeyCode.Vc3, Key.D4 => KeyCode.Vc4,
            Key.D5 => KeyCode.Vc5, Key.D6 => KeyCode.Vc6, Key.D7 => KeyCode.Vc7, Key.D8 => KeyCode.Vc8, Key.D9 => KeyCode.Vc9,
            Key.OemPlus => KeyCode.VcEquals, Key.OemMinus => KeyCode.VcMinus, Key.OemComma => KeyCode.VcComma, Key.OemPeriod => KeyCode.VcPeriod,
            Key.OemQuestion => KeyCode.VcSlash, Key.OemSemicolon => KeyCode.VcSemicolon, Key.OemQuotes => KeyCode.VcQuote,
            Key.OemOpenBrackets => KeyCode.VcOpenBracket, Key.OemCloseBrackets => KeyCode.VcCloseBracket, Key.OemPipe => KeyCode.VcBackslash, Key.OemTilde => KeyCode.VcBackQuote,
            Key.NumPad0 => KeyCode.VcNumPad0, Key.NumPad1 => KeyCode.VcNumPad1, Key.NumPad2 => KeyCode.VcNumPad2, Key.NumPad3 => KeyCode.VcNumPad3, Key.NumPad4 => KeyCode.VcNumPad4,
            Key.NumPad5 => KeyCode.VcNumPad5, Key.NumPad6 => KeyCode.VcNumPad6, Key.NumPad7 => KeyCode.VcNumPad7, Key.NumPad8 => KeyCode.VcNumPad8, Key.NumPad9 => KeyCode.VcNumPad9,
            Key.Add => KeyCode.VcNumPadAdd, Key.Subtract => KeyCode.VcNumPadSubtract, Key.Multiply => KeyCode.VcNumPadMultiply, Key.Divide => KeyCode.VcNumPadDivide, Key.Decimal => KeyCode.VcNumPadSeparator,
            Key.F1 => KeyCode.VcF1, Key.F2 => KeyCode.VcF2, Key.F3 => KeyCode.VcF3, Key.F4 => KeyCode.VcF4, Key.F5 => KeyCode.VcF5, Key.F6 => KeyCode.VcF6,
            Key.F7 => KeyCode.VcF7, Key.F8 => KeyCode.VcF8, Key.F9 => KeyCode.VcF9, Key.F10 => KeyCode.VcF10, Key.F11 => KeyCode.VcF11, Key.F12 => KeyCode.VcF12,
            Key.F13 => KeyCode.VcF13, Key.F14 => KeyCode.VcF14, Key.F15 => KeyCode.VcF15, Key.F16 => KeyCode.VcF16, Key.F17 => KeyCode.VcF17, Key.F18 => KeyCode.VcF18,
            Key.F19 => KeyCode.VcF19, Key.F20 => KeyCode.VcF20, Key.F21 => KeyCode.VcF21, Key.F22 => KeyCode.VcF22, Key.F23 => KeyCode.VcF23, Key.F24 => KeyCode.VcF24,
            Key.Up => KeyCode.VcUp, Key.Down => KeyCode.VcDown, Key.Left => KeyCode.VcLeft, Key.Right => KeyCode.VcRight,
            Key.Home => KeyCode.VcHome, Key.End => KeyCode.VcEnd, Key.PageUp => KeyCode.VcPageUp, Key.PageDown => KeyCode.VcPageDown, Key.Insert => KeyCode.VcInsert, Key.Delete => KeyCode.VcDelete,
            Key.Enter => KeyCode.VcEnter, Key.Space => KeyCode.VcSpace, Key.Tab => KeyCode.VcTab, Key.Back => KeyCode.VcBackspace, Key.Escape => KeyCode.VcEscape,
            Key.CapsLock => KeyCode.VcCapsLock, Key.NumLock => KeyCode.VcNumLock, Key.Scroll => KeyCode.VcScrollLock, Key.Pause => KeyCode.VcPause, Key.PrintScreen => KeyCode.VcPrintScreen,
            _ => Enum.TryParse<KeyCode>($"Vc{g.Key}", out var x) ? x : KeyCode.VcEscape
        };
        var m = EventMask.None;
        if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) m |= EventMask.LeftCtrl | EventMask.RightCtrl;
        if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) m |= EventMask.LeftAlt | EventMask.RightAlt;
        if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) m |= EventMask.LeftShift | EventMask.RightShift;
        if (g.KeyModifiers.HasFlag(KeyModifiers.Meta)) m |= EventMask.LeftMeta | EventMask.RightMeta;
        return (k, m);
    }
}
