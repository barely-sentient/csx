using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSX.Transformer;

namespace CSX.LanguageServer
{
    public class LspProxy
    {
        private readonly LanguageServerOptions _options;
        private readonly SourceMapResolver _resolver;
        private readonly CSX.Transformer.Transformer _transformer = new();

        private readonly Dictionary<string, string> _originalToGenerated = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _generatedToOriginal = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _injectedIds = new();
    private int _nextInjectedId = 1000000;
    private System.Threading.Tasks.TaskCompletionSource<bool>? _projectReady;
    private readonly Dictionary<int, string> _requestUris = new();

        public LspProxy(LanguageServerOptions options, SourceMapResolver resolver)
        {
            _options = options;
            _resolver = resolver;
        }

        public async Task RunAsync(Stream clientIn, Stream clientOut, Stream backendIn, Stream backendOut, CancellationToken ct)
        {
            _projectReady = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            // Synchronous handshake: load the project before the client sends didOpen,
            // so documents associate with the project instead of a misc file-based program.
            try
            {
                Console.Error.WriteLine("handshake: waiting for init");
                var initDoc = await ReadMessageAsync(clientIn, ct).ConfigureAwait(false);
                Console.Error.WriteLine("handshake: got init");
                if (initDoc != null)
                {
                    var rewrittenInit = RewriteMessage(initDoc, true);
                    await WriteMessageAsync(backendIn, rewrittenInit, ct).ConfigureAwait(false);

                    // Wait for initialize response.
                    JsonDocument? initResp = null;
                    string? initIdRaw = initDoc.RootElement.TryGetProperty("id", out var iid) ? iid.GetRawText() : null;

                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (initResp == null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        using var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        tcts.CancelAfter(TimeSpan.FromSeconds(3));
                        JsonDocument? msg = null;
                        try { msg = await ReadMessageAsync(backendOut, tcts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { msg = null; }
                        if (msg == null) break;
                        if (initIdRaw != null && msg.RootElement.TryGetProperty("id", out var rid) &&
                            rid.GetRawText() == initIdRaw &&
                            !msg.RootElement.TryGetProperty("method", out _))
                        {
                            initResp = msg;
                            Console.Error.WriteLine("handshake: got init response");
                        }
                        else
                        {
                            // Don't forward unsolicited messages to client before init response.
                            // Respond to server->client requests directly so backend doesn't block.
                            if (msg.RootElement.TryGetProperty("method", out var m) && msg.RootElement.TryGetProperty("id", out var rid2))
                            {
                                var method = m.GetString();
                                JsonDocument? resp = null;
                                if (method == "workspace/configuration")
                                {
                                    int cnt = 1;
                                    if (msg.RootElement.TryGetProperty("params", out var p) && p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                                        cnt = items.GetArrayLength();
                                    resp = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + rid2.GetRawText() + ",\"result\":[" + string.Join(",", Enumerable.Repeat("null", cnt)) + "]}");
                                }
                                else if (method == "workspace/workspaceFolders")
                                {
                                    resp = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + rid2.GetRawText() + ",\"result\":[]}");
                                }
                                if (resp != null) await WriteMessageAsync(backendIn, resp, ct).ConfigureAwait(false);
                            }
                            // discard logs/notifications during handshake
                        }
                    }

                    Console.Error.WriteLine("handshake: sending project/open");
                    if (!string.IsNullOrWhiteSpace(_options.GeneratedProjectPath) && File.Exists(_options.GeneratedProjectPath))
                    {
                        var projectUri = new Uri(_options.GeneratedProjectPath).AbsoluteUri;
                        var openProject = JsonDocument.Parse(
                            "{\"jsonrpc\":\"2.0\",\"method\":\"project/open\",\"params\":{\"projects\":[\"" + projectUri + "\"]}}");
                        await WriteMessageAsync(backendIn, openProject, ct).ConfigureAwait(false);

                        // Wait for projectInitializationComplete
                        var pdeadline = DateTime.UtcNow.AddSeconds(5);
                        while (DateTime.UtcNow < pdeadline && !ct.IsCancellationRequested)
                        {
                            using var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            tcts.CancelAfter(TimeSpan.FromSeconds(2));
                            JsonDocument? msg = null;
                            try { msg = await ReadMessageAsync(backendOut, tcts.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { msg = null; }
                            if (msg == null) break;
                            if (msg.RootElement.TryGetProperty("method", out var pm) && pm.GetString() == "workspace/projectInitializationComplete")
                            {
                                _projectReady?.TrySetResult(true);
                                break;
                            }
                            // Respond to server->client requests; discard the rest.
                            if (msg.RootElement.TryGetProperty("method", out var m2) && msg.RootElement.TryGetProperty("id", out var rid2))
                            {
                                var method = m2.GetString();
                                JsonDocument? resp = null;
                                if (method == "workspace/configuration")
                                {
                                    int cnt = 1;
                                    if (msg.RootElement.TryGetProperty("params", out var p) && p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                                        cnt = items.GetArrayLength();
                                    resp = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + rid2.GetRawText() + ",\"result\":[" + string.Join(",", Enumerable.Repeat("null", cnt)) + "]}");
                                }
                                else if (method == "workspace/workspaceFolders")
                                {
                                    resp = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + rid2.GetRawText() + ",\"result\":[]}");
                                }
                                if (resp != null) await WriteMessageAsync(backendIn, resp, ct).ConfigureAwait(false);
                            }
                        }
                    }

                    Console.Error.WriteLine("handshake: sending init response to client");
                    if (initResp != null)
                    {
                        var rewrittenResp = RewriteMessage(initResp, false);
                        await WriteMessageAsync(clientOut, rewrittenResp, ct).ConfigureAwait(false);
                        Console.Error.WriteLine("handshake: init response sent");
                    }
                    else
                    {
                        Console.Error.WriteLine("handshake: initResp is null, NOT sending response");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("csx: handshake failed: " + ex.Message);
            }

            // Normal pumps for the rest.
            var c2s = PumpAsync(clientIn, backendIn, true, ct);
            var s2c = PumpAsync(backendOut, clientOut, false, ct);
            await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
        }

        private async Task PumpAsync(Stream input, Stream output, bool clientToServer, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var doc = await ReadMessageAsync(input, ct).ConfigureAwait(false);
                if (doc == null) break;

                // Drop responses to injected warm-up requests so they don't reach the client.
                if (!clientToServer &&
                    !doc.RootElement.TryGetProperty("method", out _) &&
                    doc.RootElement.TryGetProperty("id", out var rid) &&
                    rid.ValueKind == JsonValueKind.Number &&
                    _injectedIds.TryRemove(rid.GetInt32(), out _))
                {
                    continue;
                }

                // Track project readiness.
                if (!clientToServer && doc.RootElement.TryGetProperty("method", out var mth) &&
                    mth.GetString() == "workspace/projectInitializationComplete")
                {
                    _projectReady?.TrySetResult(true);
                }

                try
                {
                    Console.Error.WriteLine("proxy: " + (clientToServer ? "C->S" : "S->C") + " " + (doc.RootElement.TryGetProperty("method", out var _mm) ? _mm.GetString() : "response id=" + (doc.RootElement.TryGetProperty("id", out var _ii) ? _ii.GetRawText() : "?")) + " | " + doc.RootElement.GetRawText().Substring(0, Math.Min(120, doc.RootElement.GetRawText().Length)));
                    var rewritten = RewriteMessage(doc, clientToServer);
                    await WriteMessageAsync(output, rewritten, ct).ConfigureAwait(false);

                    // After the client signals initialized, open the generated project
                    // in the C# backend so it provides full IntelliSense.
                    if (clientToServer && rewritten.RootElement.TryGetProperty("method", out var m) &&
                        m.GetString() == "initialized" &&
                        !string.IsNullOrWhiteSpace(_options.GeneratedProjectPath) &&
                        File.Exists(_options.GeneratedProjectPath))
                    {
                        var projectUri = new Uri(_options.GeneratedProjectPath).AbsoluteUri;
                        var openProject = JsonDocument.Parse(
                            "{\"jsonrpc\":\"2.0\",\"method\":\"project/open\",\"params\":{\"projects\":[\"" + projectUri + "\"]}}");
                        await WriteMessageAsync(output, openProject, ct).ConfigureAwait(false);
                    }

                    // (warm-up hover removed: project is loaded before didOpen now)
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("proxy: RewriteMessage failed: " + ex.Message);
                    await WriteMessageAsync(output, doc, ct).ConfigureAwait(false);
                }
            }
        }

        public static async Task<JsonDocument?> ReadMessageAsync(Stream stream, CancellationToken ct)
        {
            // Read headers
            var headerBytes = new List<byte>();
            int prev = -1;
            var oneByte = new byte[1];
            while (true)
            {
                int r = await stream.ReadAsync(oneByte, 0, 1, ct).ConfigureAwait(false);
                if (r == 0) return null;
                int b = oneByte[0];
                headerBytes.Add((byte)b);
                if (prev == '\r' && b == '\n')
                {
                    if (headerBytes.Count >= 4 &&
                        headerBytes[headerBytes.Count - 4] == '\r' &&
                        headerBytes[headerBytes.Count - 3] == '\n' &&
                        headerBytes[headerBytes.Count - 2] == '\r' &&
                        headerBytes[headerBytes.Count - 1] == '\n')
                    {
                        break;
                    }
                }
                prev = b;
            }

            var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
            int contentLength = 0;
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                }
            }

            if (contentLength <= 0) return null;

            var body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int r = await stream.ReadAsync(body, read, contentLength - read, ct).ConfigureAwait(false);
                if (r == 0) return null;
                read += r;
            }

            return JsonDocument.Parse(body);
        }

