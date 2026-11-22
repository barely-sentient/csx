using System.Text.Json;
using CSX.Transformer;

var samplePath = @"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\samples\full-class.cx";
var source = File.ReadAllText(samplePath);
var transformer = new Transformer();
var (transformed, map) = transformer.TransformWithSourceMap(source);
var outPath = @"C:\Users\jhuds\Desktop\UI Framework\CSharpExtended\Transformer\Debug\transformed.cx";
File.WriteAllText(outPath, transformed);
var mapPath = outPath + ".map";
map.SourceFile = samplePath;
map.GeneratedFile = outPath;
File.WriteAllText(mapPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote transformed output to {outPath}");
Console.WriteLine($"Wrote source map to {mapPath}");