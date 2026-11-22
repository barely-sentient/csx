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

        public LspProxy(LanguageServerOptions options, SourceMapResolver resolver)
        {
            _options = options;
            _resolver = resolver;
        }

        public async Task RunAsync(Stream clientIn, Stream clientOut, Stream backendIn, Stream backendOut, CancellationToken ct)
        {
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

                try
                {
                    var rewritten = RewriteMessage(doc, clientToServer);
                    await WriteMessageAsync(output, rewritten, ct).ConfigureAwait(false);
                }
                catch
                {
                    await WriteMessageAsync(output, doc, ct).ConfigureAwait(false);
                }
            }
        }

        private static async Task<JsonDocument?> ReadMessageAsync(Stream stream, CancellationToken ct)
        {
            // Read headers
            var headerBytes = new List<byte>();
            int prev = -1;
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) return null;
                headerBytes.Add((byte)b);
                if (prev == '\r' && b == '\n')
                {
                    // check if next is \r\n (end of headers)
                    // peek by reading two more? Instead, check headerBytes ends with \r\n\r\n
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
                if (obj.TryGetPropertyValue("uri", out var uriNode) && uriNode is JsonValue uv && uv.TryGetValue<string>(out var us))
                {
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
            if (uri == null) return;
            if (!int.TryParse(pos["line"]?.ToString(), out var line)) return;
            if (!int.TryParse(pos["character"]?.ToString(), out var ch)) return;

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
            try { return new Uri(uri).LocalPath; } catch { return uri; }
        }

        private static string PathToUri(string path)
        {
            return new Uri(path).AbsoluteUri;
        }

        private string fileUriGeneratedToOriginal(string uri) => GetOriginalUri(uri);
    }
}