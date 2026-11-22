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
            // Check sub-entries first (more precise).
            foreach (var m in map.Mappings)
            {
                if (generatedStart >= m.GeneratedStart && generatedStart < m.GeneratedStart + m.GeneratedLength)
                {
                    // For transformed entries (different lengths), don't interpolate;
                    // land on the original start (the '<' of the tag) for stability.
                    if (m.GeneratedLength != m.OriginalLength)
                    {
                        int subOffset = generatedStart - m.GeneratedStart;
                        if (subOffset == 0)
                        {
                            return (m.OriginalStart, Math.Min(generatedLength, m.OriginalLength));
                        }
                        // For non-zero offset in a transformed entry, try proportional
                        // but clamp to the original range.
                        int mapped = m.OriginalStart + (int)Math.Round((double)subOffset / m.GeneratedLength * m.OriginalLength);
                        mapped = Math.Clamp(mapped, m.OriginalStart, m.OriginalStart + m.OriginalLength - 1);
                        return (mapped, Math.Min(generatedLength, m.OriginalLength - (mapped - m.OriginalStart)));
                    }
                    // Preserved entry: identity mapping (exact).
                    int off = generatedStart - m.GeneratedStart;
                    return (m.OriginalStart + off, Math.Min(generatedLength, m.OriginalLength - off));
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