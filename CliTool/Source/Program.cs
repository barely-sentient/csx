using CSX.CliTool;
using CSX.LanguageServer;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "lang-server":
        {
            var options = new LanguageServerOptions();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--root" && i + 1 < args.Length) options.ProjectRoot = args[++i];
                else if (args[i] == "--output" && i + 1 < args.Length) options.OutputDirectory = args[++i];
                else if (args[i] == "--csharp-server" && i + 1 < args.Length) options.CSharpServerPath = args[++i];
            }

            await using var server = new LanguageServer(options);
            await server.StartAsync();
            await server.RunAsync();
            return 0;
        }
        case "enable":
            return await EnableCommand.RunAsync(args.Skip(1).ToArray());
        case "build":
            return await BuildCommand.RunAsync(args.Skip(1).ToArray());
        case "run":
            return await RunCommand.RunAsync(args.Skip(1).ToArray());
    }
}

Console.WriteLine("Usage: csx <command> [options]");
Console.WriteLine();
Console.WriteLine("Commands:");
Console.WriteLine("  lang-server [--root <path>] [--output <dir>] [--csharp-server <path>]");
Console.WriteLine("      Start the CSX language server.");
Console.WriteLine("  build [--project <path>] [--configuration <Debug|Release>] [--framework <tfm>]");
Console.WriteLine("      Transform .cx files and build the project.");
Console.WriteLine("  run [--project <path>] [--configuration <Debug|Release>] [-- <app args>]");
Console.WriteLine("      Transform .cx files and run the project.");
Console.WriteLine("  enable <vscode|rider> [--version <version|latest>] [--repo <owner/name>]");
Console.WriteLine("      Download and install the matching IDE plugin from GitHub Releases.");
Console.WriteLine();
Console.WriteLine("Run 'csx build --help' or 'csx run --help' for more options.");

return 0;
