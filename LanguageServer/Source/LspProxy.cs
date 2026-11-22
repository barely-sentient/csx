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
        private System.Threading.Tasks.TaskCompletionSource<bool>? _projectReady;
        private readonly Dictionary<int, string> _requestUris = new();
        private string? _currentMethod;
        private readonly object _rewriteLock = new();

        // Latest editor buffer for each open source file (LF-normalized). Positions from the
        // client refer to the buffer, which differs from the file on disk while it has unsaved edits.
        private readonly Dictionary<string, string> _openText = new(StringComparer.OrdinalIgnoreCase);

        // A backend read that outlived a handshake timeout. It must be consumed by the next reader
        // rather than abandoned, otherwise a message is lost or two readers race on one stream.
        private Task<JsonDocument?>? _pendingBackendRead;

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
                        // Wait for the whole remaining window; the old 3s per-read timeout gave up
                        // on a backend that was merely slow to start, so the client never got its
                        // initialize response.
                        var (timedOut, msg) = await ReadBackendAsync(backendOut, deadline - DateTime.UtcNow, ct).ConfigureAwait(false);
                        if (timedOut || msg == null) break;
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
                            var (timedOut, msg) = await ReadBackendAsync(backendOut, pdeadline - DateTime.UtcNow, ct).ConfigureAwait(false);
                            if (timedOut || msg == null) break;
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

        // Reads one backend message, giving up after `timeout` WITHOUT cancelling the read: a
        // cancelled ReadAsync can swallow part of a message. The read stays pending and is picked up
        // by the next call (or by the server->client pump).
        private async Task<(bool TimedOut, JsonDocument? Doc)> ReadBackendAsync(Stream backendOut, TimeSpan timeout, CancellationToken ct)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            _pendingBackendRead ??= ReadMessageAsync(backendOut, ct);
            var pending = _pendingBackendRead;
            var completed = await Task.WhenAny(pending, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != pending) return (true, null);

            _pendingBackendRead = null;
            return (false, await pending.ConfigureAwait(false));
        }

        private async Task PumpAsync(Stream input, Stream output, bool clientToServer, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                JsonDocument? doc;
                var pendingRead = !clientToServer ? _pendingBackendRead : null;
                if (pendingRead != null)
                {
                    _pendingBackendRead = null;
                    doc = await pendingRead.ConfigureAwait(false);
                }
                else
                {
                    doc = await ReadMessageAsync(input, ct).ConfigureAwait(false);
                }
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
                    var rewritten = RewriteMessage(doc, clientToServer, out var handledMethod);
                    await WriteMessageAsync(output, rewritten, ct).ConfigureAwait(false);

                    // didSave regenerates the file on disk; push that text to the C# backend
                    // as a full didChange so diagnostics refresh without another edit.
                    if (clientToServer && handledMethod == "textDocument/didSave" &&
                        rewritten.RootElement.TryGetProperty("params", out var saveParams) &&
                        saveParams.TryGetProperty("textDocument", out var saveDoc) &&
                        saveDoc.TryGetProperty("uri", out var saveUriNode))
                    {
                        var genUri = saveUriNode.GetString();
                        var genPath = UriToPath(genUri ?? "");
                        if (File.Exists(genPath))
                        {
                            var changeJson = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":" +
                                             JsonSerializer.Serialize(genUri) + "},\"contentChanges\":[{\"text\":" +
                                             JsonSerializer.Serialize(File.ReadAllText(genPath)) + "}]}}";
                            using var change = JsonDocument.Parse(changeJson);
                            await WriteMessageAsync(output, change, ct).ConfigureAwait(false);
                        }
                    }

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

        private JsonDocument RewriteMessage(JsonDocument doc, bool clientToServer) =>
            RewriteMessage(doc, clientToServer, out _);

        // Both pump directions rewrite messages concurrently, and rewriting touches shared state
        // (_currentMethod, _requestUris, the path maps). Serialize it, and hand the method back to
        // the caller instead of having it read a field another thread may have overwritten.
        private JsonDocument RewriteMessage(JsonDocument doc, bool clientToServer, out string? method)
        {
            lock (_rewriteLock)
            {
                var result = RewriteMessageCore(doc, clientToServer);
                method = _currentMethod;
                return result;
            }
        }

        private JsonDocument RewriteMessageCore(JsonDocument doc, bool clientToServer)
        {
            var node = JsonNode.Parse(doc.RootElement.GetRawText());
            if (node == null) return doc;

            _currentMethod = node is JsonObject methodObj &&
                             methodObj.TryGetPropertyValue("method", out var methodNode) &&
                             methodNode is JsonValue methodValue &&
                             methodValue.TryGetValue<string>(out var methodName)
                ? methodName
                : null;

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
                // Rider serializes DiagnosticTag as a number; Roslyn's LSP model expects a
                // string enum. Tags are optional, so drop them before either side sees them.
                if (obj.ContainsKey("tags") && obj.ContainsKey("message") && obj.ContainsKey("range"))
                {
                    obj.Remove("tags");
                }

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
                    else
                    {
                        Console.Error.WriteLine("ConvertPosition: id " + idInt + " not in _requestUris (count=" + _requestUris.Count + ")");
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

                if (clientToServer && _currentMethod == "textDocument/didClose" && uri != null &&
                    uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                {
                    _openText.Remove(GetOriginalPath(uri));
                }

                // didOpen: always regenerate from the current source and nearest csx.json
                if (clientToServer && obj.TryGetPropertyValue("text", out var textNode) && textNode is JsonValue tv && tv.TryGetValue<string>(out var originalText))
                {
                    if (uri != null && uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        var genPath = UriToPath(uri);
                        var origPath = GetOriginalPath(uri);

                        // When the client opened the .cx file itself, its text IS the current
                        // buffer. Only fall back to disk if it opened the generated file directly.
                        bool openedFromSource = originalUriForTracking != null &&
                            originalUriForTracking.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase);
                        var source = openedFromSource
                            ? originalText
                            : (File.Exists(origPath) ? File.ReadAllText(origPath) : originalText);
                        if (openedFromSource) _openText[origPath] = NormalizeLineEndings(originalText);
                        WriteGenerated(genPath, origPath, source);
                        obj["text"] = File.ReadAllText(genPath);
                    }
                    else if (uri != null && uri.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        var genPath = GetGeneratedPath(UriToPath(uri));
                        WriteGenerated(genPath, UriToPath(uri), originalText);
                        obj["text"] = File.ReadAllText(genPath);
                    }
                }

                // didChange (full sync): regenerate from the edited .cx text
                if (clientToServer && obj.TryGetPropertyValue("contentChanges", out var ccNode) && ccNode is JsonArray ccArr)
                {
                    foreach (var item in ccArr)
                    {
                        if (item is not JsonObject co) continue;
                        if (co.ContainsKey("range")) continue; // incremental; skip
                        if (co.TryGetPropertyValue("text", out var tNode) && tNode is JsonValue tv2 && tv2.TryGetValue<string>(out var ct))
                        {
                            if (uri != null && uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                            {
                                var genPath = UriToPath(uri);
                                var changedOrigPath = GetOriginalPath(uri);
                                if (changedOrigPath.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase))
                                    _openText[changedOrigPath] = NormalizeLineEndings(ct);
                                WriteGenerated(genPath, changedOrigPath, ct);
                                co["text"] = File.ReadAllText(genPath);
                            }
                            else if (uri != null && uri.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase))
                            {
                                var origPath = UriToPath(uri);
                                var genPath = GetGeneratedPath(origPath);
                                WriteGenerated(genPath, origPath, ct);
                                co["text"] = File.ReadAllText(genPath);
                            }
                        }
                    }
                }

                // didSave: the editor may not send didChange again, so regenerate from disk.
                if (clientToServer && _currentMethod == "textDocument/didSave" && uri != null &&
                    uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                {
                    var genPath = UriToPath(uri);
                    var origPath = GetOriginalPath(uri);
                    if (File.Exists(origPath))
                    {
                        WriteGenerated(genPath, origPath, File.ReadAllText(origPath));
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
                    if (!clientToServer)
                    {
                        // Map start and end as one unit. Mapping the end independently can
                        // land in a different source-map entry (or none at all), producing
                        // diagnostics that stretch across unrelated source lines.
                        ConvertRange((JsonObject)obj["start"]!, (JsonObject)obj["end"]!, uri ?? currentUri);
                    }
                    else
                    {
                        ConvertPosition((JsonObject)obj["start"]!, clientToServer, uri ?? currentUri, false);
                        ConvertPosition((JsonObject)obj["end"]!, clientToServer, uri ?? currentUri, true);
                    }
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
        private void ConvertPosition(JsonObject pos, bool clientToServer, string? uri, bool isRangeEnd = false)
        {
            if (uri == null) return;
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

            // Only .cx files have a generated counterpart. Positions in plain .cs files (for example
            // a definition or reference that lands in one) must pass through untouched; mapping
            // them against a non-existent generated file collapsed them onto line 0.
            if (!originalPath.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase)) return;

            _resolver.Load(generatedPath);

            string originalText = ReadOriginalText(originalPath);
            string generatedText = NormalizeLineEndings(File.Exists(generatedPath) ? File.ReadAllText(generatedPath) : "");

            int offset = clientToServer
                ? PositionToOffset(originalText, line, ch)
                : PositionToOffset(generatedText, line, ch);

            int mappedOffset;
            if (clientToServer)
            {
                mappedOffset = _resolver.ToGenerated(generatedPath, offset);

                // If the mapped position is not on the same identifier (an unmapped region, or
                // a rewritten node), snap the caret onto the nearest matching identifier in the
                // generated document. Skip this when the mapping is already exact: snapping then
                // jumped to a *closer* occurrence of the same word, e.g. the next "foo" in "foo foo".
                if (_currentMethod is "textDocument/completion" or "textDocument/hover" or
                    "textDocument/definition" or "textDocument/references" or
                    "textDocument/rename" or "textDocument/signatureHelp")
                {
                    var word = GetWordAtOffset(originalText, offset);
                    if (word.Length > 0 &&
                        !string.Equals(GetWordAtOffset(generatedText, mappedOffset), word, StringComparison.Ordinal))
                    {
                        var snapped = FindClosestOccurrence(generatedText, word, mappedOffset);
                        if (snapped >= 0) mappedOffset = snapped + word.Length;
                    }
                }
            }
            else
            {
                var r = _resolver.ToOriginal(generatedPath, offset, 1);
                mappedOffset = r?.OriginalStart ?? offset;
                if (isRangeEnd && r != null && r.Value.OriginalLength > 1)
                {
                    mappedOffset = Math.Min(originalText.Length, r.Value.OriginalStart + r.Value.OriginalLength);
                }
            }

            var (newLine, newChar) = clientToServer
                ? OffsetToPosition(generatedText, mappedOffset)
                : OffsetToPosition(originalText, mappedOffset);

            pos["line"] = newLine;
            pos["character"] = newChar;
        }

        private void ConvertRange(JsonObject start, JsonObject end, string? uri)
        {
            if (uri == null) return;
            if (!int.TryParse(start["line"]?.ToString(), out var startLine) ||
                !int.TryParse(start["character"]?.ToString(), out var startChar) ||
                !int.TryParse(end["line"]?.ToString(), out var endLine) ||
                !int.TryParse(end["character"]?.ToString(), out var endChar))
            {
                return;
            }

            var originalPath = UriToPath(uri);
            if (!originalPath.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase)) return;
            var generatedPath = GetGeneratedPath(originalPath);
            _resolver.Load(generatedPath);

            string originalText = ReadOriginalText(originalPath);
            string generatedText = NormalizeLineEndings(File.Exists(generatedPath) ? File.ReadAllText(generatedPath) : "");

            int generatedStart = PositionToOffset(generatedText, startLine, startChar);
            int generatedEnd = Math.Max(generatedStart + 1, PositionToOffset(generatedText, endLine, endChar));

            var mapping = _resolver.FindGeneratedMapping(generatedPath, generatedStart);
            int originalStart;
            int originalEnd;

            if (mapping == null)
            {
                originalStart = Math.Clamp(generatedStart, 0, originalText.Length);
                originalEnd = Math.Clamp(generatedEnd, 0, originalText.Length);
            }
            else if ((mapping.OriginalLength <= 1 && mapping.GeneratedLength != mapping.OriginalLength) ||
                     mapping.GeneratedLength <= 0)
            {
                // Every generated fragment for a JSX node is anchored to the original '<'.
                originalStart = Math.Clamp(mapping.OriginalStart, 0, originalText.Length);
                originalEnd = Math.Min(originalText.Length, originalStart + 1);
            }
            else
            {
                // Preserved (1:1) entries map exactly, so honour where inside the entry the
                // diagnostic starts. Using the entry start put every error in a long preserved
                // run (comments, whitespace, long tokens) at the beginning of that run.
                bool preserved = mapping.GeneratedLength == mapping.OriginalLength;
                int startInEntry = preserved
                    ? Math.Clamp(generatedStart - mapping.GeneratedStart, 0, mapping.OriginalLength)
                    : 0;
                originalStart = Math.Clamp(mapping.OriginalStart + startInEntry, 0, originalText.Length);

                int generatedSpan = Math.Min(
                    Math.Max(1, generatedEnd - generatedStart),
                    Math.Max(1, mapping.GeneratedStart + mapping.GeneratedLength - generatedStart));
                int originalSpan = preserved
                    ? Math.Clamp(generatedSpan, 1, Math.Max(1, mapping.OriginalLength - startInEntry))
                    : Math.Clamp(
                        (int)Math.Round((double)generatedSpan / mapping.GeneratedLength * mapping.OriginalLength),
                        1,
                        mapping.OriginalLength);
                originalEnd = Math.Min(originalText.Length, originalStart + originalSpan);

                if (preserved)
                {
                    // generatedEnd is exclusive: look up the last character covered. Looking up
                    // generatedEnd itself found the NEXT entry and overshot the range by one.
                    var endMapping = _resolver.FindGeneratedMapping(generatedPath, generatedEnd - 1);
                    if (endMapping != null && endMapping.GeneratedLength == endMapping.OriginalLength)
                    {
                        int endInEntry = Math.Clamp(generatedEnd - endMapping.GeneratedStart, 1, endMapping.OriginalLength);
                        var candidate = Math.Min(originalText.Length, endMapping.OriginalStart + endInEntry);
                        if (candidate > originalStart) originalEnd = candidate;
                    }
                }
            }

            if (originalEnd <= originalStart)
            {
                originalEnd = Math.Min(originalText.Length, originalStart + 1);
            }

            var (mappedStartLine, mappedStartChar) = OffsetToPosition(originalText, originalStart);
            var (mappedEndLine, mappedEndChar) = OffsetToPosition(originalText, originalEnd);

            start["line"] = mappedStartLine;
            start["character"] = mappedStartChar;
            end["line"] = mappedEndLine;
            end["character"] = mappedEndChar;
        }

        private static string GetWordAtOffset(string text, int offset)
        {
            if (offset < 0 || offset > text.Length) return string.Empty;
            int start = offset;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            int end = offset;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            return text.Substring(start, end - start);
        }

        private static int FindClosestOccurrence(string text, string needle, int nearOffset)
        {
            if (needle.Length == 0 || text.Length < needle.Length) return -1;

            int best = -1;
            int bestDistance = int.MaxValue;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                // Whole identifiers only ("id" must not match inside "valid"); C# is case-sensitive.
                int endIndex = index + needle.Length;
                bool wholeWord = (index == 0 || !IsWordChar(text[index - 1])) &&
                                 (endIndex >= text.Length || !IsWordChar(text[endIndex]));
                if (wholeWord)
                {
                    int distance = Math.Abs(index - nearOffset);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = index;
                    }
                }
                index++;
            }

            // Avoid snapping to an unrelated identifier far away.
            return bestDistance <= 4000 ? best : -1;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private string ReadOriginalText(string originalPath)
        {
            if (_openText.TryGetValue(originalPath, out var open)) return open;
            return NormalizeLineEndings(File.Exists(originalPath) ? File.ReadAllText(originalPath) : "");
        }

        private static string NormalizeLineEndings(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n');

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

        private void WriteGenerated(string generatedPath, string originalPath, string source)
        {
            try
            {
                var config = LanguageServer.LoadConfigForFile(originalPath, _options.TransformConfig, _options.ProjectRoot);
                var (text, map) = _transformer.TransformWithSourceMap(source, config);
                Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
                File.WriteAllText(generatedPath, text);
                map.SourceFile = originalPath;
                map.GeneratedFile = generatedPath;
                File.WriteAllText(generatedPath + ".map", System.Text.Json.JsonSerializer.Serialize(map));
                _resolver.Load(generatedPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("csx: failed to write generated file: " + ex.Message);
            }
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

            // The URI dictionaries are not always populated before didOpen (for example after
            // a server restart). Derive the source path structurally so we never re-transform
            // the generated file as if it were .cx source.
            if (path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            {
                var outputDir = Path.Combine(_options.ProjectRoot, _options.OutputDirectory);
                var rel = Path.GetRelativePath(outputDir, path);
                if (rel.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel.Substring(0, rel.Length - ".generated.cs".Length);
                    var original = Path.Combine(_options.ProjectRoot, rel);
                    _generatedToOriginal[path] = original;
                    _originalToGenerated[original] = path;
                    return original;
                }
            }
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