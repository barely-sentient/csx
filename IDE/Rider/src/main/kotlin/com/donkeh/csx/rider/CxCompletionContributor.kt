package com.donkeh.csx.rider

import com.intellij.codeInsight.completion.CompletionContributor
import com.intellij.codeInsight.completion.CompletionParameters
import com.intellij.codeInsight.completion.CompletionResultSet
import com.intellij.codeInsight.completion.PlainPrefixMatcher
import com.intellij.codeInsight.lookup.LookupElementBuilder

/**
 * Local completion fallback. The language server supplies semantic completions through LSP,
 * but this contributor guarantees that HTML tags, identifiers already present in the .cx
 * file, and the standard CSX/C# vocabulary are always completable.
 */
class CxCompletionContributor : CompletionContributor() {
    override fun fillCompletionVariants(parameters: CompletionParameters, result: CompletionResultSet) {
        val text = parameters.editor.document.text
        val offset = parameters.editor.caretModel.offset.coerceIn(0, text.length)
        val beforeCaret = text.substring(0, offset)

        var resultSet = result

        // Inside a JSX opening tag (<d) the completion prefix is just the tag name.
        val tagMatch = TAG_PREFIX_REGEX.find(beforeCaret)
        if (tagMatch != null) {
            val tagPrefix = tagMatch.groupValues[1]
            resultSet = result.withPrefixMatcher(PlainPrefixMatcher(tagPrefix))

            for (tag in HTML_TAGS) {
                resultSet.addElement(LookupElementBuilder.create(tag).withTypeText("html", true))
            }

            // Uppercase identifiers in the file are component-style tags.
            val components = IDENTIFIER_REGEX.findAll(text)
                .map { it.value }
                .filter { it.length > 1 && it[0].isUpperCase() }
                .toSet()
            for (component in components) {
                resultSet.addElement(LookupElementBuilder.create(component).withTypeText("component", true))
            }
            return
        }

        val identifiers = IDENTIFIER_REGEX.findAll(text)
            .map { it.value }
            .filter { it.length > 1 }
            .toSet()

        for (identifier in identifiers) {
            resultSet.addElement(LookupElementBuilder.create(identifier).withTypeText("CSX", true))
        }

        for (keyword in CxTokenTypes.KEYWORDS) {
            resultSet.addElement(LookupElementBuilder.create(keyword).withTypeText("keyword", true))
        }
        for (type in CxTokenTypes.PRIMITIVE_TYPES) {
            resultSet.addElement(LookupElementBuilder.create(type).withTypeText("type", true))
        }
    }

    private companion object {
        val TAG_PREFIX_REGEX = Regex("<([A-Za-z][A-Za-z0-9-]*)$")
        val IDENTIFIER_REGEX = Regex("\\b[A-Za-z_][A-Za-z0-9_]*\\b")

        val HTML_TAGS = setOf(
            "a", "abbr", "address", "area", "article", "aside", "audio", "b", "base",
            "bdi", "bdo", "blockquote", "body", "br", "button", "canvas", "caption",
            "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details",
            "dfn", "dialog", "div", "dl", "dt", "em", "embed", "fieldset", "figcaption",
            "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "head",
            "header", "hr", "html", "i", "iframe", "img", "input", "ins", "kbd", "label",
            "legend", "li", "link", "main", "map", "mark", "meta", "meter", "nav",
            "noscript", "object", "ol", "optgroup", "option", "output", "p", "param",
            "picture", "pre", "progress", "q", "rp", "rt", "ruby", "s", "samp", "script",
            "section", "select", "slot", "small", "source", "span", "strong", "style",
            "sub", "summary", "sup", "table", "tbody", "td", "template", "textarea",
            "tfoot", "th", "thead", "time", "title", "tr", "track", "u", "ul", "var",
            "video", "wbr"
        )
    }
}
