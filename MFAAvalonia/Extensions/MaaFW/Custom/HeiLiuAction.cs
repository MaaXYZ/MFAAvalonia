using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Helper;
using System;
using System.Threading;

namespace MFAAvalonia.Extensions.MaaFW.Custom;

public class HeiLiuAction : IMaaCustomAction
{
    private readonly Action<string, string> _addLog;

    public HeiLiuAction(Action<string, string> addLog)
    {
        _addLog = addLog;
    }

    public string Name { get; set; } = "HeiLiuAction";
    public long Money = 0;

    private void AddLogByColor(string content, string color = "")
    {
        _addLog(content, color);
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        try
        {
            return Execute(context, args);
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Info("Stopping MaaCustomAction");
            return false;
        }
        catch (MaaStopException)
        {
            LoggerHelper.Info("Stopping MaaCustomAction");
            return false;
        }
        catch (MaaErrorHandleException)
        {
            LoggerHelper.Info("ErrorHandle MaaCustomAction");
            try
            {
                ErrorHandle(context, args);
                return Run(context, args, results);
            }
            catch (Exception e)
            {
                LoggerHelper.Error(e);
                return false;
            }
        }
    }

    protected bool Execute(IMaaContext context, RunArgs args)
    {
        IMaaImageBuffer image = new MaaImageBuffer();
        context.GetImage(ref image);
        if ((long)image.Width * 9 != (long)image.Height * 16)
        {
            AddLogByColor(
                $"警告：当前分辨率为 {image.Width}x{image.Height}，黑流任务要求画面比例为 16:9，任务已取消。",
                "OrangeRed");
            return false;
        }

        RecognitionDetail? detail;
        bool shouldContinue = true;
        //进入
        while (shouldContinue)
        {
            context.GetImage(ref image);
            var combatCount = 0;
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("Roguelike@ExitThenAbandon.png", image, out detail, 0.75, 0, 0, 130, 60) && detail?.HitBox != null)
                {
                    Leave(context, args);
                }
                if (context.TemplateMatch("放弃本次探索.png", image, out detail, 0.75, 1002, 184, 274, 250) && detail?.HitBox != null)
                {
                    Leave(context, args, true);
                }
                if (context.TemplateMatch("开始探索.png", image, out detail, 0.84, 1006, 470, 264, 250) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                if (context.TemplateMatch("继续探索.png", image, out detail, 0.9, 1006, 470, 264, 250) && detail?.HitBox != null)
                {
                    if (!context.TemplateMatch("放弃本次探索.png", image, out var de1, 0.75, 1002, 184, 274, 250))
                    {
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                        Leave(context, args, hasBacked: true);
                    }
                }
                return false;
            })).Until(context);
            Thread.Sleep(1000);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("特勤.png", image, out detail, 0.9, 142, 192, 1107, 389) && detail?.HitBox != null)
                {
                    return true;
                }
                if (context.TemplateMatch("特勤任务影像确认.png", image, out detail, 0.9, 598, 621, 79, 66) && detail?.HitBox != null)
                {
                    Thread.Sleep(100);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    Thread.Sleep(1000);
                    return true;
                }
                if (context.TemplateMatch("获得藏品或零件.png", image, out detail, 0.8, 592, 497, 110, 103) && detail?.HitBox != null)
                {
                    Thread.Sleep(100);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    Thread.Sleep(1000);
                    return true;
                }
                return false;
            })).Until(context);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("特勤.png", image, out detail, 0.9, 142, 192, 1107, 389) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                context.Swipe(885, 406, 599, 406, 200);
                context.Swipe(599, 406, 599, 806, 200);
                return false;
            })).Until(context);
            Thread.Sleep(500);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("特勤.png", image, out detail, 0.9, 142, 192, 1107, 389) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                }
                if (context.TemplateMatch("确定.png", image, out detail, 0.9, 22, 569, 1251, 51) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                return false;
            })).Until(context);

            Thread.Sleep(1000);

            int cz = 0;
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("稳扎稳打.png", image, out detail, 0.9, 294, 160, 330, 456) && detail?.HitBox != null)
                {
                    Thread.Sleep(200);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                if (cz != 1 && context.TemplateMatch("调查预付款.png", image, out detail, 0.9, 214, 205, 840, 292) && detail?.HitBox != null)
                {
                    Thread.Sleep(200);
                    cz = 1;
                    context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                }
                if (cz != 1 && context.TemplateMatch("林间代步.png", image, out detail, 0.9, 214, 205, 840, 292) && detail?.HitBox != null)
                {
                    Thread.Sleep(200);
                    cz = 1;
                    context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                }
                if (cz != 1 && context.TemplateMatch("退行补偿.png", image, out detail, 0.9, 214, 205, 840, 292) && detail?.HitBox != null)
                {
                    Thread.Sleep(200);
                    cz = 1;
                    context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                }

                if (cz != 1 && context.TemplateMatch("空间租凭.png", image, out detail, 0.9, 214, 205, 840, 292) && detail?.HitBox != null)
                {
                    Thread.Sleep(200);
                    cz = 1;
                    context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                }

                // if (cz != 1 && context.TemplateMatch("巢寄生.png", image, out detail, 0.9, 214, 205, 840, 292) && detail?.HitBox != null)
                // {
                //     Thread.Sleep(200);
                //     cz = 1;
                //     context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                // }
                // if (cz != 1 && context.TemplateMatch("更多补给品.png", image, out detail, 0.9, 214, 205, 840, 292) && detail?.HitBox != null)
                // {
                //     Thread.Sleep(200);
                //     cz = 1;
                //     context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                // }

                if (cz == 1 && context.TemplateMatch("确定.png", image, out detail, 0.8, 263, 564, 747, 61) && detail?.HitBox != null)
                {
                    Thread.Sleep(1200);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                }
                if (cz == 1 && context.TemplateMatch("获得藏品或零件.png", image, out detail, 0.8, 592, 497, 110, 103) && detail?.HitBox != null)
                {
                    Thread.Sleep(500);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                }
                return false;
            })).Until(context, sleepMilliseconds: 500);
            Thread.Sleep(1000);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("稳扎稳打.png", image, out detail, 0.9, 294, 160, 330, 456) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                }

                if (context.TemplateMatch("确定.png", image, out detail, 0.9, 352, 536, 205, 113) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                return false;
            })).Until(context);
            Thread.Sleep(400);
            for (int i = 0; i < 1; i++)
            {
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("招募.png", image, out detail, 0.9, 222, 374, 261, 256) && detail?.HitBox != null)
                    {
                        Thread.Sleep(200);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(1000);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("招募.png", image, out detail, 0.9, 222, 374, 261, 256) && detail?.HitBox != null)
                    {
                        Thread.Sleep(600);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                    }
                    if (context.TemplateMatch("机械师.png", image, out detail, 0.85, 383, 45, 897, 599) && detail?.HitBox != null)
                    {

                        Thread.Sleep(100);
                        return true;
                    }
                    // context.Swipe(885, 406, 599, 406, 200);
                    // context.Swipe(599, 406, 599, 706, 200);
                    Thread.Sleep(500);
                    return false;
                })).Until(context);
                Thread.Sleep(500);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("一技能.png", image, out detail, 0.9, 0, 359, 144, 141) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        return true;
                    }
                    if (context.TemplateMatch("机械师.png", image, out detail, 0.85, 383, 45, 897, 599) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(200);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("确认招募.png", image, out detail, 0.9, 1036, 623, 242, 97) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(100);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("确认招募.png", image, out detail, 0.9, 1036, 623, 242, 97) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                        return true;
                    }
                    return false;
                })).Until(context);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("Roguelike@ExitThenAbandon.png", image, out detail, 0.9, 0, 0, 78, 66) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        return true;
                    }
                    context.Click(1233, 13);
                    return false;
                })).Until(context);
            }
            Thread.Sleep(200);
            for (int i = 0; i < 2; i++)
            {
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("招募.png", image, out detail, 0.9, 188, 471, 1030, 158) && detail?.HitBox != null)
                    {
                        Thread.Sleep(200);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(1000);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("招募.png", image, out detail, 0.9, 188, 471, 1030, 158) && detail?.HitBox != null)
                    {
                        Thread.Sleep(600);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                    }
                    if (context.TemplateMatch("放弃招募.png", image, out detail, 0.9, 817, 600, 260, 120) && detail?.HitBox != null)
                    {

                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(1000);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("放弃招募.png", image, out detail, 0.9, 817, 600, 260, 120) && detail?.HitBox != null)
                    {

                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                    }
                    if (context.TemplateMatch("确认放弃.png", image, out detail, 0.9, 594, 410, 533, 168) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X + 70, detail.HitBox.Y + 20);
                        return true;
                    }
                    return false;
                })).Until(context);
            }
            Thread.Sleep(1000);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("沉沦于树海.png", image, out detail, 0.9, 1030, 223, 250, 352) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                return false;
            })).Until(context);
            Thread.Sleep(5000);
            // ((Func<bool>)(() =>
            // {
            //     context.GetImage(ref image);
            //     if (context.TemplateMatch("通宝确认.png", image, out detail, 0.9, 1018, 459, 187, 162) && detail?.HitBox != null)
            //     {
            //         Thread.Sleep(800);
            //         context.Click(detail.HitBox.X, detail.HitBox.Y);
            //         return true;
            //     }
            //     context.Click(631, 610);
            //     return false;
            // })).Until(context, maxCount: 50, errorAction: () => context.Click(1103, 528));
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("rougelikeInfo.png", image, out detail, 0.9, 73, 0, 105, 98) && detail?.HitBox != null)
                {
                    Thread.Sleep(100);
                    return true;
                }
                return false;
            })).Until(context);
            Thread.Sleep(200);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("行动力.png", image, out detail, 0.9, 1149, 71, 118, 116) && detail?.HitBox != null)
                {
                    Thread.Sleep(100);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                return false;
            })).Until(context);
            Thread.Sleep(500);
            ((Func<bool>)(() =>
            {
                context.GetImage(ref image);
                if (context.TemplateMatch("结构性原理1.png", image, out detail, 0.9, 845, 155, 435, 565) && detail?.HitBox != null)
                {
                    Thread.Sleep(100);
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                    return true;
                }
                return false;
            })).Until(context);
            Thread.Sleep(1000);
            context.Click(845, 155);
            Thread.Sleep(200);
            context.Click(845, 155);
            // Thread.Sleep(200);
            // ((Func<bool>)(() =>
            // {
            //     context.GetImage(ref image);
            //     if (context.TemplateMatch("零件箱.png", image, out detail, 0.9, 646, 614, 196, 106) && detail?.HitBox != null)
            //     {
            //         Thread.Sleep(100);
            //         context.Click(detail.HitBox.X, detail.HitBox.Y);
            //         return true;
            //     }
            //     return false;
            // })).Until(context);
            // Thread.Sleep(1000);
            // ((Func<bool>)(() =>
            // {
            //     context.GetImage(ref image);
            //     if (context.TemplateMatch("结构性原理.png", image, out detail, 0.9, 324, 94, 918, 547) && detail?.HitBox != null)
            //     {
            //         Thread.Sleep(100);
            //         context.Click(detail.HitBox.X, detail.HitBox.Y);
            //         return true;
            //     }
            //     return false;
            // })).Until(context);
            // Thread.Sleep(1000);
            // ((Func<bool>)(() =>
            // {
            //     context.GetImage(ref image);
            //     if (context.TemplateMatch("装载零件.png", image, out detail, 0.9, 558, 417, 397, 171) && detail?.HitBox != null)
            //     {
            //         Thread.Sleep(100);
            //         context.Click(detail.HitBox.X, detail.HitBox.Y);
            //         return true;
            //     }
            //     return false;
            // })).Until(context);
            // Thread.Sleep(1000);
            // ((Func<bool>)(() =>
            // {
            //     context.GetImage(ref image);
            //     if (context.TemplateMatch("离开原件.png", image, out detail, 0.9, 5, 3, 87, 63) && detail?.HitBox != null)
            //     {
            //         Thread.Sleep(100);
            //         context.Click(detail.HitBox.X, detail.HitBox.Y);
            //         return true;
            //     }
            //     return false;
            // })).Until(context);
            var IsTrade = false;
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1000);

                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("诡意行商.png", image, out detail, 0.8, 194, 81, 898, 552) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        return true;
                    }
                    if (context.TemplateMatch("未知.png", image, out detail, 0.8, 194, 81, 898, 552, order: "Horizontal", index: -1) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(200);

                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("诡意行商.png", image, out detail, 0.8, 194, 81, 898, 552) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        IsTrade = true;
                        context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                        return true;
                    }
                    if (context.TemplateMatch("未知.png", image, out detail, 0.8, 194, 81, 898, 552, order: "Horizontal", index: -1) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(1000);
                ((Func<bool>)(() =>
                {
                    context.GetImage(ref image);
                    if (context.TemplateMatch("出发前往.png", image, out detail, 0.8, 913, 460, 367, 180) && detail?.HitBox != null)
                    {
                        Thread.Sleep(100);
                        context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                        return true;
                    }
                    return false;
                })).Until(context);
                Thread.Sleep(1000);
                if (IsTrade) break;
                var hasSeed = false;
                ((Func<bool>)(() =>
               {
                   var image = context.GetImage();
                   if (context.TemplateMatch("Roguelike@StageTraderInvestSystem.png", image, out detail, 0.75, 400, 172, 230, 150))
                   {
                       IsTrade = true;
                       return true;
                   }
                   if (context.TemplateMatch("不期而遇.png", image, out detail, 0.75, 0, 412, 321, 243))
                   {
                       IsTrade = false;
                       return true;
                   }
                   if (context.TemplateMatch("机械师的园圃.png", image, out detail, 0.8, 400, 190, 141, 139))
                   {
                       hasSeed = true;
                       return true;
                   }
                   return false;
               })).Until(context);
                if (IsTrade) break;
                MeetByChance(context);
            }

            if (SaveMoney(context, args) && IsTrade)
            {
                throw new Exception("999源石锭!");
            }
            else
            {
                Leave(context, args);
                return true;
            }
            // int i1 = 0, res;
            // var level = () =>
            // {
            //     if (i1++ == 0)
            //     {
            //         res = Find(context, 280, 120, 220, 460, args.NodeName, true);
            //     }
            //     else
            //     {
            //         res = Find(context, 480, 90, 485, 520, args.NodeName);
            //     }
            //     switch (res)
            //     {
            //         case -1:
            //             return true;
            //         case 0:
            //             Thread.Sleep(700);
            //             var enteringEncouter = () =>
            //             {
            //                 context.GetImage(ref image);
            //                 if (context.TemplateMatch("进入不期而遇.png", image, out detail, 0.8, 973, 421, 307, 256) && detail?.HitBox != null)
            //                 {
            //                     int x = detail.HitBox.X + 100, y = detail.HitBox.Y + 50;
            //                     context.Click(x, y);
            //                     return true;
            //                 }
            //                 return false;
            //             };
            //             enteringEncouter.Until(context);
            //             MeetByChance(context, args.NodeName);
            //             break;
            //         case 1:
            //             if (++combatCount > 1)
            //             {
            //                 if (Leave(context, args))
            //                 {
            //                     return true;
            //                 }
            //             }
            //             LoggerHelper.Info("战斗次数:" + combatCount);
            //             Combat(context, args.NodeName);
            //             break;
            //         case 2:
            //             Thread.Sleep(700);
            //             var enteringTrader = () =>
            //             {
            //                 context.GetImage(ref image);
            //                 if (context.TemplateMatch("进入诡意行商.png", image, out detail, 0.8, 973, 421, 307, 256) && detail?.HitBox != null)
            //                 {
            //                     context.Click(detail.HitBox.X + 100, detail.HitBox.Y + 100);
            //                     return true;
            //                 }
            //                 return false;
            //             };
            //             enteringTrader.Until(context);
            //             if (SaveMoney(context, args))
            //             {
            //                 throw new Exception("999源石锭!");
            //             }
            //             else
            //             {
            //                 Leave(context, args);
            //                 return true;
            //             }
            //         case 3:
            //             Thread.Sleep(700);
            //             var v1 = () =>
            //             {
            //                 context.GetImage(ref image);
            //                 if (context.TemplateMatch("进入诡意行商.png", image, out detail, 0.8, 973, 421, 307, 256) && detail?.HitBox != null)
            //                 {
            //                     int x = detail.HitBox.X + 100, y = detail.HitBox.Y + 50;
            //                     context.Click(x, y);
            //                     return true;
            //                 }
            //                 return false;
            //             };
            //             v1.Until(context);
            //             GetSomething(context);
            //             break;
            //
            //     }
            //     return false;
            // };
            // level.Until(context);
        }
        return true;
    }

    public bool SaveMoney(IMaaContext context, RunArgs args)
    {
        RecognitionDetail detail;
        var enter1 = () =>
        {
            var image = context.GetImage();
            if (context.TemplateMatch("Roguelike@StageTraderInvestSystem.png", image, out detail, 0.75, 400, 172, 230, 150))
            {
                context.Click(detail.HitBox.X, detail.HitBox.Y);
                return true;
            }
            return false;
        };
        enter1.Until(context, 150);
        Thread.Sleep(300);
        var enter2 = () =>
        {
            var image = context.GetImage();

            if (context.TemplateMatch("Roguelike@StageTraderInvestSystem.png", image, out detail, 0.75, 400, 172, 230, 150))
            {
                context.Click(detail.HitBox.X, detail.HitBox.Y);
            }
            if (context.TemplateMatch("Roguelike@StageTraderInvestSystemEnter.png", image, out detail, 0.9, 460, 273, 420, 200))
            {
                LoggerHelper.Info($"{detail.HitBox.X}, {detail.HitBox.Y}");
                context.Click(640, 360);
                return true;
            }
            return false;
        };
        enter2.Until(context, 200);
        Thread.Sleep(600);
        var imageBuffer = context.GetImage();

        var text = context.GetText(543, 334, 191, 65, imageBuffer);
        var before = text.ToInt();
        if (before == 999)
            return true;
        var result = 0;
        var error = 0;
        var saving = () =>
        {
            var image = context.GetImage();
            if (context.TemplateMatch("Roguelike@StageTraderInvestSystemEnter.png", image, out detail, 0.9, 460, 273, 420, 200))
            {
                context.Click(640, 360);
            }
            if (context.TemplateMatch("Roguelike@StageTraderInvestSystemError.png", image, out detail, 0.9, 454,
                    191,
                    355,
                    279))
            {
                result = 1;
                return true;
            }
            if (context.TemplateMatch("notEnoughMoney.png", image, out detail, 0.9, 794, 448, 390, 94))
            {
                result = 2;
                return true;
            }
            if (context.TemplateMatch("notEnoughMoney1.png", image, out detail, 0.7, 794, 448, 390, 94))
            {
                result = 2;
                return true;
            }
            if (context.OCR("不足", image, out detail, 0.3, 794, 448, 390, 94))
            {
                result = 2;
                return true;
            }
            if (context.TemplateMatch("fullOfMoney.png", image, out detail, 0.8, 728,
                    49,
                    551,
                    475))
            {
                result = 0;
                return true;
            }
            context.Click(980, 495);
            Thread.Sleep(70);
            context.Click(980, 495);
            Thread.Sleep(70);
            context.Click(980, 495);
            error++;
            if (error >= 50)
            {
                result = 0;
                LoggerHelper.Warn("多次未识别到退出条件，已默认存钱完毕。");
                return true;
            }
            return false;
        };
        saving.Until(context, 150, maxCount: 100);
        int after = 0;
        switch (result)
        {
            case 0:
                after = 999;
                break;
            case 1:
                // var leave1 = () =>
                // {
                //     var image = context.GetImage();
                //     if (context.TemplateMatch("Roguelike@StageTraderInvestSystemLeave.png", image, out var detail, 0.75, 480, 472, 300, 70))
                //     {
                //         context.Click(detail.HitBox.X, detail.HitBox.Y);
                //         return true;
                //     }
                //     return false;
                // };
                // leave1.Until(150, errorAction: () =>
                // {
                //     context.OverrideNext(args.NodeName, ["启动检测"]);
                // });
                //
                // var leave2 = () =>
                // {
                //     var image = context.GetImage();
                //     if (context.OCR("算了", image, out var detail, 732, 472, 145, 43))
                //     {
                //         context.Click(detail.HitBox.X, detail.HitBox.Y);
                //         return true;
                //     }
                //     return false;
                // };
                // leave2.Until(150, errorAction: () =>
                // {
                //     context.OverrideNext(args.NodeName, ["启动检测"]);
                // });
                Thread.Sleep(500);
                context.GetImage(ref imageBuffer);

                var text1 = context.GetText(632, 197, 78, 54, imageBuffer);
                after = text1.ToInt();

                break;
            case 2:
                Thread.Sleep(500);
                context.GetImage(ref imageBuffer);

                var text2 = context.GetText(543, 334, 191, 65, imageBuffer);
                after = text2.ToInt();

                break;
        }

        Money += after - before;
        AddLogByColor(
            $"info:已投资 {Money}(+{after - before}),存款: {after}",
            "LightSeaGreen");
        if (result != 0 && after != 999)
            return false;
        return true;
    }

    public void GetSomething(IMaaContext context)
    {
        IMaaImageBuffer image = new MaaImageBuffer();
        Thread.Sleep(1000);
        for (int i = 0; i < 3; i++)
        {
            context.Click(624, 355);
            Thread.Sleep(150);
        }
        var xh = () =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("相会.png", image, out _, 0.8, 286, 15, 552, 517))
            {
                Thread.Sleep(1000);
                return true;
            }
            context.Click(624, 355);
            return false;
        };
        xh.Until(context);
        Thread.Sleep(1000);

        SelectOne(context, 1, 1);
        Thread.Sleep(1000);
        context.Click(624, 355);
        Thread.Sleep(1000);
        SelectOne(context, 1, 3);
        var leaving = () =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("rougelikeInfo.png", image, out _, 0.8, 53, 1, 178, 64))
            {
                return true;
            }
            context.Click(628, 621);
            return false;
        };
        leaving.Until(context);
    }
    public void SeedShop(IMaaContext context)
    {
        Thread.Sleep(500);
        var image = context.GetImage();
        RecognitionDetail detail;
        var leaving = () =>
{
    context.GetImage(ref image);
    if (context.TemplateMatch("离开苗圃.png", image, out detail, 0.8, 998, 480, 282, 134))
    {
        return true;
    }
    context.Click(628, 621);
    return false;
};
        leaving.Until(context);
        var confirmleaving = () =>
{
    context.GetImage(ref image);
    if (context.TemplateMatch("确认离开苗圃.png", image, out detail, 0.8, 998, 480, 282, 134))
    {
        return true;
    }
    context.Click(628, 621);
    return false;
};
        confirmleaving.Until(context);
    }

    public void MeetByChance(IMaaContext context)
    {
        Thread.Sleep(1000);
        for (int i = 0; i < 3; i++)
        {
            context.Click(624, 355);
            Thread.Sleep(150);
        }
        var image = context.GetImage();
        RecognitionDetail detail;
        int ix = -1;
        int count = 0;
        var entering = () =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("思乡心切.png", image, out detail, 0.8, 36, 56, 909, 582)) //336, 65, 452, 417
            {
                ix = 0;
                AddLogByColor("事件: 思乡心切");
                return true;
            }
            if (context.TemplateMatch("沉寂之屋.png", image, out detail, 0.8, 36, 56, 909, 582)) //336, 65, 452, 417
            {
                ix = 1;
                AddLogByColor("事件: 沉寂之屋");
                return true;
            }
            if (context.TemplateMatch("无人商店.png", image, out detail, 0.8, 36, 56, 909, 582))
            {
                ix = 2;
                AddLogByColor("事件: 无人商店");
                return true;
            }
            if (context.TemplateMatch("险路尽头.png", image, out detail, 0.8, 36, 56, 909, 582))
            {
                ix = 3;
                AddLogByColor("事件: 险路尽头");
                return true;
            }
            if (context.TemplateMatch("三重身.png", image, out detail, 0.8, 36, 56, 909, 582))
            {
                ix = 4;
                AddLogByColor("事件: 三重身");
                return true;
            }
            if (context.TemplateMatch("桑尼的邀请.png", image, out detail, 0.8, 36, 56, 909, 582))
            {
                ix = 5;
                AddLogByColor("事件: 桑尼的邀请");
                return true;
            }
            // if (context.TemplateMatch("饕餮廊.png", image, out detail, 0.8, 36, 56, 909, 582))
            // {
            //     ix = 6;
            //     RootView.AddLogByColor("事件: 饕餮廊");
            //     return true;
            // }
            // if (context.TemplateMatch("石山.png", image, out detail, 0.8, 36, 56, 909, 582))
            // {
            //     ix = 7;
            //     RootView.AddLogByColor("事件: 石山");
            //     return true;
            // }
            count++;
            if (count >= 6)
            {
                return true;
            }
            return false;
        };
        entering.Until(context);
        HandleAllMeeting(context, ix);
        var leaving = () =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("rougelikeInfo.png", image, out detail, 0.8, 53, 1, 178, 64))
            {
                return true;
            }
            context.Click(628, 621);
            return false;
        };
        leaving.Until(context);
    }

    public void HandleAllMeeting(IMaaContext context, int i)
    {
        switch (i)
        {
            case 0:
                SelectOne(context, 2, 2);
                break;
            case 1:
                SelectOne(context, 3, 3);
                break;
            case 2:
                SelectOne(context, 1, 3);
                break;
            case 3:
                SelectOne(context, 3, 3);
                break;
            case 4:
                SelectOne(context, 2, 2);
                break;
            case 5:
                SelectOne(context, 2, 2);
                break;
            case 6:
                SelectOne(context, 3, 3);
                break;
            default:
                SelectOne(context, 3, 3);
                break;
        }
    }
    public void Combat(IMaaContext context, string NodeName)
    {
        IMaaImageBuffer image = new MaaImageBuffer();
        RecognitionDetail detail;
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("进入作战.png", image, out detail, 0.9, 973, 421, 307, 256) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X + 100, detail.HitBox.Y + 50);
                return true;
            }
            return false;
        })).Until(context);
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("开始作战.png", image, out detail, 0.9, 894, 571, 386, 149) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X + 100, detail.HitBox.Y + 50);
                return true;
            }
            return false;
        })).Until(context);
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("确认放弃.png", image, out detail, 0.9, 594, 410, 533, 168) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X + 100, detail.HitBox.Y + 30);
                return true;
            }
            return false;
        })).Until(context);

        Thread.Sleep(9000);
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("局内设置.png", image, out detail, 0.9, 0, 0, 169, 162) && detail?.HitBox != null)
            {
                Thread.Sleep(1000);
                context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
                return true;
            }
            return false;
        })).Until(context, maxCount: 300, sleepMilliseconds: 500);
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("局内设置.png", image, out detail, 0.9, 0, 0, 169, 162) && detail?.HitBox != null)
            {
                context.Click(detail.HitBox.X + 30, detail.HitBox.Y + 30);
            }
            if (context.TemplateMatch("局内放弃行动.png", image, out detail, 0.9, 633, 214, 249, 271) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X + 100, detail.HitBox.Y + 50);
                return true;
            }
            return false;
        })).Until(context, maxCount: 300, sleepMilliseconds: 500);
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("局内放弃行动.png", image, out detail, 0.9, 633, 214, 249, 271) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X + 100, detail.HitBox.Y + 50);
            }
            if (context.TemplateMatch("确认放弃行动.png", image, out detail, 0.9, 682, 411, 319, 164) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X + 70, detail.HitBox.Y + 50);
                return true;
            }
            return false;
        })).Until(context);

        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("一败涂地.png", image, out detail, 0.9, 418, 47, 450, 401) && detail?.HitBox != null)
            {
                Thread.Sleep(800);
                context.Click(detail.HitBox.X, detail.HitBox.Y);
                return true;
            }
            return false;
        })).Until(context);

        Thread.Sleep(5000);
        ((Func<bool>)(() =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("rougelikeInfo.png", image, out detail, 0.8, 53, 1, 178, 64))
            {
                return true;
            }
            context.Click(628, 621);
            return false;
        })).Until(context);
    }

    public void SelectOne(IMaaContext context, int targetOptionIndex, int totalOptions)
    {
        Thread.Sleep(600);
        var screenHeight = 720;

        var optionHeight = 140;

        var verticalSpacing = 12.5;

        var totalOptionsHeight = totalOptions * optionHeight + (totalOptions - 1) * verticalSpacing;

        var remainingSpace = screenHeight - totalOptionsHeight;

        var topMargin = remainingSpace / 2;

        var clickY = topMargin + (targetOptionIndex - 1) * (optionHeight + verticalSpacing) + optionHeight / 2d;

        var clickX = 1100;
        IMaaImageBuffer image = new MaaImageBuffer();
        Thread.Sleep(500);
        var select = () =>
        {
            context.GetImage(ref image);
            if (context.TemplateMatch("确认这么做.png", image, out var detail, 0.8, 1032, 0, 247, 720) && detail?.HitBox != null)
            {
                context.Click(detail.HitBox.X, detail.HitBox.Y);
                return true;
            }
            LoggerHelper.Info($"{clickX}, {(int)clickY}");
            context.Click(clickX, (int)clickY);
            return false;
        };
        select.Until(context);
    }

    public int Find(IMaaContext context, int x, int y, int width, int height, string NodeName, bool spec = false)
    {
        int i = 0;
        // var selectFirst = false;
        // var find = () =>
        // {
        //     RecognitionDetail detail;
        //     var image = context.GetImage();
        //     if (spec && context.TemplateMatch("不期而遇.png", image, out detail, 0.76, 740, 171, 221, 140) && detail?.HitBox != null)
        //     {
        //         selectFirst = true;
        //     }
        //     if (spec && context.TemplateMatch("得偿所愿.png", image, out detail, 0.76, 740, 171, 221, 140) && detail?.HitBox != null)
        //     {
        //         selectFirst = true;
        //     }
        //     if (!spec && context.TemplateMatch("诡意行商.png", image, out detail, 0.8, x, y, width, height) && detail?.HitBox != null)
        //     {
        //         context.Click(detail.HitBox.X, detail.HitBox.Y);
        //         RootView.AddLogByColor("关卡: 诡意行商", "ForestGreen");
        //         i = 2;
        //         return true;
        //     }
        //     if (!spec && context.TemplateMatch("不期而遇.png", image, out detail, 0.87, x, y, width, height) && detail?.HitBox != null)
        //     {
        //         if (context.ColorMatch(31, 170, 159, 1, 117, 108, image, out detail, 0.87, detail.HitBox.X, detail.HitBox.Y, detail.HitBox.Width, detail.HitBox.Height, 16) && detail?.HitBox != null)
        //         {
        //             context.Click(detail.HitBox.X, detail.HitBox.Y);
        //             i = 0;
        //             Thread.Sleep(400);
        //             context.Click(detail.HitBox.X, detail.HitBox.Y);
        //             return true;
        //         }
        //     }
        //     if (!spec && context.TemplateMatch("得偿所愿.png", image, out detail, 0.87, x, y, width, height) && detail?.HitBox != null)
        //     {
        //         if (context.ColorMatch(70, 168, 93, 0, 86, 56, image, out detail, 0.87, detail.HitBox.X, detail.HitBox.Y, detail.HitBox.Width, detail.HitBox.Height, 16) && detail?.HitBox != null)
        //         {
        //             context.Click(detail.HitBox.X, detail.HitBox.Y);
        //             RootView.AddLogByColor("关卡: 得偿所愿", "ForestGreen");
        //             i = 3;
        //             Thread.Sleep(400);
        //             context.Click(detail.HitBox.X, detail.HitBox.Y);
        //             return true;
        //         }
        //     }
        //     if (context.TemplateMatch("作战.png", image, out detail, 0.8, x, y, width, height, order: selectFirst ? "Vertical" : "Score") && detail?.HitBox != null)
        //     {
        //         context.Click(detail.HitBox.X, detail.HitBox.Y);
        //         RootView.AddLogByColor("关卡: 作战", "MediumPurple");
        //         i = 1;
        //         Thread.Sleep(400);
        //         context.Click(detail.HitBox.X, detail.HitBox.Y);
        //         return true;
        //     }
        //     if (context.TemplateMatch("紧急作战.png", image, out detail, 0.85, x, y, width, height, order: selectFirst ? "Vertical" : "Score") && detail?.HitBox != null)
        //     {
        //         context.Click(detail.HitBox.X, detail.HitBox.Y);
        //         RootView.AddLogByColor("关卡: 紧急作战", "MediumPurple");
        //         i = 1;
        //         Thread.Sleep(400);
        //         context.Click(detail.HitBox.X, detail.HitBox.Y);
        //         return true;
        //     }
        //
        //     return false;
        // };
        // find.Until(context, 700, maxCount: 12);
        return i;
    }

    public bool Leave(IMaaContext context, RunArgs args, bool hasExited = false, bool hasBacked = false)
    {
        if (!hasBacked)
        {
            if (!hasExited)
            {
                ((Func<bool>)(() =>
                {
                    var image = context.GetImage();
                    if (context.TemplateMatch("Roguelike@ExitThenAbandon.png", image, out var detail, 0.75, 0, 0, 130, 60) && detail?.HitBox != null)
                    {
                        context.Click(detail.HitBox.X, detail.HitBox.Y);
                        return true;
                    }
                    return false;
                })).Until(context);
            }
            Thread.Sleep(400);
            ((Func<bool>)(() =>
            {
                var image = context.GetImage();
                if (context.TemplateMatch("Roguelike@ExitThenAbandon.png", image, out var detail, 0.75, 0, 0, 130, 60) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X, detail.HitBox.Y);
                }
                if (context.TemplateMatch("放弃本次探索.png", image, out detail, 0.75, 1002, 184, 274, 250) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X + 50, detail.HitBox.Y + 50);
                    return true;
                }
                return false;
            })).Until(context);
            Thread.Sleep(400);
            ((Func<bool>)(() =>
            {
                var image = context.GetImage();
                if (context.TemplateMatch("放弃本次探索.png", image, out var detail, 0.75, 1002, 184, 274, 250) && detail?.HitBox != null)
                {
                    context.Click(detail.HitBox.X + 50, detail.HitBox.Y + 50);
                }
                if (context.TemplateMatch("确认放弃.png", image, out detail, 0.9, 594, 410, 533, 168) && detail?.HitBox != null)
                {
                    Thread.Sleep(100);
                    context.Click(detail.HitBox.X + 70, detail.HitBox.Y + 20);
                    return true;
                }
                return false;
            })).Until(context);
        }
        ((Func<bool>)(() =>
        {
            var image = context.GetImage();
            if (context.TemplateMatch("开始探索.png", image, out var detail, 0.75, 1006, 470, 264, 250) && detail?.HitBox != null)
            {
                Thread.Sleep(1400);
                return true;
            }
            context.Click(631, 610);
            return false;
        })).Until(context, sleepMilliseconds: 500, maxCount: 300);
        Thread.Sleep(1400);
        ((Func<bool>)(() =>
        {
            var image = context.GetImage();
            if (context.TemplateMatch("开始探索.png", image, out var detail, 0.75, 1006, 470, 264, 250) && detail?.HitBox != null)
            {
                return true;
            }
            context.Click(631, 610);
            return false;
        })).Until(context, sleepMilliseconds: 500, maxCount: 300);
        Thread.Sleep(1400);
        ((Func<bool>)(() =>
        {
            var image = context.GetImage();
            if (context.TemplateMatch("开始探索.png", image, out var detail, 0.75, 1006, 470, 264, 250) && detail?.HitBox != null)
            {
                return true;
            }
            context.Click(631, 610);
            return false;
        })).Until(context, sleepMilliseconds: 500, maxCount: 300);
        return true;
    }

    protected virtual void ErrorHandle(IMaaContext context, RunArgs args)
    {
        ((Func<bool>)(() =>
        {
            var image = context.GetImage();
            if (context.TemplateMatch("Roguelike@ExitThenAbandon.png", image, out var detail, 0.75, 0, 0, 130, 60) && detail?.HitBox != null)
            {
                Leave(context, args);
            }
            if (context.TemplateMatch("放弃本次探索.png", image, out detail, 0.75, 1002, 184, 274, 250) && detail?.HitBox != null)
            {
                Leave(context, args, true);
            }
            if (context.TemplateMatch("开始探索.png", image, out detail, 0.84, 1006, 470, 264, 250) && detail?.HitBox != null)
            {
                return true;
            }
            return false;
        })).Until(context, outE: true);
    }
}
