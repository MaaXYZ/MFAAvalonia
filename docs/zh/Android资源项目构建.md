# Android 资源项目接入

MFAAvalonia 本身只提供 Android UI、Native Controller 和资源加载能力，不包含具体项目资源。资源开发者在自己的仓库中运行 Android 工作流，将 Project Interface、资源和可选 Agent 在构建期一并打入 APK。

资源与 APK 绑定。更新资源需要重新构建 APK，不能只替换设备上的 `interface.json`。

独立工作流参考 [workflows/android.yml](../../workflows/android.yml)。将它复制到资源仓库的 `.github/workflows/android.yml`；已有桌面发布流程的项目可以保留原工作流，两者互不覆盖。

## 环境

- GitHub Actions `ubuntu-latest`
- JDK 17、Android SDK 36、Android NDK
- .NET 10 Android workload
- Python 3.11；仅在项目声明 Python Agent 时使用
- `interface_version: 2` 的 Project Interface

最终用户需要 Android 8.0 或更高版本，并通过 Shizuku 或 root 授予 Native Controller 权限。MFA APK 已包含经过校验的 Shizuku 安装引导，但 Shizuku 服务仍需用户自行启动和授权。

## 工作流配置

建议将会因资源项目而变化的配置集中放在工作流顶部。复制工作流后，通常只需修改这一段：

```yaml
env:
  PROJECT_NAME: MaaExample
  APPLICATION_ID: io.github.example.maaexample
  LAUNCHER_LABEL: Maa Example
  LAUNCHER_ICON: android/icon.png
  RESOURCE_ROOT: .
  MFA_REPOSITORY: MaaXYZ/MFAAvalonia
  MFA_REF: main
  P4A_ENTRYPOINT: agent/main.py
```

字段说明：

| 字段 | 说明 |
| --- | --- |
| `PROJECT_NAME` | APK 文件名和 GitHub Artifact 名称 |
| `APPLICATION_ID` | Android 包名；正式发布后必须保持不变，否则无法覆盖安装或自更新 |
| `LAUNCHER_LABEL` | 桌面图标下显示的应用名称 |
| `LAUNCHER_ICON` | 相对于资源仓库根目录的 PNG 图标，同时用于 Android 12+ 开屏图标 |
| `RESOURCE_ROOT` | 成品资源根目录，目录内必须直接包含 `interface.json` 或 `interface.jsonc` |
| `MFA_REPOSITORY` | 要构建的 MFAAvalonia 仓库；可指向自己的 Fork |
| `MFA_REF` | MFA 分支、Tag 或 commit；正式发布建议固定 Tag 或 commit |
| `P4A_ENTRYPOINT` | Python Agent 入口；没有 Python Agent 时留空并移除 P4A 构建步骤 |

不要把 `GITHUB_RUN_NUMBER` 拼进 `APPLICATION_ID`。运行编号可以用于版本号和文件名，但 ApplicationId 必须稳定。

旧的通用 [install.yml](../../workflows/install.yml) 仍支持通过 GitHub Repository Variables 设置：

```text
MFA_ANDROID_APPLICATION_ID
MFA_ANDROID_LAUNCHER_LABEL
MFA_ANDROID_LAUNCHER_ICON
MFA_ANDROID_MFA_REPOSITORY
MFA_ANDROID_MFA_REF
MFA_ANDROID_RESOURCE_ROOT
MFA_ANDROID_PYTHON_ENTRYPOINT
MFA_ANDROID_PYTHON_REQUIREMENTS
MFA_ANDROID_P4A_LOCAL_RECIPES
```

项目自己的 `android.yml` 更推荐使用顶部 `env` 配置区，阅读和复制更直接。

## 资源目录

推荐目录：

```text
项目仓库/
├─ .github/workflows/android.yml
├─ interface.json
├─ resource/
│  ├─ pipeline/
│  ├─ image/
│  └─ model/
├─ agent/                         # 可选
│  └─ main.py
├─ python/                        # 可选的纯 Python 模块
├─ data/                          # 可选
├─ locales/                       # 可选
├─ tasks/                         # 可选
├─ requirements-android.txt       # 可选，优先于 requirements.txt
├─ requirements.txt              # 可选
└─ android/icon.png               # 可选
```

如果项目的成品资源由 `install.py` 生成到 `install/` 或 `assets/`，工作流应先运行项目原有安装脚本，再把成品目录作为 `AndroidResourcePackageSource`。不要根据仓库源码目录猜测最终 payload；APK 中的根目录必须能直接看到 `interface.json`。

