package com.donkeh.csx.rider

import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.platform.lsp.api.LspServerManager

class CxStartServerAction : AnAction("Start CSX Language Server") {
    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        LspServerManager.getInstance(project).startServersIfNeeded(CxLspServerSupportProvider::class.java)
    }
}

class CxRestartServerAction : AnAction("Restart CSX Language Server") {
    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        LspServerManager.getInstance(project).stopAndRestartIfNeeded(CxLspServerSupportProvider::class.java)
    }

    override fun update(e: AnActionEvent) {
        val project = e.project
        e.presentation.isEnabled = project != null &&
            LspServerManager.getInstance(project)
                .getServersForProvider(CxLspServerSupportProvider::class.java)
                .isNotEmpty()
    }
}
