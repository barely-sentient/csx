# CSX Rider Plugin

Rider plugin that adds support for CSharpExtended (`.cx`) files. It registers the file type, provides syntax highlighting, and reuses the existing `csx lang-server` LSP server for diagnostics, completion, navigation, and other language features.

## Build

The Gradle wrapper is included. Use JDK 21 (required by the IntelliJ Platform Gradle Plugin), then run:

```powershell
.\gradlew.bat buildPlugin
```

The distributable ZIP is written to `build/distributions/csx-rider-<version>.zip`.

By default the build downloads the public Rider 2026.1 distribution, so it also works on CI machines without Rider installed. To build against a local Rider installation instead and skip the download, set `CSX_RIDER_LOCAL` (or pass `-Pcsx.rider.local`):

```powershell
$env:CSX_RIDER_LOCAL = "C:\Program Files\JetBrains\Rider 2026.1.4"
.\gradlew.bat buildPlugin
```

The downloaded platform version can be changed with `CSX_RIDER_PLATFORM_VERSION` or `-Pcsx.rider.platformVersion`. GitHub Actions builds both this plugin and the VS Code VSIX; see `../README.md`.

## Install

The released plugin matching the installed CLI version can be installed with `csx enable rider`
(see [`../README.md`](../README.md)). To install a locally built ZIP:

1. Build the plugin ZIP.
2. In Rider, go to **Settings | Plugins | ⚙ | Install Plugin from Disk...** and select the ZIP.
3. Restart Rider.

## Configuration

Open **Settings | Languages & Frameworks | CSX** to choose how the language server is launched. Settings are global (like VS Code user settings) and stored in `csx-rider.xml`.

| Rider field | VS Code setting | Meaning |
| --- | --- | --- |
| Server mode | `csx.server.mode` | `Local build (development)` or `NuGet build (csx on PATH)` |
| Server command | `csx.server.path` | Installed `csx` command; defaults to `csx` |
| Server args | `csx.server.args` | Space-separated arguments; defaults to `lang-server` |
| Local CSX path | `csx.local.projectPath` | `CSX.CliTool.exe`, `.dll`, `.csproj`, or its directory |

The plugin auto-detects a build under `CliTool/Source/bin/<Debug|Release>/net10.0/` when **Local CSX path** is blank. Installed mode uses the NuGet global tool (`dotnet tool install -g CSX.CliTool`).

The language server starts automatically when a `.cx` file is opened. You can also start/restart it from **Tools | Start CSX Language Server** / **Restart CSX Language Server**.

## Project layout

```
src/main/kotlin/com/donkeh/csx/rider/
  CxLanguage.kt, CxFileType.kt, CxIcons.kt  - file type registration
  CxLexer.kt, CxTokenTypes.kt, CxSyntaxHighlighter.kt - syntax highlighting
  CxSettings.kt, CxConfigurable.kt           - persistent global settings and UI
  CxServerResolver.kt                        - resolves the csx lang-server command
  CxLspServerDescriptor.kt                   - LSP process descriptor
  CxLspServerSupportProvider.kt              - LSP lifecycle integration
  CxCompletionAutoPopupHandler.kt            - VS Code-style trigger-character popups
  CxServerActions.kt                         - start/restart actions
src/main/resources/META-INF/plugin.xml       - plugin descriptor
```
