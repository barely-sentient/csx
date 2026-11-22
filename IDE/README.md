# CSX editor extensions

This directory contains the editor integrations for CSharpExtended (`.cx`) files:

| IDE | Source | Artifact | Marketplace |
| --- | --- | --- | --- |
| VS Code | [`VsCode/`](VsCode/) | `csx-vscode.vsix` | `donkeh.csx-vscode` |
| Rider | [`Rider/`](Rider/) | `csx-rider-<version>.zip` | not published yet |

Both extensions provide syntax highlighting and start the same `csx lang-server` process for
diagnostics, completion, navigation and other language features.

## Before you start

The extensions launch the language server from the `CSX.CliTool` project, which targets **.NET 10**.
Choose one of these setups:

- **Global tool** (recommended): `dotnet tool install -g CSX.CliTool` — then select the
  *Installed tool* mode in the extension settings and leave the command as `csx`.
- **Local build**: run `dotnet build CSharpExtended.sln -c Release` in the repository and select
  the *Local build* mode. You can point the extension at `CliTool/Source/CSX.CliTool.csproj`,
  at the built `CSX.CliTool.dll`/`.exe`, or leave the path empty to auto-detect a build.

The transformer is configured by `csx.json` in the project root. The language server creates a
default file if one does not exist; see the [root README](../README.md#csxjson) for the options.

## Getting the artifacts

The workflow `.github/workflows/build-ide-plugins.yml` builds both artifacts on pushes to
`main`/`master`, on `v*` tags, on pull requests, and on demand:

1. Open the repository's **Actions** tab and select **Build IDE plugins (VSIX + Rider)**.
2. Open a successful run and download the `csx-vscode-vsix` and/or `csx-rider-plugin` artifacts.
3. GitHub wraps each artifact in an extra ZIP. Extract it first; for Rider, install the
   `csx-rider-<version>.zip` file *inside* that download.

## VS Code

Install the published extension from the Extensions view by searching for **CSX Language Support**
(`donkeh.csx-vscode`), or install a downloaded VSIX manually:

```powershell
code --install-extension csx-vscode.vsix
```

Alternatively: **Extensions → … → Install from VSIX…**.

Open a `.cx` file, then use the `CSX` settings (`csx.server.mode`, `csx.server.path`,
`csx.server.args`, `csx.local.projectPath`) to select local or installed-tool mode. Useful commands
are **CSX: Start/Restart Language Server** and **CSX: Show Output** from the command palette.

To build the VSIX locally:

```powershell
cd IDE/VsCode
npm ci
npm run build
```

## Rider

Requires **Rider 2026.1 or newer**. Install the plugin from disk:

1. **Settings | Plugins | ⚙ | Install Plugin from Disk…**
2. Select `csx-rider-<version>.zip` (the inner ZIP from the artifact download).
3. Restart Rider.

Configure the server under **Settings | Languages & Frameworks | CSX**: choose *Local build* or
*Installed tool (csx on PATH)* and set the path/command accordingly. The server starts
automatically when a `.cx` file is opened. You can also use **Tools | Start CSX Language Server**
and **Tools | Restart CSX Language Server**.

To build the plugin locally (JDK 21 required), see [`Rider/README.md`](Rider/README.md):

```powershell
cd IDE/Rider
.\gradlew.bat buildPlugin
```

## Troubleshooting

- **The language server does not start**: confirm `dotnet --version` reports .NET 10, the CLI is
  built or installed, and the extension's server mode/path matches your setup.
- **VS Code**: run **CSX: Show Output** to inspect the server log.
- **Rider**: use **Tools | Start CSX Language Server**, then check `idea.log`
  (**Help | Show Log in Explorer/Finder**) for `CxServerResolver` warnings.
