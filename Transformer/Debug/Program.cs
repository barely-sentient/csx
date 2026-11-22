using CSX.Transformer;

var samplePath = @"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\samples\full-class.cx";
var source = File.ReadAllText(samplePath);
var transformed = new Transformer().Transform(source);
var outPath = @"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\Transformer\Debug\transformed.cx";
File.WriteAllText(outPath, transformed);
Console.WriteLine($"Wrote transformed output to {outPath}");