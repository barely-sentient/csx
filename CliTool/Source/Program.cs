using CSX.LanguageServer;

if (args.Length > 0 && args[0] == "lang-server")
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
}
else
{
    Console.WriteLine("Usage: csx lang-server [--root <path>] [--output <dir>] [--csharp-server <path>]");
}