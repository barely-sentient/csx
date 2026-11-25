using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSX.LanguageServer
{
    internal sealed record ComponentInfo(string Name, IReadOnlyList<string> Props);

    /// <summary>
    /// Finds CSX components the way the user described them: every type that derives from
    /// <c>Component&lt;TProps&gt;</c> (qualified or not), plus the public members of its props
    /// type. Syntax-only scanning is used so it works from both the proxy and the fallback server.
    /// </summary>
    internal static class ComponentDiscovery
    {
        private static readonly object Gate = new();
        private static string _cacheKey = "";
        private static DateTime _cacheTime = DateTime.MinValue;
        private static List<ComponentInfo> _cache = new();

        public static IReadOnlyList<ComponentInfo> Discover(string root, string outputDirectory, string fileExtension)
        {
            var key = root + "|" + outputDirectory + "|" + fileExtension;
            lock (Gate)
            {
                if (key == _cacheKey && (DateTime.UtcNow - _cacheTime).TotalSeconds < 2)
                {
                    return _cache;
                }

                var result = Scan(root, outputDirectory, fileExtension);
                _cache = result;
                _cacheKey = key;
                _cacheTime = DateTime.UtcNow;
                return result;
            }
        }

        private static List<ComponentInfo> Scan(string root, string outputDirectory, string fileExtension)
        {
            var result = new List<ComponentInfo>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return result;
            }

            var generatedDir = Path.GetFullPath(Path.Combine(root, outputDirectory))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var types = new List<TypeDeclarationSyntax>();
            var trees = new List<SyntaxTree>();
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(root, "*" + fileExtension, SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: component discovery could not enumerate files under '{root}': {ex.Message}");
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
                    trees.Add(tree);
                    types.AddRange(tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>());
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"csx: component discovery could not read '{file}': {ex.Message}");
                }
            }

            var propsByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                if (type.Identifier.Text.Length == 0) continue;
                var members = type.Members
                    .OfType<PropertyDeclarationSyntax>()
                    .Select(p => p.Identifier.Text)
                    .Concat(type.Members.OfType<FieldDeclarationSyntax>()
                        .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text)))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                if (!propsByName.TryGetValue(type.Identifier.Text, out var existing))
                {
                    propsByName[type.Identifier.Text] = members;
                }
                else if (members.Count > existing.Count)
                {
                    propsByName[type.Identifier.Text] = members;
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                var propsTypeName = GetComponentPropsTypeName(type);
                if (propsTypeName == null || !seen.Add(type.Identifier.Text)) continue;

                var props = propsByName.TryGetValue(propsTypeName, out var found)
                    ? found
                    : new List<string>();
                result.Add(new ComponentInfo(type.Identifier.Text, props));
            }

            // Components often live in a referenced framework assembly rather than in the
            // project's own sources, so also look at real symbols from the built binaries.
            try
            {
                var references = new List<MetadataReference>();
                var coreAssembly = typeof(object).Assembly.Location;
                if (File.Exists(coreAssembly)) references.Add(MetadataReference.CreateFromFile(coreAssembly));
                var seenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var binRoot = Path.Combine(root, "bin");
                if (Directory.Exists(binRoot))
                {
                    foreach (var dll in Directory.EnumerateFiles(binRoot, "*.dll", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(dll);
                        if (!seenAssemblies.Add(name)) continue;
                        try { references.Add(MetadataReference.CreateFromFile(dll)); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"csx: component discovery skipped metadata reference '{dll}': {ex.Message}");
                        }
                    }
                }

                var compilation = CSharpCompilation.Create(
                    "CSX.ComponentDiscovery",
                    trees,
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                foreach (var type in EnumerateTypes(compilation.GlobalNamespace))
                {
                    if (type.TypeKind != TypeKind.Class) continue;
                    var propsType = GetPropsType(type);
                    if (propsType == null || !seen.Add(type.Name)) continue;
                    var props = propsType.GetMembers()
                            .OfType<IPropertySymbol>()
                            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
                            .Select(p => p.Name)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
                    result.Add(new ComponentInfo(type.Name, props));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"csx: component symbol discovery failed: {ex.Message}");
            }

            return result.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    foreach (var nested in EnumerateTypes(child)) yield return nested;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in EnumerateNestedTypes(type)) yield return nested;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var child in EnumerateNestedTypes(nested)) yield return child;
            }
        }

        private static INamedTypeSymbol? GetPropsType(INamedTypeSymbol type)
        {
            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (current.Name == "Component" && current.IsGenericType && current.TypeArguments.Length > 0)
                {
                    return current.TypeArguments[0] as INamedTypeSymbol;
                }
            }
            return null;
        }

        private static string? GetComponentPropsTypeName(TypeDeclarationSyntax type)
        {
            var bases = type.BaseList?.Types;
            if (bases == null) return null;
            foreach (var baseType in bases)
            {
                var name = baseType.Type switch
                {
                    GenericNameSyntax generic => generic,
                    QualifiedNameSyntax qualified => qualified.Right as GenericNameSyntax,
                    AliasQualifiedNameSyntax alias => alias.Name as GenericNameSyntax,
                    _ => null
                };

                if (name == null || !string.Equals(name.Identifier.Text, "Component", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.TypeArgumentList.Arguments.Count == 0) continue;
                var propsType = name.TypeArgumentList.Arguments[0];
                return propsType switch
                {
                    SimpleNameSyntax simple => simple.Identifier.Text,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                    AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
                    _ => null
                };
            }
            return null;
        }
    }
}
