package com.donkeh.csx.rider

import com.intellij.openapi.vfs.VirtualFile

/** Extension-only matching so every .cx file is handled regardless of project state. */
object CxFileUtil {
    fun isCxFile(file: VirtualFile?): Boolean =
        file?.extension?.equals("cx", ignoreCase = true) == true
}
