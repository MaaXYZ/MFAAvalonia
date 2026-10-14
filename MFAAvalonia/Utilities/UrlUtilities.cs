using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace MFAAvalonia.Utilities;

public static class UrlUtilities
{
    public static Action<string>? PlatformOpenUrl { get; set; }

    /// <summary>
    /// Open the URL in the default browser.
    /// </summary>
    /// <param name="url"></param>
    public static void OpenUrl(string url)
    {
        if (OperatingSystem.IsAndroid())
        {
            if (PlatformOpenUrl == null)
                throw new PlatformNotSupportedException("Android URL launcher is not initialized.");
            PlatformOpenUrl(url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url.Replace("&", "^&"))
            {
                UseShellExecute = true
            });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start("xdg-open", url);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
    }
    
    public static OpenLinkCommand OpenLink = new();

    public class OpenLinkCommand : ICommand
    {
        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            if (!(parameter is string str1))
                return;
            try
            {
                OpenUrl(str1);
            }
            catch (Exception ex)
            {
                Helper.LoggerHelper.Error($"打开链接失败：{str1}，原因={ex.Message}", ex);
            }
        }
#pragma warning disable CS0067 //从不使用事件
        public event EventHandler CanExecuteChanged;
    }
}
