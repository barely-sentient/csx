using CSX.Parser;

public class TokenizerTests
{
    [Fact]
    public void EmptyInputReturnsEmpty()
    {
        Assert.Empty(Tokenizer.Tokenize(""));
        Assert.Empty(Tokenizer.Tokenize(null!));
    }

    [Fact]
    public void AliasesWork()
    {
        var src = "int x = 1;";
        Assert.Equal(Tokenizer.Tokenize(src).Count, Tokenizer.GetTokens(src).Count);
        Assert.Equal(Tokenizer.Tokenize(src).Count, Tokenizer.Lex(src).Count);
        Assert.Equal(Tokenizer.Tokenize(src).Count, Tokenizer.TokenizeToArray(src).Length);
    }

    [Fact]
    public void SimpleCSharpTokens()
    {
        var tokens = Tokenizer.Tokenize("class A { int x = 1; }");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && t.Text == "class");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Identifier && t.Text == "A");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Number && t.Text == "1");
    }

    [Fact]
    public void GenericsAreNotJsx()
    {
        var src = "var x = new List<int>(); var y = new Dictionary<string, List<User>>();";
        var tokens = Tokenizer.Tokenize(src);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.JsxOpenBracket);
        Assert.Contains(tokens, t => t.Kind == TokenKind.LessThan);
    }

    [Fact]
    public void GenericTupleType()
    {
        var src = "List<(User User, int Score)> x;";
        var tokens = Tokenizer.Tokenize(src);
        Assert.DoesNotContain(tokens, t => t.Kind.ToString().StartsWith("Jsx"));
    }

    [Fact]
    public void NormalTagIdentified()
    {
        var tokens = Tokenizer.Tokenize("return <div className=\"dashboard\">text</div>;");
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxOpenBracket);
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxTagName && t.Text == "div");
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxAttributeName && t.Text == "className");
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxText && t.Text == "text");
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxCloseTagOpen);
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxCloseBracket);
    }

    [Fact]
    public void SelfClosingTagIdentified()
    {
        var tokens = Tokenizer.Tokenize("<div />");
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxOpenBracket);
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxTagName && t.Text == "div");
        Assert.Contains(tokens, t => t.Kind == TokenKind.JsxSelfClose);
    }

    [Fact]
    public void ComponentIdentified()
    {
        var tokens = Tokenizer.Tokenize("<UserCard />");
        var t = tokens.First(x => x.Kind == TokenKind.JsxComponentName);
        Assert.True(t.IsComponent);
        Assert.Equal("UserCard", t.TagName);
    }

    [Fact]
    public void NestedTags()
    {
        var src = "<div><span>{x}</span></div>";
        var tokens = Tokenizer.Tokenize(src);
        Assert.Equal(2, tokens.Count(t => t.Kind == TokenKind.JsxOpenBracket));
        Assert.Equal(2, tokens.Count(t => t.Kind == TokenKind.JsxCloseTagOpen));
    }

    [Fact]
    public void ExpressionInBraces()
    {
        var tokens = Tokenizer.Tokenize("<div>{message}</div>");
        Assert.Contains(tokens, t => t.Kind == TokenKind.OpenBrace);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Identifier && t.Text == "message");
        Assert.Contains(tokens, t => t.Kind == TokenKind.CloseBrace);
    }

    [Fact]
    public void TernaryJsx()
    {
        var src = "{cond ? <A /> : <B />}";
        var tokens = Tokenizer.Tokenize(src);
        Assert.Equal(2, tokens.Count(t => t.Kind == TokenKind.JsxSelfClose));
    }

    [Fact]
    public void InterpolatedString()
    {
        var tokens = Tokenizer.Tokenize("var s = $\"Found {users.Count} active\";");
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Text != null && t.Text.Contains("Found"));
    }

    [Fact]
    public void SampleTokenizesWithoutUnknown()
    {
        var src = File.ReadAllText(@"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\samples\full-class.cx");
        var tokens = Tokenizer.Tokenize(src);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Unknown);
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void SampleHasJsxBrackets()
    {
        var src = File.ReadAllText(@"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\samples\full-class.cx");
        var tokens = Tokenizer.Tokenize(src);
        Assert.True(tokens.Count(t => t.Kind == TokenKind.JsxOpenBracket) >= 10);
        Assert.True(tokens.Count(t => t.Kind == TokenKind.JsxSelfClose) >= 2);
    }

    [Fact]
    public void TagTextPositions()
    {
        var src = "<div />";
        var tokens = Tokenizer.Tokenize(src);
        Assert.Equal(0, tokens[0].StartPosition);
    }

    [Fact]
    public void LessThanComparisonNotJsx()
    {
        var tokens = Tokenizer.Tokenize("if (a < b && c > d) { }");
        Assert.DoesNotContain(tokens, t => t.Kind.ToString().StartsWith("Jsx"));
        Assert.Contains(tokens, t => t.Kind == TokenKind.LessThan);
        Assert.Contains(tokens, t => t.Kind == TokenKind.GreaterThan);
    }

    [Fact]
    public void GenericAfterNewNotJsx()
    {
        var tokens = Tokenizer.Tokenize("var x = new Foo<Bar>();");
        Assert.DoesNotContain(tokens, t => t.Kind.ToString().StartsWith("Jsx"));
    }

    [Fact]
    public void ComponentWithGenerics()
    {
        var tokens = Tokenizer.Tokenize("<Loop<List<User>> over={users}>");
        var t = tokens.First(x => x.Kind == TokenKind.JsxComponentName);
        Assert.True(t.IsComponent);
        Assert.Equal("Loop", t.TagName);
    }
}