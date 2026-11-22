using System.Text;
using CSX.Parser;

namespace CSX.Transformer
{
    public class Transformer
    {
        public string Transform(string source, TransformConfig? config = null)
        {
            return TransformWithSourceMap(source, config).Text;
        }

        public (string Text, SourceMap Map) TransformWithSourceMap(string source, TransformConfig? config = null)
        {
            // Generated output always uses LF. Normalize first so source-map offsets stay aligned
            // with the generated offsets; CRLF input otherwise shifts every mapping by one per line.
            source = NormalizeLineEndings(source);
            var tokens = Tokenizer.Tokenize(source);
            return TransformWithSourceMap(source, tokens, config);
        }

        public (string Text, SourceMap Map) TransformWithSourceMap(string source, List<Token> tokens, TransformConfig? config = null)
        {
            source = NormalizeLineEndings(source);
            config ??= new TransformConfig();
            var sb = new StringBuilder();
            var map = new SourceMap { SourceFile = null, GeneratedFile = null };
            int i = 0;
            int pos = 0;
            int count = tokens.Count;

            while (i < count)
            {
                var t = tokens[i];
                if (t.StartPosition > pos)
                {
                    string gap = source.Substring(pos, t.StartPosition - pos);
                    int genStart = sb.Length;
                    sb.Append(gap);
                    map.Add(genStart, gap.Length, pos, gap.Length);
                }

                if (t.Kind == TokenKind.JsxOpenBracket)
                {
                    int genStart = sb.Length;
                    int origStart = t.StartPosition;
                    string element = TransformElement(source, tokens, ref i, config, map, genStart, origStart);
                    sb.Append(element);
                    // The generated element is heavily rewritten. Anchor the whole element
                    // mapping to the original '<' only; specific sub-mappings below handle
                    // tag names and the framework marker.
                    map.Add(genStart, element.Length, origStart, 1);
                    if (i > 0 && i - 1 < tokens.Count)
                    {
                        pos = tokens[i - 1].StartPosition + tokens[i - 1].Length;
                    }
                }
                else
                {
                    int genStart = sb.Length;
                    sb.Append(t.Text);
                    map.Add(genStart, (t.Text ?? "").Length, t.StartPosition, t.Length);
                    pos = t.StartPosition + t.Length;
                    i++;
                }
            }

            if (pos < source.Length)
            {
                string tail = source.Substring(pos);
                int genStart = sb.Length;
                sb.Append(tail);
                map.Add(genStart, tail.Length, pos, tail.Length);
            }

            return (sb.ToString(), map);
        }

        public string Transform(string source, List<Token> tokens, TransformConfig? config = null)
        {
            return TransformWithSourceMap(source, tokens, config).Text;
        }

