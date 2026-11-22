package com.donkeh.csx.rider

import com.intellij.extapi.psi.ASTWrapperPsiElement
import com.intellij.lang.ASTNode
import com.intellij.lang.PsiBuilder
import com.intellij.lang.PsiParser
import com.intellij.lexer.Lexer
import com.intellij.openapi.project.Project
import com.intellij.psi.FileViewProvider
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.IFileElementType
import com.intellij.psi.tree.TokenSet

/** Minimal parser definition; the language server owns the real semantic analysis. */
class CxParserDefinition : com.intellij.lang.ParserDefinition {
    override fun createLexer(project: Project?): Lexer = CxLexer()

    override fun createParser(project: Project?): PsiParser = object : PsiParser {
        override fun parse(root: IElementType, builder: PsiBuilder): ASTNode {
            val marker = builder.mark()
            while (!builder.eof()) builder.advanceLexer()
            marker.done(root)
            return builder.treeBuilt
        }
    }

    override fun getFileNodeType(): IFileElementType = CxTokenTypes.FILE

    override fun getWhitespaceTokens(): TokenSet = TokenSet.create(TokenType.WHITE_SPACE)

    override fun getCommentTokens(): TokenSet =
        TokenSet.create(CxTokenTypes.LINE_COMMENT, CxTokenTypes.BLOCK_COMMENT)

    override fun getStringLiteralElements(): TokenSet = TokenSet.create(
        CxTokenTypes.STRING,
        CxTokenTypes.VERBATIM_STRING,
        CxTokenTypes.INTERPOLATED_STRING,
        CxTokenTypes.CHAR_LITERAL
    )

    override fun createElement(node: ASTNode): PsiElement = ASTWrapperPsiElement(node)

    override fun createFile(viewProvider: FileViewProvider): PsiFile = CxFile(viewProvider)
}
