package com.donkeh.csx.rider

import com.intellij.lexer.LexerBase
import com.intellij.psi.tree.IElementType

/**
 * Lexer for CSX: C# tokens plus a small JSX state machine. The JSX rules mirror the
 * scopes in IDE/VsCode/syntaxes/cx.tmLanguage.json (tags, components, attributes,
 * strings and embedded code expressions).
 */
class CxLexer : LexerBase() {
    private enum class Mode { CODE, TAG, EXPR }

    private var buffer: CharSequence = ""
    private var bufferEnd = 0
    private var tokenStart = 0
    private var tokenEnd = 0
    private var currentToken: IElementType? = null

    private var mode = Mode.CODE
    private var jsxDepth = 0
    private var exprDepth = 0
    private var tagIsClosing = false
    private var afterTagOpen = false
    private var exprReturnToTag = false

    override fun start(buffer: CharSequence, startOffset: Int, endOffset: Int, initialState: Int) {
        this.buffer = buffer
        this.bufferEnd = endOffset
        this.tokenStart = startOffset
        this.tokenEnd = startOffset
        this.currentToken = null

        val state = if (initialState <= 0) 1 else initialState
        mode = Mode.values().getOrElse((state and 3) - 1) { Mode.CODE }
        jsxDepth = (state shr 2) and 0xFF
        tagIsClosing = (state and (1 shl 10)) != 0
        afterTagOpen = (state and (1 shl 11)) != 0
        exprReturnToTag = (state and (1 shl 12)) != 0
        exprDepth = (state shr 13) and 0xFF

        advance()
    }

    override fun advance() {
        if (tokenEnd >= bufferEnd) {
            currentToken = null
            tokenStart = bufferEnd
            tokenEnd = bufferEnd
            return
        }

        tokenStart = tokenEnd
        when (mode) {
            Mode.TAG -> lexTag()
            Mode.EXPR -> lexCode(insideExpression = true)
            Mode.CODE -> lexCode(insideExpression = false)
        }
    }

    private fun lexCode(insideExpression: Boolean) {
        val c = buffer[tokenStart]

        if (c.isWhitespace()) return finish(CxTokenTypes.WHITESPACE, skipWhitespace())

        if (c == '/' && tokenStart + 1 < bufferEnd) {
            when (buffer[tokenStart + 1]) {
                '/' -> {
                    var i = tokenStart + 2
                    while (i < bufferEnd && buffer[i] != '\n' && buffer[i] != '\r') i++
                    return finish(CxTokenTypes.LINE_COMMENT, i)
                }
                '*' -> {
                    var i = tokenStart + 2
                    while (i + 1 < bufferEnd && !(buffer[i] == '*' && buffer[i + 1] == '/')) i++
                    return finish(CxTokenTypes.BLOCK_COMMENT, (i + 2).coerceAtMost(bufferEnd))
                }
            }
        }

        if (c == '@' && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1].isJavaIdentifierStart()) {
            var i = tokenStart + 2
            while (i < bufferEnd && buffer[i].isJavaIdentifierPart()) i++
            return finish(CxTokenTypes.ANNOTATION, i)
        }

