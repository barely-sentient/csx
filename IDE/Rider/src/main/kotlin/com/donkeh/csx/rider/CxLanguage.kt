package com.donkeh.csx.rider

import com.intellij.lang.Language

/** The CSX (.cx) language, a C# dialect with JSX-like syntax. */
class CxLanguage : Language("CSX") {
    companion object {
        @JvmField
        val INSTANCE = CxLanguage()
    }
}
