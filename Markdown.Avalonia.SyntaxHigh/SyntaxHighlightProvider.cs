using Avalonia.Platform;
using Avalonia;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.TextMate;
using SukiUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Xml;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using TextMateSharp.Grammars;

namespace Markdown.Avalonia.SyntaxHigh
{
    public class SyntaxHighlightProvider
    {
        private ObservableCollection<Alias> _aliases;

        private Dictionary<string, string> _nameSolver;
        private Dictionary<string, IHighlightingDefinition> _definitions;
        // TextMate 相关的缓存 - 支持主题切换
        private static RegistryOptions _registryOptions = new(ThemeName.LightPlus);
        private static ThemeName _currentThemeName = ThemeName.LightPlus;

        // 缓存已安装 TextMate 的编辑器及其对应的 scopeName，用于主题切换时重新应用高亮
        private static readonly Dictionary<TextEditor, (TextMate.Installation Installation, string ScopeName)> _textMateInstallations =
            new();
        private static readonly ConditionalWeakTable<TextEditor, object> _lifecycleSubscriptions = new();
        private static readonly Lock _installLock = new();
        // 是否已订阅主题变更事件
        private static bool _isThemeChangeSubscribed = false;

        public SyntaxHighlightProvider(ObservableCollection<Alias> aliases)
        {
            _aliases = aliases;
            _nameSolver = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _definitions = new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);

            _aliases.CollectionChanged += (s, e) => AliasesCollectionChanged(e);
            AliasesCollectionChanged(null);

            // 初始化 TextMate 主题并订阅 SukiUI 主题变更事件
            InitializeAndSubscribeTheme();
        }

        /// <summary>
        /// 初始化 TextMate 主题并订阅 SukiUI 主题变更事件
        /// </summary>
        private static void InitializeAndSubscribeTheme()
        {
            if (_isThemeChangeSubscribed)
                return;
            try
            {
                var sukiTheme = SukiTheme.GetInstance();

                // 根据当前 SukiUI 主题设置 TextMate 主题
                var newThemeName = sukiTheme.ActiveBaseTheme == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
                if (newThemeName != _currentThemeName)
                {
                    _currentThemeName = newThemeName;
                    _registryOptions = new RegistryOptions(_currentThemeName);
                }

                // 订阅主题变更事件
                sukiTheme.OnBaseThemeChanged += OnSukiThemeChanged;
                _isThemeChangeSubscribed = true;
            }
            catch
            {
                // 如果无法获取 SukiTheme 实例，使用默认浅色主题
            }
        }

        /// <summary>
        /// SukiUI 主题变更时的处理
        /// </summary>
        private static void OnSukiThemeChanged(ThemeVariant themeVariant)
        {
            var newThemeName = themeVariant == ThemeVariant.Dark ? ThemeName.Dark : ThemeName.Light;

            if (newThemeName == _currentThemeName)
                return;

            _currentThemeName = newThemeName;
            _registryOptions = new RegistryOptions(_currentThemeName);

            // 更新所有已安装的 TextMate 编辑器的主题
            UpdateAllTextMateThemes();
        }

        /// <summary>
        /// 更新所有已安装 TextMate 的编辑器的主题
        /// </summary>
        private static void UpdateAllTextMateThemes()
        {
            lock (_installLock)
            {
                // 收集需要更新的编辑器及其 scopeName
                var editorsToUpdate = _textMateInstallations.ToList();

                foreach (var kvp in editorsToUpdate)
                {
                    var editor = kvp.Key;
                    var scopeName = kvp.Value.ScopeName;

                    try
                    {
                        // 释放旧的安装
                        DisposeInstallation(editor, kvp.Value.Installation);
                        _textMateInstallations.Remove(editor);

                        // 使用新的 RegistryOptions 重新安装 TextMate
                        var newInstallation = editor.InstallTextMate(_registryOptions);
                        newInstallation.SetGrammar(scopeName);
                        _textMateInstallations[editor] = (newInstallation, scopeName);
                    }
                    catch
                    {
                        // 忽略错误，编辑器可能已被释放
                        _textMateInstallations.Remove(editor);
                    }
                }
            }
        }