        if (c == '"' || c == '\'') return finish(CxTokenTypes.STRING, consumeString(tokenStart, c))
        if ((c == '@' || c == '$') && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1] == '"') {
            val type = if (c == '@') CxTokenTypes.VERBATIM_STRING else CxTokenTypes.INTERPOLATED_STRING
            return finish(type, consumeString(tokenStart + 1, '"'))
        }
        if (c == '$' && tokenStart + 2 < bufferEnd &&
            buffer[tokenStart + 1] == '@' && buffer[tokenStart + 2] == '"'
        ) {
            return finish(CxTokenTypes.INTERPOLATED_STRING, consumeString(tokenStart + 2, '"'))
        }

        if (c.isDigit()) {
            var i = tokenStart + 1
            while (i < bufferEnd && isNumberPart(i)) i++
            return finish(CxTokenTypes.NUMBER, i)
        }

        if (c.isJavaIdentifierStart()) {
            var i = tokenStart + 1
            while (i < bufferEnd && (buffer[i].isJavaIdentifierPart() || buffer[i] == '@')) i++
            val word = buffer.subSequence(tokenStart, i).toString()
            return finish(classifyCodeIdentifier(word, i), i)
        }

        // JSX opening/closing markers. Generics and comparison operators are not JSX.
        if (c == '<' && isJsxStart(tokenStart)) {
            val closing = tokenStart + 1 < bufferEnd && buffer[tokenStart + 1] == '/'
            val end = if (closing) tokenStart + 2 else tokenStart + 1
            tagIsClosing = closing
            afterTagOpen = true
            mode = Mode.TAG
            return finish(CxTokenTypes.JSX_TAG_START, end)
        }

        if (c == '{') {
            if (insideExpression) exprDepth++
            else {
                exprDepth = 1
                exprReturnToTag = false
                mode = Mode.EXPR
            }
            return finish(CxTokenTypes.LBRACE, tokenStart + 1)
        }

        if (c == '}' && insideExpression) {
            exprDepth--
            if (exprDepth <= 0) {
                exprDepth = 0
                mode = if (exprReturnToTag) Mode.TAG else Mode.CODE
                exprReturnToTag = false
            }
            return finish(CxTokenTypes.RBRACE, tokenStart + 1)
        }

        if (tokenStart + 2 < bufferEnd && buffer.subSequence(tokenStart, tokenStart + 3).toString() == "...") {
            return finish(CxTokenTypes.OPERATOR, tokenStart + 3)
        }
        if (tokenStart + 1 < bufferEnd) {
            val two = buffer.subSequence(tokenStart, tokenStart + 2).toString()
            if (two in TWO_CHAR_OPERATORS) return finish(CxTokenTypes.OPERATOR, tokenStart + 2)
        }

        val single = when (c) {
            '.' -> CxTokenTypes.DOT
            ',' -> CxTokenTypes.COMMA
            ';' -> CxTokenTypes.SEMICOLON
            ':' -> CxTokenTypes.COLON
            '(' -> CxTokenTypes.LPAREN
            ')' -> CxTokenTypes.RPAREN
            '[' -> CxTokenTypes.LBRACKET
            ']' -> CxTokenTypes.RBRACKET
            '{' -> CxTokenTypes.LBRACE
            '}' -> CxTokenTypes.RBRACE
            '<' -> CxTokenTypes.LT
            '>' -> CxTokenTypes.GT
            '+', '-', '*', '/', '%', '&', '|', '^', '~', '!', '=' -> CxTokenTypes.OPERATOR
            else -> CxTokenTypes.BAD_CHARACTER
        }
        return finish(single, tokenStart + 1)
    }

    private fun lexTag() {
        val c = buffer[tokenStart]

        if (c.isWhitespace()) return finish(CxTokenTypes.WHITESPACE, skipWhitespace())

        if (c == '/' && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1] == '>') {
            tagIsClosing = false
            afterTagOpen = false
            mode = Mode.CODE
            return finish(CxTokenTypes.JSX_TAG_END, tokenStart + 2)
        }

        if (c == '>') {
            if (tagIsClosing) jsxDepth = (jsxDepth - 1).coerceAtLeast(0)
            else jsxDepth = (jsxDepth + 1).coerceAtMost(0xFF)
            tagIsClosing = false
            afterTagOpen = false
            mode = Mode.CODE
            return finish(CxTokenTypes.JSX_TAG_END, tokenStart + 1)
        }

        if (c == '"' || c == '\'') return finish(CxTokenTypes.JSX_STRING, consumeString(tokenStart, c))

        if (c == '{') {
            exprDepth = 1
            exprReturnToTag = true
            mode = Mode.EXPR
            return finish(CxTokenTypes.LBRACE, tokenStart + 1)
        }

        if (c.isJavaIdentifierStart()) {
            var i = tokenStart + 1
            while (i < bufferEnd && (buffer[i].isJavaIdentifierPart() || buffer[i] == ':' || buffer[i] == '-' || buffer[i] == '.')) i++
            val word = buffer.subSequence(tokenStart, i).toString()
            val type = when {
                afterTagOpen -> {
                    afterTagOpen = false
                    if (word[0].isUpperCase() || word[0] == '_') CxTokenTypes.JSX_COMPONENT_NAME else CxTokenTypes.JSX_TAG_NAME
                }
                word[0].isUpperCase() -> CxTokenTypes.TYPE_NAME
                isFollowedByEquals(i) -> CxTokenTypes.JSX_ATTRIBUTE
                else -> CxTokenTypes.IDENTIFIER
            }
            return finish(type, i)
        }

        val type = when (c) {
            '=' -> CxTokenTypes.OPERATOR
            '<' -> CxTokenTypes.LT
            '?' -> CxTokenTypes.OPERATOR
            '@' -> CxTokenTypes.ANNOTATION
            else -> CxTokenTypes.BAD_CHARACTER
        }
        return finish(type, tokenStart + 1)
    }

    private fun classifyCodeIdentifier(word: String, end: Int): IElementType {
        if (word in CxTokenTypes.KEYWORDS) return CxTokenTypes.KEYWORD
        if (word in CxTokenTypes.PRIMITIVE_TYPES) return CxTokenTypes.PRIMITIVE_TYPE
        if (word in CxTokenTypes.LITERALS) return CxTokenTypes.LITERAL

        when (previousWord(tokenStart)) {
            "namespace" -> return CxTokenTypes.NAMESPACE
            "class", "struct", "interface", "enum", "record" -> return CxTokenTypes.CLASS_NAME
        }

        var p = tokenStart - 1
        while (p >= 0 && buffer[p].isWhitespace()) p--
        var q = end
        while (q < bufferEnd && buffer[q].isWhitespace()) q++

        return when {
            p >= 0 && buffer[p] == '.' -> CxTokenTypes.PROPERTY_NAME
            q < bufferEnd && buffer[q] == '(' -> CxTokenTypes.FUNCTION_NAME
            (p >= 0 && (buffer[p] == '(' || buffer[p] == ',')) &&
                (q >= bufferEnd || buffer[q] == ')' || buffer[q] == ',' || buffer[q] == '=') -> CxTokenTypes.PARAMETER
            word[0].isUpperCase() -> CxTokenTypes.TYPE_NAME
            else -> CxTokenTypes.IDENTIFIER
        }
    }

    private fun previousWord(start: Int): String? {
        var end = start
        while (end > 0 && buffer[end - 1].isWhitespace()) end--
        var begin = end
        while (begin > 0 && buffer[begin - 1].isJavaIdentifierPart()) begin--
        if (begin == end) return null
        return buffer.subSequence(begin, end).toString()
    }

    private fun isFollowedByEquals(end: Int): Boolean {
        var i = end
        while (i < bufferEnd && buffer[i].isWhitespace()) i++
        return i < bufferEnd && buffer[i] == '='
    }

    private fun isJsxStart(pos: Int): Boolean {
        var i = pos - 1
        while (i >= 0 && buffer[i].isWhitespace()) i--
        if (i >= 0) {
            val prev = buffer[i]
            if (prev.isJavaIdentifierPart() || prev == '>' || prev == ']' || prev == ')') return false
        }

        var next = pos + 1
        if (next < bufferEnd && buffer[next] == '/') next++
        return next < bufferEnd && (buffer[next].isJavaIdentifierStart() || buffer[next] == '>')
    }

    private fun skipWhitespace(): Int {
        var i = tokenStart + 1
        while (i < bufferEnd && buffer[i].isWhitespace()) i++
        return i
    }

    private fun isNumberPart(i: Int): Boolean {
        val c = buffer[i]
        if (c.isDigit() || c == '_' || c == '.' || c == 'x' || c == 'X' || c == 'e' || c == 'E') return true
        if (c == '+' || c == '-') {
            val prev = buffer[i - 1]
            return prev == 'e' || prev == 'E'
        }
        return c in 'a'..'f' || c in 'A'..'F' || c in 'l'..'l' || c in 'L'..'L' ||
            c in 'u'..'u' || c in 'U'..'U' || c in 'd'..'d' || c in 'D'..'D' || c in 'm'..'m' || c in 'M'..'M'
    }

    private fun consumeString(start: Int, quote: Char): Int {
        val verbatim = start + 1 < bufferEnd && quote == '"' &&
            (buffer[start] == '@' || (start > 0 && buffer[start - 1] == '@') ||
                (start > 1 && buffer[start - 2] == '@'))
        var i = if (buffer[start] == quote) start + 1 else start + 2
        while (i < bufferEnd) {
            val ch = buffer[i]
            if (verbatim && ch == '"' && i + 1 < bufferEnd && buffer[i + 1] == '"') {
                i += 2
                continue
            }
            if (ch == quote) return i + 1
            if (ch == '\\' && !verbatim && i + 1 < bufferEnd) i++
            i++
        }
        return bufferEnd
    }

    private fun finish(type: IElementType, end: Int) {
        currentToken = type
        tokenEnd = end.coerceAtMost(bufferEnd)
    }

    private fun Char.isJavaIdentifierStart(): Boolean = this == '_' || this == '$' || isLetter()
    private fun Char.isJavaIdentifierPart(): Boolean = isJavaIdentifierStart() || isDigit()

    override fun getTokenType(): IElementType? = currentToken
    override fun getTokenStart(): Int = tokenStart
    override fun getTokenEnd(): Int = tokenEnd

    override fun getState(): Int {
        var state = 1 + mode.ordinal
        state = state or ((jsxDepth.coerceIn(0, 0xFF) shl 2))
        if (tagIsClosing) state = state or (1 shl 10)
        if (afterTagOpen) state = state or (1 shl 11)
        if (exprReturnToTag) state = state or (1 shl 12)
        state = state or (exprDepth.coerceIn(0, 0xFF) shl 13)
        return state
    }

    override fun getBufferSequence(): CharSequence = buffer
    override fun getBufferEnd(): Int = bufferEnd

    private companion object {
        val TWO_CHAR_OPERATORS = setOf(
            "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "++", "--",
            "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^="
        )
    }
}
