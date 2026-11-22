package com.donkeh.csx.rider

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import com.intellij.platform.lsp.api.customization.LspCustomization

/** Describes the CSX language server process for the legacy LSP server API. */
class CxLspServerDescriptor(
    project: Project,
    private val openedFile: VirtualFile?
) : ProjectWideLspServerDescriptor(project, "CSX Language Server") {

    private val log = Logger.getInstance(CxLspServerDescriptor::class.java)

    /** A fresh customization object enables every LSP editor feature the server advertises. */
    override val lspCustomization: LspCustomization = LspCustomization()

    override fun isSupportedFile(file: VirtualFile): Boolean = CxFileUtil.isCxFile(file)

    /** The CSX proxy registers its capabilities against this language id. */
    override fun getLanguageId(file: VirtualFile): String = "cx"

    @Throws(ExecutionException::class)
    override fun createCommandLine(): GeneralCommandLine {
        val resolver = CxServerResolver(project, openedFile)
        val launch = resolver.resolve()
            ?: throw ExecutionException(
                "Could not resolve the CSX language server. " +
                    "Configure it in Settings | Languages & Frameworks | CSX."
            )

        val root = resolver.serverRoot()
        log.info("Starting CSX language server: ${launch.command} ${launch.arguments.joinToString(" ")} --root $root")

        return GeneralCommandLine(launch.command)
            .withParameters(launch.arguments)
            .withParameters("--root", root)
            .withWorkDirectory(root)
            .withCharset(Charsets.UTF_8)
    }
}