        /// <summary>
        /// 解析语言别名为实际语言名称
        /// </summary>
        public string ResolveLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang))
                return lang;
            for (var i = 0; i < 10; ++i)
                if (_nameSolver.TryGetValue(lang.ToLower(), out var realName))
                    lang = realName;
                else
                    break;
            return lang;
        }

        /// <summary>
        /// 获取 AvaloniaEdit 的高亮定义（用于传统方式）
        /// </summary>
        public IHighlightingDefinition? Solve(string lang)
        {
            lang = ResolveLanguage(lang);

            if (_definitions.TryGetValue(lang, out var def))
                return def;

            // Try to get definition by extension first
            var result = HighlightingManager.Instance.GetDefinitionByExtension("." + lang);
            if (result != null)
                return result;

            // If not found by extension, try to get by name
            return HighlightingManager.Instance.GetDefinition(lang);
        }

        /// <summary>
        /// 为 TextEditor 应用 TextMate 高亮（支持更多语言如 jsonc）
        /// </summary>
        public void ApplyTextMateHighlighting(TextEditor editor, string lang)
        {
            if (editor == null || string.IsNullOrEmpty(lang))
                return;

            try
            {
                lang = ResolveLanguage(lang);
                var scopeName = GetTextMateScopeName(lang);

                if (string.IsNullOrEmpty(scopeName))
                    return;

                lock (_installLock)
                {
                    // 检查是否已经安装过 TextMate
                    if (!_textMateInstallations.TryGetValue(editor, out var cached))
                    {
                        var installation = editor.InstallTextMate(_registryOptions);
                        installation.SetGrammar(scopeName);
                        _textMateInstallations[editor] = (installation, scopeName);

                        // Subscribe once per editor. A virtualized editor may be
                        // detached/attached many times during its lifetime.
                        if (!_lifecycleSubscriptions.TryGetValue(editor, out _))
                        {
                            editor.DetachedFromVisualTree += (s, e) =>
                            {
                                lock (_installLock)
                                {
                                    if (_textMateInstallations.TryGetValue(editor, out var inst))
                                    {
                                        DisposeInstallation(editor, inst.Installation);
                                        _textMateInstallations.Remove(editor);
                                    }
                                }
                            };
                            _lifecycleSubscriptions.Add(editor, new object());
                        }
                    }
                    else
                    {
                        // 如果已安装但 scopeName 不同，更新语法
                        if (cached.ScopeName != scopeName)
                        {
                            cached.Installation.SetGrammar(scopeName);
                            _textMateInstallations[editor] = (cached.Installation, scopeName);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // TextMate 高亮失败时静默处理，编辑器将显示无高亮的纯文本
            }
        }

        /// <summary>
        /// Completely removes a TextMate installation from an editor.
        /// AvaloniaEdit.TextMate 11.4.1 disposes its coloring transformer but
        /// leaves it in LineTransformers. Reusing that disposed transformer can
        /// prevent asynchronous grammars (for example JavaScript) from being
        /// redrawn after a virtualized control is attached again.
        /// </summary>
        private static void DisposeInstallation(TextEditor editor, TextMate.Installation installation)
        {
            installation.Dispose();

            var lineTransformers = editor.TextArea.TextView.LineTransformers;
            foreach (var transformer in lineTransformers.OfType<TextMateColoringTransformer>().ToArray())
            {
                lineTransformers.Remove(transformer);
            }
        }

        /// <summary>
        /// 获取语言对应的 TextMate scope name
        /// </summary>
        private string? GetTextMateScopeName(string lang)
        {
            if (string.IsNullOrEmpty(lang))
                return null;

            lang = lang.ToLowerInvariant();

            // TextMate scope 映射表
            var scopeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // JSON 相关 - 这是 HighlightingManager 不支持的
                {
                    "json", "source.json"
                },
                {
                    "jsonc", "source.json.comments"
                },
                {
                    "json5", "source.json"
                },

                // JavaScript/TypeScript
                {
                    "js", "source.js"
                },
                {
                    "javascript", "source.js"
                },
                {
                    "jsx", "source.js.jsx"
                },
                {
                    "ts", "source.ts"
                },
                {
                    "typescript", "source.ts"
                },
                {
                    "tsx", "source.tsx"
                },

                // C#
                {
                    "cs", "source.cs"
                },
                {
                    "csharp", "source.cs"
                },
                {
                    "c#", "source.cs"
                },

                // Python
                {
                    "py", "source.python"
                },
                {
                    "python", "source.python"
                },

                // C/C++
                {
                    "c", "source.c"
                },
                {
                    "cpp", "source.cpp"
                },
                {
                    "c++", "source.cpp"
                },
                {
                    "h", "source.c"
                },
                {
                    "hpp", "source.cpp"
                },

                // Web
                {
                    "html", "text.html.basic"
                },
                {
                    "htm", "text.html.basic"
                },
                {
                    "css", "source.css"
                },
                {
                    "scss", "source.css.scss"
                },
                {
                    "less", "source.css.less"
                },
                {
                    "xml", "text.xml"
                },

                // Shell
                {
                    "sh", "source.shell"
                },
                {
                    "bash", "source.shell"
                },
                {
                    "zsh", "source.shell"
                },
                {
                    "ps1", "source.powershell"
                },
                {
                    "powershell", "source.powershell"
                },

                // 其他常用语言
                {
                    "java", "source.java"
                },
                {
                    "go", "source.go"
                },
                {
                    "rust", "source.rust"
                },
                {
                    "rs", "source.rust"
                },
                {
                    "ruby", "source.ruby"
                },
                {
                    "rb", "source.ruby"
                },
                {
                    "php", "source.php"
                },
                {
                    "swift", "source.swift"
                },
                {
                    "kotlin", "source.kotlin"
                },
                {
                    "kt", "source.kotlin"
                },
                {
                    "scala", "source.scala"
                },
                {
                    "lua", "source.lua"
                },
                {
                    "perl", "source.perl"
                },
                {
                    "r", "source.r"
                },
                {
                    "sql", "source.sql"
                },
                // 配置文件
                {
                    "yaml", "source.yaml"
                },
                {
                    "yml", "source.yaml"
                },
                {
                    "toml", "source.toml"
                },
                {
                    "ini", "source.ini"
                },
                // 标记语言
                {
                    "md", "text.html.markdown"
                },
                {
                    "markdown", "text.html.markdown"
                },
                {
                    "tex", "text.tex"
                },
                {
                    "latex", "text.tex.latex"
                },

                // 其他
                {
                    "dockerfile", "source.dockerfile"
                },
                {
                    "docker", "source.dockerfile"
                },
                {
                    "makefile", "source.makefile"
                },
                {
                    "make", "source.makefile"
                },
                {
                    "diff", "source.diff"
                },
                {
                    "patch", "source.diff"
                },
            };

            if (scopeMap.TryGetValue(lang, out var scopeName))
                return scopeName;

            // 尝试通过 RegistryOptions 获取
            try
            {
                var language = _registryOptions.GetLanguageByExtension("." + lang);
                if (language != null)
                {
                    return _registryOptions.GetScopeByLanguageId(language.Id);
                }
            }
            catch
            {
                // 忽略错误
            }

            return null;
        }

        private void AliasesCollectionChanged(NotifyCollectionChangedEventArgs? arg)
        {
            IEnumerable<Alias> adding;

            if (arg is null || arg.OldItems != null)
            {
                _nameSolver.Clear();
                _definitions.Clear();
                SetupForBuiltIn();

                adding = _aliases;
            }
            else if (arg?.NewItems != null)
            {
                adding = arg.NewItems.Cast<Alias>();
            }
            else
                adding = Array.Empty<Alias>();


            foreach (var alias in adding)
            {
                if (alias.Name is null) continue;

                if (!String.IsNullOrEmpty(alias.RealName))
                {
                    _nameSolver[alias.Name] = alias.RealName;
                }
                else if (alias.XSHD != null)
                {
                    var definition = Load(alias.XSHD);

                    if (definition is null)
                        throw new ArgumentException($"Failed loading: {alias.XSHD}");

                    _definitions[alias.Name] = definition;
                }
            }
        }

        private void SetupForBuiltIn()
        {
            // https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/src/AvaloniaEdit/Highlighting/Resources/Resources.cs
            // 语言别名映射
            _nameSolver["c#"] = "cs";
            _nameSolver["csharp"] = "cs";
            _nameSolver["javascript"] = "js";
            _nameSolver["coco"] = "atg";
            _nameSolver["c++"] = "cpp";
            _nameSolver["powershell"] = "ps1";
            _nameSolver["python"] = "py";
            _nameSolver["markdown"] = "md";

            // 添加更多别名以支持 TextMate
            _nameSolver["typescript"] = "ts";
            _nameSolver["shell"] = "sh";
            _nameSolver["bash"] = "sh";
        }

        private IHighlightingDefinition? Load(Uri source)
        {
            switch (source.Scheme)
            {
                case "file":
                    return File.Exists(source.LocalPath) ? Open(File.OpenRead(source.LocalPath)) : null;

                case "avares":
                    return AssetLoader.Exists(source) ? Open(AssetLoader.Open(source)) : null;

                default:
                    throw new ArgumentException($"unsupport scheme '{source.Scheme}'");
            }

            IHighlightingDefinition Open(Stream stream)
            {
                try
                {
                    using (var reader = XmlReader.Create(stream))
                        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
                }
                finally
                {
                    stream.Close();
                }
            }
        }
    }
}