        private static string TransformElement(string source, List<Token> tokens, ref int i, TransformConfig config, SourceMap? map = null, int genBase = 0, int origBase = 0)
        {
            int count = tokens.Count;
            // i at JsxOpenBracket
            i++; // consume <

            var cls = config.FrameworkHtmlClass;
            var tagMethod = config.FrameworkHtmlClassMethod;
            var componentMethod = config.FrameworkComponentMethod;
            var fragmentMethod = config.FrameworkFragmentMethod;

            // fragment <>
            if (i < count && tokens[i].Kind == TokenKind.JsxCloseBracket)
            {
                i++; // consume >
                var fragResult = new StringBuilder();
                fragResult.Append(cls).Append('.').Append(fragmentMethod).Append('(');
                AddFrameworkMarkerMapping(map, genBase, origBase, fragResult.Length);

                bool hasFragChild = false;
                while (i < count && tokens[i].Kind != TokenKind.JsxCloseTagOpen)
                {
                    if (tokens[i].Kind == TokenKind.JsxText)
                    {
                        if (hasFragChild) fragResult.Append(", ");
                        fragResult.Append(QuoteText(tokens[i].Text ?? ""));
                        i++;
                        hasFragChild = true;
                    }
                    else if (tokens[i].Kind == TokenKind.OpenBrace)
                    {
                        if (hasFragChild) fragResult.Append(", ");
                        fragResult.Append(TransformBraced(source, tokens, ref i, config, map, genBase + fragResult.Length, tokens[i].StartPosition));
                        hasFragChild = true;
                    }
                    else if (tokens[i].Kind == TokenKind.JsxOpenBracket)
                    {
                        if (hasFragChild) fragResult.Append(", ");
                        fragResult.Append(TransformElement(source, tokens, ref i, config, map, genBase + fragResult.Length, tokens[i].StartPosition));
                        hasFragChild = true;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (i < count && tokens[i].Kind == TokenKind.JsxCloseTagOpen)
                {
                    i++; // </
                    while (i < count && tokens[i].Kind != TokenKind.JsxCloseBracket) i++;
                    if (i < count && tokens[i].Kind == TokenKind.JsxCloseBracket) i++;
                }

                fragResult.Append(')');
                return fragResult.ToString();
            }

            bool isComponent = false;
            string tagName = "";
            int origTagNameStart = -1;
            if (i < count && tokens[i].Kind == TokenKind.JsxComponentName)
            {
                isComponent = true;
                tagName = tokens[i].Text ?? "";
                origTagNameStart = tokens[i].StartPosition;
                i++;
            }
            else if (i < count && tokens[i].Kind == TokenKind.JsxTagName)
            {
                tagName = tokens[i].Text ?? "";
                i++;
            }

            // consume generics
            string genericText = "";
            if (i < count && tokens[i].Kind == TokenKind.LessThan)
            {
                int genStart = tokens[i].StartPosition;
                int depth = 0;
                while (i < count)
                {
                    if (tokens[i].Kind == TokenKind.LessThan) { depth++; i++; }
                    else if (tokens[i].Kind == TokenKind.GreaterThan) { depth--; i++; if (depth <= 0) break; }
                    else i++;
                }
                int genEnd = i > 0 ? tokens[i - 1].StartPosition + tokens[i - 1].Length : genStart;
                genericText = source.Substring(genStart, genEnd - genStart).Trim();
            }

            // attributes
            var attrs = new List<string>();
            while (i < count && tokens[i].Kind != TokenKind.JsxCloseBracket && tokens[i].Kind != TokenKind.JsxSelfClose)
            {
                if (tokens[i].Kind == TokenKind.JsxAttributeName)
                {
                    string name = tokens[i].Text ?? "";
                    i++;
                    if (i < count && tokens[i].Kind == TokenKind.Operator && tokens[i].Text == "=")
                    {
                        i++;
                    }
                    string value = ParseAttributeValue(source, tokens, ref i, config);
                    attrs.Add(name + " = " + value);
                }
                else
                {
                    i++;
                }
            }

            bool selfClosing = false;
            if (i < count && tokens[i].Kind == TokenKind.JsxSelfClose)
            {
                selfClosing = true;
                i++;
            }
            else if (i < count && tokens[i].Kind == TokenKind.JsxCloseBracket)
            {
                i++;
            }

            string tagMethodUsed = isComponent ? componentMethod : tagMethod;
            var result = new StringBuilder();
            if (isComponent)
            {
                result.Append(cls).Append('.').Append(tagMethodUsed)
                      .Append('<').Append(tagName).Append(genericText).Append(", ").Append(tagName).Append("Props>")
                      .Append('(');
            }
            else
            {
                result.Append(cls).Append('.').Append(tagMethodUsed).Append('(').Append(Quote(tagName));
            }

            // Roslyn reports a missing framework class/method on the generated marker. Anchor
            // that range to the original '<' so .cx shows the error exactly on the JSX bracket.
            AddFrameworkMarkerMapping(map, genBase, origBase, result.Length);
            if (map != null && origTagNameStart >= 0 && tagName.Length > 0)
            {
                int genOffset = isComponent
                    ? cls.Length + 1 + tagMethodUsed.Length + 1
                    : cls.Length + 1 + tagMethodUsed.Length + 2;
                map.Add(genBase + genOffset, tagName.Length, origTagNameStart, tagName.Length);
            }

            if (attrs.Count > 0)
            {
                if (isComponent)
                {
                    result.Append("new() { ").Append(string.Join(", ", attrs)).Append(" }");
                }
                else
                {
                    result.Append(", new() { ").Append(string.Join(", ", attrs)).Append(" }");
                }
            }
            else
            {
                result.Append(isComponent ? "new() { }" : ", new() { }");
            }

            if (selfClosing)
            {
                result.Append(')');
                return result.ToString();
            }

            // The attribute object has already been emitted as an argument, so the first
            // child must be comma-separated even when there are no attributes.
            bool hasChild = true;
            while (i < count && tokens[i].Kind != TokenKind.JsxCloseTagOpen)
            {
                if (tokens[i].Kind == TokenKind.JsxText)
                {
                    if (hasChild) result.Append(", ");
                    result.Append(QuoteText(tokens[i].Text ?? ""));
                    i++;
                    hasChild = true;
                }
                else if (tokens[i].Kind == TokenKind.OpenBrace)
                {
                    if (hasChild) result.Append(", ");
                    result.Append(TransformBraced(source, tokens, ref i, config, map, genBase + result.Length, tokens[i].StartPosition));
                    hasChild = true;
                }
                else if (tokens[i].Kind == TokenKind.JsxOpenBracket)
                {
                    if (hasChild) result.Append(", ");
                    result.Append(TransformElement(source, tokens, ref i, config, map, genBase + result.Length, tokens[i].StartPosition));
                    hasChild = true;
                }
                else
                {
                    i++;
                }
            }

            // consume close tag
            if (i < count && tokens[i].Kind == TokenKind.JsxCloseTagOpen)
            {
                i++; // </
                while (i < count && tokens[i].Kind != TokenKind.JsxCloseBracket) i++;
                if (i < count && tokens[i].Kind == TokenKind.JsxCloseBracket) i++;
            }

            result.Append(')');
            return result.ToString();
        }

        private static void AddFrameworkMarkerMapping(SourceMap? map, int genBase, int origBase, int prefixLength)
        {
            if (map == null || origBase < 0 || prefixLength <= 0) return;
            map.Add(genBase, prefixLength, origBase, 1);
        }

        private static string ParseAttributeValue(string source, List<Token> tokens, ref int i, TransformConfig config)
        {
            int count = tokens.Count;
            if (i >= count) return "null";

            if (tokens[i].Kind == TokenKind.String)
            {
                string v = tokens[i].Text ?? "";
                i++;
                return v;
            }

            if (tokens[i].Kind == TokenKind.OpenBrace)
            {
                return TransformBraced(source, tokens, ref i, config);
            }

            if (tokens[i].Kind == TokenKind.JsxOpenBracket)
            {
                return TransformElement(source, tokens, ref i, config);
            }

            string fallback = tokens[i].Text ?? "";
            i++;
            return fallback;
        }

        private static string TransformBraced(string source, List<Token> tokens, ref int i, TransformConfig config, SourceMap? map = null, int genBase = 0, int origBase = 0)
        {
            int count = tokens.Count;
            if (i >= count || tokens[i].Kind != TokenKind.OpenBrace) return "";

            var sb = new StringBuilder();
            int pos = tokens[i].StartPosition;
            int depth = 0;

            while (i < count)
            {
                var t = tokens[i];
                if (t.StartPosition > pos)
                {
                    sb.Append(source.Substring(pos, t.StartPosition - pos));
                }

                if (t.Kind == TokenKind.OpenBrace)
                {
                    depth++;
                    sb.Append(t.Text);
                    pos = t.StartPosition + t.Length;
                    i++;
                }
                else if (t.Kind == TokenKind.CloseBrace)
                {
                    depth--;
                    sb.Append(t.Text);
                    pos = t.StartPosition + t.Length;
                    i++;
                    if (depth <= 0) break;
                }
                else if (t.Kind == TokenKind.JsxOpenBracket)
                {
                    string elem = TransformElement(source, tokens, ref i, config, map, genBase + sb.Length, t.StartPosition);
                    sb.Append(elem);
                    if (i > 0 && i - 1 < tokens.Count)
                    {
                        pos = tokens[i - 1].StartPosition + tokens[i - 1].Length;
                    }
                }
                else
                {
                    sb.Append(t.Text);
                    pos = t.StartPosition + t.Length;
                    i++;
                }
            }

            var result = sb.ToString().Trim();
            if (result.StartsWith("{") && result.EndsWith("}"))
            {
                result = result.Substring(1, result.Length - 2).Trim();
            }
            return result;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string QuoteText(string value)
        {
            value = value.Trim();
            return Quote(value);
        }

        private static string NormalizeLineEndings(string source) =>
            source.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
