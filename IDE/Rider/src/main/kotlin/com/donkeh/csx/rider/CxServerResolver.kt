package com.donkeh.csx.rider

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.guessProjectDir
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.util.execution.ParametersListUtil
import java.io.File

data class CxLaunchCommand(val command: String, val arguments: List<String>)

/** Resolves how to launch the CSX language server and which root to give it. */
class CxServerResolver(
    private val project: Project,
    private val openedFile: VirtualFile? = null
) {
    private val log = Logger.getInstance(CxServerResolver::class.java)

    fun resolve(): CxLaunchCommand? {
        val settings = ApplicationManager.getApplication().getService(CxSettings::class.java)
        val launch = when (settings.serverMode) {
            CxSettings.MODE_LOCAL -> resolveLocal(settings.localProjectPath)
            CxSettings.MODE_INSTALLED -> resolveInstalled(settings.serverPath)
            else -> {
                log.warn("Unknown CSX server mode: ${settings.serverMode}")
                null
            }
        } ?: return null

        // Mirror VS Code's `csx.server.args` setting. The resolver puts `lang-server` at the
        // end so it can be replaced by the user's argument list without touching dll/exe prefixes.
        val args = launch.arguments.toMutableList()
        if (args.lastOrNull() == "lang-server") args.removeAt(args.lastIndex)
        val configured = ParametersListUtil.parse(settings.serverArgs)
        args.addAll(if (configured.isEmpty()) listOf("lang-server") else configured)
        return launch.copy(arguments = args)
    }

    /**
     * The root must be the repository that owns the .cx file when possible. Rider may open only
     * the samples directory, or infer a solution from a parent, so start from the file itself.
     */
    fun serverRoot(): String {
        val starts = listOfNotNull(
            openedFile?.parent?.path,
            project.guessProjectDir()?.path,
            project.basePath,
            System.getProperty("user.dir")
        )

        for (start in starts) {
            val root = findRoot(File(start))
            if (root != null) return root.absolutePath
        }

        return File(starts.first()).absolutePath
    }

    private fun resolveLocal(setting: String): CxLaunchCommand? {
        var target = setting.trim()
        if (target.isEmpty()) {
            val detected = System.getenv("CSX_CLI_PATH")?.takeIf { it.isNotBlank() }
                ?.let { File(it.trim()).takeIf { f -> f.exists() }?.absolutePath }
                ?: autoDetectLocalCli()
            if (detected == null) {
                log.warn("No local CSX.CliTool build found")
                return null
            }
            target = detected
        }

        val file = File(target)
        if (file.isDirectory) {
            val projectFile = sequenceOf(
                File(file, "CSX.CliTool.csproj"),
                File(file, "CliTool/Source/CSX.CliTool.csproj")
            ).firstOrNull { it.exists() }
            if (projectFile != null) return resolveLocal(projectFile.absolutePath)
            findBuiltCli(file)?.let { return resolveLocal(it) }
        }

        if (file.name.endsWith(".csproj", ignoreCase = true)) {
            val built = findBuiltCli(file.parentFile)
            if (built == null) {
                log.warn("No built output found for ${file.absolutePath}; run 'dotnet build' first")
                return null
            }
            return resolveLocal(built)
        }

        val normalized = if (file.exists()) file.absolutePath else target
        return when {
            normalized.endsWith(".exe", ignoreCase = true) -> CxLaunchCommand(normalized, listOf("lang-server"))
            normalized.endsWith(".dll", ignoreCase = true) ->
                CxLaunchCommand("dotnet", listOf(normalized, "lang-server"))
            else -> CxLaunchCommand(normalized, listOf("lang-server"))
        }
    }

    private fun resolveInstalled(setting: String): CxLaunchCommand? {
        val command = setting.trim().ifEmpty { "csx" }
        if (!commandExists(command)) {
            log.warn("CSX command '$command' not found on PATH")
            return null
        }
        return CxLaunchCommand(command, listOf("lang-server"))
    }

    private fun autoDetectLocalCli(): String? {
        val bases = LinkedHashSet<String>()

        // Start from the opened file and the server root, then fall back to the plugin location.
        addAncestors(openedFile?.parent?.path, bases)
        addAncestors(serverRoot(), bases)
        addAncestors(project.guessProjectDir()?.path, bases)
        addAncestors(project.basePath, bases)
        addAncestors(pluginLocation(), bases)

        for (base in bases) {
            findBuiltCli(File(base))?.let { return it }
        }
        return null
    }

    private fun pluginLocation(): String? = try {
        File(CxServerResolver::class.java.protectionDomain.codeSource.location.toURI()).parentFile?.absolutePath
    } catch (e: Exception) {
        log.debug("Could not determine plugin location: ${e.message}")
        null
    }

    private fun addAncestors(start: String?, destination: MutableSet<String>) {
        if (start.isNullOrBlank()) return
        var current: File? = File(start).absoluteFile
        repeat(10) {
            if (current == null) return
            destination.add(current.absolutePath)
            current = current.parentFile
        }
    }

    private fun findRoot(start: File): File? {
        var current: File? = start.absoluteFile
        var fallback: File? = null

        repeat(12) {
            if (current == null) return fallback ?: start.absoluteFile

            // A repository root is always preferred over a local csx.json (samples has one).
            if (File(current, "CSharpExtended.sln").exists() ||
                File(current, "CliTool/Source/CSX.CliTool.csproj").exists() ||
                File(current, "LanguageServer/Source/CSX.LanguageServer.csproj").exists() ||
                current.listFiles()?.any { it.extension.equals("sln", true) } == true
            ) {
                return current
            }

            if (fallback == null &&
                (File(current, "csx.json").exists() ||
                    current.listFiles()?.any { it.extension.equals("csproj", true) } == true)
            ) {
                fallback = current
            }

            current = current.parentFile
        }

        return fallback ?: start.absoluteFile
    }

    private fun findBuiltCli(base: File): String? {
        val direct = File(base, "CSX.CliTool.exe").takeIf { it.isFile }
            ?: File(base, "CSX.CliTool.dll").takeIf { it.isFile }
        if (direct != null) return direct.absolutePath

        val searchRoots = listOf(File(base, "bin"), File(base, "CliTool/Source/bin"))
        for (root in searchRoots) {
            if (!root.exists()) continue

            val exact = sequenceOf("Debug", "Release").flatMap { configuration ->
                sequenceOf("net10.0", "net9.0", "net8.0").flatMap { framework ->
                    sequenceOf(
                        File(root, "$configuration/$framework/CSX.CliTool.exe"),
                        File(root, "$configuration/$framework/CSX.CliTool.dll")
                    )
                }
            }.firstOrNull { it.isFile }

            if (exact != null) return exact.absolutePath

            val found = root.walkTopDown()
                .onFail { file, e -> log.debug("Could not inspect ${file.path}: ${e.message}") }
                .firstOrNull { it.isFile && it.name.equals("CSX.CliTool.exe", true) }
                ?: root.walkTopDown()
                    .onFail { file, e -> log.debug("Could not inspect ${file.path}: ${e.message}") }
                    .firstOrNull { it.isFile && it.name.equals("CSX.CliTool.dll", true) }

            if (found != null) return found.absolutePath
        }

        return null
    }

    private fun commandExists(command: String): Boolean = try {
        val isWindows = System.getProperty("os.name").startsWith("Windows", ignoreCase = true)
        val probe = ProcessBuilder(if (isWindows) "where.exe" else "which", command)
            .redirectErrorStream(true)
            .start()
        probe.waitFor() == 0
    } catch (e: Exception) {
        log.debug("Failed to probe command '$command': ${e.message}")
        false
    }
}
