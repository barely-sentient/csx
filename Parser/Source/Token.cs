namespace CSX.Parser
{
    public class Token
    {
        public TokenKind Kind;

        public int StartPosition;

        public int Length;

        public string? Text;

        public bool IsSelfClosing;

        public bool IsCloseTag;

        public bool IsComponent;

        public string? TagName;

        public int EndPosition => StartPosition + Length;

        public string? Value => Text;

        public string? Lexeme => Text;

        public string? Content => Text;
    }
}