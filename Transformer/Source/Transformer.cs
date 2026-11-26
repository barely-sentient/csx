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
            // Generated output always uses LF, so transform a normalized copy. The map's ORIGINAL
            // offsets therefore refer to the LF-normalized source. LspProxy and LanguageServer
            // normalize the source the same way before converting line/character <-> offset.
            string normalized = NormalizeLineEndings(source);
            var tokens = Tokenizer.Tokenize(normalized);
            return TransformCore(normalized, tokens, config);
        }

        public (string Text, SourceMap Map) TransformWithSourceMap(string source, List<Token> tokens, TransformConfig? config = null)
        {
            // Token positions are only valid for the exact text they were produced from. If the
            // source contains CR characters we cannot safely normalize it underneath them, so
            // re-tokenize via the normalizing overload instead.
            if (source.IndexOf('\r') >= 0)
                return TransformWithSourceMap(source, config);

            return TransformCore(source, tokens, config);
        }

        public string Transform(string source, List<Token> tokens, TransformConfig? config = null)
        {
            return TransformWithSourceMap(source, tokens, config).Text;
        }

        private (string Text, SourceMap Map) TransformCore(string source, List<Token> tokens, TransformConfig? config)
        {
            config ??= new TransformConfig();
            var sb = new StringBuilder();
            var map = new SourceMap { SourceFile = null, GeneratedFile = null };

            // Injected usings have no original source counterpart, so they are written before
            // any mapped text. Appending them first keeps every generated offset recorded below
            // naturally shifted past the prefix.
            sb.Append(BuildInjectedUsings(source, tokens, config.InjectedUsings));

            int i = 0;
            int pos = 0;
            int count = tokens.Count;

            while (i < count)
            {
                var t = tokens[i];
                if (t.StartPosition > pos)
                {
                    string gap = source.Substring(pos, t.StartPosition - pos);
                    int gapStart = sb.Length;
                    sb.Append(gap);
                    map.Add(gapStart, gap.Length, pos, gap.Length);
                }

                if (t.Kind == TokenKind.JsxOpenBracket)
                {
                    int genStart = sb.Length;
                    // TransformElement records all of its own mappings (including a fallback
                    // for the whole element anchored to the '<').
                    string element = TransformElement(source, tokens, ref i, config, map, genStart, t.StartPosition);
                    sb.Append(element);
                    if (i > 0 && i - 1 < tokens.Count)
                    {
                        pos = Math.Max(pos, tokens[i - 1].StartPosition + tokens[i - 1].Length);
                    }
                }
                else
                {
                    int genStart = sb.Length;
                    string tokText = t.Text ?? "";
                    sb.Append(tokText);
                    map.Add(genStart, tokText.Length, t.StartPosition, t.Length);
                    pos = Math.Max(pos, t.StartPosition + t.Length);
                    i++;
                }
            }

            if (pos < source.Length)
            {
                string tail = source.Substring(pos);
                int tailStart = sb.Length;
                sb.Append(tail);
                map.Add(tailStart, tail.Length, pos, tail.Length);
            }

            return (sb.ToString(), map);
        }

        /// <summary>
        /// Transforms one JSX element. <paramref name="genBase"/> is the ABSOLUTE offset in the
        /// final generated text where the returned string will be placed (or, when called from a
        /// helper that uses a temporary map, an offset relative to that map's own origin).
        /// </summary>
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
                        AppendText(fragResult, tokens[i], map, genBase);
                        i++;
                        hasFragChild = true;
                    }
                    else if (tokens[i].Kind == TokenKind.OpenBrace)
                    {
                        if (hasFragChild) fragResult.Append(", ");
                        int childGen = genBase + fragResult.Length;
                        fragResult.Append(TransformBraced(source, tokens, ref i, config, map, childGen, tokens[i].StartPosition));
                        hasFragChild = true;
                    }
                    else if (tokens[i].Kind == TokenKind.JsxOpenBracket)
                    {
                        if (hasFragChild) fragResult.Append(", ");
                        int childGen = genBase + fragResult.Length;
                        fragResult.Append(TransformElement(source, tokens, ref i, config, map, childGen, tokens[i].StartPosition));
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
                AddElementFallback(map, genBase, fragResult.Length, origBase);
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
                origTagNameStart = tokens[i].StartPosition;
                i++;
            }

            // consume generics
            string genericText = "";
            int origGenericStart = -1;
            if (i < count && tokens[i].Kind == TokenKind.LessThan)
            {
                int genericStart = tokens[i].StartPosition;
                origGenericStart = genericStart;
                int depth = 0;
                while (i < count)
                {
                    if (tokens[i].Kind == TokenKind.LessThan) { depth++; i++; }
                    else if (tokens[i].Kind == TokenKind.GreaterThan) { depth--; i++; if (depth <= 0) break; }
                    else i++;
                }
                int genericEnd = i > 0 ? tokens[i - 1].StartPosition + tokens[i - 1].Length : genericStart;
                genericText = source.Substring(genericStart, genericEnd - genericStart).Trim();
            }

            // attributes. The joined attribute text is built here and its mappings are recorded
            // relative to its own start; they are shifted into place once we know where it lands.
            var attrBuilder = new StringBuilder();
            var attrMap = map != null ? new SourceMap() : null;
            int attrCount = 0;
            while (i < count && tokens[i].Kind != TokenKind.JsxCloseBracket && tokens[i].Kind != TokenKind.JsxSelfClose)
            {
                if (tokens[i].Kind == TokenKind.JsxAttributeName)
                {
                    var nameToken = tokens[i];
                    string name = nameToken.Text ?? "";
                    i++;
                    bool hasValue = false;
                    if (i < count && tokens[i].Kind == TokenKind.Operator && tokens[i].Text == "=")
                    {
                        i++;
                        hasValue = true;
                    }

                    if (attrCount > 0) attrBuilder.Append(", ");
                    attrMap?.Add(attrBuilder.Length, name.Length, nameToken.StartPosition, nameToken.Length);
                    attrBuilder.Append(name).Append(" = ");
                    if (hasValue)
                    {
                        string value = ParseAttributeValue(source, tokens, ref i, config, attrMap, attrBuilder.Length);
                        attrBuilder.Append(value);
                    }
                    else
                    {
                        // Valueless attribute (e.g. <input disabled />) means true, as in JSX.
                        // Anchor the generated `true` to the attribute name.
                        const string boolValue = "true";
                        attrMap?.Add(attrBuilder.Length, boolValue.Length, nameToken.StartPosition, nameToken.Length);
                        attrBuilder.Append(boolValue);
                    }
                    attrCount++;
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
            bool omitEmptyComponentProps = isComponent && selfClosing && attrCount == 0;
            var result = new StringBuilder();
            if (isComponent)
            {
                result.Append(cls).Append('.').Append(tagMethodUsed)
                      .Append('<').Append(tagName).Append(genericText);
                if (!omitEmptyComponentProps)
                {
                    result.Append(", ").Append(tagName).Append("Props");
                }
                result.Append('>')
                      .Append('(');
            }
            else
            {
                result.Append(cls).Append('.').Append(tagMethodUsed).Append('(').Append(Quote(tagName));
            }

            // Offset of the tag name inside the generated prefix.
            int genOffset = isComponent
                ? cls.Length + 1 + tagMethodUsed.Length + 1
                : cls.Length + 1 + tagMethodUsed.Length + 2;
            bool hasTagMapping = origTagNameStart >= 0 && tagName.Length > 0;

            // Roslyn reports a missing framework class/method on the generated marker. Anchor
            // that range (up to, but not including, the tag name) to the original '<'.
            AddFrameworkMarkerMapping(map, genBase, origBase, hasTagMapping ? genOffset : result.Length);
            if (map != null && hasTagMapping)
            {
                map.Add(genBase + genOffset, tagName.Length, origTagNameStart, tagName.Length);
                if (isComponent && genericText.Length > 0 && origGenericStart >= 0)
                {
                    map.Add(genBase + genOffset + tagName.Length, genericText.Length, origGenericStart, genericText.Length);
                }
            }

            if (attrCount > 0)
            {
                result.Append(isComponent ? "new() { " : ", new() { ");
                if (map != null && attrMap != null)
                {
                    CopyMappings(attrMap, map, genBase + result.Length, 0, int.MaxValue);
                }
                result.Append(attrBuilder).Append(" }");
            }
            else if (!omitEmptyComponentProps)
            {
                result.Append(isComponent ? "new() { }" : ", new() { }");
            }

            if (selfClosing)
            {
                result.Append(')');
                AddElementFallback(map, genBase, result.Length, origBase);
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
                    AppendText(result, tokens[i], map, genBase);
                    i++;
                    hasChild = true;
                }
                else if (tokens[i].Kind == TokenKind.OpenBrace)
                {
                    if (hasChild) result.Append(", ");
                    int childGen = genBase + result.Length;
                    result.Append(TransformBraced(source, tokens, ref i, config, map, childGen, tokens[i].StartPosition));
                    hasChild = true;
                }
                else if (tokens[i].Kind == TokenKind.JsxOpenBracket)
                {
                    if (hasChild) result.Append(", ");
                    int childGen = genBase + result.Length;
                    result.Append(TransformElement(source, tokens, ref i, config, map, childGen, tokens[i].StartPosition));
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
            AddElementFallback(map, genBase, result.Length, origBase);
            return result.ToString();
        }

        private static void AddFrameworkMarkerMapping(SourceMap? map, int genBase, int origBase, int prefixLength)
        {
            if (map == null || origBase < 0 || prefixLength <= 0) return;
            map.Add(genBase, prefixLength, origBase, 1);
        }

        // Added last for each element so the more specific mappings above/inside it come first.
        private static void AddElementFallback(SourceMap? map, int genBase, int length, int origBase)
        {
            if (map == null || origBase < 0 || length <= 0) return;
            map.Add(genBase, length, origBase, 1);
        }

        private static void AppendText(StringBuilder sb, Token token, SourceMap? map, int genBase)
        {
            string quoted = QuoteText(token.Text ?? "");
            map?.Add(genBase + sb.Length, quoted.Length, token.StartPosition, token.Length);
            sb.Append(quoted);
        }

        private static string ParseAttributeValue(string source, List<Token> tokens, ref int i, TransformConfig config, SourceMap? map = null, int genBase = 0)
        {
            int count = tokens.Count;
            if (i >= count) return "null";

            if (tokens[i].Kind == TokenKind.String)
            {
                var tok = tokens[i];
                string v = tok.Text ?? "";
                map?.Add(genBase, v.Length, tok.StartPosition, tok.Length);
                i++;
                return v;
            }

            if (tokens[i].Kind == TokenKind.OpenBrace)
            {
                return TransformBraced(source, tokens, ref i, config, map, genBase, tokens[i].StartPosition);
            }

            if (tokens[i].Kind == TokenKind.JsxOpenBracket)
            {
                return TransformElement(source, tokens, ref i, config, map, genBase, tokens[i].StartPosition);
            }

            var fb = tokens[i];
            string fallback = fb.Text ?? "";
            map?.Add(genBase, fallback.Length, fb.StartPosition, fb.Length);
            i++;
            return fallback;
        }

        private static string TransformBraced(string source, List<Token> tokens, ref int i, TransformConfig config, SourceMap? map = null, int genBase = 0, int origBase = 0)
        {
            int count = tokens.Count;
            if (i >= count || tokens[i].Kind != TokenKind.OpenBrace) return "";

            var sb = new StringBuilder();
            // Mappings are collected relative to the start of the raw (untrimmed) braced text,
            // because the surrounding braces and whitespace are stripped afterwards.
            var local = map != null ? new SourceMap() : null;
            int pos = tokens[i].StartPosition;
            int depth = 0;

            while (i < count)
            {
                var t = tokens[i];
                if (t.StartPosition > pos)
                {
                    string gap = source.Substring(pos, t.StartPosition - pos);
                    local?.Add(sb.Length, gap.Length, pos, gap.Length);
                    sb.Append(gap);
                }

                if (t.Kind == TokenKind.JsxOpenBracket)
                {
                    string elem = TransformElement(source, tokens, ref i, config, local, sb.Length, t.StartPosition);
                    sb.Append(elem);
                    if (i > 0 && i - 1 < tokens.Count)
                    {
                        pos = Math.Max(pos, tokens[i - 1].StartPosition + tokens[i - 1].Length);
                    }
                    continue;
                }

                string tokText = t.Text ?? "";
                local?.Add(sb.Length, tokText.Length, t.StartPosition, t.Length);
                sb.Append(tokText);
                pos = Math.Max(pos, t.StartPosition + t.Length);
                i++;

                if (t.Kind == TokenKind.OpenBrace)
                {
                    depth++;
                }
                else if (t.Kind == TokenKind.CloseBrace)
                {
                    depth--;
                    if (depth <= 0) break;
                }
            }

            // Trim whitespace, strip the outer { }, trim again, tracking exactly how much was
            // removed from the front so nested mappings can be shifted by the same amount.
            string raw = sb.ToString();
            int start = 0;
            int end = raw.Length;
            while (start < end && char.IsWhiteSpace(raw[start])) start++;
            while (end > start && char.IsWhiteSpace(raw[end - 1])) end--;
            if (end - start >= 2 && raw[start] == '{' && raw[end - 1] == '}')
            {
                start++;
                end--;
                while (start < end && char.IsWhiteSpace(raw[start])) start++;
                while (end > start && char.IsWhiteSpace(raw[end - 1])) end--;
            }

            if (map != null && local != null)
            {
                CopyMappings(local, map, genBase - start, start, end);
            }

            return raw.Substring(start, end - start);
        }

        /// <summary>
        /// Copies mappings from <paramref name="from"/> into <paramref name="to"/>, adding
        /// <paramref name="shift"/> to the generated offsets and clipping the generated range to
        /// [<paramref name="lo"/>, <paramref name="hi"/>) (pre-shift coordinates).
        /// </summary>
        private static void CopyMappings(SourceMap from, SourceMap to, int shift, int lo, int hi)
        {
            foreach (var e in from.Mappings)
            {
                int gs = e.GeneratedStart;
                int ge = gs + e.GeneratedLength;
                int ns = Math.Max(gs, lo);
                int ne = Math.Min(ge, hi);
                if (ne <= ns) continue;

                int os = e.OriginalStart;
                int ol = e.OriginalLength;
                if (e.GeneratedLength == e.OriginalLength)
                {
                    // 1:1 text, so clipping the generated side clips the original side equally.
                    os += ns - gs;
                    ol = ne - ns;
                }

                to.Add(ns + shift, ne - ns, os, ol);
            }
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

        /// <summary>
        /// Builds the prefix of `using` directives requested through
        /// <see cref="TransformConfig.InjectedUsings"/>. Entries may be bare namespaces
        /// ("System.Text") or complete directives ("using static System.Math;").
        /// Directives already present in the source, and duplicates in the config, are skipped.
        /// </summary>
        private static string BuildInjectedUsings(string source, List<Token> tokens, string[]? injectedUsings)
        {
            if (injectedUsings == null || injectedUsings.Length == 0)
                return "";

            HashSet<string>? existing = null;
            var added = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();

            foreach (var raw in injectedUsings)
            {
                string? directive = NormalizeUsing(raw);
                if (directive == null) continue;

                string key = GetUsingKey(directive);
                existing ??= CollectExistingUsingKeys(source, tokens);
                if (existing.Contains(key)) continue;
                if (!added.Add(key)) continue;

                sb.Append(directive).Append('\n');
            }

            if (sb.Length > 0) sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Normalizes one configured import into a complete directive. Accepts either a
        /// namespace name or an already-formed directive, with or without the trailing semicolon.
        /// </summary>
        private static string? NormalizeUsing(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string value = CollapseWhitespace(raw.Trim());
            if (value.EndsWith(';')) value = value.Substring(0, value.Length - 1).TrimEnd();
            if (value.Length == 0) return null;

            if (!value.StartsWith("using ", StringComparison.Ordinal) &&
                !value.StartsWith("global using ", StringComparison.Ordinal))
            {
                value = "using " + value;
            }

            return value + ";";
        }

        /// <summary>
        /// Reduces a directive to the imported namespace/alias so that a plain `using X;`
        /// and a `global using X;` are treated as the same import.
        /// </summary>
        private static string GetUsingKey(string directive)
        {
            string value = directive.Trim();
            if (value.EndsWith(';')) value = value.Substring(0, value.Length - 1).TrimEnd();
            if (value.StartsWith("global ", StringComparison.Ordinal))
                value = value.Substring("global ".Length).TrimStart();
            if (value.StartsWith("using ", StringComparison.Ordinal))
                value = value.Substring("using ".Length).TrimStart();
            return value;
        }

        private static HashSet<string> CollectExistingUsingKeys(string source, List<Token> tokens)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int braceDepth = 0;

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.OpenBrace)
                {
                    braceDepth++;
                    continue;
                }
                if (t.Kind == TokenKind.CloseBrace)
                {
                    if (braceDepth > 0) braceDepth--;
                    continue;
                }

                // Usings are only valid at file or namespace scope. Ignoring anything nested
                // deeper also avoids mistaking local `using` statements for directives.
                if (braceDepth > 1 || t.Kind != TokenKind.Keyword || t.Text != "using")
                    continue;

                int j = i + 1;
                while (j < tokens.Count && tokens[j].Length == 0) j++;
                if (j >= tokens.Count) break;

                var first = tokens[j];
                bool looksLikeDirective =
                    first.Kind == TokenKind.Identifier ||
                    (first.Kind == TokenKind.Keyword && first.Text == "static");
                if (!looksLikeDirective) continue;

                int end = -1;
                for (int k = j; k < tokens.Count; k++)
                {
                    var u = tokens[k];
                    if (u.Kind == TokenKind.OpenBrace || u.Kind == TokenKind.JsxOpenBracket) break;
                    if (u.Kind == TokenKind.Semicolon)
                    {
                        end = u.StartPosition + u.Length;
                        break;
                    }
                }

                if (end <= t.StartPosition) continue;

                string? directive = NormalizeUsing(source.Substring(t.StartPosition, end - t.StartPosition));
                if (directive != null) keys.Add(GetUsingKey(directive));
            }

            return keys;
        }

        private static string CollapseWhitespace(string value)
        {
            var sb = new StringBuilder(value.Length);
            bool pendingSpace = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string NormalizeLineEndings(string source) =>
            source.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}