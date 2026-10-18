using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MaaFramework.Binding;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Extensions.MaaFW.Custom;

/// <summary>
/// 解析 MaaFramework 传递的 custom_action_param 字符串。
/// MaaFramework 可能将 JObject 序列化为双重编码的字符串（如 "{\"seconds\":60}"），
/// 此工具类会自动处理这种情况。
/// </summary>
public static class ActionParamHelper
{
    public static void ClickWithProgress<T>(this T context, int x, int y) where T : IMaaContext
    {
        context.Click(x, y);
        ResolveOwnerProcessor(context)?.RecordCustomActionActivity();
    }

    public static void SwipeWithProgress<T>(this T context, int x1, int y1, int x2, int y2, int duration) where T : IMaaContext
    {
        context.Swipe(x1, y1, x2, y2, duration);
        ResolveOwnerProcessor(context)?.RecordCustomActionActivity();
    }

    public static void TouchDownWithProgress<T>(this T context, int contact, int x, int y, int pressure) where T : IMaaContext
    {
        context.TouchDown(contact, x, y, pressure);
        ResolveOwnerProcessor(context)?.RecordCustomActionActivity();
    }

    public static void TouchMoveWithProgress<T>(this T context, int contact, int x, int y, int pressure) where T : IMaaContext
    {
        context.TouchMove(contact, x, y, pressure);
        ResolveOwnerProcessor(context)?.RecordCustomActionActivity();
    }

    public static void TouchUpWithProgress<T>(this T context, int contact) where T : IMaaContext
    {
        context.TouchUp(contact);
        ResolveOwnerProcessor(context)?.RecordCustomActionActivity();
    }

    /// <summary>根据任务执行器定位所属实例，避免多实例时把进度记到当前选中的其它实例。</summary>
    public static MaaProcessor? ResolveOwnerProcessor(IMaaContext? context)
        => MaaProcessorManager.Instance.Instances.FirstOrDefault(processor => ReferenceEquals(processor.MaaTasker, context?.Tasker))
           ?? MaaProcessorManager.Instance.Current;

    public static JObject Parse(string actionParam)
    {
        if (string.IsNullOrWhiteSpace(actionParam))
            return new JObject();

        // 先尝试直接解析为 JObject
        try
        {
            return JObject.Parse(actionParam);
        }
        catch
        {
            // 如果失败，可能是双重编码的字符串，先反序列化外层字符串
            try
            {
                var unwrapped = JsonConvert.DeserializeObject<string>(actionParam);
                if (!string.IsNullOrWhiteSpace(unwrapped))
                    return JObject.Parse(unwrapped);
            }
            catch
            {
                // ignored
            }
        }

        return new JObject();
    }

    public static void ThrowIfStopping(IMaaContext context)
    {
        if (context.Tasker.IsStopping)
        {
            throw new MaaStopException();
        }
    }

    public static void SleepWithStopCheck(IMaaContext context, int totalMilliseconds, int sliceMilliseconds = 200)
    {
        if (totalMilliseconds <= 0)
        {
            ThrowIfStopping(context);
            return;
        }

        var remaining = totalMilliseconds;
        while (remaining > 0)
        {
            ThrowIfStopping(context);

            var sleep = Math.Min(remaining, sliceMilliseconds);
            Thread.Sleep(sleep);
            remaining -= sleep;
        }
    }

    public static HttpResponseMessage SendHttpWithStopCheck(
        IMaaContext context,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        ThrowIfStopping(context);

        using var cts = new CancellationTokenSource();
        // Capture the token value before cleanup. Reading cts.Token after the
        // source is disposed throws ObjectDisposedException.
        var cancellationToken = cts.Token;
        var monitor = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (context.Tasker.IsStopping)
                    {
                        cts.Cancel();
                        break;
                    }

                    await Task.Delay(200, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        });

        try
        {
            return sendAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (context.Tasker.IsStopping)
        {
            throw new MaaStopException();
        }
        finally
        {
            // Stop the monitor and wait for it before disposing the source.
            // This prevents an unobserved monitor exception during finalization.
            cts.Cancel();
            monitor.GetAwaiter().GetResult();
        }
    }
}
