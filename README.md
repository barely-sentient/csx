# CSharpExtended (CSX)

CSharpExtended extends C# with JSX-like syntax so you can write UI components directly in C# files (`.cx`).  
It provides:

- **Parser** (`CSX.Parser`): tokenizes `.cx` files, distinguishing JSX tags from C# generics.
- **Transformer** (`CSX.Transformer`): rewrites JSX into configurable C# method calls (`_.H`, `_.C`, `_.F` by default) with source maps.
- **Language Server** (`CSX.LanguageServer`): an LSP proxy that transforms `.cx` files and forwards requests to a C# language server, mapping positions back via source maps.
- **CLI** (`csx`): `csx lang-server` starts the language server.

## Build

```bash
dotnet build CSharpExtended.sln
```

## Test

```bash
dotnet test CSharpExtended.sln
```

## Run the language server

```bash
dotnet run --project LanguageServer/Source/CSX.LanguageServer.csproj -- lang-server --root <project> --csharp-server <path-to-csharp-ls>
```

## Packages

Published to NuGet on tag push (`v*`). See `.github/workflows/publish-nuget.yml`.

## VS Code Extension

Install the `donkeh.csx-vscode` extension. On first open of a `.cx` file you'll be prompted to choose:

- **Local build (dev mode)**: point at your `CSX.CliTool` build output or csproj.
- **Installed tool**: uses the `csx` command from PATH (auto-downloads from NuGet if missing).

Open the settings panel via `CSX: Open Settings` to configure.

## csx.json

The language server auto-creates a `csx.json` in the project root if missing. This file configures the transformer:

```json
{
  "FrameworkHtmlClass": "_",
  "FrameworkHtmlClassMethod": "H",
  "FrameworkComponentMethod": "C",
  "FrameworkFragmentMethod": "F",
  "GeneratedFileOutput": "obj/csx"
}
```

Edit these values to match your UI framework's helper class and methods.