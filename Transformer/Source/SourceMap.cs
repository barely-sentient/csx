namespace CSX.Transformer
{
    public class SourceMapEntry
    {
        public int GeneratedStart { get; set; }

        public int GeneratedLength { get; set; }

        public int OriginalStart { get; set; }

        public int OriginalLength { get; set; }
    }

    public class SourceMap
    {
        public string? SourceFile { get; set; }

        public string? GeneratedFile { get; set; }

        // Needs a setter: System.Text.Json ignores get-only collection properties when
        // deserializing, which left every map loaded by SourceMapResolver empty.
        public List<SourceMapEntry> Mappings { get; set; } = new List<SourceMapEntry>();

        public void Add(int generatedStart, int generatedLength, int originalStart, int originalLength)
        {
            if (generatedLength <= 0 || originalLength <= 0)
                return;

            Mappings.Add(new SourceMapEntry
            {
                GeneratedStart = generatedStart,
                GeneratedLength = generatedLength,
                OriginalStart = originalStart,
                OriginalLength = originalLength
            });
        }
    }
}