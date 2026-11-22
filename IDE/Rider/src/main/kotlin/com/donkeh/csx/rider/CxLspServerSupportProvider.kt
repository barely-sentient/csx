package com.donkeh.csx.rider

import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.lsWidget.LspServerWidgetItem

/**
 * Uses the JetBrains LSP server API rather than the newer generic client API.
 * The server API is the one Rider's completion, hover, navigation and diagnostics
 * integrations are wired to.
 */
class CxLspServerSupportProvider : LspServerSupportProvider {
    private val log = Logger.getInstance(CxLspServerSupportProvider::class.java)

    override fun fileOpened(project: Project, file: VirtualFile, starter: LspServerSupportProvider.LspServerStarter) {
        if (!CxFileUtil.isCxFile(file)) return

        log.info("CSX LSP: fileOpened ${file.path}; starting language server")
        starter.ensureServerStarted(CxLspServerDescriptor(project, file))
    }

    override fun createLspServerWidgetItem(lspServer: LspServer, currentFile: VirtualFile?): LspServerWidgetItem =
        LspServerWidgetItem(lspServer, currentFile, CxIcons.FILE, CxConfigurable::class.java)
}
