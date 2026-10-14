using NETCore.Encrypt;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ProtectedData = CrossPlatformProtectedData.ProtectedData;


namespace MFAAvalonia.Helper;

public static class SimpleEncryptionHelper
{
#pragma warning disable CA1416
    public static string Generate()
    {
        // 跨平台系统特征参数
        var originalOsDescription = RuntimeInformation.OSDescription;
        var stableOsDescription = GetStableOSDescription(originalOsDescription);
        var osArchitecture = RuntimeInformation.OSArchitecture.ToString();
        var plainTextSpecificId = GetPlatformSpecificId();
        var machineName = Environment.MachineName;

        // 混合参数生成哈希
        var combinedString = $"{stableOsDescription}_{osArchitecture}_{plainTextSpecificId}_{machineName}";
        return EncryptProvider.Sha256(combinedString);
    }

    private static string GenerateLegacy()
    {
        // 保留原始逻辑（包含完整系统版本号）
        var osDescription = RuntimeInformation.OSDescription;
        var osArchitecture = RuntimeInformation.OSArchitecture.ToString();
        var plainTextSpecificId = GetPlatformSpecificId();
        var machineName = Environment.MachineName;

        var combinedString = $"{osDescription}_{osArchitecture}_{plainTextSpecificId}_{machineName}";
        return EncryptProvider.Sha256(combinedString);
    }

    private static string GetStableOSDescription(string originalOSDescription)
    {
        if (string.IsNullOrWhiteSpace(originalOSDescription))
            return originalOSDescription;

        // 匹配Windows版本格式（如 "Microsoft Windows 10.0.27975" 或 "Windows 11 Pro 10.0.22621"）
        var match = Regex.Match(originalOSDescription, @"^(.*?Windows\s+(?:10|11|10\.0|11\.0))(?:\.\d+|.*)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            // 保留大版本部分（如 "Microsoft Windows 10.0" 或 "Windows 11"）
            return match.Groups[1].Value.Trim();
        }

        // 非Windows系统或匹配失败时，直接返回原始描述（通常Linux/macOS版本描述不包含易变小版本）
        return originalOSDescription;
    }

    public static string GetPlatformSpecificId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 使用WMI获取主板UUID
                using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
                foreach (ManagementObject obj in searcher.Get())
                    return obj["UUID"].ToString();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // 读取DMI产品UUID
                try
                {
                    if (File.Exists("/sys/class/dmi/id/product_uuid"))
                        return File.ReadAllText("/sys/class/dmi/id/product_uuid").Trim();
                }
                catch (UnauthorizedAccessException)
                {
                    LoggerHelper.Warning("权限不足，无法访问/sys/class/dmi/id/product_uuid，尝试备选方法");
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"读取Linux UUID失败: {ex.Message}");
                }

                // 备选方法：尝试读取机器ID
                try
                {
                    if (File.Exists("/etc/machine-id"))
                        return File.ReadAllText("/etc/machine-id").Trim();
                    if (File.Exists("/var/lib/dbus/machine-id"))
                        return File.ReadAllText("/var/lib/dbus/machine-id").Trim();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"读取Linux机器ID失败: {ex.Message}");
                }

                // 备选方法：使用网络接口MAC地址
                try
                {
                    var nic = NetworkInterface.GetAllNetworkInterfaces()
                        .OrderByDescending(n => n.Speed)
                        .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && !n.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase));
                    if (nic != null)
                    {
                        var mac = nic.GetPhysicalAddress();
                        return BitConverter.ToString(mac.GetAddressBytes()).Replace("-", "");
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"获取Linux MAC地址失败: {ex.Message}");
                }
            }

            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ioreg",
                        Arguments = "-rd1 -c IOPlatformExpertDevice",
                        RedirectStandardOutput = true,
                        UseShellExecute = false
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                var match = Regex.Match(output, @"IOPlatformUUID"" = ""(.+?)""");
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"获取设备唯一标识失败：原因={e.Message}", e);
            return string.Empty;
        }
        return string.Empty;
    }
    private static string GetDeviceKeys(string fingerprint)
    {
        var key = fingerprint.Substring(0, 32);
        return key;
    }

    // 加密（自动绑定设备）
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return string.Empty;

        // CrossPlatformProtectedData creates an AndroidKeyStore EC key and then
        // attempts to export its private parameters. CheckJNI aborts the process
        // before a managed exception can be caught, so Android uses the existing
        // device-bound AES fallback directly.
        if (OperatingSystem.IsAndroid())
        {
            var androidKey = GetDeviceKeys(Generate());
            return EncryptProvider.AESEncrypt(plainText, androidKey);
        }

        try
        {
            var data = Encoding.UTF8.GetBytes(plainText);
            var wEncryptedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(wEncryptedData);
        }
        catch (Exception e)
        {
            LoggerHelper.Warn("跨平台数据加密失败: " + e.Message);
            var key = GetDeviceKeys(Generate());
            var encryptedData = EncryptProvider.AESEncrypt(plainText, key);
            return encryptedData;
        }
    }

    // 解密（仅当前设备可用）
    public static string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            return string.Empty;
        string result;

        if (!OperatingSystem.IsAndroid())
        {
            try
            {
                var data = Convert.FromBase64String(encryptedBase64);
                var decryptedData = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                result = Encoding.UTF8.GetString(decryptedData);
                if (string.IsNullOrWhiteSpace(result))
                    throw new Exception("result is null");
                return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Warn("跨平台数据解密失败: " + e.Message);
            }
        }


        try
        {
            // 1. 尝试用新机器码解密
            var newKey = GetDeviceKeys(Generate()); // 基于Generate()的新机器码
            result = EncryptProvider.AESDecrypt(encryptedBase64, newKey);
            if (string.IsNullOrWhiteSpace(result))
                throw new Exception("result is null");
            return result;
        }
        catch (Exception)
        {
            try
            {
                // 2. 尝试用旧机器码解密（包含完整版本号，兼容历史数据）
                var legacyKey = GetDeviceKeys(GenerateLegacy());
                result = EncryptProvider.AESDecrypt(encryptedBase64, legacyKey);
                if (string.IsNullOrWhiteSpace(result))
                    throw new Exception("result is null");
                return result;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
