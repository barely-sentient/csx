namespace CSX.CliTool;

internal static class ProjectResolver
{
    public static string ResolveRoot(string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return Directory.GetCurrentDirectory();
        }

        var full = Path.GetFullPath(project);
        if (File.Exists(full))
        {
            return Path.GetDirectoryName(full)!;
        }

        if (Directory.Exists(full))
        {
            return full;
        }

        return Path.GetFullPath(project);
    }

    public static string? ResolveCsproj(string root, string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            var full = Path.GetFullPath(project);
            if (File.Exists(full) &&
                full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        // Prefer a project file directly in the root.
        var rootProjects = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
        if (rootProjects.Length > 0)
        {
            return rootProjects[0];
        }

        // Fall back to the generated project, if present.
        var generatedDir = Path.Combine(root, "src", "generated");
        if (Directory.Exists(generatedDir))
        {
            var generatedProjects = Directory.GetFiles(generatedDir, "*.csproj", SearchOption.TopDirectoryOnly);
            if (generatedProjects.Length > 0)
            {
                return generatedProjects[0];
            }
        }

        // Last resort: search recursively, skipping build output.
        foreach (var candidate in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, candidate);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                          p.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }
}