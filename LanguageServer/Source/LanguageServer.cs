using System.Diagnostics;
using CSX.Transformer;

namespace CSX.LanguageServer
{
    public class LanguageServer : IAsyncDisposable
    {
        private readonly LanguageServerOptions _options;
        private readonly SourceMapResolver _resolver = new();
        private readonly List<string> _generatedFiles = new();
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private Process? _csharpServer;

        public LanguageServer(LanguageServerOptions? options = null)
        {
            _options = options ?? new LanguageServerOptions();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await TransformAllAsync(_cts.Token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_options.CSharpServerPath) && File.Exists(_options.CSharpServerPath))
            {
                var psi = new ProcessStartInfo(_options.CSharpServerPath)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                _csharpServer = Process.Start(psi);
            }

            _runTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        }

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

        private async Task TransformAllAsync(CancellationToken ct)
        {
            var root = _options.ProjectRoot;
            var outputDir = Path.Combine(root, _options.OutputDirectory);
            Directory.CreateDirectory(outputDir);

            foreach (var file in Directory.EnumerateFiles(root, "*" + _options.FileExtension, SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                var source = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var transformer = new CSX.Transformer.Transformer();
                var (text, map) = transformer.TransformWithSourceMap(source);
                var relative = Path.GetRelativePath(root, file);
                var outPath = Path.Combine(outputDir, relative + ".generated.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllTextAsync(outPath, text, ct).ConfigureAwait(false);
                map.SourceFile = file;
                map.GeneratedFile = outPath;
                await File.WriteAllTextAsync(outPath + ".map", System.Text.Json.JsonSerializer.Serialize(map), ct).ConfigureAwait(false);
                _generatedFiles.Add(outPath);
                _resolver.Load(outPath);
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            if (_csharpServer == null)
            {
                try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return;
            }

            var proxy = new LspProxy(_options, _resolver);
            var backendIn = _csharpServer.StandardInput.BaseStream;
            var backendOut = _csharpServer.StandardOutput.BaseStream;

            await proxy.RunAsync(stdin, stdout, backendIn, backendOut, ct).ConfigureAwait(false);
        }
    }
}