using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;

namespace MFAAvalonia;

public static class PrivatePathHelper
{
    // 缓存已加载的库句柄，避免重复加载
    private static readonly Dictionary<string, IntPtr> _loadedLibraries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _resolverLock = new();
    private static bool _managedResolverRegistered;
    private static bool _nativeResolverRegistered;

    // Windows API: 添加 DLL 搜索目录
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string NewDirectory);

    /// <summary>
    /// 设置原生库解析器
    /// 使用 AssemblyLoadContext.ResolvingUnmanagedDll 事件，不会与第三方库的 SetDllImportResolver 冲突
    /// </summary>
    public static void SetupNativeLibraryResolver(bool enableManagedLibraryResolver = true)
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            var libsPath = Path.Combine(baseDirectory, AppContext.GetData("SubdirectoriesToProbe") as string ?? "libs");

            lock (_resolverLock)
            {
                if (enableManagedLibraryResolver && !_managedResolverRegistered)
                {
                    AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
                    {
                        try
                        {
                            var assemblyPath = FindManagedAssemblyInLibs(libsPath, assemblyName);
                            if (assemblyPath == null)
                            {
                                return null;
                            }

                            var loadedAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);

                            try
                            {
                                LoggerHelper.Info($"已从 libs 目录加载托管程序集：{assemblyPath}");
                            }
                            catch
                            {
                            }

                            return loadedAssembly;
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                LoggerHelper.Warning($"解析托管程序集失败：{assemblyName.FullName}，原因：{ex.Message}");
                            }
                            catch
                            {
                            }

                            return null;
                        }
                    };

                    _managedResolverRegistered = true;
                }
            }

            // Windows: 使用 AddDllDirectory API 添加搜索路径（更高效，避免黑魔法）
            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(libsPath))
                {
                    try
                    {
                        AddDllDirectory(libsPath);
                        LoggerHelper.Info($"已通过 Windows API 添加 DLL 搜索目录：{libsPath}");
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"添加 DLL 搜索目录失败：{ex.Message}");
                    }
                }

                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";

                // 检查是否已经包含该路径，避免重复添加
                if (!currentPath.Contains(libsPath, StringComparison.OrdinalIgnoreCase))
                {
                    // 将 libs 路径添加到 PATH 开头（优先搜索）
                    Environment.SetEnvironmentVariable("PATH", libsPath + Path.PathSeparator + currentPath);
                }
            }

            lock (_resolverLock)
            {
                if (!_nativeResolverRegistered)
                {
                    AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, libraryName) =>
                    {
                        try
                        {
                            // 检查缓存
                            if (_loadedLibraries.TryGetValue(libraryName, out IntPtr cachedHandle))
                            {
                                return cachedHandle;
                            }

                            string? libraryPath = null;

                            // 首先在 libs 文件夹中查找
                            if (Directory.Exists(libsPath))
                            {
                                libraryPath = FindLibraryInLibs(libsPath, libraryName);
                            }

                            if (libraryPath != null)
                            {
                                IntPtr handle = NativeLibrary.Load(libraryPath);
                                _loadedLibraries[libraryName] = handle;

                                try
                                {
                                    LoggerHelper.Info($"已加载原生库：{libraryPath}");
                                }
                                catch { }

                                return handle;
                            }

                            // 返回 IntPtr.Zero 让系统使用默认的解析逻辑
                            return IntPtr.Zero;
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                LoggerHelper.Warning($"解析原生库失败：{libraryName}，原因：{ex.Message}");
                            }
                            catch { }
                            return IntPtr.Zero;
                        }
                    };

                    _nativeResolverRegistered = true;
                }
            }

            try
            {
                LoggerHelper.Info($"库解析器初始化完成：libsPath={libsPath}，目录存在={Directory.Exists(libsPath)}");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                LoggerHelper.Warning($"初始化原生库解析器失败：{ex.Message}");
            }
            catch { }
        }
    }

    /// <summary>
    /// 在 libs 文件夹中查找托管程序集
    /// </summary>
    private static string? FindManagedAssemblyInLibs(string libsPath, AssemblyName assemblyName)
    {
        try
        {
            if (!Directory.Exists(libsPath) || string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                return null;
            }

            var assemblyPath = Path.Combine(libsPath, assemblyName.Name + ".dll");
            return File.Exists(assemblyPath) ? assemblyPath : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在 libs 文件夹中查找库文件
    /// </summary>
    private static string? FindLibraryInLibs(string libsPath, string libraryName)
    {
        try
        {
            if (!Directory.Exists(libsPath) || string.IsNullOrEmpty(libraryName))
                return null;

            // 定义平台特定的扩展名
            string[] extensions = OperatingSystem.IsWindows()
                    ? [".dll"]
                    : OperatingSystem.IsLinux()
                        ? [".so"]
                        : OperatingSystem.IsMacOS()
                            ?
                            [
                                ".dylib",
                                ".so"
                            ]
                            :
                            [
                                ".dll",
                                ".so",
                                ".dylib"
                            ]
                ;

            // 定义平台特定的前缀（Linux/macOS 上的库通常以 "lib" 开头）
            string[] prefixes = OperatingSystem.IsWindows()
                ? [""]
                :
                [
                    "",
                    "lib"
                ];

            // 首先尝试直接匹配（libraryName 可能已经包含扩展名）
            string directPath = Path.Combine(libsPath, libraryName);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // 获取不带扩展名的库名
            string nameWithoutExt = Path.GetFileNameWithoutExtension(libraryName);
            // 如果库名以 "lib" 开头，也准备一个不带 "lib" 前缀的版本
            string nameWithoutLibPrefix = nameWithoutExt.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                ? nameWithoutExt.Substring(3)
                : nameWithoutExt;

            // 尝试所有前缀和扩展名的组合
            foreach (string prefix in prefixes)
            {
                foreach (string ext in extensions)
                {
                    // 尝试原始名称
                    string pathWithExt = Path.Combine(libsPath, prefix + libraryName + ext);
                    if (File.Exists(pathWithExt))
                    {
                        return pathWithExt;
                    }

                    // 尝试不带扩展名的名称
                    if (nameWithoutExt != libraryName)
                    {
                        string path = Path.Combine(libsPath, prefix + nameWithoutExt + ext);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }

                    // 尝试不带 "lib" 前缀的名称（如果原名以 lib 开头）
                    if (nameWithoutLibPrefix != nameWithoutExt)
                    {
                        string path = Path.Combine(libsPath, prefix + nameWithoutLibPrefix + ext);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
            }

            // 最后尝试模糊匹配：在 libs 目录中查找包含库名的文件
            // 这对于版本化的库文件很有用，如 libfoo.so.1.2.3
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var files = Directory.GetFiles(libsPath);
                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        // 检查文件名是否包含库名（不区分大小写）
                        if (fileName.Contains(nameWithoutExt, StringComparison.OrdinalIgnoreCase) || fileName.Contains(nameWithoutLibPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            // 确保是共享库文件
                            if (fileName.Contains(".so") || fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                            {
                                return file;
                            }
                        }
                    }
                }
                catch
                {
                    // 忽略目录枚举错误
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清理同目录中与 libs 文件夹和 runtimes 文件夹重复的动态库文件，防止新旧版本冲突
    /// </summary>
    public static void CleanupDuplicateLibraries(string baseDirectory, string? lib)
    {
        try
        {
            // 收集所有需要排除的库文件名（来自 libs 和 runtimes）
            var duplicateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 收集 libs 文件夹中的所有动态库文件
            lib ??= "libs";
            var libsPath = Path.Combine(baseDirectory, lib);
            if (Directory.Exists(libsPath))
            {
                var libsDirectoryInfo = new DirectoryInfo(libsPath);
                var libsFileInfos = libsDirectoryInfo.GetFiles()
                    .Where(f => IsNativeLibrary(f.Extension));

                foreach (var fileInfo in libsFileInfos)
                {
                    duplicateFiles.Add(fileInfo.Name);
                }
            }

            // 2. 收集 runtimes 文件夹及其子目录中的所有动态库文件
            var runtimesPath = Path.Combine(baseDirectory, "runtimes");
            if (Directory.Exists(runtimesPath))
            {
                try
                {
                    // 递归搜索 runtimes 目录下的所有动态库文件
                    var runtimeFiles = Directory.EnumerateFiles(runtimesPath, "*", SearchOption.AllDirectories)
                        .Where(f => IsNativeLibrary(Path.GetExtension(f)));

                    foreach (var filePath in runtimeFiles)
                    {
                        var fileName = Path.GetFileName(filePath);
                        duplicateFiles.Add(fileName);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        LoggerHelper.Warning($"遍历 runtimes 目录失败：{ex.Message}");
                    }
                    catch { }
                }
            }
            var agentPath = Path.Combine(baseDirectory, "MaaAgentBinary");
            if (Path.Exists(agentPath))
                Directory.Delete(agentPath, true);
            if (duplicateFiles.Count == 0)
                return;

            // 3. 检查并删除同目录中的重复文件
            var baseDirectoryInfo = new DirectoryInfo(baseDirectory);
            var baseFileInfos = baseDirectoryInfo.GetFiles()
                .Where(f => IsNativeLibrary(f.Extension));

            foreach (var fileInfo in baseFileInfos)
            {
                // 如果同目录中的文件在 libs 或 runtimes 中也存在，删除同目录中的文件
                if (duplicateFiles.Contains(fileInfo.Name))
                {
                    try
                    {
                        fileInfo.Delete();
                        try
                        {
                            LoggerHelper.Info($"已删除重复库文件：{fileInfo.Name}（该文件也存在于 libs 或 runtimes 目录中）");
                        }
                        catch
                        {
                            // LoggerHelper 可能还未初始化
                        }
                    }
                    catch (Exception ex)
                    {
                        // 删除失败，记录错误但不中断程序
                        try
                        {
                            LoggerHelper.Warning($"删除重复库文件失败：{fileInfo.Name}，原因：{ex.Message}");
                        }
                        catch
                        {
                            // LoggerHelper 可能还未初始化
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 清理失败，记录错误但不中断程序
            try
            {
                LoggerHelper.Warning($"清理重复库文件失败：{ex.Message}");
            }
            catch
            {
                // LoggerHelper 可能还未初始化
            }
        }
    }

    /// <summary>
    /// 判断文件扩展名是否为原生库文件
    /// </summary>
    private static bool IsNativeLibrary(string extension)
    {
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".so", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
    }
}
