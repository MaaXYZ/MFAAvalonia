# Android Resource Project Integration

MFAAvalonia provides the Android UI, native controller, and resource loader, but no project-specific resources. Resource developers build their Project Interface, resources, and optional Agent into a project APK at CI time.

Resources are tied to the APK. Rebuild the APK to publish new resources; replacing only `interface.json` on a device is not supported.

Use [workflows/android.yml](../../workflows/android.yml) as the standalone workflow template. Copy it to `.github/workflows/android.yml` in the resource repository. Existing desktop release workflows can remain unchanged.

## Requirements

- GitHub Actions on `ubuntu-latest`
- JDK 17, Android SDK 36, and Android NDK
- .NET 10 Android workload
- Python 3.11 when the project uses a Python Agent
- A Project Interface with `interface_version: 2`

Users need Android 8.0 or later and must authorize the native controller through Shizuku or root. The APK contains the verified Shizuku installation guide, but users still start and authorize the service themselves.

## Central configuration

Resource-specific values are kept at the top of the workflow:

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

| Variable | Purpose |
| --- | --- |
| `PROJECT_NAME` | APK filename and GitHub artifact name |
| `APPLICATION_ID` | Stable Android package name used for upgrades and app updates |
| `LAUNCHER_LABEL` | Name displayed by the Android launcher |
| `LAUNCHER_ICON` | PNG path relative to the repository; also used by the Android 12+ splash screen |
| `RESOURCE_ROOT` | Final payload root containing `interface.json` or `interface.jsonc` directly |
| `MFA_REPOSITORY` | MFAAvalonia repository or fork to build |
| `MFA_REF` | MFA branch, tag, or commit; pin a tag or commit for releases |
| `P4A_ENTRYPOINT` | Python Agent entry point; omit the P4A block when no Python Agent is used |

Never include `GITHUB_RUN_NUMBER` in `APPLICATION_ID`. It may be used in the version and filename, but changing the package name prevents Android from treating the APK as an update.

## Resource layout

Recommended layout:

```text
project/
├─ .github/workflows/android.yml
├─ interface.json
├─ resource/
│  ├─ pipeline/
│  ├─ image/
│  └─ model/
├─ agent/main.py                 # optional
├─ python/                       # optional pure-Python modules
├─ data/                         # optional
├─ locales/                      # optional
├─ tasks/                        # optional
├─ requirements-android.txt      # preferred Android dependencies
├─ requirements.txt
└─ android/icon.png
```

If the project generates a portable payload under `install/` or `assets/`, run its existing installer first and set `RESOURCE_ROOT` to that output. The selected directory must contain the Project Interface at its root.

The payload is stored in the APK as:

```text
assets/MfaPackage/package.zip
```

MFA extracts it on first launch or when its embedded fingerprint changes. User configuration is stored separately and is preserved across APK upgrades.

## Project Interface

Keep the normal desktop controller declarations. Android uses MFA's Shizuku/root native controller and does not require a separate Android controller entry.

The app label, package name, and launcher icon are build metadata and must be configured in the workflow rather than inferred from `interface.json` after installation.

For GitHub or MirrorChyan updates, publish `.apk` assets with SHA256 values and distinguish architectures clearly:

```text
MaaExample-v1.0.0-android-arm64.apk
MaaExample-v1.0.0-android-x64.apk
```

## Python Agent

Only projects declaring an Agent need a Python runtime. Agent source is part of the payload; python-for-android compiles the interpreter and dependencies into an AAR during CI.

```json
{
  "interface_version": 2,
  "agent": {
    "child_exec": "python",
    "child_args": ["agent/main.py"]
  }
}
```

The Android adapter appends the Maa Agent identifier as the final argument and uses the extracted resource root as the working directory.

Dependency resolution order:

1. `requirements-android.txt`
2. `requirements.txt`
3. an explicit workflow override

Dependencies are compiled at build time. Do not run `pip install` on user devices or require Termux.

The example `android.yml` installs `packaging` before resolving requirements because its resolver uses that package's PEP 508 parser. Resource projects do not need to add `packaging` to their own requirements. If you copy the workflow and replace the resolver, make sure the Python environment running it still provides `packaging`.

An exact `maafw==VERSION` must select the same version for:

- the MaaFramework Python binding;
- the Android runtime NuGet packages;
- `libMaaFramework.so` and `libMaaAgentServer.so` in the APK.

Do not copy desktop `.pyd`, DLL, `MaaAgentBinary`, or a desktop `site-packages` tree into Android. Pure-Python packages can be packaged directly. Native extensions require a python-for-android recipe, a local recipe, an Android wheel, or an Android-compatible alternative. The workflow must resolve transitive dependencies such as `numpy` and `strenum`, not only top-level requirement names.

Projects without an Agent should remove the python-for-android AAR build, leave `P4A_ENTRYPOINT` empty, and package only the interface and resources.

## Architectures

| RID | ABI | Typical target |
| --- | --- | --- |
| `android-arm64` | `arm64-v8a` | Most physical Android devices |
| `android-x64` | `x86_64` | x64 emulators such as MuMu |

## Build verification

The workflow should verify that:

- `assets/MfaPackage/package.zip` exists;
- the payload root contains the Project Interface;
- the selected ABI contains `libMaaFramework.so`;
- an Agent build contains `libpython3.x.so`;
- the Agent service declares `foregroundServiceType=dataSync`;
- `aapt2 dump badging` reports the configured ApplicationId;
- launcher and Android 12+ splash icons were replaced;
- release assets contain the correct architecture name and SHA256.

## Testing and logs

Install the APK matching the device ABI, start and authorize Shizuku or root, and verify resources, announcements, localization, and Agent actions. Test background execution and an APK upgrade from an older build to ensure task order and notification settings are retained.

```bash
adb logcat -v threadtime | grep -E "MFA|MaaFw|MfaBridge|mfaagent|python"
```

## Common problems

| Symptom | Cause and action |
| --- | --- |
| Update installs as another app | Keep `APPLICATION_ID` stable |
| Launcher or splash icon is unchanged | Supply a repository-local PNG through `LAUNCHER_ICON` |
| Agent startup stalls | Check the `:service_mfaagent` log, dependency bundle, entry point, and MaaFramework version alignment |
| `ModuleNotFoundError` | Add the dependency or P4A recipe and rebuild it in CI |
| ARM phone cannot run the APK | Install the `android-arm64` asset |
| MuMu cannot run the APK | Install the `android-x64` asset |
| Download reaches 100% without installation | Publish an APK with a valid SHA256 and allow unknown-app installation |
| Configuration disappears after upgrading | Keep ApplicationId stable and never overwrite the user data directory with payload defaults |
