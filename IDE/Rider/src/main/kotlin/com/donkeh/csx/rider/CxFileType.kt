package com.donkeh.csx.rider

import com.intellij.openapi.fileTypes.LanguageFileType
import javax.swing.Icon

/** Registers .cx files as CSX source files so Rider opens them with our language and LSP support. */
class CxFileType : LanguageFileType(CxLanguage.INSTANCE) {
    override fun getName(): String = "CSX"

    override fun getDescription(): String = "CSharpExtended source file"

    override fun getDefaultExtension(): String = "cx"

    override fun getIcon(): Icon? = CxIcons.FILE

    companion object {
        @JvmField
        val INSTANCE = CxFileType()
    }
}