构建时 payload 会压缩到：

```text
assets/MfaPackage/package.zip
```

首次启动或 APK 内资源指纹变化后，MFA 会将其提取到应用数据目录。用户配置保存在独立数据路径中，覆盖安装 APK 时不会用 payload 内的默认配置覆盖已有配置。

## Project Interface 约定

Controller 仍按桌面项目习惯声明即可。Android 会使用 MFA 提供的 Shizuku/root Native Controller，不需要在 `interface.json` 中额外增加一种 Android Controller。

Launcher 名称、包名和图标属于 APK 构建信息，不能在安装后根据 `interface.json` 动态修改，应由工作流顶部配置决定。

GitHub 或 MirrorChyan 更新时，Android 资产必须为 `.apk`，并应提供 SHA256。上传两种架构时建议使用清晰名称：

```text
MaaExample-v1.0.0-android-arm64.apk
MaaExample-v1.0.0-android-x64.apk
```

MFA 会根据 `RuntimeInformation.ProcessArchitecture` 选择 `arm64` 或 `x64`。MirrorChyan 请求使用 `os=android`，架构使用 `arm64` 或 `amd64`。

## Python Agent

只有 `interface.json` 声明了 Agent 的项目才需要 Python runtime。Agent 源码属于资源 payload；解释器和编译后的依赖由 python-for-android 在 CI 中构建进 AAR，再合入 APK。

入口示例：

```json
{
  "interface_version": 2,
  "agent": {
    "child_exec": "python",
    "child_args": ["agent/main.py"]
  }
}
```

Android adapter 会把 Maa Agent identifier 作为最后一个参数传给入口，工作目录是提取后的资源根目录。

### 依赖

依赖读取顺序：

1. `requirements-android.txt`
2. `requirements.txt`
3. 工作流显式设置的依赖

工作流应在构建期解析并编译依赖，而不是在用户设备上运行 `pip install`。这样 APK 安装后即可运行，也不依赖 Termux、网络或设备编译环境。

示例 `android.yml` 会在解析 requirements 前安装 `packaging`，用于解析 PEP 508 依赖声明；资源项目无需把 `packaging` 额外写入自己的 requirements。若复制工作流时重写了解析脚本，也应确保运行该脚本的 Python 环境已安装 `packaging`。

`maafw==VERSION` 同时决定：

- 检出的 MaaFramework Python binding 版本；
- MFA Android 使用的 MaaFramework runtime NuGet 版本；
- APK 内 `libMaaFramework.so` 和 `libMaaAgentServer.so` 版本。

三者必须一致。不要把 Desktop 的 `site-packages`、`.pyd`、Windows DLL 或 `MaaAgentBinary` 原样复制进 Android APK。

纯 Python 包可以直接由 P4A 打包。包含 C/C++/Fortran 扩展的包必须满足至少一个条件：

- python-for-android 已提供 recipe；
- 项目提供 `p4a-recipes` 本地 recipe；
- 有兼容 Android ABI 的预编译实现；
- 使用 Android 可用的替代包。

若 `requirements.txt` 中的 `maafw` 会依赖 `numpy`、`strenum` 等包，CI 需要解析完整依赖闭包，而不是只读取顶层包名。

## 无 Agent 项目

没有 Agent 时：

- 不构建 python-for-android AAR；
- 不设置 `P4A_ENTRYPOINT`；
- 不复制 `maa` Python binding；
- 仍然打包 interface、Pipeline、图片、OCR 模型和本地化文件。

这样可以显著缩短 CI 时间并减小 APK。

## 构建

建议先保留手动触发：

```yaml
on:
  workflow_dispatch:
```

验证通过后再添加 Tag 发布：

```yaml
on:
  push:
    tags: ["v*"]
  workflow_dispatch:
```

标准构建矩阵：

| RID | Android ABI | 适用设备 |
| --- | --- | --- |
| `android-arm64` | `arm64-v8a` | 绝大多数 Android 真机、Apple Silicon 上的 ARM Android 环境 |
| `android-x64` | `x86_64` | MuMu 等 x64 模拟器 |

构建核心命令：

