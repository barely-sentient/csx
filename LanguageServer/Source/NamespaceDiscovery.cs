using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSX.LanguageServer
{
    /// <summary>
    /// Collects namespaces available in the project's own .cx and .cs files, plus any imports
    /// configured through csx.json. Roslyn's using-directive completion only surfaces metadata
    /// namespaces in the middle of an incomplete directive, so the proxy merges these in to keep
    /// project and CSX-specific namespaces discoverable while typing `using`.
    /// </summary>
    internal static class NamespaceDiscovery
    {
        private static readonly object Gate = new();
        private static string _cacheKey = "";
        private static DateTime _cacheTime = DateTime.MinValue;
        private static List<string> _cache = new();

        public static IReadOnlyList<string> Discover(
            string root,
            string outputDirectory,
            string fileExtension,
            IEnumerable<string>? injectedUsings)
        {
            var configured = (injectedUsings ?? Array.Empty<string>()).ToArray();
            var key = root + "|" + outputDirectory + "|" + fileExtension + "|" +
                      string.Join("\u001f", configured);

            lock (Gate)
            {
                if (key == _cacheKey && (DateTime.UtcNow - _cacheTime).TotalSeconds < 2)
                {
                    return _cache;
                }

                var result = Scan(root, outputDirectory, fileExtension, configured);
                _cache = result;
                _cacheKey = key;
                _cacheTime = DateTime.UtcNow;
                return result;
            }
        }

        private static List<string> Scan(
            string root,
            string outputDirectory,
            string fileExtension,
            IReadOnlyList<string> configuredUsings)
        {
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return namespaces.OrderBy(n => n, StringComparer.Ordinal).ToList();
            }

            var generatedDir = Path.GetFullPath(Path.Combine(root, outputDirectory))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var files = new List<string>();
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(root, "*" + fileExtension, SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: namespace discovery could not enumerate files under '{root}': {ex.Message}");
            }

            foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var full = Path.GetFullPath(file);
                    if (full.StartsWith(generatedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (full.Split(Path.DirectorySeparatorChar).Any(p =>
                            p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var tree = CSharpSyntaxTree.ParseText(
                        File.ReadAllText(full),
                        new CSharpParseOptions(LanguageVersion.Preview));
                    foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                    {
                        AddWithParents(namespaces, FullName(declaration));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"csx: namespace discovery could not read '{file}': {ex.Message}");
                }
            }

            foreach (var raw in configuredUsings)
            {
                var target = NormalizeConfiguredUsing(raw);
                if (target != null) AddWithParents(namespaces, target);
            }

            return namespaces.OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        private static string FullName(BaseNamespaceDeclarationSyntax declaration)
        {
            var parts = new Stack<string>();
            SyntaxNode? current = declaration;
            while (current is BaseNamespaceDeclarationSyntax ns)
            {
                var name = ns.Name.ToString().Trim();
                if (name.Length > 0) parts.Push(name);
                current = current.Parent;
            }
            return string.Join(".", parts);
        }

        private static void AddWithParents(HashSet<string> namespaces, string name)
        {
            var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 1; i <= parts.Length; i++)
            {
                namespaces.Add(string.Join(".", parts.Take(i)));
            }
        }

        private static string? NormalizeConfiguredUsing(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var value = raw.Trim();
            if (value.EndsWith(';')) value = value.Substring(0, value.Length - 1).TrimEnd();
            if (value.StartsWith("global ", StringComparison.Ordinal))
            {
                value = value.Substring("global ".Length).TrimStart();
            }
            if (value.StartsWith("using ", StringComparison.Ordinal))
            {
                value = value.Substring("using ".Length).TrimStart();
            }

            // Static imports and aliases do not name a namespace on their own.
            if (value.StartsWith("static ", StringComparison.Ordinal) ||
                value.Contains('='))
            {
                return null;
            }

            return value.Length == 0 ? null : value;
        }
    }
}
