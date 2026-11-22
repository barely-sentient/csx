namespace CSX.Parser
{
    public enum TokenKind
    {
        Unknown,
        Identifier,
        Keyword,
        Number,
        String,
        Char,
        Operator,
        Punctuation,
        LessThan,
        GreaterThan,
        OpenBrace,
        CloseBrace,
        OpenParen,
        CloseParen,
        OpenBracket,
        CloseBracket,
        Comma,
        Colon,
        Semicolon,
        Dot,
        Question,
        Preprocessor,
        Comment,
        Whitespace,

        // JSX tags / elements
        JsxOpenTag,
        JsxOpenComponent,
        JsxOpenElement,
        JsxCloseTag,
        JsxCloseComponent,
        JsxCloseElement,
        JsxSelfClosingTag,
        JsxSelfClosingComponent,
        JsxSelfClosingElement,
        JsxComponent,
        JsxElement,
        JsxTag,

        // JSX pieces
        JsxOpenBracket,
        JsxCloseBracket,
        JsxSelfClose,
        JsxCloseTagOpen,
        JsxTagName,
        JsxComponentName,
        JsxElementName,
        JsxAttributeName,
        JsxText,
        JsxExpression
    }
}