```bash
dotnet restore MFAAvalonia.Android/MFAAvalonia.Android.csproj \
  -r "$RID" \
  -p:AndroidSdkDirectory="$ANDROID_HOME"

dotnet publish MFAAvalonia.Android/MFAAvalonia.Android.csproj \
  -c Release \
  -f net10.0-android \
  -r "$RID" \
  --no-restore \
  -p:AndroidEmbedResourcePackage=true \
  "-p:ApplicationId=$APPLICATION_ID" \
  "-p:AndroidResourcePackageSource=$PAYLOAD_ROOT" \
  "-p:AndroidLauncherLabel=$LAUNCHER_LABEL" \
  "-p:AndroidLauncherIcon=$LAUNCHER_ICON" \
  "-p:AndroidPythonRuntimeAar=$PYTHON_AAR" \
  "-p:AndroidPythonAgentEntryPoint=$P4A_ENTRYPOINT"
```

## 构建后校验

工作流至少应检查：

- APK 中存在 `assets/MfaPackage/package.zip`；
- payload 根目录存在 `interface.json`；
- 对应 ABI 下存在 `libMaaFramework.so`；
- 使用 Python Agent 时存在 `libpython3.x.so`；
- Python Agent Service 声明 `foregroundServiceType=dataSync`；
- `aapt2 dump badging` 得到的包名等于 `APPLICATION_ID`；
- Launcher 图标和 Android 12+ splash 图标均已替换；
- APK 文件名包含正确的 `arm64` 或 `x64`；
- 发布时生成 SHA256 文件。

## 安装与测试

1. 安装与设备架构匹配的 APK。
2. 启动 Shizuku并向 MFA 授权，或使用 root。
3. 首次打开等待资源提取完成。
4. 确认任务列表、公告、图标和本地化均来自资源项目。
5. 启动任务，确认 Agent 成功连接并能执行自定义识别/动作。
6. 将应用切到后台，确认前台服务仍在运行。
7. 用旧版 APK 覆盖更新到新版，确认任务顺序、外部通知等用户配置仍保留。

常用日志：

```bash
adb logcat -v threadtime | grep -E "MFA|MaaFw|MfaBridge|mfaagent|python"
```

macOS 用户可先通过 Android Studio 的 SDK Manager 安装 platform-tools，再使用同一条 `adb logcat` 命令。

## 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| 安装时报签名或包冲突 | 旧 APK 签名不同；卸载旧测试包，正式发布后固定签名和 ApplicationId |
| 更新被识别成另一个应用 | `APPLICATION_ID` 随 CI 次数变化；改为固定值 |
| 图标或开屏动画没有替换 | 没传 `AndroidLauncherIcon`，或图标不在资源仓库内；使用 PNG 并检查 splash 校验步骤 |
| 启动 Agent 后停住 | 查看 `:service_mfaagent` 日志；通常是依赖未编入 P4A AAR、入口错误或 MaaFramework 版本不一致 |
| `ModuleNotFoundError` | 依赖只存在于 Desktop 环境；加入 requirements/P4A recipe，在 CI 中重新构建 runtime |
| `LogFile` 没有 `encoding` | 使用旧版 Android adapter；更新 MFA_REF，不要让资源脚本依赖 Desktop 独有的 stdout 类型 |
| ARM64 真机无法启动 x64 APK | APK 架构不匹配；真机使用 `android-arm64` |
| MuMu 无法启动 arm64 APK | 模拟器通常使用 x64；安装 `android-x64` |
| 下载更新到 100% 后不安装 | Android Release 资产必须是 APK，并提供正确 SHA256；同时允许系统安装未知应用 |
| 覆盖更新后配置丢失 | 不要更改 ApplicationId，也不要把默认配置复制到用户数据路径覆盖已有文件 |

## 发布前检查

- [ ] `APPLICATION_ID` 固定且属于项目自身。
- [ ] 项目名、Launcher 名称和图标只在顶部配置区维护。
- [ ] payload 根目录能直接看到 Project Interface。
- [ ] Agent 入口相对于 payload 根目录有效。
- [ ] requirements 在 CI 中编译完成，没有设备端安装步骤。
- [ ] Python binding、Native MaaFramework 和 AgentServer 版本一致。
- [ ] arm64 与 x64 APK 分别验证。
- [ ] APK 内资源、OCR 模型、本地化和公告文件完整。
- [ ] Release 上传 APK 和 SHA256，而不是把桌面 ZIP 当作 Android 资产。
- [ ] 覆盖安装测试确认用户配置保持不变。
