# Background layout tests

Run with the .NET 10 SDK:

```sh
dotnet test MFAAvalonia.Background.Tests/MFAAvalonia.Background.Tests.csproj -c Release
```

The project compiles the production `ScenicBackground` control directly, loads
the production scenic styles, and references the real SukiUI control templates. It uses Avalonia's headless platform with Skia, without
starting the automation host or connecting to a device.

Coverage includes image layout, clearing and brightness; light/dark scenic styles
with selected, unselected, disabled and indeterminate controls.
Interactive control surfaces and glyphs are compared with their normal-theme
baseline while only explicitly marked application panels receive scenic styling.

Set `MFAA_STYLE_EVIDENCE` to a local output directory to save the four rendered
control matrices as PNG files. These are offscreen control renders, not captures
of a running desktop client. Native window appearance, settings input and actual
device/game behavior require separate acceptance.
