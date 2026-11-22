package com.donkeh.csx.rider

import com.intellij.psi.tree.IElementType

class CxTokenType(debugName: String) : IElementType(debugName, CxLanguage.INSTANCE) {
    override fun toString(): String = "CxTokenType.${super.toString()}"
}
