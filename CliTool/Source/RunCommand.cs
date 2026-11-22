using System.Diagnostics;

namespace CSX.CliTool;

internal static class RunCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? project = null;
        string configuration = "Debug";
        var appArgs = new List<string>();

        var separator = Array.IndexOf(args, "--");
        var commandArgs = separator < 0 ? args : args[..separator];
        if (separator >= 0)
        {
            appArgs.AddRange(args[(separator + 1)..]);
        }

        for (var i = 0; i < commandArgs.Length; i++)
        {
            switch (commandArgs[i])
            {
                case "--project":
                case "-p":
                    if (++i >= commandArgs.Length)
                    {
                        Console.Error.WriteLine("--project requires a value.");
                        return 1;
                    }

                    project = commandArgs[i];
                    break;
                case "--configuration":
                case "-c":
                    if (++i >= commandArgs.Length)
                    {
                        Console.Error.WriteLine("--configuration requires a value.");
                        return 1;
                    }

                    configuration = commandArgs[i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (project is null && !commandArgs[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        project = commandArgs[i];
                    }
                    else
                    {
                        Console.Error.WriteLine($"Unknown option '{commandArgs[i]}'.");
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

        Console.WriteLine($"csx: running {csproj}...");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        if (appArgs.Count > 0)
        {
            psi.ArgumentList.Add("--");
            foreach (var argument in appArgs)
            {
                psi.ArgumentList.Add(argument);
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: csx run [--project <path>] [--configuration <Debug|Release>] [-- <app args>]");
    }
}