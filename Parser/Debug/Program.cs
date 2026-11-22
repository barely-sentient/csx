using CSX.Parser;

var samplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "samples", "full-class.cx");
if (!File.Exists(samplePath))
{
    samplePath = @"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\samples\full-class.cx";
}

var source = File.ReadAllText(samplePath);
var tokens = Tokenizer.Tokenize(source);

var logPath = @"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\Parser\Debug\tokens.log";
using var writer = new StreamWriter(logPath);
writer.WriteLine($"Token count: {tokens.Count}");
writer.WriteLine();

foreach (var token in tokens)
{
    var text = (token.Text ?? "").Replace("\r", "").Replace("\n", "\\n");
    var extra = "";
    if (token.TagName is not null) extra += $" tag={token.TagName}";
    if (token.IsComponent) extra += " component";
    if (token.IsSelfClosing) extra += " selfClosing";
    if (token.IsCloseTag) extra += " closeTag";
    writer.WriteLine($"{token.StartPosition,5}:{token.Kind,-24} {token.Length,4} {text}{extra}");
}

Console.WriteLine($"Wrote {tokens.Count} tokens to {logPath}");