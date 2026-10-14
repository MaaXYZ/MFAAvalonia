using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Extensions.MaaFW.Custom;
using MFAAvalonia.Helper;
using MFAAvalonia.Views.Windows;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MFAAvalonia.Extensions.MaaFW;

public static class MaaExtensions
{
    public class RecognitionQuery
    {
        [JsonProperty("all")] public List<RecognitionResult>? All;
        [JsonProperty("best")] public RecognitionResult? Best;
        [JsonProperty("filtered")] public List<RecognitionResult>? Filtered;
    }

    public class RecognitionResult
    {
        [JsonProperty("box")] public List<int>? Box;
        [JsonProperty("score")] public double? Score;
        [JsonProperty("text")] public string? Text;
    }

    public static bool IsHit(
        this RecognitionDetail? detail)
    {
        if (detail is null || detail.HitBox?.IsDefaultHitBox() == true || !detail.Hit)
            return false;
        return true;
    }

    private static bool IsDefaultHitBox(this IMaaRectBuffer hitBox)
    {
        return hitBox is null or { X: 0, Y: 0, Width: 0, Height: 0 };
    }

    public static MaaTaskJob AppendTask(this IMaaTasker maaTasker, MaaNode task)
    {
        return maaTasker.AppendTask(task.Name, task.ToString());
    }


