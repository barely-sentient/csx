using CSX.Parser;
public class DbgTests {
    [Fact]
    public void Dbg() {
        var src = File.ReadAllText(@"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\samples\full-class.cx");
        // find index of "<div className"
        int idx = src.IndexOf("<div className");
        Console.WriteLine("IDX=" + idx);
        // tokenize up to idx to get last token
        var toks = Tokenizer.Tokenize(src.Substring(0, idx));
        Console.WriteLine("LAST=" + (toks.Count>0 ? toks[toks.Count-1].Kind + " " + toks[toks.Count-1].Text : "none"));
        // reflection? IsJsxStart private. We'll just trust.
    }
}
