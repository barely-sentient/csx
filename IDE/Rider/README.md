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

1. Build the plugin ZIP.
2. In Rider, go to **Settings | Plugins | ⚙ | Install Plugin from Disk...** and select the ZIP.
3. Restart Rider.

## Configuration

Open **Settings | Languages & Frameworks | CSX** to choose how the language server is launched:

- **Local build (development)**: point at `CSX.CliTool.exe`, `CSX.CliTool.dll`, `CSX.CliTool.csproj`, or the repository root. The plugin auto-detects a build under `CliTool/Source/bin/<Debug|Release>/net10.0/` when the path is left blank.
- **Installed tool (csx on PATH)**: uses the `csx` command (override with the Server command field). Install with `dotnet tool install -g CSX.CliTool`.

The language server starts automatically when a `.cx` file is opened. You can also start/restart it from **Tools | Start CSX Language Server** / **Restart CSX Language Server**.

## Project layout

```
src/main/kotlin/com/donkeh/csx/rider/
  CxLanguage.kt, CxFileType.kt, CxIcons.kt  - file type registration
  CxLexer.kt, CxTokenTypes.kt, CxSyntaxHighlighter.kt - syntax highlighting
  CxSettings.kt, CxConfigurable.kt           - persistent settings and UI
  CxServerResolver.kt                        - resolves the csx lang-server command
  CxLspClientDescriptor.kt                   - LSP process descriptor
  CxLspIntegrationProvider.kt                - LSP lifecycle integration
  CxServerActions.kt                         - start/restart actions
src/main/resources/META-INF/plugin.xml       - plugin descriptor
```
