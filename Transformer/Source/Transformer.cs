using System.Text;
using CSX.Parser;

namespace CSX.Transformer
{
    public class Transformer
    {
        public string Transform(string source, TransformConfig? config = null)
        {
            return TransformWithSourceMap(source, Tokenizer.Tokenize(source), config).Text;
        }

        public (string Text, SourceMap Map) TransformWithSourceMap(string source, TransformConfig? config = null)
        {
            var tokens = Tokenizer.Tokenize(source);
            return TransformWithSourceMap(source, tokens, config);
        }

        public (string Text, SourceMap Map) TransformWithSourceMap(string source, List<Token> tokens, TransformConfig? config = null)
        {
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
                    int origEnd = i > 0 ? tokens[i - 1].StartPosition + tokens[i - 1].Length : origStart;
                    map.Add(genStart, element.Length, origStart, origEnd - origStart);
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

            // fragment <>
            if (i < count && tokens[i].Kind == TokenKind.JsxCloseBracket)
            {
                i++; // consume >
                var fragChildren = new List<string>();
                while (i < count && tokens[i].Kind != TokenKind.JsxCloseTagOpen)
                {
                    if (tokens[i].Kind == TokenKind.JsxText)
                    {
                        fragChildren.Add(QuoteText(tokens[i].Text ?? ""));
                        i++;
                    }
                    else if (tokens[i].Kind == TokenKind.OpenBrace)
                    {
                        fragChildren.Add(TransformBraced(source, tokens, ref i, config));
                    }
                    else if (tokens[i].Kind == TokenKind.JsxOpenBracket)
                    {
                        fragChildren.Add(TransformElement(source, tokens, ref i, config));
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

                var cls = config.FrameworkHtmlClass;
                var fragMethod = config.FrameworkFragmentMethod;
                var sb = new StringBuilder();
                sb.Append(cls).Append('.').Append(fragMethod).Append('(');
                if (fragChildren.Count > 0)
                {
                    sb.Append(string.Join(", ", fragChildren));
                }
                sb.Append(')');
                return sb.ToString();
            }

            bool isComponent = false;
            string tagName = "";
             int origTagNameStart = -1;
            if (i < count && tokens[i].Kind == TokenKind.JsxComponentName)
            {
                isComponent = true;
                tagName = tokens[i].Text ?? "";
                 origTagNameStart = tokens[i].StartPosition;
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

            string clsName = config.FrameworkHtmlClass;
            string tagMethod = isComponent ? config.FrameworkComponentMethod : config.FrameworkHtmlClassMethod;

            if (selfClosing)
            {
                var sb = new StringBuilder();
                if (isComponent)
                {
                    sb.Append(clsName).Append('.').Append(tagMethod)
                      .Append('<').Append(tagName).Append(genericText).Append(", ").Append(tagName).Append("Props>")
                      .Append('(');
                    if (attrs.Count > 0)
                    {
                        sb.Append("new() { ").Append(string.Join(", ", attrs)).Append(" }");
                    }
                    else
                    {
                        sb.Append("new() { }");
                    }
                    sb.Append(')');
                }
                else
                {
                    sb.Append(clsName).Append('.').Append(tagMethod).Append('(').Append(Quote(tagName));
                    if (attrs.Count > 0)
                    {
                        sb.Append(", new() { ").Append(string.Join(", ", attrs)).Append(" }");
                    }
                    else
                    {
                        sb.Append(", new() { }");
                    }
                    sb.Append(')');
                }
                if (map != null && origTagNameStart >= 0 && tagName.Length > 0)
                {
                    int genOffset = isComponent ? 3 : 4;
                    map.Add(genBase + genOffset, tagName.Length, origTagNameStart, tagName.Length);
                }
                return sb.ToString();
            }

            // children
            var children = new List<string>();
            while (i < count && tokens[i].Kind != TokenKind.JsxCloseTagOpen)
            {
                if (tokens[i].Kind == TokenKind.JsxText)
                {
                    children.Add(QuoteText(tokens[i].Text ?? ""));
                    i++;
                }
                else if (tokens[i].Kind == TokenKind.OpenBrace)
                {
                    children.Add(TransformBraced(source, tokens, ref i, config));
                }
                else if (tokens[i].Kind == TokenKind.JsxOpenBracket)
                {
                    children.Add(TransformElement(source, tokens, ref i, config));
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

            var result = new StringBuilder();
            if (isComponent)
            {
                result.Append(clsName).Append('.').Append(tagMethod)
                      .Append('<').Append(tagName).Append(genericText).Append(", ").Append(tagName).Append("Props>")
                      .Append('(');
                if (attrs.Count > 0)
                {
                    result.Append("new() { ").Append(string.Join(", ", attrs)).Append(" }");
                }
                else
                {
                    result.Append("new() { }");
                }
            }
            else
            {
                result.Append(clsName).Append('.').Append(tagMethod).Append('(').Append(Quote(tagName));
                if (attrs.Count > 0)
                {
                    result.Append(", new() { ").Append(string.Join(", ", attrs)).Append(" }");
                }
                else
                {
                    result.Append(", new() { }");
                }
            }
            foreach (var child in children)
            {
                result.Append(", ").Append(child);
            }
            result.Append(')');
            if (map != null && origTagNameStart >= 0 && tagName.Length > 0)
            {
                int genOffset = isComponent ? 3 : 4;
                map.Add(genBase + genOffset, tagName.Length, origTagNameStart, tagName.Length);
            }
            return result.ToString();
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

        private static string TransformBraced(string source, List<Token> tokens, ref int i, TransformConfig config)
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
                    string elem = TransformElement(source, tokens, ref i, config);
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
    }
}
