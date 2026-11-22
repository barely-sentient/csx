using System.Diagnostics;

namespace CSX.CliTool;

internal static class BuildCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? project = null;
        string configuration = "Debug";
        string? framework = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project":
                case "-p":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--project requires a value.");
                        return 1;
                    }

                    project = args[i];
                    break;
                case "--configuration":
                case "-c":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--configuration requires a value.");
                        return 1;
                    }

                    configuration = args[i];
                    break;
                case "--framework":
                case "-f":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--framework requires a value.");
                        return 1;
                    }

                    framework = args[i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (project is null && !args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        project = args[i];
                    }
                    else
                    {
                        Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                        PrintUsage();
                        return 1;
                    }

                    break;
            }
        }

        var root = ProjectResolver.ResolveRoot(project);
        var csproj = ProjectResolver.ResolveCsproj(root, project);
        if (csproj is null)
        {
            Console.Error.WriteLine($"csx: no .csproj found under {root}.");
            return 1;
        }

        Console.WriteLine("csx: transforming...");
        await CsxTransform.TransformAllAsync(root).ConfigureAwait(false);

        Console.WriteLine($"csx: building {csproj}...");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        if (!string.IsNullOrWhiteSpace(framework))
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(framework!);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: csx build [--project <path>] [--configuration <Debug|Release>] [--framework <tfm>]");
    }
}