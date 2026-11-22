package com.donkeh.csx.rider

import com.intellij.openapi.vfs.VirtualFile

/** Extension-only matching so every .cx file is handled regardless of project state. */
object CxFileUtil {
    fun isCxFile(file: VirtualFile?): Boolean =
        file?.extension?.equals("cx", ignoreCase = true) == true

    /**
     * Debug/build harness fixtures are .cx files, but they are copies of real sources. They
     * must not be the file that starts the language server: doing so hijacks the project-wide
     * session and means the user's actual file never gets diagnostics.
     */
    fun isIgnoredCxPath(file: VirtualFile?): Boolean {
        val path = file?.path ?: return false
        return path.split('\\', '/').any { part ->
            part.equals("bin", ignoreCase = true) ||
            part.equals("obj", ignoreCase = true) ||
            part.equals("Debug", ignoreCase = true) ||
            part.equals("Release", ignoreCase = true)
        }
    }
}