        private static async Task WriteMessageAsync(Stream stream, JsonDocument doc, CancellationToken ct)
        {
            var json = doc.RootElement.GetRawText();
            var bytes = Encoding.UTF8.GetBytes(json);
            var header = $"Content-Length: {bytes.Length}\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private JsonDocument RewriteMessage(JsonDocument doc, bool clientToServer)
        {
            var node = JsonNode.Parse(doc.RootElement.GetRawText());
            if (node == null) return doc;

            // Force full-document sync so the proxy can transform whole documents
            // instead of having to replay incremental edits.
            if (!clientToServer)
            {
                try
                {
                    var caps = node["result"]?["capabilities"] as JsonObject;
                    if (caps != null)
                    {
                        var sync = caps["textDocumentSync"];
                        if (sync is JsonObject so)
                        {
                            so["change"] = 1;
                            so["openClose"] = true;
                        }
                        else if (sync != null)
                        {
                            caps["textDocumentSync"] = 1;
                        }
                        caps.Remove("semanticTokensProvider");
                        caps["inlayHintProvider"] = false;
                    }
                }
                catch { }
            }

            // Log hover/completion for debugging
            try
            {
                var method = node["method"]?.GetValue<string>();
                if (method == "textDocument/hover" || method == "textDocument/completion")
                {
                    Console.Error.WriteLine("proxy: " + (clientToServer ? "C->S" : "S->C") + " " + method);
                }
            }
            catch { }

            // Ensure generated files exist for any uri encountered
            RewriteNode(node, clientToServer, null);

            var newJson = node.ToJsonString();
            return JsonDocument.Parse(newJson);
        }

        private void RewriteNode(JsonNode? node, bool clientToServer, string? currentUri)
        {
            if (node is JsonObject obj)
            {
                string? uri = currentUri;
                string? originalUriForTracking = null;
                if (obj.TryGetPropertyValue("uri", out var uriNode) && uriNode is JsonValue uv && uv.TryGetValue<string>(out var us))
                {
                    originalUriForTracking = us;
                    if (clientToServer)
                    {
                        uri = GetGeneratedUri(us);
                    }
                    else
                    {
                        uri = GetOriginalUri(us);
                    }
                    obj["uri"] = uri;
                }
                else if (obj.TryGetPropertyValue("textDocument", out var tdNode) && tdNode is JsonObject tdObj &&
                         tdObj.TryGetPropertyValue("uri", out var tdu) && tdu is JsonValue tuv && tuv.TryGetValue<string>(out var tus))
                {
                    // Resolve uri from nested textDocument even if it appears later in traversal.
                    if (clientToServer) uri = GetGeneratedUri(tus);
                    else uri = GetOriginalUri(tus);
                }
                else if (!clientToServer && obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idv &&
                         idv.TryGetValue<int>(out var idInt) && !obj.ContainsKey("method"))
                {
                    // Response to a previous client request: resolve uri from tracked request.
                    if (_requestUris.TryGetValue(idInt, out var origUri))
                    {
                        uri = origUri;
                    }
                }

                // Track request uris for response mapping.
                if (clientToServer && obj.TryGetPropertyValue("id", out var ridNode) && ridNode is JsonValue ridv &&
                    ridv.TryGetValue<int>(out var ridInt) && obj.TryGetPropertyValue("method", out _))
                {
                    // find uri in this request (may be nested under params.textDocument)
                    string? reqUri = originalUriForTracking;
                    if (reqUri == null && obj.TryGetPropertyValue("params", out var rparams) && rparams is JsonObject rpo &&
                        rpo.TryGetPropertyValue("textDocument", out var rtd) && rtd is JsonObject rtdo &&
                        rtdo.TryGetPropertyValue("uri", out var rtdu) && rtdu is JsonValue rtuv && rtuv.TryGetValue<string>(out var rtus)) reqUri = rtus;
                    if (reqUri != null) _requestUris[ridInt] = reqUri;
                }

                // didOpen: replace text with generated content
                if (clientToServer && obj.TryGetPropertyValue("text", out var textNode) && textNode is JsonValue tv && tv.TryGetValue<string>(out var originalText))
                {
                    // only if parent has uri (textDocument)
                    if (uri != null && uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        var genPath = UriToPath(uri);
                        if (File.Exists(genPath))
                        {
                            obj["text"] = File.ReadAllText(genPath);
                        }
                        else
                        {
                            // transform on the fly
                            var origPath = GetOriginalPath(uri);
                            if (origPath != null && File.Exists(origPath))
                            {
                                var source = File.ReadAllText(origPath);
                                var (text, _) = _transformer.TransformWithSourceMap(source);
                                Directory.CreateDirectory(Path.GetDirectoryName(genPath)!);
                                File.WriteAllText(genPath, text);
                                obj["text"] = text;
                            }
                            else
                            {
                                // transform the provided text directly
                                var (t, _) = _transformer.TransformWithSourceMap(originalText);
                                obj["text"] = t;
                            }
                        }
                    }
                    else if (uri != null && uri.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        // fallback: transform provided text
                        var (t, _) = _transformer.TransformWithSourceMap(originalText);
                        obj["text"] = t;
                    }
                }

                // didChange (full sync): transform each content change's text
                if (clientToServer && obj.TryGetPropertyValue("contentChanges", out var ccNode) && ccNode is JsonArray ccArr)
                {
                    foreach (var item in ccArr)
                    {
                        if (item is not JsonObject co) continue;
                        if (co.ContainsKey("range")) continue; // incremental; skip
                        if (co.TryGetPropertyValue("text", out var tNode) && tNode is JsonValue tv2 && tv2.TryGetValue<string>(out var ct))
                        {
                            string? genText = null;
                            SourceMap? genMap = null;
                            if (uri != null && (uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase)))
                            {
                                var (t, m) = _transformer.TransformWithSourceMap(ct);
                                genText = t;
                                genMap = m;
                            }
                            if (genText != null)
                            {
                                co["text"] = genText;
                                // persist updated generated file + map for position mapping
                                if (uri != null && uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        var genPath = UriToPath(uri);
                                        Directory.CreateDirectory(Path.GetDirectoryName(genPath)!);
                                        File.WriteAllText(genPath, genText);
                                        if (genMap != null)
                                        {
                                            genMap.SourceFile = GetOriginalPath(uri);
                                            genMap.GeneratedFile = genPath;
                                            File.WriteAllText(genPath + ".map", System.Text.Json.JsonSerializer.Serialize(genMap));
                                        }
                                        _resolver.Load(genPath);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }

                // position objects
                if (obj.ContainsKey("line") && obj.ContainsKey("character"))
                {
                    ConvertPosition(obj, clientToServer, uri ?? currentUri);
                    return;
                }

                // range objects
                if (obj.ContainsKey("start") && obj.ContainsKey("end") &&
                    obj["start"] is JsonObject && obj["end"] is JsonObject)
                {
                    ConvertPosition((JsonObject)obj["start"]!, clientToServer, uri ?? currentUri);
                    ConvertPosition((JsonObject)obj["end"]!, clientToServer, uri ?? currentUri);
                }

                foreach (var kv in obj.ToList())
                {
                    RewriteNode(kv.Value, clientToServer, uri);
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    RewriteNode(item, clientToServer, currentUri);
                }
            }
        }

        // Helpers for URI mapping
        private void ConvertPosition(JsonObject pos, bool clientToServer, string? uri)
        {
            if (uri == null) { Console.Error.WriteLine("ConvertPosition: uri null"); return; }
            if (!int.TryParse(pos["line"]?.ToString(), out var line)) return;
            if (!int.TryParse(pos["character"]?.ToString(), out var ch)) return;
            Console.Error.WriteLine($"ConvertPosition: {(clientToServer ? "C->S" : "S->C")} uri={uri} line={line} ch={ch}");

            string generatedPath;
            string originalPath;
            if (clientToServer)
            {
                generatedPath = UriToPath(uri);
                originalPath = GetOriginalPath(uri);
            }
            else
            {
                originalPath = UriToPath(uri);
                generatedPath = GetGeneratedPath(originalPath);
            }

            _resolver.Load(generatedPath);

            string originalText = File.Exists(originalPath) ? File.ReadAllText(originalPath) : "";
            string generatedText = File.Exists(generatedPath) ? File.ReadAllText(generatedPath) : "";

            int offset = clientToServer
                ? PositionToOffset(originalText, line, ch)
                : PositionToOffset(generatedText, line, ch);

            int mappedOffset;
            if (clientToServer)
            {
                mappedOffset = _resolver.ToGenerated(generatedPath, offset);
            }
            else
            {
                var r = _resolver.ToOriginal(generatedPath, offset, 1);
                mappedOffset = r?.OriginalStart ?? offset;
            }

            var (newLine, newChar) = clientToServer
                ? OffsetToPosition(generatedText, mappedOffset)
                : OffsetToPosition(originalText, mappedOffset);

            pos["line"] = newLine;
            pos["character"] = newChar;
        }

        private static int PositionToOffset(string text, int line, int character)
        {
            var lines = text.Split('\n');
            if (line < 0) line = 0;
            if (line >= lines.Length) return text.Length;
            int offset = 0;
            for (int i = 0; i < line; i++) offset += lines[i].Length + 1;
            int lineLen = lines[line].Length;
            if (lineLen > 0 && lines[line][lineLen - 1] == '\r') lineLen--;
            int col = Math.Min(character, lineLen);
            return Math.Min(offset + col, text.Length);
        }

        private static (int Line, int Character) OffsetToPosition(string text, int offset)
        {
            if (offset < 0) offset = 0;
            if (offset > text.Length) offset = text.Length;
            int line = 0;
            int col = 0;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\n') { line++; col = 0; }
                else col++;
            }
            return (line, col);
        }

        private string GetGeneratedUri(string originalUri)
        {
            var path = UriToPath(originalUri);
            if (!path.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase)) return originalUri;
            var genPath = GetGeneratedPath(path);
            _originalToGenerated[path] = genPath;
            _generatedToOriginal[genPath] = path;
            return PathToUri(genPath);
        }

        private string GetOriginalUri(string generatedUri)
        {
            var path = UriToPath(generatedUri);
            if (_generatedToOriginal.TryGetValue(path, out var orig)) return PathToUri(orig);
            if (path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            {
                // derive original
                var root = _options.ProjectRoot;
                var rel = Path.GetRelativePath(Path.Combine(root, _options.OutputDirectory), path);
                if (rel.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel.Substring(0, rel.Length - ".generated.cs".Length);
                    var origPath = Path.Combine(root, rel);
                    _generatedToOriginal[path] = origPath;
                    _originalToGenerated[origPath] = path;
                    return PathToUri(origPath);
                }
            }
            return generatedUri;
        }

        private string GetGeneratedPath(string originalPath)
        {
            var root = _options.ProjectRoot;
            var rel = Path.GetRelativePath(root, originalPath);
            var outDir = Path.Combine(root, _options.OutputDirectory);
            return Path.Combine(outDir, rel + ".generated.cs");
        }

        private string GetOriginalPath(string generatedUri)
        {
            var path = UriToPath(generatedUri);
            if (_generatedToOriginal.TryGetValue(path, out var orig)) return orig;
            return path;
        }

        private static string UriToPath(string uri)
        {
            try
            {
                // VS Code may send the drive-letter colon percent-encoded (%3A).
                var unescaped = Uri.UnescapeDataString(uri);
                var path = new Uri(unescaped).LocalPath;
                // Normalize forward slashes and strip a leading slash before a drive letter.
                path = path.Replace('/', '\\');
                if (path.Length >= 3 && path[0] == '\\' && char.IsLetter(path[1]) && path[2] == ':')
                {
                    path = path.Substring(1);
                }
                return path;
            }
            catch { return uri; }
        }

        private static string PathToUri(string path)
        {
            return new Uri(path).AbsoluteUri;
        }

        private string fileUriGeneratedToOriginal(string uri) => GetOriginalUri(uri);
    }
}