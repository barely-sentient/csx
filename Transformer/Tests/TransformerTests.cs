using CSX.Parser;
using CSX.Transformer;

public class TransformerTests
{
    [Fact]
    public void SimpleElement()
    {
        var source = "var x = <div />;";
        var result = new Transformer().Transform(source);
        Assert.Contains("_.H(\"div\", new() { })", result);
    }

    [Fact]
    public void ElementWithTextChild()
    {
        var source = "return <p>Hello</p>;";
        var result = new Transformer().Transform(source);
        Assert.Contains("_.H(\"p\", new() { }, \"Hello\")", result);
    }

    [Fact]
    public void ElementWithExpressionChild()
    {
        var source = "return <div>{message}</div>;";
        var result = new Transformer().Transform(source);
        Assert.Contains("_.H(\"div\", new() { }, message)", result);
    }

    [Fact]
    public void Attributes()
    {
        var source = "return <div className=\"dashboard\" id={x} />;";
        var result = new Transformer().Transform(source);
        Assert.Contains("className = \"dashboard\"", result);
        Assert.Contains("id = x", result);
    }

    [Fact]
    public void NestedElements()
    {
        var source = "return <div><span>{x}</span></div>;";
        var result = new Transformer().Transform(source);
        Assert.Contains("_.H(\"div\", new() { }, _.H(\"span\", new() { }, x))", result);
    }

    [Fact]
    public void ComponentUsesC()
    {
        var source = "return <UserCard user={u} />;";
        var result = new Transformer().Transform(source);
        Assert.Contains("_.C<UserCard, UserCardProps>(new() { user = u }", result);
    }

    [Fact]
    public void ComponentWithoutPropsDoesNotRequirePropsType()
    {
        var source = "return <Header />;";
        var result = new Transformer().Transform(source);
        Assert.Contains("_.C<Header>()", result);
        Assert.DoesNotContain("HeaderProps", result);
    }

    [Fact]
    public void CustomConfig()
    {
        var cfg = new TransformConfig
        {
            FrameworkHtmlClass = "Html",
            FrameworkHtmlClassMethod = "Create",
            FrameworkComponentMethod = "Component",
            FrameworkFragmentMethod = "Fragment"
        };
        var source = "return <div />;";
        var result = new Transformer().Transform(source, cfg);
        Assert.Contains("Html.Create(\"div\", new() { })", result);
    }

    [Fact]
    public void InjectedUsingsArePrepended()
    {
        var cfg = new TransformConfig
        {
            InjectedUsings = ["System", "System.Collections.Generic"]
        };
        var result = new Transformer().Transform("var x = <div />;", cfg);
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n\n", result);
        Assert.Contains("_.H(\"div\", new() { })", result);
    }

    [Fact]
    public void InjectedUsingsAcceptCompleteDirectives()
    {
        var cfg = new TransformConfig
        {
            InjectedUsings = ["using static System.Math;", "Alias = System.Text.StringBuilder"]
        };
        var result = new Transformer().Transform("var x = <div />;", cfg);
        Assert.Contains("using static System.Math;", result);
        Assert.Contains("using Alias = System.Text.StringBuilder;", result);
    }

    [Fact]
    public void InjectedUsingsSkipExistingAndDuplicateImports()
    {
        var source = "using System;\nglobal using static System.Math;\n\nnamespace Sample;\n\nvar x = <div />;";
        var cfg = new TransformConfig
        {
            InjectedUsings = ["System", "static System.Math", "System.Linq", "System.Linq", "", "  "]
        };

        var result = new Transformer().Transform(source, cfg);

        Assert.Equal(1, CountOccurrences(result, "using System;"));
        Assert.Equal(1, CountOccurrences(result, "using static System.Math;"));
        Assert.Equal(1, CountOccurrences(result, "using System.Linq;"));
    }

    [Fact]
    public void InjectedUsingsShiftSourceMap()
    {
        var cfg = new TransformConfig { InjectedUsings = ["System"] };
        var source = "var x = <div />;";
        var (text, map) = new Transformer().TransformWithSourceMap(source, cfg);

        const string prefix = "using System;\n\n";
        Assert.StartsWith(prefix, text);

        var tagMapping = map.Mappings.Single(m => m.OriginalStart == 9 && m.OriginalLength == 3);
        Assert.Equal("div", text.Substring(tagMapping.GeneratedStart, tagMapping.GeneratedLength));
        Assert.True(tagMapping.GeneratedStart >= prefix.Length);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public void PreservesSurroundingCode()
    {
        var source = "int x = 1;\nreturn <div />;\nint y = 2;";
        var result = new Transformer().Transform(source);
        Assert.Contains("int x = 1;", result);
        Assert.Contains("int y = 2;", result);
    }

    [Fact]
    public void SampleTransforms()
    {
        var source = File.ReadAllText(FindSample());
        var result = new Transformer().Transform(source);
        Assert.Contains("_.H(\"div\"", result);
        Assert.Contains("_.C<UserCard<User>, UserCardProps>", result);
        Assert.DoesNotContain("<div", result);
    }

    private static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "full-class.cx");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "samples", "full-class.cx");
    }
}