    public static void Click(this IMaaTasker maaTasker, int x, int y)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.Click(x, y).Wait();
    }

    public static void Swipe(this IMaaTasker maaTasker, int x1, int y1, int x2, int y2, int duration)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.Swipe(x1, y1, x2, y2, duration).Wait();
    }

    public static void TouchDown(this IMaaTasker maaTasker, int contact, int x, int y, int pressure)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.TouchDown(contact, x, y, pressure).Wait();
    }

    public static void TouchMove(this IMaaTasker maaTasker, int contact, int x, int y, int pressure)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.TouchMove(contact, x, y, pressure).Wait();

    }
    public static void TouchUp(this IMaaTasker maaTasker, int contact)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.TouchUp(contact).Wait();
    }

    public static void PressKey(this IMaaTasker maaTasker, int key)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.ClickKey(key).Wait();
    }
    public static void InputText(this IMaaTasker maaTasker, string text)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.InputText(text).Wait();
    }

    public static void Screencap(this IMaaTasker maaTasker)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.Screencap().Wait();
    }

    public static bool GetCachedImage(this IMaaTasker maaTasker, IMaaImageBuffer imageBuffer)
    {
        if (maaTasker.IsStopping)
        {
            return false;
        }
        return maaTasker.Controller.GetCachedImage(imageBuffer);
    }

    public static void StartApp(this IMaaTasker maaTasker, string intent)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.StartApp(intent).Wait();
    }

    public static void StopApp(this IMaaTasker maaTasker, string intent)
    {
        if (maaTasker.IsStopping)
        {
            return;
        }
        maaTasker.Controller.StopApp(intent).Wait();
    }
    //
    public static void Click(this IMaaContext maaContext, int x, int y)
    {
        maaContext.Tasker.Click(x, y);
    }

    public static void Swipe(this IMaaContext maaContext, int x1, int y1, int x2, int y2, int duration)
    {
        maaContext.Tasker.Swipe(x1, y1, x2, y2, duration);
    }

    public static void TouchDown(this IMaaContext maaContext, int contact, int x, int y, int pressure)
    {
        maaContext.Tasker.TouchDown(contact, x, y, pressure);
    }

    public static void TouchMove(this IMaaContext maaContext, int contact, int x, int y, int pressure)
    {
        maaContext.Tasker.TouchMove(contact, x, y, pressure);

    }
    public static void SmoothTouchMove(this IMaaContext maaContext, int contact, int startX, int startY, int endX, int endY, int durationMs, int steps = 1)
    {
        if (steps <= 0)
        {
            throw new ArgumentException("步数必须大于0", nameof(steps));
        }

        if (durationMs <= 0)
        {
            throw new ArgumentException("持续时间必须大于0", nameof(durationMs));
        }

        var xStep = (endX - startX) / steps;
        var yStep = (endY - startY) / steps;

        for (var i = 0; i < steps; i++)
        {
            var currentX = startX + i * xStep;
            var currentY = startY + i * yStep;
            maaContext.TouchMove(contact, currentX, currentY, 100);
            int sleepTime = durationMs / steps;
            Thread.Sleep(sleepTime);
        }
    }
    public static void TouchUp(this IMaaContext maaContext, int contact)
    {
        maaContext.Tasker.TouchUp(contact);
    }

    public static void PressKey(this IMaaContext maaContext, int key)
    {
        maaContext.Tasker.PressKey(key);
    }
    public static void InputText(this IMaaContext maaContext, string text)
    {
        maaContext.Tasker.InputText(text);
    }

    public static void Screencap(this IMaaContext maaContext)
    {
        maaContext.Tasker.Screencap();
    }

    public static bool GetCachedImage(this IMaaContext maaContext, IMaaImageBuffer imageBuffer)
    {
        return maaContext.Tasker.GetCachedImage(imageBuffer);
    }

    public static IMaaImageBuffer? GetImage(this IMaaContext maaContext)
    {
        maaContext.Screencap();
        var imageBuffer = new MaaImageBuffer();
        if (!maaContext.GetCachedImage(imageBuffer))
        {
            // 如果获取图像失败，释放 buffer避免内存泄漏
            imageBuffer.Dispose();
            return null;
        }
        return imageBuffer;
    }

    public static IMaaImageBuffer GetImage(this IMaaContext maaContext, ref IMaaImageBuffer buffer)
    {
        maaContext.Screencap();
        if (!maaContext.GetCachedImage(buffer))
            return null;
        return buffer;
    }

    public static void StartApp(this IMaaContext maaContext, string intent)
    {
        maaContext.Tasker.StartApp(intent);
    }

    public static void StopApp(this IMaaContext maaContext, string intent)
    {
        maaContext.Tasker.StopApp(intent);
    }

    public static bool TemplateMatch(this IMaaTasker maaTasker, string template, double threshold = 0.8D, int x = 0, int y = 0, int w = 0, int h = 0)
    {
        var job = maaTasker.AppendTask(new MaaNode()
        {
            Template = [template],
            Recognition = "TemplateMatch",
            Threshold = threshold,
            Roi = new[]
            {
                x,
                y,
                w,
                h
            }
        });
        if (job.WaitFor(MaaJobStatus.Succeeded) == null)
            return false;
        return job.QueryRecognitionDetail().IsHit();
    }

    public static bool OCR(this IMaaTasker maaTasker, string text, int x = 0, int y = 0, int w = 0, int h = 0)
    {
        var job = maaTasker.AppendTask(new MaaNode
        {
            Expected = [text],
            Recognition = "OCR",
            Roi = new[]
            {
                x,
                y,
                w,
                h
            }
        });
        if (job.WaitFor(MaaJobStatus.Succeeded) == null)
            return false;
        return job.QueryRecognitionDetail().IsHit();
    }

    public static bool TemplateMatch(this IMaaContext maaContext,
        string template,
        IMaaImageBuffer imageBuffer,
        out RecognitionDetail? detail,
        double threshold = 0.8D,
        int x = 0,
        int y = 0,
        int w = 0,
        int h = 0,
        bool greenmask = false,
        string order = "Score", int index = 0)
    {
        detail = maaContext.RunRecognition(new MaaNode
        {
            Template = [template],
            GreenMask = greenmask,
            Recognition = "TemplateMatch",
            Threshold = threshold,
            OrderBy = order,
            Index = index,
            Roi = new[]
            {
                x,
                y,
                w,
                h
            },
        }, imageBuffer);
        LoggerHelper.Info(detail?.Detail);
        LoggerHelper.Info($"TemplateMatch: {template} ,roi: [{x},{y},{w},{h}], Hit: {detail.IsHit()}");
        return detail.IsHit();
    }

    public static bool ColorMatch(this IMaaContext maaContext,
        int ru,
        int gu,
        int bu,
        int rl,
        int gl,
        int bl,
        IMaaImageBuffer imageBuffer,
        out RecognitionDetail? detail,
        double threshold = 0.8D,
        int x = 0,
        int y = 0,
        int w = 0,
        int h = 0,
        int count = 1)
    {
        detail = maaContext.RunRecognition(new MaaNode
        {
            Upper = new List<int>
            {
                ru,
                gu,
                bu
            },
            Lower = new List<int>
            {
                rl,
                gl,
                bl
            },
            Count = count,
            Recognition = "ColorMatch",
            Threshold = threshold,
            OrderBy = "Score",
            Roi = new[]
            {
                x,
                y,
                w,
                h
            },
        }, imageBuffer);
        LoggerHelper.Info(detail?.Detail);
        LoggerHelper.Info($"ColorMatch: upper:{string.Join(",", new List<int>
        {
            rl,
            gl,
            bl
        })} lower::{string.Join(",", new List<int>
    {
        ru,
        gu,
        bu
    })} ,Hit: {detail.IsHit()}");
        return detail.IsHit();
    }

    public static bool OCR(this IMaaContext maaContext, string text, IMaaImageBuffer imageBuffer, out RecognitionDetail? detail, double threshold = 0.3D, int x = 0, int y = 0, int w = 0, int h = 0)
    {
        detail = maaContext.RunRecognition(new MaaNode
        {
            Expected = [text],
            Recognition = "OCR",
            Roi = new[]
            {
                x,
                y,
                w,
                h
            },
            Threshold = threshold
        }, imageBuffer);
        LoggerHelper.Info($"OCR: {text} ,Hit: {detail?.IsHit()}");
        return detail?.IsHit() == true;
    }

    public static RecognitionDetail? RunRecognition(this IMaaContext maaContext, MaaNode taskModel, IMaaImageBuffer imageBuffer)
    {
        if (maaContext.Tasker.IsStopping)
        {
            return null;
        }
        return maaContext.RunRecognition(taskModel.Name ?? "测试", imageBuffer, taskModel.ToJson() ?? "{}");
    }


    public static string GetText(this IMaaContext maaContext, int x, int y, int w, int h, IMaaImageBuffer imageBuffer)
    {
        if (maaContext.Tasker.IsStopping)
        {
            throw new MaaStopException();
        }
        var result = string.Empty;
        var taskModel = new MaaNode()
        {
            Name = "AppendOCR",
            Recognition = "OCR",
            Roi = new List<int>
            {
                x,
                y,
                w,
                h
            },
        };
        var detail = maaContext.RunRecognition(taskModel, imageBuffer);

        if (detail != null)
        {
            var query = JsonConvert.DeserializeObject<RecognitionQuery>(detail.Detail);
            if (!string.IsNullOrWhiteSpace(query?.Best?.Text))
                result = query.Best.Text;
        }
        else
        {
            // Instances.TaskQueueViewModel.AddLog("识别失败！", (IBrush?)null);
            var processor = MaaProcessor.Processors.FirstOrDefault(p => p.MaaTasker == maaContext.Tasker);
            processor?.ViewModel?.AddLog("识别失败！", (IBrush?)null);
        }

        LoggerHelper.Info($"识别结果: {result}");

        return result;
    }

    public static int ToInt(this string str)
    {
        string numberStr = new string(str.Replace(" ", "").Replace('b', '6').Replace('B', '8')
            .Where(char.IsDigit).ToArray());
        if (int.TryParse(numberStr, out int result))
        {
            return result;
        }
        return 0;
    }

    public static bool Until(
        this Func<bool> action,
        IMaaContext context,
        int sleepMilliseconds = 500,
        bool condition = true,
        int maxCount = 50,
        Action? errorAction = null,
        bool outE = false
    )
    {
        int count = 0;
        while (true)
        {
            if (context.Tasker.IsStopping)
            {
                throw new MaaStopException();
            }

            if (action() == condition)
                break;

            if (++count >= maxCount)
            {
                if (!outE)
                {
                    errorAction?.Invoke();
                    throw new MaaErrorHandleException();
                }
                else
                {
                    throw new Exception("条件未满足，超出最大尝试次数");
                }
            }

            if (sleepMilliseconds >= 0)
                Thread.Sleep(sleepMilliseconds);
        }

        return true;
    }
}
