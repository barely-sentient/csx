package com.donkeh.csx.rider

import com.intellij.lexer.Lexer
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.psi.tree.IElementType

object CxColors {
    val KEYWORD = TextAttributesKey.createTextAttributesKey("CX_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD)
    val PRIMITIVE_TYPE = TextAttributesKey.createTextAttributesKey("CX_PRIMITIVE_TYPE", DefaultLanguageHighlighterColors.KEYWORD)
    val LITERAL = TextAttributesKey.createTextAttributesKey("CX_LITERAL", DefaultLanguageHighlighterColors.CONSTANT)
    val STRING = TextAttributesKey.createTextAttributesKey("CX_STRING", DefaultLanguageHighlighterColors.STRING)
    val VERBATIM_STRING = TextAttributesKey.createTextAttributesKey("CX_VERBATIM_STRING", DefaultLanguageHighlighterColors.STRING)
    val INTERPOLATED_STRING = TextAttributesKey.createTextAttributesKey("CX_INTERPOLATED_STRING", DefaultLanguageHighlighterColors.STRING)
    val CHAR_LITERAL = TextAttributesKey.createTextAttributesKey("CX_CHAR_LITERAL", DefaultLanguageHighlighterColors.STRING)
    val NUMBER = TextAttributesKey.createTextAttributesKey("CX_NUMBER", DefaultLanguageHighlighterColors.NUMBER)
    val COMMENT = TextAttributesKey.createTextAttributesKey("CX_COMMENT", DefaultLanguageHighlighterColors.LINE_COMMENT)
    val BLOCK_COMMENT = TextAttributesKey.createTextAttributesKey("CX_BLOCK_COMMENT", DefaultLanguageHighlighterColors.BLOCK_COMMENT)
    val IDENTIFIER = TextAttributesKey.createTextAttributesKey("CX_IDENTIFIER", DefaultLanguageHighlighterColors.IDENTIFIER)
    val NAMESPACE = TextAttributesKey.createTextAttributesKey("CX_NAMESPACE", DefaultLanguageHighlighterColors.CLASS_NAME)
    val TYPE_NAME = TextAttributesKey.createTextAttributesKey("CX_TYPE_NAME", DefaultLanguageHighlighterColors.CLASS_NAME)
    val CLASS_NAME = TextAttributesKey.createTextAttributesKey("CX_CLASS_NAME", DefaultLanguageHighlighterColors.CLASS_NAME)
    val FUNCTION_NAME = TextAttributesKey.createTextAttributesKey("CX_FUNCTION_NAME", DefaultLanguageHighlighterColors.FUNCTION_CALL)
    val PARAMETER = TextAttributesKey.createTextAttributesKey("CX_PARAMETER", DefaultLanguageHighlighterColors.PARAMETER)
    val PROPERTY_NAME = TextAttributesKey.createTextAttributesKey("CX_PROPERTY_NAME", DefaultLanguageHighlighterColors.INSTANCE_FIELD)
    val ANNOTATION = TextAttributesKey.createTextAttributesKey("CX_ANNOTATION", DefaultLanguageHighlighterColors.KEYWORD)
    val JSX_TAG = TextAttributesKey.createTextAttributesKey("CX_JSX_TAG", DefaultLanguageHighlighterColors.MARKUP_TAG)
    val JSX_COMPONENT = TextAttributesKey.createTextAttributesKey("CX_JSX_COMPONENT", DefaultLanguageHighlighterColors.CLASS_NAME)
    val JSX_ATTRIBUTE = TextAttributesKey.createTextAttributesKey("CX_JSX_ATTRIBUTE", DefaultLanguageHighlighterColors.MARKUP_ATTRIBUTE)
    val JSX_TAG_MARKER = TextAttributesKey.createTextAttributesKey("CX_JSX_TAG_MARKER", DefaultLanguageHighlighterColors.MARKUP_TAG)
    val JSX_STRING = TextAttributesKey.createTextAttributesKey("CX_JSX_STRING", DefaultLanguageHighlighterColors.STRING)
    val OPERATOR = TextAttributesKey.createTextAttributesKey("CX_OPERATOR", DefaultLanguageHighlighterColors.OPERATION_SIGN)
    val BRACES = TextAttributesKey.createTextAttributesKey("CX_BRACES", DefaultLanguageHighlighterColors.BRACES)
    val BRACKETS = TextAttributesKey.createTextAttributesKey("CX_BRACKETS", DefaultLanguageHighlighterColors.BRACKETS)
    val PARENTHESES = TextAttributesKey.createTextAttributesKey("CX_PARENTHESES", DefaultLanguageHighlighterColors.PARENTHESES)
    val DOT = TextAttributesKey.createTextAttributesKey("CX_DOT", DefaultLanguageHighlighterColors.DOT)
    val COMMA = TextAttributesKey.createTextAttributesKey("CX_COMMA", DefaultLanguageHighlighterColors.COMMA)
    val SEMICOLON = TextAttributesKey.createTextAttributesKey("CX_SEMICOLON", DefaultLanguageHighlighterColors.SEMICOLON)
    val BAD_CHARACTER = TextAttributesKey.createTextAttributesKey("CX_BAD_CHARACTER", DefaultLanguageHighlighterColors.INVALID_STRING_ESCAPE)
}

class CxSyntaxHighlighter : SyntaxHighlighterBase() {
    override fun getHighlightingLexer(): Lexer = CxLexer()

