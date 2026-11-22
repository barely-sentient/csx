namespace CSX.Parser
{
    public static class Tokenizer
    {
        public static List<Token> Tokenize(string source) => TokenizeCore(source);

        public static List<Token> Tokenise(string source) => TokenizeCore(source);

        public static List<Token> GetTokens(string source) => TokenizeCore(source);

        public static List<Token> GetAllTokens(string source) => TokenizeCore(source);

        public static List<Token> Lex(string source) => TokenizeCore(source);

        public static List<Token> TokenizeAll(string source) => TokenizeCore(source);

        public static Token[] TokenizeToArray(string source) => TokenizeCore(source).ToArray();

        private enum Mode
        {
            Code,
            JsxTag,
            JsxChildren,
            JsxExpression
        }

        private class Frame
        {
            public Mode Mode;
            public Frame? Parent;
            public int BraceDepth;
            public int Depth;
            public bool ExpectingTagName;
            public bool IsCloseTag;
            public bool BracketConsumed;

            public Frame(Mode mode, Frame? parent, int depth)
            {
                Mode = mode;
                Parent = parent;
                Depth = depth;
                BraceDepth = 0;
                ExpectingTagName = false;
                IsCloseTag = false;
                BracketConsumed = false;
            }
        }

        private static List<Token> TokenizeCore(string source)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrEmpty(source))
                return tokens;

            var stack = new Stack<Frame>();
            stack.Push(new Frame(Mode.Code, null, 0));

            int i = 0;
            int n = source.Length;

            while (i < n)
            {
                if (char.IsWhiteSpace(source[i]))
                {
                    i++;
                    continue;
                }

                if (i + 1 < n && source[i] == '/' && source[i + 1] == '/')
                {
                    while (i < n && source[i] != '\n') i++;
                    continue;
                }

                if (i + 1 < n && source[i] == '/' && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(n, i + 2);
                    continue;
                }

                if (stack.Count == 0) break;

                var frame = stack.Peek();

                switch (frame.Mode)
                {
                    case Mode.Code:
                        if (source[i] == '{')
                        {
                            Emit(tokens, TokenKind.OpenBrace, source, i, 1);
                            i++;
                        }
                        else if (source[i] == '}')
                        {
                            Emit(tokens, TokenKind.CloseBrace, source, i, 1);
                            i++;
                        }
                        else if (source[i] == '<')
                        {
                            if (IsJsxStart(source, i, tokens.Count > 0 ? tokens[tokens.Count - 1] : null))
                            {
                                bool isClose = i + 1 < n && source[i + 1] == '/';
                                if (isClose)
                                {
                                    Emit(tokens, TokenKind.JsxCloseTagOpen, source, i, 2);
                                    i += 2;
                                }
                                else
                                {
                                    Emit(tokens, TokenKind.JsxOpenBracket, source, i, 1);
                                    i++;
                                }

                                var tagFrame = new Frame(Mode.JsxTag, frame, 0)
                                {
                                    BracketConsumed = true,
                                    ExpectingTagName = true,
                                    IsCloseTag = isClose
                                };
                                stack.Push(tagFrame);
                            }
                            else
                            {
                                ScanCSharpToken(source, ref i, tokens);
                            }
                        }
                        else
                        {
                            ScanCSharpToken(source, ref i, tokens);
                        }
                        break;

                    case Mode.JsxTag:
                        if (source[i] == '>')
                        {
                            Emit(tokens, TokenKind.JsxCloseBracket, source, i, 1);
                            i++;

                            var parent = frame.Parent;
                            stack.Pop();

                            if (frame.IsCloseTag)
                            {
                                if (stack.Count > 0 && stack.Peek().Mode == Mode.JsxChildren)
                                {
                                    stack.Pop();
                                }
                            }
                            else
                            {
                                stack.Push(new Frame(Mode.JsxChildren, parent, 1));
                            }
                        }
                        else if (source[i] == '/' && i + 1 < n && source[i + 1] == '>')
                        {
                            Emit(tokens, TokenKind.JsxSelfClose, source, i, 2);
                            i += 2;
                            stack.Pop();
                        }
                        else if (source[i] == '{')
                        {
                            Emit(tokens, TokenKind.OpenBrace, source, i, 1);
                            i++;
                            stack.Push(new Frame(Mode.JsxExpression, frame, 0) { BraceDepth = 1 });
                        }
                        else if (frame.ExpectingTagName)
                        {
                            // tag name (with dots)
                            int nameStart = i;
                            while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                            while (i < n && source[i] == '.')
                            {
                                i++;
                                int partStart = i;
                                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                                if (i == partStart) break;
                            }
                            string tagName = source.Substring(nameStart, i - nameStart);
                            string firstSegment = tagName;
                            int dot = tagName.IndexOf('.');
                            if (dot >= 0) firstSegment = tagName.Substring(0, dot);
                            bool isComponent = firstSegment.Length > 0 && char.IsUpper(firstSegment[0]);

                            tokens.Add(new Token
                            {
                                Kind = isComponent ? TokenKind.JsxComponentName : TokenKind.JsxTagName,
                                StartPosition = nameStart,
                                Length = i - nameStart,
                                Text = source.Substring(nameStart, i - nameStart),
                                TagName = tagName,
                                IsComponent = isComponent
                            });

                            // generics
                            if (i < n && source[i] == '<')
                            {
                                ScanGenericTypeArguments(source, ref i, tokens);
                            }

                            frame.ExpectingTagName = false;
                        }
                        else if (char.IsLetter(source[i]) || source[i] == '_' || source[i] == ':')
                        {
                            int attrStart = i;
                            while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_' ||
                                   source[i] == '-' || source[i] == ':'))
                            {
                                i++;
                            }
                            Emit(tokens, TokenKind.JsxAttributeName, source, attrStart, i - attrStart);
                        }
                        else if (source[i] == '=')
                        {
                            Emit(tokens, TokenKind.Operator, source, i, 1);
                            i++;
                        }
                        else
                        {
                            ScanCSharpToken(source, ref i, tokens);
                        }
                        break;

                    case Mode.JsxChildren:
                        if (source[i] == '<')
                        {
                            bool isClose = i + 1 < n && source[i + 1] == '/';
                            if (isClose)
                            {
                                Emit(tokens, TokenKind.JsxCloseTagOpen, source, i, 2);
                                i += 2;
                            }
                            else
                            {
                                Emit(tokens, TokenKind.JsxOpenBracket, source, i, 1);
                                i++;
                            }

                            var tagFrame = new Frame(Mode.JsxTag, frame, 0)
                            {
                                BracketConsumed = true,
                                ExpectingTagName = true,
                                IsCloseTag = isClose
                            };
                            stack.Push(tagFrame);
                        }
                        else if (source[i] == '{')
                        {
                            Emit(tokens, TokenKind.OpenBrace, source, i, 1);
                            i++;
                            stack.Push(new Frame(Mode.JsxExpression, frame, 0) { BraceDepth = 1 });
                        }
                        else
                        {
                            int textStart = i;
                            while (i < n && source[i] != '<' && source[i] != '{')
                            {
                                i++;
                            }

                            string raw = source.Substring(textStart, i - textStart);
                            string trimmed = raw.Trim();
                            if (trimmed.Length > 0)
                            {
                                int lead = raw.Length - raw.TrimStart().Length;
                                int trail = raw.Length - raw.TrimEnd().Length;
                                int start = textStart + lead;
                                int len = raw.Length - lead - trail;
                                if (len > 0)
                                {
                                    tokens.Add(new Token
                                    {
                                        Kind = TokenKind.JsxText,
                                        StartPosition = start,
                                        Length = len,
                                        Text = source.Substring(start, len)
                                    });
                                }
                            }
                        }
                        break;

                    case Mode.JsxExpression:
                        if (source[i] == '{')
                        {
                            Emit(tokens, TokenKind.OpenBrace, source, i, 1);
                            frame.BraceDepth++;
                            i++;
                        }
                        else if (source[i] == '}')
                        {
                            if (frame.BraceDepth <= 1)
                            {
                                Emit(tokens, TokenKind.CloseBrace, source, i, 1);
                                i++;
                                stack.Pop();
                            }
                            else
                            {
                                Emit(tokens, TokenKind.CloseBrace, source, i, 1);
                                frame.BraceDepth--;
                                i++;
                            }
                        }
                        else if (source[i] == '<')
                        {
                            if (IsJsxStart(source, i, tokens.Count > 0 ? tokens[tokens.Count - 1] : null))
                            {
                                bool isClose = i + 1 < n && source[i + 1] == '/';
                                if (isClose)
                                {
                                    Emit(tokens, TokenKind.JsxCloseTagOpen, source, i, 2);
                                    i += 2;
                                }
                                else
                                {
                                    Emit(tokens, TokenKind.JsxOpenBracket, source, i, 1);
                                    i++;
                                }

                                var tagFrame = new Frame(Mode.JsxTag, frame, 0)
                                {
                                    BracketConsumed = true,
                                    ExpectingTagName = true,
                                    IsCloseTag = isClose
                                };
                                stack.Push(tagFrame);
                            }
                            else
                            {
                                ScanCSharpToken(source, ref i, tokens);
                            }
                        }
                        else
                        {
                            ScanCSharpToken(source, ref i, tokens);
                        }
                        break;
                }
            }

            return tokens;
        }

        private static void Emit(List<Token> tokens, TokenKind kind, string source, int start, int length)
        {
            tokens.Add(new Token
            {
                Kind = kind,
                StartPosition = start,
                Length = length,
                Text = source.Substring(start, length)
            });
        }

        private static bool IsJsxStart(string source, int i, Token? last)
        {
            if (i >= source.Length || source[i] != '<') return false;

            if (last != null)
            {
                var k = last.Kind;
                if (k == TokenKind.Identifier || k == TokenKind.Number ||
                    k == TokenKind.String || k == TokenKind.Char || k == TokenKind.JsxText ||
                    k == TokenKind.JsxCloseBracket || k == TokenKind.CloseParen ||
                    k == TokenKind.CloseBracket || k == TokenKind.GreaterThan ||
                    k == TokenKind.JsxTagName || k == TokenKind.JsxComponentName ||
                    k == TokenKind.JsxAttributeName || k == TokenKind.JsxSelfClose ||
                    k == TokenKind.JsxCloseTagOpen || k == TokenKind.CloseBrace ||
                    k == TokenKind.Dot || k == TokenKind.JsxOpenBracket)
                {
                    return false;
                }
            }

            return TryParseJsxTagHeader(source, i);
        }

        private static bool TryParseJsxTagHeader(string source, int i)
        {
            int n = source.Length;
            int j = i;
            if (j >= n || source[j] != '<') return false;
            j++;
            if (j < n && source[j] == '/') j++;
            SkipWs(source, ref j);
            if (j >= n || !IsIdentStart(source[j])) return false;

            while (j < n && IsIdentPart(source[j])) j++;
            while (j < n && source[j] == '.')
            {
                j++;
                if (j >= n || !IsIdentStart(source[j])) return false;
                while (j < n && IsIdentPart(source[j])) j++;
            }

            while (j < n && source[j] == '<')
            {
                if (!SkipGeneric(source, ref j)) return false;
            }

            SkipWs(source, ref j);
            if (j >= n) return false;
            if (source[j] == '>' || (source[j] == '/' && j + 1 < n && source[j + 1] == '>')) return true;
            if (IsIdentStart(source[j])) return true;
            return false;
        }

        private static bool SkipGeneric(string source, ref int j)
        {
            int n = source.Length;
            if (j >= n || source[j] != '<') return false;
            int depth = 0;
            bool inStr = false;
            j++;
            depth = 1;
            while (j < n && depth > 0)
            {
                char c = source[j];
                if (inStr)
                {
                    if (c == '\\') { j += 2; continue; }
                    if (c == '"') inStr = false;
                    j++;
                    continue;
                }
                if (c == '"')
                {
                    inStr = true;
                    j++;
                    continue;
                }
                if (c == '\'')
                {
                    j++;
                    while (j < n && source[j] != '\'')
                    {
                        if (source[j] == '\\') j++;
                        j++;
                    }
                    if (j < n) j++;
                    continue;
                }
                if (c == '<') { depth++; j++; continue; }
                if (c == '>') { depth--; j++; continue; }
                j++;
            }
            return depth == 0;
        }

        private static void SkipWs(string source, ref int j)
        {
            int n = source.Length;
            while (j < n && char.IsWhiteSpace(source[j])) j++;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static void ScanGenericTypeArguments(string source, ref int i, List<Token> tokens)
        {
            int n = source.Length;
            int depth = 0;
            while (i < n)
            {
                if (source[i] == '<')
                {
                    Emit(tokens, TokenKind.LessThan, source, i, 1);
                    i++;
                    depth++;
                    continue;
                }

                if (source[i] == '>')
                {
                    Emit(tokens, TokenKind.GreaterThan, source, i, 1);
                    i++;
                    depth--;
                    if (depth <= 0) break;
                    continue;
                }

                if (char.IsWhiteSpace(source[i]))
                {
                    i++;
                    continue;
                }

                if (source[i] == '"' || source[i] == '\'' || source[i] == '@' || source[i] == '$')
                {
                    ScanCSharpToken(source, ref i, tokens);
                    continue;
                }

                if (char.IsLetterOrDigit(source[i]) || source[i] == '_')
                {
                    ScanCSharpToken(source, ref i, tokens);
                    continue;
                }

                ScanCSharpToken(source, ref i, tokens);
            }
        }

        private static void ScanCSharpToken(string source, ref int i, List<Token> tokens)
        {
            int n = source.Length;
            int start = i;
            char c = source[i];

            if (c == '#' && (i == 0 || source[i - 1] == '\n'))
            {
                while (i < n && source[i] != '\n') i++;
                tokens.Add(new Token { Kind = TokenKind.Preprocessor, StartPosition = start, Length = i - start, Text = source.Substring(start, i - start) });
                return;
            }

            if (c == '"' || c == '\'' || c == '@' || c == '$')
            {
                int j = i;
                bool interp = false;
                bool verbatim = false;
                while (j < n && (source[j] == '$' || source[j] == '@'))
                {
                    if (source[j] == '$') interp = true;
                    if (source[j] == '@') verbatim = true;
                    j++;
                }

                if (j < n && j + 2 < n && source[j] == '"' && source[j + 1] == '"' && source[j + 2] == '"')
                {
                    int quoteCount = 0;
                    while (j + quoteCount < n && source[j + quoteCount] == '"') quoteCount++;
                    j += quoteCount;
                    while (j < n)
                    {
                        if (source[j] == '"')
                        {
                            int cnt = 0;
                            while (j + cnt < n && source[j + cnt] == '"') cnt++;
                            if (cnt >= quoteCount)
                            {
                                j += quoteCount;
                                break;
                            }
                        }
                        j++;
                    }
                    i = j;
                    tokens.Add(new Token { Kind = TokenKind.String, StartPosition = start, Length = i - start, Text = source.Substring(start, i - start) });
                    return;
                }

                if (j < n && source[j] == '"')
                {
                    i = j;
                    i++; // opening quote
                    if (verbatim)
                    {
                        while (i < n)
                        {
                            if (source[i] == '"')
                            {
                                if (i + 1 < n && source[i + 1] == '"') { i += 2; continue; }
                                i++;
                                break;
                            }
                            i++;
                        }
                    }
                    else if (interp)
                    {
                        while (i < n)
                        {
                            if (source[i] == '"') { i++; break; }
                            if (source[i] == '\\') { i += 2; continue; }
                            if (source[i] == '{')
                            {
                                i++;
                                int depth = 1;
                                while (i < n && depth > 0)
                                {
                                    if (source[i] == '"')
                                    {
                                        i++;
                                        while (i < n)
                                        {
                                            if (source[i] == '\\') { i += 2; continue; }
                                            if (source[i] == '"') { i++; break; }
                                            i++;
                                        }
                                        continue;
                                    }
                                    if (source[i] == '{') depth++;
                                    else if (source[i] == '}') depth--;
                                    i++;
                                }
                                continue;
                            }
                            i++;
                        }
                    }
                    else
                    {
                        while (i < n)
                        {
                            if (source[i] == '\\') { i += 2; continue; }
                            if (source[i] == '"') { i++; break; }
                            i++;
                        }
                    }
                    tokens.Add(new Token { Kind = TokenKind.String, StartPosition = start, Length = i - start, Text = source.Substring(start, i - start) });
                    return;
                }

                if (j < n && source[j] == '\'')
                {
                    i = j;
                    i++; // opening quote
                    while (i < n)
                    {
                        if (source[i] == '\\') { i += 2; continue; }
                        if (source[i] == '\'') { i++; break; }
                        i++;
                    }
                    tokens.Add(new Token { Kind = TokenKind.Char, StartPosition = start, Length = i - start, Text = source.Substring(start, i - start) });
                    return;
                }

                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                if (i == start) i++;
                tokens.Add(new Token { Kind = TokenKind.Identifier, StartPosition = start, Length = i - start, Text = source.Substring(start, i - start) });
                return;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(source[i + 1])))
            {
                if (c == '0' && i + 1 < n && (source[i + 1] == 'x' || source[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < n && IsHexDigit(source[i])) i++;
                }
                else if (c == '0' && i + 1 < n && (source[i + 1] == 'b' || source[i + 1] == 'B'))
                {
                    i += 2;
                    while (i < n && (source[i] == '0' || source[i] == '1' || source[i] == '_')) i++;
                }
                else
                {
                    while (i < n && (char.IsDigit(source[i]) || source[i] == '_')) i++;
                    if (i < n && source[i] == '.')
                    {
                        i++;
                        while (i < n && (char.IsDigit(source[i]) || source[i] == '_')) i++;
                    }
                    if (i < n && (source[i] == 'e' || source[i] == 'E'))
                    {
                        i++;
                        if (i < n && (source[i] == '+' || source[i] == '-')) i++;
                        while (i < n && (char.IsDigit(source[i]) || source[i] == '_')) i++;
                    }
                }
                while (i < n && (char.IsLetter(source[i]) || source[i] == '_')) i++;
                tokens.Add(new Token { Kind = TokenKind.Number, StartPosition = start, Length = i - start, Text = source.Substring(start, i - start) });
                return;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string id = source.Substring(start, i - start);
                bool isKw = Keywords.Contains(id);
                tokens.Add(new Token { Kind = isKw ? TokenKind.Keyword : TokenKind.Identifier, StartPosition = start, Length = i - start, Text = id });
                return;
            }

            int bestLen = 0;
            string? bestOp = null;
            foreach (var op in Operators)
            {
                if (i + op.Length <= n && source.Substring(i, op.Length) == op)
                {
                    if (op.Length > bestLen)
                    {
                        bestLen = op.Length;
                        bestOp = op;
                    }
                }
            }

            if (bestOp != null)
            {
                TokenKind kind;
                if (bestOp == "{") kind = TokenKind.OpenBrace;
                else if (bestOp == "}") kind = TokenKind.CloseBrace;
                else if (bestOp == "(") kind = TokenKind.OpenParen;
                else if (bestOp == ")") kind = TokenKind.CloseParen;
                else if (bestOp == "[") kind = TokenKind.OpenBracket;
                else if (bestOp == "]") kind = TokenKind.CloseBracket;
                else if (bestOp == ",") kind = TokenKind.Comma;
                else if (bestOp == ":") kind = TokenKind.Colon;
                else if (bestOp == ";") kind = TokenKind.Semicolon;
                else if (bestOp == ".") kind = TokenKind.Dot;
                else if (bestOp == "?") kind = TokenKind.Question;
                else if (bestOp == "<") kind = TokenKind.LessThan;
                else if (bestOp == ">") kind = TokenKind.GreaterThan;
                else kind = TokenKind.Operator;

                tokens.Add(new Token { Kind = kind, StartPosition = start, Length = bestLen, Text = bestOp });
                i += bestLen;
                return;
            }

            tokens.Add(new Token { Kind = TokenKind.Unknown, StartPosition = start, Length = 1, Text = source.Substring(start, 1) });
            i++;
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
            "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
            "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
            "internal","is","lock","long","namespace","new","null","object","operator","out","override",
            "params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof",
            "uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
            "add","alias","ascending","async","await","by","descending","equals","from","get","global",
            "group","into","join","let","nameof","notnull","on","orderby","partial","record","remove",
            "required","scoped","select","set","unmanaged","value","var","when","where","with","yield"
        };

        private static readonly string[] Operators = new[]
        {
            "??=", ">>=", "<<=", "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "++", "--",
            "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "->", "::", "?.", "?[",
            "...", "..", "?", ":", ".", ",", ";", "(", ")", "{", "}", "[", "]", "+", "-", "*", "/",
            "%", "&", "|", "^", "~", "!", "=", "<", ">", "@"
        };
    }
}