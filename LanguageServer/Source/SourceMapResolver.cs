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

        public SourceMapEntry? FindGeneratedMapping(string generatedPath, int generatedOffset)
        {
            if (!_maps.TryGetValue(generatedPath, out var map)) return null;
            // Mappings are added from the inside out (sub-mappings first, then the
            // containing element), so the first match is the most specific one.
            foreach (var m in map.Mappings)
            {
                if (generatedOffset >= m.GeneratedStart &&
                    generatedOffset < m.GeneratedStart + m.GeneratedLength)
                {
                    return m;
                }
            }
            return null;
        }

        public (int OriginalStart, int OriginalLength)? ToOriginal(string generatedPath, int generatedStart, int generatedLength)
        {
            var m = FindGeneratedMapping(generatedPath, generatedStart);
            if (m == null) return null;

            // For transformed entries (different lengths), don't interpolate when the
            // diagnostic starts at the generated marker - land on the original '<'.
            if (m.GeneratedLength != m.OriginalLength || m.OriginalLength == 1)
            {
                int subOffset = generatedStart - m.GeneratedStart;
                if (subOffset == 0 || m.OriginalLength <= 1)
                {
                    return (m.OriginalStart, Math.Min(Math.Max(generatedLength, 1), m.OriginalLength));
                }
                int mapped = m.OriginalStart + (int)Math.Round((double)subOffset / m.GeneratedLength * (m.OriginalLength - 1));
                mapped = Math.Clamp(mapped, m.OriginalStart, m.OriginalStart + m.OriginalLength - 1);
                return (mapped, Math.Min(Math.Max(generatedLength, 1), m.OriginalStart + m.OriginalLength - mapped));
            }

            // Preserved entry: identity mapping (exact).
            int off = generatedStart - m.GeneratedStart;
            return (m.OriginalStart + off, Math.Min(Math.Max(generatedLength, 1), m.OriginalLength - off));
        }

        public int ToGenerated(string generatedPath, int originalOffset)
        {
            if (!_maps.TryGetValue(generatedPath, out var map)) return originalOffset;
            foreach (var m in map.Mappings)
            {
                if (originalOffset >= m.OriginalStart && originalOffset < m.OriginalStart + m.OriginalLength)
                {
                    if (m.GeneratedLength != m.OriginalLength)
                    {
                        // Transformed entry: map to start of generated (stable anchor).
                        if (originalOffset == m.OriginalStart)
                        {
                            return m.GeneratedStart;
                        }
                        int mapped = m.GeneratedStart + (int)Math.Round((double)(originalOffset - m.OriginalStart) / m.OriginalLength * m.GeneratedLength);
                        return Math.Clamp(mapped, m.GeneratedStart, m.GeneratedStart + m.GeneratedLength - 1);
                    }
                    // Preserved entry: identity.
                    return m.GeneratedStart + (originalOffset - m.OriginalStart);
                }
            }
            return originalOffset;
        }
    }
}