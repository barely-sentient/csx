package com.donkeh.csx.rider

import com.intellij.extapi.psi.PsiFileBase
import com.intellij.openapi.fileTypes.FileType
import com.intellij.psi.FileViewProvider

/** Gives .cx files a real PSI file so LSP hover/documentation providers can run. */
class CxFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, CxLanguage.INSTANCE) {
    override fun getFileType(): FileType = CxFileType.INSTANCE
}
