package com.donkeh.csx.rider

import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.IFileElementType

/** Token types produced by [CxLexer] and consumed by [CxSyntaxHighlighter]. */
object CxTokenTypes {
    @JvmField val FILE: IFileElementType = IFileElementType("CX_FILE", CxLanguage.INSTANCE)
    @JvmField val WHITESPACE: IElementType = TokenType.WHITE_SPACE
    @JvmField val LINE_COMMENT = CxTokenType("LINE_COMMENT")
    @JvmField val BLOCK_COMMENT = CxTokenType("BLOCK_COMMENT")
    @JvmField val STRING = CxTokenType("STRING")
    @JvmField val VERBATIM_STRING = CxTokenType("VERBATIM_STRING")
    @JvmField val INTERPOLATED_STRING = CxTokenType("INTERPOLATED_STRING")
    @JvmField val CHAR_LITERAL = CxTokenType("CHAR_LITERAL")
    @JvmField val NUMBER = CxTokenType("NUMBER")
    @JvmField val KEYWORD = CxTokenType("KEYWORD")
    @JvmField val PRIMITIVE_TYPE = CxTokenType("PRIMITIVE_TYPE")
    @JvmField val LITERAL = CxTokenType("LITERAL")
    @JvmField val IDENTIFIER = CxTokenType("IDENTIFIER")
    @JvmField val NAMESPACE = CxTokenType("NAMESPACE")
    @JvmField val TYPE_NAME = CxTokenType("TYPE_NAME")
    @JvmField val CLASS_NAME = CxTokenType("CLASS_NAME")
    @JvmField val FUNCTION_NAME = CxTokenType("FUNCTION_NAME")
    @JvmField val PARAMETER = CxTokenType("PARAMETER")
    @JvmField val PROPERTY_NAME = CxTokenType("PROPERTY_NAME")
    @JvmField val ANNOTATION = CxTokenType("ANNOTATION")
    @JvmField val JSX_TAG_START = CxTokenType("JSX_TAG_START")
    @JvmField val JSX_TAG_END = CxTokenType("JSX_TAG_END")
    @JvmField val JSX_TAG_NAME = CxTokenType("JSX_TAG_NAME")
    @JvmField val JSX_COMPONENT_NAME = CxTokenType("JSX_COMPONENT_NAME")
    @JvmField val JSX_ATTRIBUTE = CxTokenType("JSX_ATTRIBUTE")
    @JvmField val JSX_STRING = CxTokenType("JSX_STRING")
    @JvmField val OPERATOR = CxTokenType("OPERATOR")
    @JvmField val DOT = CxTokenType("DOT")
    @JvmField val COMMA = CxTokenType("COMMA")
    @JvmField val SEMICOLON = CxTokenType("SEMICOLON")
    @JvmField val COLON = CxTokenType("COLON")
    @JvmField val LPAREN = CxTokenType("LPAREN")
    @JvmField val RPAREN = CxTokenType("RPAREN")
    @JvmField val LBRACE = CxTokenType("LBRACE")
    @JvmField val RBRACE = CxTokenType("RBRACE")
    @JvmField val LBRACKET = CxTokenType("LBRACKET")
    @JvmField val RBRACKET = CxTokenType("RBRACKET")
    @JvmField val LT = CxTokenType("LT")
    @JvmField val GT = CxTokenType("GT")
    @JvmField val BAD_CHARACTER = CxTokenType("BAD_CHARACTER")

    @JvmField val KEYWORDS = setOf(
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "break", "continue",
        "return", "try", "catch", "finally", "throw", "yield", "goto", "when",
        "public", "private", "protected", "internal", "static", "readonly", "const", "async",
        "abstract", "virtual", "override", "sealed", "partial", "extern", "unsafe", "volatile", "required",
        "class", "struct", "interface", "enum", "record", "namespace", "var", "delegate", "event",
        "using", "new", "await", "typeof", "nameof", "default", "checked", "unchecked", "lock",
        "in", "out", "ref", "params", "is", "as", "where", "sizeof", "stackalloc",
        "get", "set", "init"
    )

    @JvmField val PRIMITIVE_TYPES = setOf(
        "int", "bool", "byte", "sbyte", "short", "ushort", "uint", "long", "ulong",
        "float", "double", "decimal", "char", "string", "object", "void", "dynamic", "nint", "nuint"
    )

    @JvmField val LITERALS = setOf("true", "false", "null", "this", "base")
}
