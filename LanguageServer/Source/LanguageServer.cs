using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSX.Transformer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSX.LanguageServer
{
    public class LanguageServer : IAsyncDisposable
    {
        private readonly LanguageServerOptions _options;
        private readonly SourceMapResolver _resolver = new();
        private readonly List<string> _generatedFiles = new();
        private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private Process? _csharpServer;
        private List<string>? _componentCache;
        private DateTime _componentCacheTime = DateTime.MinValue;

        public LanguageServer(LanguageServerOptions? options = null)
        {
            _options = options ?? new LanguageServerOptions();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await LoadOrCreateConfigAsync(_cts.Token).ConfigureAwait(false);
            await TransformAllAsync(_cts.Token).ConfigureAwait(false);
            await GenerateSyntheticProjectAsync(_cts.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(_options.CSharpServerPath))
            {
                _options.CSharpServerPath = FindRoslynLanguageServer();
            }

            if (!string.IsNullOrWhiteSpace(_options.CSharpServerPath))
            {
                try
                {
                    var psi = new ProcessStartInfo(_options.CSharpServerPath)
                    {
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("--stdio");
                    _csharpServer = Process.Start(psi);
                    if (_csharpServer != null)
                    {
                        // stderr is redirected, so it must be drained: once the pipe buffer fills,
                        // the backend blocks on its next log write and the whole server hangs.
                        _csharpServer.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data != null) Console.Error.WriteLine("roslyn: " + e.Data);
                        };
                        _csharpServer.BeginErrorReadLine();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"csx: could not start C# backend '{_options.CSharpServerPath}': {ex.Message}");
                    _csharpServer = null;
                }
            }

            _runTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        }

        // "2.100.1" must sort above "2.99.0"; a plain string sort gets that backwards.
        private static Version ExtensionVersion(string exePath)
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exePath)) ?? "");
            var match = Regex.Match(dirName, @"csharp-(\d+(?:\.\d+){1,3})");
            return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : new Version(0, 0);
        }

        private static string? FindRoslynLanguageServer()
        {
            // PATH
            foreach (var name in new[] { "roslyn-language-server", "roslyn-language-server.exe" })
            {
                if (IsOnPath(name)) return name;
            }

            // VS Code extension dirs
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var extRoot = Path.Combine(home, ".vscode", "extensions");
                if (Directory.Exists(extRoot))
                {
                    var candidates = new List<string>();
                    foreach (var dir in Directory.EnumerateDirectories(extRoot, "ms-dotnettools.csharp-*"))
                    {
                        var exe = Path.Combine(dir, ".roslyn", "roslyn-language-server.exe");
                        if (File.Exists(exe)) candidates.Add(exe);
                    }
                    if (candidates.Count > 0)
                    {
                        return candidates.OrderByDescending(ExtensionVersion).ThenByDescending(p => p, StringComparer.Ordinal).First();
                    }
                }
            }
            catch { }

            // Common explicit locations
            foreach (var p in new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\Extensions\Microsoft\Roslyn\roslyn-language-server.exe",
            })
            {
                if (File.Exists(p)) return p;
            }

            return null;
        }

        private async Task GenerateSyntheticProjectAsync(CancellationToken ct)
        {
            try
            {
                var root = _options.ProjectRoot;
                var outputDir = Path.Combine(root, _options.OutputDirectory);
                Directory.CreateDirectory(outputDir);

                // Detect TFM and package references from existing csproj files.
                string tfm = "net10.0";
                var packageRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var frameworkRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;
                    var rel = Path.GetRelativePath(root, csproj);
                    if (rel.Split(Path.DirectorySeparatorChar).Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase))) continue;
                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Load(csproj);
                        var tfmNode = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework" || e.Name.LocalName == "TargetFrameworks");
                        if (tfmNode != null && !string.IsNullOrWhiteSpace(tfmNode.Value)) tfm = tfmNode.Value.Split(';').First().Trim();
                        foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                        {
                            var inc = pr.Attribute("Include")?.Value;
                            var ver = pr.Attribute("Version")?.Value;
                            if (!string.IsNullOrWhiteSpace(inc))
                            {
                                packageRefs.Add(string.IsNullOrWhiteSpace(ver) ? inc! : $"{inc}|{ver}");
                            }
                        }
                        foreach (var fr in doc.Descendants().Where(e => e.Name.LocalName == "FrameworkReference"))
                        {
                            var inc = fr.Attribute("Include")?.Value;
                            if (!string.IsNullOrWhiteSpace(inc)) frameworkRefs.Add(inc!);
                        }
                    }
                    catch { }
                }

                var csprojPath = Path.Combine(outputDir, "CSX.Generated.csproj");
                _options.GeneratedProjectPath = csprojPath;

                var sb = new StringBuilder();
                sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
                sb.AppendLine("  <PropertyGroup>");
                sb.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
                sb.AppendLine("    <Nullable>enable</Nullable>");
                sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
                sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
                sb.AppendLine("    <OutputType>Library</OutputType>");
                sb.AppendLine("  </PropertyGroup>");
                sb.AppendLine("  <ItemGroup>");
                sb.AppendLine("    <Compile Include=\"**/*.generated.cs\" />");
                // Include real .cs files from the project root, excluding bin/obj and the output dir.
                // The root is located relative to the generated project, whatever depth the output
                // directory has, and the output directory is excluded so generated files are not
                // compiled twice (a duplicate Compile item is a build error).
                var up = Path.GetRelativePath(outputDir, root).Replace('\\', '/').TrimEnd('/');
                var outRel = Path.GetRelativePath(root, outputDir).Replace('\\', '/').TrimEnd('/');
                sb.AppendLine($"    <Compile Include=\"{up}/**/*.cs\" Exclude=\"{up}/bin/**;{up}/obj/**;{up}/**/bin/**;{up}/**/obj/**;{up}/**/Debug/**;{up}/**/Release/**;{up}/{outRel}/**\" />");
                sb.AppendLine("  </ItemGroup>");

                if (packageRefs.Count > 0 || frameworkRefs.Count > 0)
                {
                    sb.AppendLine("  <ItemGroup>");
                    foreach (var pr in packageRefs)
                    {
                        var parts = pr.Split('|');
                        if (parts.Length == 2)
                            sb.AppendLine($"    <PackageReference Include=\"{parts[0]}\" Version=\"{parts[1]}\" />");
                        else
                            sb.AppendLine($"    <PackageReference Include=\"{parts[0]}\" />");
                    }
                    foreach (var fr in frameworkRefs)
                    {
                        sb.AppendLine($"    <FrameworkReference Include=\"{fr}\" />");
                    }
                    sb.AppendLine("  </ItemGroup>");
                }

                sb.AppendLine("</Project>");
                await File.WriteAllTextAsync(csprojPath, sb.ToString(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: failed to generate synthetic project: {ex.Message}");
            }
        }

        public Task RunAsync(CancellationToken cancellationToken = default) => _runTask ?? Task.CompletedTask;

        public async Task StopAsync()
        {
            if (_cts != null) _cts.Cancel();
            if (_runTask != null)
            {
                try { await _runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            if (_csharpServer != null && !_csharpServer.HasExited)
            {
                _csharpServer.Kill(entireProcessTree: true);
                await _csharpServer.WaitForExitAsync().ConfigureAwait(false);
            }
            _csharpServer?.Dispose();
            _cts?.Dispose();
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

        private static bool IsOnPath(string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(2000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task LoadOrCreateConfigAsync(CancellationToken ct)
        {
            var root = _options.ProjectRoot;
            var configPath = Path.Combine(root, "csx.json");
            try
            {
                if (!File.Exists(configPath))
                {
                    var defaultConfig = new CSX.Transformer.TransformConfig();
                    var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(configPath, json, ct).ConfigureAwait(false);
                    Console.Error.WriteLine($"csx: created {configPath}");
                }

                var text = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
                var config = System.Text.Json.JsonSerializer.Deserialize<CSX.Transformer.TransformConfig>(text);
                if (config != null)
                {
                    _options.TransformConfig = config;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: failed to load csx.json: {ex.Message}");
                _options.TransformConfig ??= new CSX.Transformer.TransformConfig();
            }
            _options.TransformConfig ??= new CSX.Transformer.TransformConfig();
            if (!string.IsNullOrWhiteSpace(_options.TransformConfig.GeneratedFileOutput))
            {
                _options.OutputDirectory = _options.TransformConfig.GeneratedFileOutput!;
            }
        }

        private async Task TransformAllAsync(CancellationToken ct)
        {
            var root = _options.ProjectRoot;
            if (!Directory.Exists(root))
            {
                return;
            }

            var outputDir = Path.Combine(root, _options.OutputDirectory);
            Directory.CreateDirectory(outputDir);

            // Remove generated output from previous runs so files that are no longer source
            // (for example stale debug fixtures) cannot leak into the synthetic project.
            try
            {
                foreach (var stale in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
                {
                    if (stale.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
                        stale.EndsWith(".generated.cs.map", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(stale);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: failed to clear generated output: {ex.Message}");
            }

            foreach (var file in Directory.EnumerateFiles(root, "*" + _options.FileExtension, SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;

                var relative = Path.GetRelativePath(root, file);
                if (IsIgnoredSourcePath(relative)) continue;

                try
                {
                    var source = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var transformer = new CSX.Transformer.Transformer();
                    var config = LoadConfigForFile(file, _options.TransformConfig, root);
                    var (text, map) = transformer.TransformWithSourceMap(source, config);
                    var outPath = Path.Combine(outputDir, relative + ".generated.cs");
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    await File.WriteAllTextAsync(outPath, text, ct).ConfigureAwait(false);
                    map.SourceFile = file;
                    map.GeneratedFile = outPath;
                    await File.WriteAllTextAsync(outPath + ".map", System.Text.Json.JsonSerializer.Serialize(map), ct).ConfigureAwait(false);
                    _generatedFiles.Add(outPath);
                    _resolver.Load(outPath);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"csx: failed to transform '{file}': {ex.Message}");
                }
            }
        }

        internal static TransformConfig LoadConfigForFile(string file, TransformConfig? fallback, string root)
        {
            try
            {
                var dir = new FileInfo(file).Directory;
                var rootDir = new DirectoryInfo(root);
                while (dir != null)
                {
                    var configPath = Path.Combine(dir.FullName, "csx.json");
                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        var loaded = JsonSerializer.Deserialize<TransformConfig>(json);
                        if (loaded != null) return loaded;
                    }
                    if (string.Equals(dir.FullName, rootDir.FullName, StringComparison.OrdinalIgnoreCase)) break;
                    dir = dir.Parent;
                }
            }
            catch { }
            return fallback ?? new TransformConfig();
        }

        private static bool IsIgnoredSourcePath(string relativePath)
        {
            var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            foreach (var part in relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                // Build outputs and debug harness fixtures contain copies of the sample .cx
                // files. Compiling them alongside the real sources causes duplicate type and
                // member diagnostics.
                if (part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("Release", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private async Task RunMinimalServerAsync(CancellationToken ct)
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            while (!ct.IsCancellationRequested)
            {
                using var doc = await LspProxy.ReadMessageAsync(stdin, ct).ConfigureAwait(false);
                if (doc == null) break;
                var message = doc.RootElement;
                var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;

                // A message without a method is a client *response*; answering it with a null
                // result (the default case below) sends the client a bogus reply.
                if (method == null) continue;
                JsonElement? id = message.TryGetProperty("id", out var i) ? i.Clone() : (JsonElement?)null;

                string idJson = id.HasValue ? id.Value.GetRawText() : "null";

                switch (method)
                {
                    case "initialize":
                    {
                        var json = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"capabilities\":{\"textDocumentSync\":1,\"completionProvider\":{\"resolveProvider\":false,\"triggerCharacters\":[\"<\",\".\",\" \",\"\\\"\"]},\"hoverProvider\":true},\"serverInfo\":{\"name\":\"csx\",\"version\":\"0.0.1\"}}}";
                        await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        break;
                    }
                    case "initialized":
                    case "$/setTrace":
                    case "workspace/didChangeConfiguration":
                    case "workspace/didChangeWatchedFiles":
                        break;
                    case "shutdown":
                    {
                        var json = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":null}";
                        await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        break;
                    }
                    case "exit":
                        return;
                    case "textDocument/didOpen":
                    {
                        if (TryGetTextDocument(message, out var uri, out var text))
                        {
                            _documents[uri] = text;
                            await PublishDiagnosticsAsync(stdout, uri, text, ct).ConfigureAwait(false);
                        }
                        break;
                    }
                    case "textDocument/didChange":
                    {
                        if (TryGetTextDocument(message, out var uri, out _) &&
                            message.TryGetProperty("params", out var p) &&
                            p.TryGetProperty("contentChanges", out var changes) &&
                            changes.ValueKind == JsonValueKind.Array)
                        {
                            string? latest = null;
                            foreach (var change in changes.EnumerateArray())
                            {
                                if (change.TryGetProperty("text", out var t))
                                {
                                    latest = t.GetString();
                                }
                            }
                            if (latest != null)
                            {
                                _documents[uri] = latest;
                                await PublishDiagnosticsAsync(stdout, uri, latest, ct).ConfigureAwait(false);
                            }
                        }
                        break;
                    }
                    case "textDocument/didSave":
                    {
                        if (TryGetTextDocument(message, out var uri, out _) && _documents.TryGetValue(uri, out var text))
                        {
                            await PublishDiagnosticsAsync(stdout, uri, text, ct).ConfigureAwait(false);
                        }
                        break;
                    }
                    case "textDocument/didClose":
                    {
                        if (TryGetTextDocument(message, out var uri, out _))
                        {
                            _documents.Remove(uri);
                            var json = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":{\"uri\":" + JsonSerializer.Serialize(uri) + ",\"diagnostics\":[]}}";
                            await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        }
                        break;
                    }
                    case "textDocument/completion":
                    {
                        var text = GetDocumentText(message);
                        var (line, character) = GetPosition(message);
                        var completionUri = message.TryGetProperty("params", out var cp) &&
                                            cp.TryGetProperty("textDocument", out var ctd) &&
                                            ctd.TryGetProperty("uri", out var cu)
                            ? cu.GetString() ?? ""
                            : "";
                        var items = BuildCompletions(text, line, character, completionUri);
                        var json = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + JsonSerializer.Serialize(items) + "}";
                        await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        break;
                    }
                    case "textDocument/hover":
                    {
                        var text = GetDocumentText(message);
                        var (line, character) = GetPosition(message);
                        var word = GetWordAt(text, line, character);
                        var result = string.IsNullOrWhiteSpace(word)
                            ? "null"
                            : JsonSerializer.Serialize(new
                            {
                                contents = new
                                {
                                    kind = "markdown",
                                    value = $"**{word}**\n\nCSX symbol."
                                }
                            });
                        var json = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}";
                        await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        break;
                    }
                    case "workspace/configuration":
                    {
                        var count = 1;
                        if (message.TryGetProperty("params", out var p) &&
                            p.TryGetProperty("items", out var items) &&
                            items.ValueKind == JsonValueKind.Array)
                        {
                            count = Math.Max(items.GetArrayLength(), 1);
                        }
                        var values = string.Join(",", Enumerable.Repeat("null", count));
                        var json = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":[" + values + "]}";
                        await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        break;
                    }
                    default:
                        if (id.HasValue)
                        {
                            var json = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":null}";
                            await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
                        }
                        break;
                }
            }
        }

        private static bool TryGetTextDocument(JsonElement message, out string uri, out string text)
        {
            uri = "";
            text = "";
            if (!message.TryGetProperty("params", out var p) ||
                !p.TryGetProperty("textDocument", out var td) ||
                !td.TryGetProperty("uri", out var u))
            {
                return false;
            }

            uri = u.GetString() ?? "";
            if (td.TryGetProperty("text", out var t))
            {
                text = t.GetString() ?? "";
            }
            return uri.Length > 0;
        }

        private string GetDocumentText(JsonElement message)
        {
            if (message.TryGetProperty("params", out var p) &&
                p.TryGetProperty("textDocument", out var td) &&
                td.TryGetProperty("uri", out var u))
            {
                var uri = u.GetString() ?? "";
                if (_documents.TryGetValue(uri, out var text))
                {
                    return text;
                }
            }
            return "";
        }

        private static (int Line, int Character) GetPosition(JsonElement message)
        {
            if (message.TryGetProperty("params", out var p) && p.TryGetProperty("position", out var position))
            {
                int line = position.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                int character = position.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
                return (line, character);
            }
            return (0, 0);
        }

        private async Task PublishDiagnosticsAsync(Stream stdout, string uri, string text, CancellationToken ct)
        {
            var diagnostics = GetDiagnostics(text, uri);
            var json = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":{\"uri\":" +
                       JsonSerializer.Serialize(uri) + ",\"diagnostics\":" +
                       JsonSerializer.Serialize(diagnostics) + "}}";
            await WriteRawAsync(stdout, json, ct).ConfigureAwait(false);
        }

        private List<object> GetDiagnostics(string text, string uri)
        {
            var diagnostics = new List<object>();
            var openTags = new Stack<(string Name, int Start)>();

            try
            {
                var tokens = CSX.Parser.Tokenizer.Tokenize(text);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];

                    if (token.Kind == CSX.Parser.TokenKind.JsxOpenBracket)
                    {
                        string? name = null;
                        int nameStart = token.StartPosition;
                        int j = i + 1;
                        while (j < tokens.Count && tokens[j].Kind == CSX.Parser.TokenKind.Whitespace)
                        {
                            j++;
                        }
                        if (j < tokens.Count &&
                            (tokens[j].Kind == CSX.Parser.TokenKind.JsxTagName || tokens[j].Kind == CSX.Parser.TokenKind.JsxComponentName))
                        {
                            name = tokens[j].TagName ?? tokens[j].Text;
                            nameStart = tokens[j].StartPosition;
                        }

                        bool selfClosing = false;
                        int k = j + 1;
                        while (k < tokens.Count)
                        {
                            if (tokens[k].Kind == CSX.Parser.TokenKind.JsxSelfClose)
                            {
                                selfClosing = true;
                                break;
                            }
                            if (tokens[k].Kind == CSX.Parser.TokenKind.JsxCloseBracket)
                            {
                                break;
                            }
                            if (tokens[k].Kind == CSX.Parser.TokenKind.JsxOpenBracket ||
                                tokens[k].Kind == CSX.Parser.TokenKind.JsxCloseTagOpen)
                            {
                                break;
                            }
                            k++;
                        }

                        if (!selfClosing && !string.IsNullOrEmpty(name))
                        {
                            openTags.Push((name, nameStart));
                        }
                        bool cutShort = k < tokens.Count &&
                            (tokens[k].Kind == CSX.Parser.TokenKind.JsxOpenBracket ||
                             tokens[k].Kind == CSX.Parser.TokenKind.JsxCloseTagOpen);
                        i = cutShort ? k - 1 : k;
                    }
                    else if (token.Kind == CSX.Parser.TokenKind.JsxCloseTagOpen)
                    {
                        string? name = null;
                        int j = i + 1;
                        while (j < tokens.Count && tokens[j].Kind == CSX.Parser.TokenKind.Whitespace)
                        {
                            j++;
                        }
                        if (j < tokens.Count &&
                            (tokens[j].Kind == CSX.Parser.TokenKind.JsxTagName || tokens[j].Kind == CSX.Parser.TokenKind.JsxComponentName))
                        {
                            name = tokens[j].TagName ?? tokens[j].Text;
                        }

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (openTags.Count == 0)
                            {
                                diagnostics.Add(CreateDiagnostic(text, token.StartPosition, token.Length,
                                    $"Unexpected closing tag </{name}>.", 1));
                            }
                            else
                            {
                                var open = openTags.Pop();
                                if (!string.Equals(open.Name, name, StringComparison.Ordinal))
                                {
                                    diagnostics.Add(CreateDiagnostic(text, token.StartPosition, token.Length,
                                        $"Closing tag </{name}> does not match <{open.Name}>.", 1));
                                }
                            }
                        }

                        int k = j + 1;
                        while (k < tokens.Count && tokens[k].Kind != CSX.Parser.TokenKind.JsxCloseBracket)
                        {
                            k++;
                        }
                        i = k;
                    }
                }

                foreach (var open in openTags)
                {
                    diagnostics.Add(CreateDiagnostic(text, open.Start, 1,
                        $"Tag <{open.Name}> is never closed.", 1));
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(CreateDiagnostic(text, 0, 1, "CSX parser error: " + ex.Message, 2));
            }

            try
            {
                var projected = ProjectToCSharp(text);
                var tree = CSharpSyntaxTree.ParseText(
                    projected,
                    new CSharpParseOptions(LanguageVersion.Preview),
                    path: uri);
                var compilation = GetCompilation(uri, tree);

                foreach (var diagnostic in compilation.GetDiagnostics())
                {
                    if (diagnostic.Location.SourceTree != tree ||
                        diagnostic.Severity != DiagnosticSeverity.Error)
                    {
                        continue;
                    }

                    var span = diagnostic.Location.SourceSpan;
                    diagnostics.Add(CreateDiagnostic(
                        text,
                        span.Start,
                        span.Length,
                        diagnostic.GetMessage(),
                        1));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: C# semantic check failed: {ex.Message}");
            }

            return diagnostics;
        }

        /// <summary>
        /// Replaces JSX elements with valid C# expressions while preserving every
        /// character offset and line break. This lets Roslyn type-check the C#
        /// around the JSX without reporting errors for the CSX helper methods.
        /// </summary>
        private static string ProjectToCSharp(string source)
        {
            var tokens = CSX.Parser.Tokenizer.Tokenize(source);
            var chars = source.ToCharArray();
            var spans = new List<(int Start, int End)>();
            var openTags = new Stack<int>();

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                if (token.Kind == CSX.Parser.TokenKind.JsxOpenBracket)
                {
                    int start = token.StartPosition;
                    bool selfClosing = false;
                    int j = i + 1;
                    while (j < tokens.Count)
                    {
                        if (tokens[j].Kind == CSX.Parser.TokenKind.JsxSelfClose)
                        {
                            selfClosing = true;
                            break;
                        }
                        if (tokens[j].Kind == CSX.Parser.TokenKind.JsxCloseBracket)
                        {
                            break;
                        }
                        if (tokens[j].Kind == CSX.Parser.TokenKind.JsxOpenBracket ||
                            tokens[j].Kind == CSX.Parser.TokenKind.JsxCloseTagOpen)
                        {
                            break;
                        }
                        j++;
                    }

                    // A malformed tag can be cut short by the next '<' or '</'. That token starts
                    // something new: don't blank it, and don't skip it in the outer loop.
                    bool cutShort = j < tokens.Count &&
                        (tokens[j].Kind == CSX.Parser.TokenKind.JsxOpenBracket ||
                         tokens[j].Kind == CSX.Parser.TokenKind.JsxCloseTagOpen);
                    int tagEnd = j >= tokens.Count
                        ? source.Length
                        : cutShort
                            ? tokens[j].StartPosition
                            : tokens[j].StartPosition + tokens[j].Length;

                    if (selfClosing)
                    {
                        spans.Add((start, tagEnd));
                    }
                    else
                    {
                        openTags.Push(start);
                    }
                    i = cutShort ? j - 1 : j;
                }
                else if (token.Kind == CSX.Parser.TokenKind.JsxCloseTagOpen)
                {
                    int j = i + 1;
                    while (j < tokens.Count && tokens[j].Kind != CSX.Parser.TokenKind.JsxCloseBracket)
                    {
                        j++;
                    }
                    int tagEnd = j < tokens.Count
                        ? tokens[j].StartPosition + tokens[j].Length
                        : source.Length;

                    int start = openTags.Count > 0 ? openTags.Pop() : token.StartPosition;
                    spans.Add((start, tagEnd));
                    i = j;
                }
            }

            while (openTags.Count > 0)
            {
                spans.Add((openTags.Pop(), source.Length));
            }

            foreach (var (start, end) in spans.OrderByDescending(span => span.Start))
            {
                if (end <= start)
                {
                    continue;
                }

                for (int k = start; k < end && k < chars.Length; k++)
                {
                    if (chars[k] != '\n' && chars[k] != '\r')
                    {
                        chars[k] = ' ';
                    }
                }

                int length = Math.Min(end, chars.Length) - start;
                string replacement = length >= 12 ? "new object()"
                    : length >= 7 ? "default"
                    : length >= 4 ? "null"
                    : "0";

                for (int k = 0; k < replacement.Length && start + k < end && start + k < chars.Length; k++)
                {
                    chars[start + k] = replacement[k];
                }
            }

            return new string(chars);
        }

        private static string? GetSourceDirectory(string uri)
        {
            try
            {
                return Path.GetDirectoryName(new Uri(uri).LocalPath);
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> EnumerateSourceFiles(string root, string? excludeDirectory = null)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".cx", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }

                IEnumerable<string> subdirectories;
                try
                {
                    subdirectories = Directory.EnumerateDirectories(directory);
                }
                catch
                {
                    continue;
                }

                foreach (var subdirectory in subdirectories)
                {
                    var name = Path.GetFileName(subdirectory);
                    if (excludeDirectory != null &&
                        string.Equals(Path.GetFullPath(subdirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                      excludeDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".vscode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    pending.Push(subdirectory);
                }
            }
        }

        private CSharpCompilation GetCompilation(string uri, SyntaxTree currentTree)
        {
            var trees = new List<SyntaxTree> { currentTree };
            var sourceDirectory = GetSourceDirectory(uri);
            var currentPath = UriToPath(uri);

            if (sourceDirectory != null && Directory.Exists(sourceDirectory))
            {
                // Skip the generated-output directory: its .generated.cs files duplicate the
                // projected .cx files and produce bogus duplicate-definition errors.
                var generatedDirectory = Path.GetFullPath(Path.Combine(_options.ProjectRoot, _options.OutputDirectory))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var file in EnumerateSourceFiles(sourceDirectory, generatedDirectory))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var content = File.ReadAllText(file);
                        var projected = file.EndsWith(".cx", StringComparison.OrdinalIgnoreCase)
                            ? ProjectToCSharp(content)
                            : content;
                        trees.Add(CSharpSyntaxTree.ParseText(
                            projected,
                            new CSharpParseOptions(LanguageVersion.Preview),
                            path: file));
                    }
                    catch
                    {
                        // Ignore unreadable files.
                    }
                }
            }

            return CSharpCompilation.Create(
                "CSX.Document",
                trees,
                GetMetadataReferences(),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));
        }

        private static List<MetadataReference>? _metadataReferences;

        private static List<MetadataReference> GetMetadataReferences()
        {
            if (_metadataReferences != null)
            {
                return _metadataReferences;
            }

            var references = new List<MetadataReference>();
            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            foreach (var dll in Directory.EnumerateFiles(runtimeDirectory, "*.dll"))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(dll));
                }
                catch
                {
                    // Not a managed assembly.
                }
            }

            _metadataReferences = references;
            return references;
        }

        private static string UriToPath(string uri)
        {
            try
            {
                return new Uri(uri).LocalPath;
            }
            catch
            {
                return uri;
            }
        }

        private static int MapOriginalToGenerated(SourceMap map, int originalOffset)
        {
            foreach (var entry in map.Mappings)
            {
                int end = entry.OriginalStart + entry.OriginalLength;
                if (originalOffset < entry.OriginalStart || originalOffset > end)
                {
                    continue;
                }

                int delta = originalOffset - entry.OriginalStart;
                if (entry.OriginalLength <= 0 || entry.GeneratedLength <= 0)
                {
                    return entry.GeneratedStart;
                }

                double ratio = (double)delta / entry.OriginalLength;
                int mapped = entry.GeneratedStart + (int)Math.Round(ratio * entry.GeneratedLength);
                return Math.Clamp(mapped, entry.GeneratedStart, entry.GeneratedStart + entry.GeneratedLength);
            }

            return -1;
        }

        private static object CreateDiagnostic(string text, int start, int length, string message, int severity)
        {
            var (line, character) = OffsetToPosition(text, start);
            var (endLine, endCharacter) = OffsetToPosition(text, Math.Min(start + Math.Max(length, 1), text.Length));
            return new
            {
                range = new
                {
                    start = new { line, character },
                    end = new { line = endLine, character = endCharacter }
                },
                severity,
                source = "csx",
                message
            };
        }

        private static int PositionToOffset(string text, int line, int character)
        {
            if (line < 0)
            {
                line = 0;
            }

            int offset = 0;
            int currentLine = 0;
            while (offset < text.Length && currentLine < line)
            {
                if (text[offset] == '\n')
                {
                    currentLine++;
                }
                offset++;
            }

            if (currentLine < line)
            {
                return text.Length;
            }

            int lineEnd = text.IndexOf('\n', offset);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }
            return Math.Min(offset + Math.Max(character, 0), lineEnd);
        }

        private static (int Line, int Character) OffsetToPosition(string text, int offset)
        {
            if (offset < 0)
            {
                offset = 0;
            }
            if (offset > text.Length)
            {
                offset = text.Length;
            }

            int line = 0;
            int character = 0;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    character = 0;
                }
                else
                {
                    character++;
                }
            }
            return (line, character);
        }

        private static string GetWordAt(string text, int line, int character)
        {
            int offset = PositionToOffset(text, line, character);
            int start = offset;
            int end = offset;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            {
                start--;
            }
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }
            return text.Substring(start, end - start);
        }

        private static string GetPrefix(string text, int offset)
        {
            int start = offset;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            {
                start--;
            }
            return text.Substring(start, offset - start);
        }

        private List<CompletionItem> BuildCompletions(string text, int line, int character, string uri)
        {
            // Source-map original offsets are relative to LF-normalized text.
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int offset = PositionToOffset(text, line, character);
            int lastOpen = offset > 0 ? text.LastIndexOf('<', offset - 1) : -1;
            int lastClose = offset > 0 ? text.LastIndexOf('>', offset - 1) : -1;
            bool insideTag = lastOpen > lastClose;
            var prefix = GetPrefix(text, offset);
            var items = new List<CompletionItem>();

            if (insideTag)
            {
                var inside = text.Substring(lastOpen, offset - lastOpen);
                bool inAttributes = inside.Length > 1 && inside.Skip(1).Any(char.IsWhiteSpace);
                if (inAttributes)
                {
                    foreach (var attribute in AttributeNames)
                    {
                        items.Add(new CompletionItem(attribute, 10, "JSX attribute"));
                    }
                }
                else
                {
                    foreach (var tag in HtmlTags)
                    {
                        items.Add(new CompletionItem(tag, 7, "HTML element"));
                    }
                    foreach (var component in CollectComponentNames())
                    {
                        items.Add(new CompletionItem(component, 7, "CSX component"));
                    }
                }
            }
            else
            {
                bool handledMemberAccess = AddSemanticCompletions(items, text, uri, offset);

                if (!handledMemberAccess)
                {
                    foreach (var keyword in CSharpKeywords)
                    {
                        items.Add(new CompletionItem(keyword, 14, "C# keyword"));
                    }
                    foreach (var type in CommonTypes)
                    {
                        items.Add(new CompletionItem(type, 7, "C# type"));
                    }
                    foreach (var component in CollectComponentNames())
                    {
                        items.Add(new CompletionItem(component, 7, "CSX component"));
                    }
                    items.Add(new CompletionItem("H", 3, "CSX HTML helper"));
                    items.Add(new CompletionItem("C", 3, "CSX component helper"));
                    items.Add(new CompletionItem("F", 3, "CSX fragment helper"));
                }
            }

            if (prefix.Length > 0)
            {
                items = items
                    .Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Roslyn can return the same symbol from several scopes.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            items = items
                .Where(item => seen.Add(item.Kind + ":" + item.Label))
                .Take(500)
                .ToList();

            return items;
        }

        private bool AddSemanticCompletions(List<CompletionItem> items, string text, string uri, int offset)
        {
            try
            {
                var transformer = new CSX.Transformer.Transformer();
                var (generated, map) = transformer.TransformWithSourceMap(text);
                int generatedOffset = MapOriginalToGenerated(map, offset);
                if (generatedOffset < 0)
                {
                    generatedOffset = Math.Min(offset, generated.Length);
                }

                var tree = CSharpSyntaxTree.ParseText(
                    generated,
                    new CSharpParseOptions(LanguageVersion.Preview),
                    path: uri);
                var compilation = GetCompilation(uri, tree);
                var model = compilation.GetSemanticModel(tree);
                int position = Math.Clamp(generatedOffset, 0, generated.Length);

                int prefixStart = position;
                while (prefixStart > 0 &&
                       (char.IsLetterOrDigit(generated[prefixStart - 1]) || generated[prefixStart - 1] == '_'))
                {
                    prefixStart--;
                }

                int dot = prefixStart - 1;
                while (dot >= 0 && char.IsWhiteSpace(generated[dot]))
                {
                    dot--;
                }

                if (dot >= 0 && generated[dot] == '.')
                {
                    var root = tree.GetRoot();
                    var token = root.FindToken(Math.Max(0, dot - 1));
                    SyntaxNode? expression = token.Parent;
                    while (expression != null && expression is not ExpressionSyntax)
                    {
                        expression = expression.Parent;
                    }
                    while (expression?.Parent is MemberAccessExpressionSyntax member &&
                           (member.Name == expression || member.Expression == expression) &&
                           !member.Name.IsMissing &&
                           member.Span.End <= dot)
                    {
                        expression = member;
                    }

                    if (expression is ExpressionSyntax typedExpression)
                    {
                        var type = model.GetTypeInfo(typedExpression).Type;
                        if (type != null && type.TypeKind != TypeKind.Error)
                        {
                            foreach (var member in type.GetMembers())
                            {
                                if (member.IsImplicitlyDeclared ||
                                    member.DeclaredAccessibility == Accessibility.Private)
                                {
                                    continue;
                                }
                                AddSymbol(items, member);
                            }
                            return true;
                        }
                    }
                }

                foreach (var symbol in model.LookupSymbols(position))
                {
                    AddSymbol(items, symbol);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: semantic completion failed: {ex.Message}");
            }

            return false;
        }

        private static void AddSymbol(List<CompletionItem> items, ISymbol symbol)
        {
            if (symbol.IsImplicitlyDeclared)
            {
                return;
            }

            var name = symbol.Name;
            if (string.IsNullOrEmpty(name) || name.StartsWith("<", StringComparison.Ordinal))
            {
                return;
            }

            if (symbol is IMethodSymbol method &&
                (method.MethodKind == MethodKind.PropertyGet ||
                 method.MethodKind == MethodKind.PropertySet ||
                 method.MethodKind == MethodKind.EventAdd ||
                 method.MethodKind == MethodKind.EventRemove ||
                 method.MethodKind == MethodKind.Constructor ||
                 method.MethodKind == MethodKind.StaticConstructor ||
                 method.MethodKind == MethodKind.Destructor))
            {
                return;
            }

            int kind = symbol switch
            {
                INamespaceSymbol => 9,
                INamedTypeSymbol named => named.TypeKind switch
                {
                    TypeKind.Interface => 8,
                    TypeKind.Struct => 22,
                    TypeKind.Enum => 13,
                    _ => 7
                },
                IMethodSymbol => 2,
                IPropertySymbol => 10,
                IFieldSymbol field when field.IsConst => 21,
                IFieldSymbol => 5,
                IEventSymbol => 23,
                ILocalSymbol => 6,
                IParameterSymbol => 6,
                ITypeParameterSymbol => 25,
                _ => 1
            };

            var detail = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            items.Add(new CompletionItem(name, kind, detail));
        }

        private List<string> CollectComponentNames()
        {
            if (_componentCache != null && (DateTime.UtcNow - _componentCacheTime).TotalSeconds < 5)
            {
                return _componentCache;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (Directory.Exists(_options.ProjectRoot))
                {
                    int scanned = 0;
                    foreach (var file in Directory.EnumerateFiles(_options.ProjectRoot, "*" + _options.FileExtension, SearchOption.AllDirectories))
                    {
                        if (++scanned > 2000)
                        {
                            break;
                        }
                        try
                        {
                            var source = File.ReadAllText(file);
                            foreach (Match match in Regex.Matches(source, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)"))
                            {
                                names.Add(match.Groups[1].Value);
                            }
                            foreach (Match match in Regex.Matches(source, @"<\s*([A-Z][A-Za-z0-9_]*)"))
                            {
                                names.Add(match.Groups[1].Value);
                            }
                        }
                        catch
                        {
                            // Ignore unreadable files.
                        }
                    }
                }
            }
            catch
            {
                // Ignore project scan failures.
            }

            _componentCache = names.OrderBy(name => name, StringComparer.Ordinal).ToList();
            _componentCacheTime = DateTime.UtcNow;
            return _componentCache;
        }

        private sealed record CompletionItem(
            [property: System.Text.Json.Serialization.JsonPropertyName("label")] string Label,
            [property: System.Text.Json.Serialization.JsonPropertyName("kind")] int Kind,
            [property: System.Text.Json.Serialization.JsonPropertyName("detail")] string Detail);

        private static readonly string[] CSharpKeywords =
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while", "async", "await", "var", "record", "init", "get", "set"
        };

        private static readonly string[] CommonTypes =
        {
            "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte",
            "short", "string", "uint", "ulong", "ushort", "void", "var", "List", "Dictionary",
            "Task", "IEnumerable", "IReadOnlyList", "DateTime", "Guid", "Action", "Func"
        };

        private static readonly string[] HtmlTags =
        {
            "div", "span", "p", "a", "button", "input", "label", "form", "ul", "ol", "li", "table",
            "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6", "img", "section", "header",
            "footer", "nav", "main", "article", "aside", "select", "option", "textarea", "strong",
            "em", "small", "br", "hr"
        };

        private static readonly string[] AttributeNames =
        {
            "className", "id", "style", "key", "ref", "onClick", "onChange", "onInput", "onSubmit",
            "disabled", "value", "type", "href", "src", "alt", "title", "placeholder", "data-testid"
        };

        private static async Task WriteRawAsync(Stream stream, string json, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var header = $"Content-Length: {bytes.Length}\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(header), ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            if (_csharpServer == null)
            {
                await RunMinimalServerAsync(ct).ConfigureAwait(false);
                return;
            }

            var proxy = new LspProxy(_options, _resolver);
            var backendIn = _csharpServer.StandardInput.BaseStream;
            var backendOut = _csharpServer.StandardOutput.BaseStream;

            await proxy.RunAsync(stdin, stdout, backendIn, backendOut, ct).ConfigureAwait(false);
        }
    }
}