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
            int off = generatedStart - m.GeneratedStart;
            return (m.OriginalStart + off, Math.Min(Math.Max(generatedLength, 1), m.OriginalLength - off));
        }

        public int ToGenerated(string generatedPath, int originalOffset)
        {
            if (!_maps.TryGetValue(generatedPath, out var map)) return originalOffset;

            // Prefer a mapping that actually contains the caret.
            foreach (var m in map.Mappings)
            {
                if (m.OriginalLength <= 0) continue;
                if (originalOffset >= m.OriginalStart && originalOffset < m.OriginalStart + m.OriginalLength)
                {
                    if (m.GeneratedLength != m.OriginalLength)
                    {
                        if (originalOffset == m.OriginalStart) return m.GeneratedStart;
                        int mapped = m.GeneratedStart + (int)Math.Round((double)(originalOffset - m.OriginalStart) / m.OriginalLength * m.GeneratedLength);
                        return Math.Clamp(mapped, m.GeneratedStart, m.GeneratedStart + m.GeneratedLength - 1);
                    }
                    return m.GeneratedStart + (originalOffset - m.OriginalStart);
                }
            }

            // The caret is past the last mapped token (e.g. directly after a '.', ')' or
            // whitespace the transformer did not copy verbatim). Generated offsets are not
            // comparable to original offsets, so land on the end of the nearest preceding
            // mapped token instead of returning a meaningless raw offset.
            SourceMapEntry? nearest = null;
            int nearestEnd = -1;
            foreach (var m in map.Mappings)
            {
                if (m.OriginalLength <= 0) continue;
                int originalEnd = m.OriginalStart + m.OriginalLength;
                if (originalEnd <= originalOffset && originalEnd > nearestEnd)
                {
                    nearest = m;
                    nearestEnd = originalEnd;
                }
            }

            if (nearest != null)
            {
                return nearest.GeneratedStart + nearest.GeneratedLength;
            }

            return Math.Clamp(originalOffset, 0, Math.Max(0, map.Mappings.Count == 0 ? originalOffset : map.Mappings[0].GeneratedStart));
        }
    }
}