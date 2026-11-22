using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace CSX.CliTool;

/// <summary>
/// Downloads and installs the IDE plugin versions that match the CLI version.
/// </summary>
internal static class EnableCommand
{
    private const string DefaultRepository = "donkeh/csx";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        var ide = args[0].ToLowerInvariant();
        if (ide is not ("vscode" or "rider"))
        {
            Console.Error.WriteLine($"Unknown IDE '{args[0]}'. Use 'vscode' or 'rider'.");
            PrintUsage();
            return 1;
        }

        string? versionOption = null;
        var repository = Environment.GetEnvironmentVariable("CSX_REPOSITORY") ?? DefaultRepository;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version":
                case "-v":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--version requires a value.");
                        return 1;
                    }

                    versionOption = args[i];
                    break;
                case "--repo":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--repo requires a value.");
                        return 1;
                    }

                    repository = args[i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                    PrintUsage();
                    return 1;
            }
        }

        try
        {
            var requested = versionOption ?? Environment.GetEnvironmentVariable("CSX_VERSION");
            var version = await ResolveVersionAsync(requested, repository);
            var asset = ide == "vscode"
                ? $"csx-vscode-{version}.vsix"
                : $"csx-rider-{version}.zip";

            Console.WriteLine($"CSX: enabling {ide} extension v{version}...");
            var file = await DownloadAsync(repository, version, asset);

            return ide == "vscode" ? InstallVsCode(file) : InstallRider(file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"csx enable {ide} failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: csx enable <vscode|rider> [--version <version|latest>] [--repo <owner/name>]");
        Console.WriteLine();
        Console.WriteLine("Downloads the matching IDE plugin from GitHub Releases and installs it.");
        Console.WriteLine("By default the version of the running csx tool is used.");
        Console.WriteLine();
        Console.WriteLine("Environment overrides:");
        Console.WriteLine("  CSX_VERSION             Version to install instead of the CLI version.");
        Console.WriteLine("  CSX_REPOSITORY          GitHub repository (default: donkeh/csx).");
        Console.WriteLine("  CSX_VSCODE_BIN          Path to the 'code' executable.");
        Console.WriteLine("  CSX_RIDER_BIN           Path to the Rider executable.");
        Console.WriteLine("  CSX_RIDER_PLUGINS_DIR   Rider plugins directory (skips auto-detection).");
    }

    private static async Task<string> ResolveVersionAsync(string? requested, string repository)
    {
        var version = string.IsNullOrWhiteSpace(requested)
            ? CurrentVersion()
            : requested.Trim();

        version = version.TrimStart('v', 'V');

        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases/latest");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var tag = json.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new InvalidOperationException("The latest GitHub release has no tag name.");
            }

            version = tag.TrimStart('v', 'V');
        }

        return version;
    }

    private static string CurrentVersion()
    {
        var informational = typeof(EnableCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var version = informational.Split('+')[0];
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return typeof(EnableCommand).Assembly.GetName().Version?.ToString() ?? "0.0.1";
    }

    private static async Task<string> DownloadAsync(string repository, string version, string asset)
    {
        var url = $"https://github.com/{repository}/releases/download/v{version}/{asset}";
        Console.WriteLine($"Downloading {url}");

        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not download {url} ({(int)response.StatusCode} {response.ReasonPhrase}). " +
                $"Check that release v{version} contains {asset}, or pass --version <version>.");
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "csx-enable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, asset);
        await using var output = File.Create(path);
        await response.Content.CopyToAsync(output);
        return path;
    }

    private static int InstallVsCode(string vsix)
    {
        var executable = FindVsCode();
        if (executable is null)
        {
            throw new InvalidOperationException(
                "The VS Code 'code' command was not found. Add it to PATH or set CSX_VSCODE_BIN.");
        }

        Console.WriteLine($"Installing with {executable} ...");
        RunProcess(executable, "--install-extension", vsix, "--force");
        Console.WriteLine("VS Code extension installed. Reload VS Code if it is open.");
        return 0;
    }

    private static int InstallRider(string zip)
    {
        var executable = FindRider();
        if (executable is not null)
        {
            Console.WriteLine($"Installing with {executable} ...");
            try
            {
                RunProcess(executable, "installPlugin", zip);
                Console.WriteLine("Rider plugin installed. Restart Rider if it is open.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.Message} Falling back to copying the plugin to disk.");
            }
        }

        var pluginsDirectory = FindRiderPluginsDirectory();
        Console.WriteLine($"Installing plugin into {pluginsDirectory} ...");
        ZipFile.ExtractToDirectory(zip, pluginsDirectory, overwriteFiles: true);
        Console.WriteLine("Rider plugin installed. Restart Rider if it is open.");
        return 0;
    }

    private static string? FindVsCode()
    {
        var configured = Environment.GetEnvironmentVariable("CSX_VSCODE_BIN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var name in new[] { "code", "code-insiders", "codium" })
        {
            var found = FindOnPath(name);
            if (found is not null)
            {
                return found;
            }
        }

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            AddEnvCandidate(candidates, "LOCALAPPDATA", @"Programs\Microsoft VS Code\bin\code.cmd");
            AddEnvCandidate(candidates, "ProgramFiles", @"Microsoft VS Code\bin\code.cmd");
            AddEnvCandidate(candidates, "ProgramFiles(x86)", @"Microsoft VS Code\bin\code.cmd");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code");
        }
        else
        {
            candidates.Add("/usr/share/code/bin/code");
            candidates.Add("/usr/bin/code");
            candidates.Add("/snap/bin/code");
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRider()
    {
        var configured = Environment.GetEnvironmentVariable("CSX_RIDER_BIN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var name in new[] { "rider64", "rider", "rider64.exe", "rider.cmd" })
        {
            var found = FindOnPath(name);
            if (found is not null)
            {
                return found;
            }
        }

        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            AddRiderInstallCandidates(
                candidates,
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty, "Programs"),
                "JetBrains Rider *");
            AddRiderInstallCandidates(candidates, Environment.GetEnvironmentVariable("ProgramFiles"), @"JetBrains\JetBrains Rider *");
            AddRiderToolboxCandidates(candidates, Path.Combine(
                Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty,
                "JetBrains", "Toolbox", "apps", "Rider"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Rider.app/Contents/MacOS/rider");
            AddRiderToolboxCandidates(candidates, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "JetBrains", "Toolbox", "apps", "Rider"));
        }
        else
        {
            candidates.Add("/opt/rider/bin/rider");
            AddRiderToolboxCandidates(candidates, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "JetBrains", "Toolbox", "apps", "Rider"));
        }

        return candidates
            .Where(File.Exists)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void AddRiderInstallCandidates(List<string> candidates, string? root, string directoryPattern)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var executableName = OperatingSystem.IsWindows() ? "rider64.exe" : "rider";
        foreach (var directory in Directory.GetDirectories(root, directoryPattern))
        {
            candidates.Add(Path.Combine(directory, "bin", executableName));
        }
    }

    private static void AddRiderToolboxCandidates(List<string> candidates, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var executableName = OperatingSystem.IsWindows() ? "rider64.exe" : "rider";
        foreach (var channel in Directory.GetDirectories(root))
        {
            candidates.Add(Path.Combine(channel, "bin", executableName));
            foreach (var nested in Directory.GetDirectories(channel))
            {
                candidates.Add(Path.Combine(nested, "bin", executableName));
            }
        }
    }

    private static string FindRiderPluginsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CSX_RIDER_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        var root = RiderConfigRoot();
        var installations = Directory.Exists(root)
            ? Directory.GetDirectories(root, "Rider*")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        if (installations.Count == 0)
        {
            throw new InvalidOperationException(
                $"No Rider configuration directory was found under {root}. " +
                "Start Rider once, or set CSX_RIDER_PLUGINS_DIR to the plugins directory.");
        }

        var plugins = Path.Combine(installations[0], "plugins");
        Directory.CreateDirectory(plugins);
        return plugins;
    }

    private static string RiderConfigRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "JetBrains");
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configHome, "JetBrains");
    }

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void AddEnvCandidate(List<string> candidates, string variable, string relativePath)
    {
        var root = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(root))
        {
            candidates.Add(Path.Combine(root, relativePath));
        }
    }

    private static void RunProcess(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = OperatingSystem.IsWindows()
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csx-cli");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