    override fun getTokenHighlights(tokenType: IElementType?): Array<TextAttributesKey> =
        when (tokenType) {
            CxTokenTypes.KEYWORD -> arrayOf(CxColors.KEYWORD)
            CxTokenTypes.PRIMITIVE_TYPE -> arrayOf(CxColors.PRIMITIVE_TYPE)
            CxTokenTypes.LITERAL -> arrayOf(CxColors.LITERAL)
            CxTokenTypes.STRING -> arrayOf(CxColors.STRING)
            CxTokenTypes.VERBATIM_STRING -> arrayOf(CxColors.VERBATIM_STRING)
            CxTokenTypes.INTERPOLATED_STRING -> arrayOf(CxColors.INTERPOLATED_STRING)
            CxTokenTypes.CHAR_LITERAL -> arrayOf(CxColors.CHAR_LITERAL)
            CxTokenTypes.NUMBER -> arrayOf(CxColors.NUMBER)
            CxTokenTypes.LINE_COMMENT -> arrayOf(CxColors.COMMENT)
            CxTokenTypes.BLOCK_COMMENT -> arrayOf(CxColors.BLOCK_COMMENT)
            CxTokenTypes.IDENTIFIER -> arrayOf(CxColors.IDENTIFIER)
            CxTokenTypes.NAMESPACE -> arrayOf(CxColors.NAMESPACE)
            CxTokenTypes.TYPE_NAME -> arrayOf(CxColors.TYPE_NAME)
            CxTokenTypes.CLASS_NAME -> arrayOf(CxColors.CLASS_NAME)
            CxTokenTypes.FUNCTION_NAME -> arrayOf(CxColors.FUNCTION_NAME)
            CxTokenTypes.PARAMETER -> arrayOf(CxColors.PARAMETER)
            CxTokenTypes.PROPERTY_NAME -> arrayOf(CxColors.PROPERTY_NAME)
            CxTokenTypes.ANNOTATION -> arrayOf(CxColors.ANNOTATION)
            CxTokenTypes.JSX_TAG_START, CxTokenTypes.JSX_TAG_END -> arrayOf(CxColors.JSX_TAG_MARKER)
            CxTokenTypes.JSX_TAG_NAME -> arrayOf(CxColors.JSX_TAG)
            CxTokenTypes.JSX_COMPONENT_NAME -> arrayOf(CxColors.JSX_COMPONENT)
            CxTokenTypes.JSX_ATTRIBUTE -> arrayOf(CxColors.JSX_ATTRIBUTE)
            CxTokenTypes.JSX_STRING -> arrayOf(CxColors.JSX_STRING)
            CxTokenTypes.OPERATOR -> arrayOf(CxColors.OPERATOR)
            CxTokenTypes.LBRACE, CxTokenTypes.RBRACE -> arrayOf(CxColors.BRACES)
            CxTokenTypes.LBRACKET, CxTokenTypes.RBRACKET -> arrayOf(CxColors.BRACKETS)
            CxTokenTypes.LPAREN, CxTokenTypes.RPAREN -> arrayOf(CxColors.PARENTHESES)
            CxTokenTypes.DOT -> arrayOf(CxColors.DOT)
            CxTokenTypes.COMMA -> arrayOf(CxColors.COMMA)
            CxTokenTypes.SEMICOLON -> arrayOf(CxColors.SEMICOLON)
            CxTokenTypes.COLON -> arrayOf(CxColors.COMMA)
            CxTokenTypes.BAD_CHARACTER -> arrayOf(CxColors.BAD_CHARACTER)
            else -> emptyArray()
        }

    class Factory : SyntaxHighlighterFactory() {
        override fun getSyntaxHighlighter(project: Project?, virtualFile: VirtualFile?): CxSyntaxHighlighter = CxSyntaxHighlighter()
    }
}
