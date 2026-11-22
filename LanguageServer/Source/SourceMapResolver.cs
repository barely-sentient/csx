using CSX.Transformer;

namespace CSX.LanguageServer
{
    public class SourceMapResolver
    {
        private readonly Dictionary<string, SourceMap> _maps = new(StringComparer.OrdinalIgnoreCase);

        public void Load(string generatedPath)
        {
            var mapPath = generatedPath + ".map";
            if (!File.Exists(mapPath)) return;
            var json = File.ReadAllText(mapPath);
            var map = System.Text.Json.JsonSerializer.Deserialize<SourceMap>(json);
            if (map != null) _maps[generatedPath] = map;
        }

        public (int OriginalStart, int OriginalLength)? ToOriginal(string generatedPath, int generatedStart, int generatedLength)
        {
            if (!_maps.TryGetValue(generatedPath, out var map)) return null;
            foreach (var m in map.Mappings)
            {
                if (generatedStart >= m.GeneratedStart && generatedStart < m.GeneratedStart + m.GeneratedLength)
                {
                    int offset = generatedStart - m.GeneratedStart;
                    return (m.OriginalStart + offset, Math.Min(generatedLength, m.OriginalLength - offset));
                }
            }
            return null;
        }

        public int ToGenerated(string generatedPath, int originalOffset)
        {
            if (!_maps.TryGetValue(generatedPath, out var map)) return originalOffset;
            foreach (var m in map.Mappings)
            {
                if (originalOffset >= m.OriginalStart && originalOffset < m.OriginalStart + m.OriginalLength)
                {
                    return m.GeneratedStart + (originalOffset - m.OriginalStart);
                }
            }
            return originalOffset;
        }
    }
}