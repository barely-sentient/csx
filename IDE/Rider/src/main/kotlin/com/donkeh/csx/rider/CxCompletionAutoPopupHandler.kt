package com.donkeh.csx.rider

import com.intellij.codeInsight.AutoPopupController
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiFile

/**
 * VS Code-style completion popup for .cx files.
 *
 * The platform LSP handler respects the server's trigger characters, but only after the
 * LSP client has loaded capabilities. Typing the first trigger immediately after opening a
 * file can therefore feel unresponsive. This handler mirrors VS Code's
 * `editor.suggestOnTriggerCharacters` deterministically while the LSP contributor still
 * supplies every item.
 */
class CxCompletionAutoPopupHandler : TypedHandlerDelegate() {
    override fun checkAutoPopup(
        charTyped: Char,
        project: Project,
        editor: Editor,
        file: PsiFile
    ): Result {
        if (charTyped !in TRIGGER_CHARACTERS) return Result.CONTINUE

        val virtualFile = file.originalFile.virtualFile ?: return Result.CONTINUE
        if (!CxFileUtil.isCxFile(virtualFile)) return Result.CONTINUE

        AutoPopupController.getInstance(project).scheduleAutoPopup(editor)
        return Result.STOP
    }

    private companion object {
        // Advertised by the CSX/Roslyn LSP server; keep in sync with the initialize result.
        val TRIGGER_CHARACTERS = setOf('"', '(', ':', '[', '\\', '{', '#', '.', '>', ' ', '~', '<')
    }
}
