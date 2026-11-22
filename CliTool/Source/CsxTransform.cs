using System.Text.Json;
using CSX.Transformer;

namespace CSX.CliTool;

internal static class CsxTransform
{
    public static async Task TransformAllAsync(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        var config = LoadOrCreateConfig(root);

        var outputDir = Path.Combine(root, string.IsNullOrWhiteSpace(config.GeneratedFileOutput)
            ? "src/generated"
            : config.GeneratedFileOutput!);
        Directory.CreateDirectory(outputDir);

        // Remove generated output from previous runs so stale files cannot leak.
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

        foreach (var file in Directory.EnumerateFiles(root, "*.cx", SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;

            var relative = Path.GetRelativePath(root, file);
            if (IsIgnoredSourcePath(relative)) continue;

            try
            {
                var source = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var transformer = new CSX.Transformer.Transformer();
                var (text, map) = transformer.TransformWithSourceMap(source, config);
                var outPath = Path.Combine(outputDir, relative + ".generated.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllTextAsync(outPath, text, ct).ConfigureAwait(false);
                map.SourceFile = file;
                map.GeneratedFile = outPath;
                await File.WriteAllTextAsync(outPath + ".map",
                    JsonSerializer.Serialize(map), ct).ConfigureAwait(false);
                Console.WriteLine($"csx: transformed {relative}");
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

    private static TransformConfig LoadOrCreateConfig(string root)
    {
        var configPath = Path.Combine(root, "csx.json");
        try
        {
            if (!File.Exists(configPath))
            {
                var defaultConfig = new TransformConfig();
                var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                Console.WriteLine($"csx: created {configPath}");
            }

            var text = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<TransformConfig>(text);
            if (config != null) return config;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"csx: failed to load csx.json: {ex.Message}");
        }

        return new TransformConfig();
    }

    private static bool IsIgnoredSourcePath(string relativePath)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var part in relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
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
}