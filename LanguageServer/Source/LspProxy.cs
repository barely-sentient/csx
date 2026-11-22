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
        private readonly Dictionary<string, HashSet<string>> _diagnosticIdentifiers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _pendingDiagnosticRefreshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _diagnosticGenerations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, (string Uri, string Identifier, int Generation)> _diagnosticRequests = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _diagnosticRefreshRequests = new(StringComparer.Ordinal);
        private readonly object _rewriteLock = new();
        private readonly object _pendingReadLock = new();
        private readonly SemaphoreSlim _clientOutputLock = new(1, 1);
        private bool _clientSupportsDiagnosticRefresh;
        private int _nextDiagnosticRefreshRequestId;

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
                    var rewrittenInit = RewriteMessage(initDoc, true, out _);
                    await WriteMessageAsync(backendIn, rewrittenInit, ct).ConfigureAwait(false);

                    // Wait for initialize response.
                    JsonDocument? initResp = null;
                    string? initIdRaw = initDoc.RootElement.TryGetProperty("id", out var iid) ? iid.GetRawText() : null;

                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (initResp == null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
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
                        var rewrittenResp = RewriteMessage(initResp, false, out _);
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
            var c2s = PumpAsync(clientIn, backendIn, true, clientOut, ct);
            var s2c = PumpAsync(backendOut, clientOut, false, clientOut, ct);
            await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
        }

        // Reads one backend message, giving up after `timeout` WITHOUT cancelling the read: a
        // cancelled ReadAsync can swallow part of a message. The read stays pending and is picked up
        // by the next call (or by the server->client pump).
        private async Task<(bool TimedOut, JsonDocument? Doc)> ReadBackendAsync(Stream backendOut, TimeSpan timeout, CancellationToken ct)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            Task<JsonDocument?> pending;
            lock (_pendingReadLock)
            {
                _pendingBackendRead ??= ReadMessageAsync(backendOut, ct);
                pending = _pendingBackendRead;
            }

            var completed = await Task.WhenAny(pending, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != pending) return (true, null);

            lock (_pendingReadLock)
            {
                if (_pendingBackendRead == pending)
                {
                    _pendingBackendRead = null;
                }
            }
            return (false, await pending.ConfigureAwait(false));
        }

        private async Task PumpAsync(Stream input, Stream output, bool clientToServer, Stream clientOutput, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                JsonDocument? doc;
                if (!clientToServer)
                {
                    Task<JsonDocument?>? pendingRead;
                    lock (_pendingReadLock)
                    {
                        pendingRead = _pendingBackendRead;
                        _pendingBackendRead = null;
                    }
                    doc = pendingRead != null
                        ? await pendingRead.ConfigureAwait(false)
                        : await ReadMessageAsync(input, ct).ConfigureAwait(false);
                }
                else
                {
                    doc = await ReadMessageAsync(input, ct).ConfigureAwait(false);
                }

                if (doc == null) break;

                if (clientToServer &&
                    !doc.RootElement.TryGetProperty("method", out _) &&
                    doc.RootElement.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.String &&
                    _diagnosticRefreshRequests.TryRemove(responseId.GetString()!, out _))
                {
                    doc.Dispose();
                    continue;
                }

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
                    if (clientToServer)
                    {
                        await WriteMessageAsync(output, rewritten, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteClientMessageAsync(output, rewritten, ct).ConfigureAwait(false);
                    }

                    // After the client signals initialized, open the generated project
                    // in the C# backend so it provides full IntelliSense.
                    if (clientToServer && handledMethod == "initialized" &&
                        !string.IsNullOrWhiteSpace(_options.GeneratedProjectPath) &&
                        File.Exists(_options.GeneratedProjectPath))
                    {
                        var projectUri = new Uri(_options.GeneratedProjectPath).AbsoluteUri;
                        var openProject = JsonDocument.Parse(
                            "{\"jsonrpc\":\"2.0\",\"method\":\"project/open\",\"params\":{\"projects\":[\"" + projectUri + "\"]}}");
                        await WriteMessageAsync(output, openProject, ct).ConfigureAwait(false);
                    }

                    if (clientToServer &&
                        handledMethod == "textDocument/didSave" &&
                        rewritten.RootElement.TryGetProperty("params", out var saveParams) &&
                        saveParams.TryGetProperty("textDocument", out var saveDocument) &&
                        saveDocument.TryGetProperty("uri", out var saveUriNode) &&
                        saveUriNode.GetString() is string generatedUri)
                    {
                        try
                        {
                            var generatedPath = UriToPath(generatedUri);
                            if (File.Exists(generatedPath))
                            {
                                var changeJson =
                                    "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":" +
                                    JsonSerializer.Serialize(generatedUri) + "},\"contentChanges\":[{\"text\":" +
                                    JsonSerializer.Serialize(File.ReadAllText(generatedPath)) + "}]}}";
                                using var change = JsonDocument.Parse(changeJson);
                                await WriteMessageAsync(output, change, ct).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("csx: could not refresh generated document after save: " + ex.Message);
                        }
                    }

                    if (clientToServer &&
                        _clientSupportsDiagnosticRefresh &&
                        (handledMethod == "textDocument/didChange" || handledMethod == "textDocument/didSave"))
                    {
                        try
                        {
                            await RequestDiagnosticRefreshAsync(clientOutput, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("csx: diagnostic refresh request failed: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("proxy: RewriteMessage failed: " + ex.Message);
                    await WriteMessageAsync(output, doc, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task RequestDiagnosticRefreshAsync(Stream clientOutput, CancellationToken ct)
        {
            var requestId = "csx-diagnostic-refresh-" +
                            System.Threading.Interlocked.Increment(ref _nextDiagnosticRefreshRequestId)
                                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            _diagnosticRefreshRequests.TryAdd(requestId, 0);

            using var request = JsonDocument.Parse(
                "{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(requestId) +
                ",\"method\":\"workspace/diagnostic/refresh\",\"params\":{}}");
            try
            {
                await WriteClientMessageAsync(clientOutput, request, ct).ConfigureAwait(false);
            }
            catch
            {
                _diagnosticRefreshRequests.TryRemove(requestId, out _);
                throw;
            }
        }

        private async Task WriteClientMessageAsync(Stream clientOutput, JsonDocument doc, CancellationToken ct)
        {
            await _clientOutputLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WriteMessageAsync(clientOutput, doc, ct).ConfigureAwait(false);
            }
            finally
            {
                _clientOutputLock.Release();
            }
        }

        private void InvalidateDiagnostics(JsonObject message)
        {
            if (!TryGetDocumentParams(message, out _, out var uri))
            {
                return;
            }

            if (!_diagnosticIdentifiers.TryGetValue(uri, out var identifiers) || identifiers.Count == 0)
            {
                return;
            }

            var generation = _diagnosticGenerations.TryGetValue(uri, out var previousGeneration)
                ? previousGeneration + 1
                : 1;
            _diagnosticGenerations[uri] = generation;
            _pendingDiagnosticRefreshes[uri] = new HashSet<string>(identifiers, StringComparer.Ordinal);
        }

        private void PrepareDiagnosticRequest(JsonObject message)
        {
            if (!TryGetDocumentParams(message, out var parameters, out var uri) ||
                parameters["identifier"] is not JsonValue identifierNode ||
                !identifierNode.TryGetValue<string>(out var identifier) ||
                string.IsNullOrEmpty(identifier))
            {
                return;
            }

            if (!_diagnosticIdentifiers.TryGetValue(uri, out var identifiers))
            {
                identifiers = new HashSet<string>(StringComparer.Ordinal);
                _diagnosticIdentifiers[uri] = identifiers;
            }

            identifiers.Add(identifier);
            if (!_pendingDiagnosticRefreshes.TryGetValue(uri, out var pending) ||
                !pending.Contains(identifier))
            {
                return;
            }

            parameters.Remove("previousResultId");
            if (message["id"] is JsonValue requestIdNode &&
                requestIdNode.TryGetValue<int>(out var requestId))
            {
                var generation = _diagnosticGenerations.TryGetValue(uri, out var currentGeneration)
                    ? currentGeneration
                    : 0;
                _diagnosticRequests[requestId] = (uri, identifier, generation);
            }
        }

        private void CompleteDiagnosticRequest(JsonObject message)
        {
            if (message["id"] is not JsonValue requestIdNode ||
                !requestIdNode.TryGetValue<int>(out var requestId) ||
                !_diagnosticRequests.Remove(requestId, out var request))
            {
                return;
            }

            if (message["result"]?["kind"] is not JsonValue kindNode ||
                !kindNode.TryGetValue<string>(out var kind) ||
                kind != "full" ||
                !_diagnosticGenerations.TryGetValue(request.Uri, out var currentGeneration) ||
                currentGeneration != request.Generation ||
                !_pendingDiagnosticRefreshes.TryGetValue(request.Uri, out var pending))
            {
                return;
            }

            pending.Remove(request.Identifier);
            if (pending.Count == 0)
            {
                _pendingDiagnosticRefreshes.Remove(request.Uri);
            }
        }

        private void ClearDiagnosticState(JsonObject message)
        {
            if (!TryGetDocumentParams(message, out _, out var uri))
            {
                return;
            }

            _diagnosticIdentifiers.Remove(uri);
            _pendingDiagnosticRefreshes.Remove(uri);
            _diagnosticGenerations.Remove(uri);
            foreach (var requestId in _diagnosticRequests
                         .Where(request => string.Equals(request.Value.Uri, uri, StringComparison.OrdinalIgnoreCase))
                         .Select(request => request.Key)
                         .ToArray())
            {
                _diagnosticRequests.Remove(requestId);
            }
        }

        private static bool TryGetDocumentParams(JsonObject message, out JsonObject parameters, out string uri)
        {
            parameters = null!;
            uri = "";
            if (message["params"] is not JsonObject paramsObject ||
                paramsObject["textDocument"] is not JsonObject textDocument ||
                textDocument["uri"] is not JsonValue uriNode ||
                !uriNode.TryGetValue<string>(out var value) ||
                string.IsNullOrEmpty(value))
            {
                return false;
            }

            parameters = paramsObject;
            uri = value;
            return true;
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

        private JsonDocument RewriteMessage(JsonDocument doc, bool clientToServer, out string? methodName)
        {
            lock (_rewriteLock)
            {
                return RewriteMessageCore(doc, clientToServer, out methodName);
            }
        }

        private JsonDocument RewriteMessageCore(JsonDocument doc, bool clientToServer, out string? methodName)
        {
            var node = JsonNode.Parse(doc.RootElement.GetRawText());
            if (node == null)
            {
                methodName = null;
                return doc;
            }

            methodName = node is JsonObject methodObj &&
                         methodObj.TryGetPropertyValue("method", out var methodNode) &&
                         methodNode is JsonValue methodValue &&
                         methodValue.TryGetValue<string>(out var parsedMethod)
                ? parsedMethod
                : null;

            if (clientToServer && methodName == "initialize")
            {
                _clientSupportsDiagnosticRefresh = HasDiagnosticRefreshSupport(node);
            }

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

            if (methodName == "textDocument/hover" || methodName == "textDocument/completion")
            {
                Console.Error.WriteLine("proxy: " + (clientToServer ? "C->S" : "S->C") + " " + methodName);
            }

            RewriteNode(node, clientToServer, null, methodName);

            if (node is JsonObject messageObject)
            {
                if (clientToServer && methodName == "textDocument/didClose")
                {
                    ClearDiagnosticState(messageObject);
                }
                else if (clientToServer &&
                         (methodName == "textDocument/didChange" || methodName == "textDocument/didSave"))
                {
                    InvalidateDiagnostics(messageObject);
                }
                else if (clientToServer && methodName == "textDocument/diagnostic")
                {
                    PrepareDiagnosticRequest(messageObject);
                }
                else if (!clientToServer && methodName == null)
                {
                    CompleteDiagnosticRequest(messageObject);
                }

                if (!clientToServer &&
                    !messageObject.ContainsKey("method") &&
                    messageObject["id"] is JsonValue responseIdNode &&
                    responseIdNode.TryGetValue<int>(out var responseId))
                {
                    _requestUris.Remove(responseId);
                }
            }

            var newJson = node.ToJsonString();
            return JsonDocument.Parse(newJson);
        }

        private static bool HasDiagnosticRefreshSupport(JsonNode? message)
        {
            return message is JsonObject root &&
                   root["params"] is JsonObject parameters &&
                   parameters["capabilities"] is JsonObject capabilities &&
                   capabilities["workspace"] is JsonObject workspace &&
                   workspace["diagnostics"] is JsonObject diagnostics &&
                   diagnostics["refreshSupport"] is JsonValue refreshSupport &&
                   refreshSupport.TryGetValue<bool>(out var supported) &&
                   supported;
        }

        private void RewriteNode(JsonNode? node, bool clientToServer, string? currentUri, string? methodName)
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
                    uri = clientToServer ? GetGeneratedUri(us) : GetOriginalUri(us);
                    obj["uri"] = uri;
                }
                else if (obj.TryGetPropertyValue("textDocument", out var tdNode) && tdNode is JsonObject tdObj &&
                         tdObj.TryGetPropertyValue("uri", out var tdu) && tdu is JsonValue tuv && tuv.TryGetValue<string>(out var tus))
                {
                    uri = clientToServer ? GetGeneratedUri(tus) : GetOriginalUri(tus);
                }
                else if (!clientToServer && obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idv &&
                         idv.TryGetValue<int>(out var idInt) && !obj.ContainsKey("method"))
                {
                    if (_requestUris.TryGetValue(idInt, out var origUri))
                    {
                        uri = origUri;
                    }
                    else
                    {
                        Console.Error.WriteLine("ConvertPosition: id " + idInt + " not in _requestUris (count=" + _requestUris.Count + ")");
                    }
                }

                if (clientToServer && obj.TryGetPropertyValue("id", out var ridNode) && ridNode is JsonValue ridv &&
                    ridv.TryGetValue<int>(out var ridInt) && obj.TryGetPropertyValue("method", out _))
                {
                    string? reqUri = originalUriForTracking;
                    if (reqUri == null && obj.TryGetPropertyValue("params", out var rparams) && rparams is JsonObject rpo &&
                        rpo.TryGetPropertyValue("textDocument", out var rtd) && rtd is JsonObject rtdo &&
                        rtdo.TryGetPropertyValue("uri", out var rtdu) && rtdu is JsonValue rtuv && rtuv.TryGetValue<string>(out var rtus))
                    {
                        reqUri = rtus;
                    }
                    if (reqUri != null) _requestUris[ridInt] = reqUri;
                }

                if (clientToServer &&
                    methodName == "textDocument/didOpen" &&
                    uri != null &&
                    uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) &&
                    obj.ContainsKey("languageId"))
                {
                    obj["languageId"] = "csharp";
                }

                if (clientToServer && methodName == "textDocument/didClose" && uri != null &&
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
                        if (!co.TryGetPropertyValue("text", out var tNode) ||
                            tNode is not JsonValue tv2 ||
                            !tv2.TryGetValue<string>(out var ct))
                        {
                            continue;
                        }

                        if (uri == null) continue;

                        string originalPath;
                        string generatedPath;
                        if (uri.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                        {
                            generatedPath = UriToPath(uri);
                            originalPath = GetOriginalPath(uri);
                        }
                        else if (uri.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase))
                        {
                            originalPath = UriToPath(uri);
                            generatedPath = GetGeneratedPath(originalPath);
                        }
                        else
                        {
                            continue;
                        }

                        string source = ReadOriginalText(originalPath);
                        if (co.TryGetPropertyValue("range", out var rangeNode) && rangeNode is JsonObject range)
                        {
                            source = ApplyTextChange(source, range, ct);
                        }
                        else
                        {
                            source = NormalizeLineEndings(ct);
                        }

                        _openText[originalPath] = source;
                        WriteGenerated(generatedPath, originalPath, source);
                        co["text"] = File.ReadAllText(generatedPath);
                        co.Remove("range");
                        co.Remove("rangeLength");
                    }
                }

                // didSave: the editor may not send didChange again, so regenerate from disk.
                if (clientToServer && methodName == "textDocument/didSave" && uri != null &&
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
                        ConvertRange((JsonObject)obj["start"]!, (JsonObject)obj["end"]!, uri ?? currentUri);
                    }
                    else
                    {
                        ConvertRangeToGenerated((JsonObject)obj["start"]!, (JsonObject)obj["end"]!, uri ?? currentUri);
                    }

                    return;
                }

                foreach (var kv in obj.ToList())
                {
                    if (kv.Key == "changes" && kv.Value is JsonObject workspaceChanges)
                    {
                        var rewrittenChanges = new JsonObject();
                        foreach (var change in workspaceChanges)
                        {
                            var changeUri = clientToServer
                                ? GetGeneratedUri(change.Key)
                                : GetOriginalUri(change.Key);
                            var edits = change.Value?.DeepClone();
                            RewriteNode(edits, clientToServer, changeUri, methodName);

                            if (rewrittenChanges.TryGetPropertyValue(changeUri, out var existingEdits))
                            {
                                if (existingEdits is not JsonArray existingArray || edits is not JsonArray newArray)
                                {
                                    throw new InvalidOperationException($"Conflicting workspace edits for '{changeUri}'.");
                                }

                                foreach (var edit in newArray)
                                {
                                    existingArray.Add(edit?.DeepClone());
                                }
                            }
                            else
                            {
                                rewrittenChanges[changeUri] = edits;
                            }
                        }

                        obj[kv.Key] = rewrittenChanges;
                        continue;
                    }

                    RewriteNode(kv.Value, clientToServer, uri, methodName);
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    RewriteNode(item, clientToServer, currentUri, methodName);
                }
            }
        }

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
                originalStart = Math.Clamp(mapping.OriginalStart, 0, originalText.Length);
                originalEnd = Math.Min(originalText.Length, originalStart + 1);
            }
            else
            {
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

        private void ConvertRangeToGenerated(JsonObject start, JsonObject end, string? uri)
        {
            if (uri == null ||
                !int.TryParse(start["line"]?.ToString(), out var startLine) ||
                !int.TryParse(start["character"]?.ToString(), out var startChar) ||
                !int.TryParse(end["line"]?.ToString(), out var endLine) ||
                !int.TryParse(end["character"]?.ToString(), out var endChar))
            {
                return;
            }

            var generatedPath = UriToPath(uri);
            var originalPath = GetOriginalPath(uri);
            if (!originalPath.EndsWith(_options.FileExtension, StringComparison.OrdinalIgnoreCase)) return;

            _resolver.Load(generatedPath);
            string originalText = ReadOriginalText(originalPath);
            string generatedText = NormalizeLineEndings(File.Exists(generatedPath) ? File.ReadAllText(generatedPath) : "");

            int originalStart = PositionToOffset(originalText, startLine, startChar);
            int originalEnd = PositionToOffset(originalText, endLine, endChar);
            if (originalEnd < originalStart)
            {
                throw new InvalidOperationException("Received a range whose end precedes its start.");
            }

            int generatedStart = _resolver.ToGenerated(generatedPath, originalStart);
            int generatedEnd = _resolver.ToGenerated(generatedPath, originalEnd);
            if (generatedEnd < generatedStart)
            {
                Console.Error.WriteLine($"Non-monotonic source-map range for {uri}: {generatedStart}..{generatedEnd}; ordering mapped boundaries.");
                (generatedStart, generatedEnd) = (generatedEnd, generatedStart);
            }

            var (mappedStartLine, mappedStartChar) = OffsetToPosition(generatedText, generatedStart);
            var (mappedEndLine, mappedEndChar) = OffsetToPosition(generatedText, generatedEnd);
            start["line"] = mappedStartLine;
            start["character"] = mappedStartChar;
            end["line"] = mappedEndLine;
            end["character"] = mappedEndChar;
        }

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
            int col = Math.Clamp(character, 0, lineLen);
            return Math.Clamp(offset + col, 0, text.Length);
        }

        private static string ApplyTextChange(string source, JsonObject range, string replacement)
        {
            if (range["start"] is not JsonObject start || range["end"] is not JsonObject end ||
                !int.TryParse(start["line"]?.ToString(), out int startLine) ||
                !int.TryParse(start["character"]?.ToString(), out int startCharacter) ||
                !int.TryParse(end["line"]?.ToString(), out int endLine) ||
                !int.TryParse(end["character"]?.ToString(), out int endCharacter))
            {
                throw new InvalidOperationException("Received a text change with an invalid range.");
            }

            int startOffset = PositionToOffset(source, startLine, startCharacter);
            int endOffset = PositionToOffset(source, endLine, endCharacter);
            if (endOffset < startOffset)
            {
                throw new InvalidOperationException("Received a text change whose end precedes its start.");
            }

            return source.Substring(0, startOffset) +
                   NormalizeLineEndings(replacement) +
                   source.Substring(endOffset);
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
                var unescaped = Uri.UnescapeDataString(uri);
                var path = new Uri(unescaped).LocalPath;
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
    }
}