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