using System.Text.RegularExpressions;

namespace DxAggregator.Services;

public class CtyEntity
{
    public string Name { get; set; } = "";
    public int CqZone { get; set; }
    public int ItuZone { get; set; }
    public string Continent { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }  // standard: positive=East
    public double GmtOffset { get; set; }
    public string PrimaryPrefix { get; set; } = "";
}

public class CtyParser
{
    private readonly ILogger<CtyParser> _logger;
    private readonly Dictionary<string, (double Lat, double Lon, string Entity)> _exactMatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Lat, double Lon, string Entity)> _prefixDict = new(StringComparer.OrdinalIgnoreCase);
    private int _maxPrefixLength;

    public int EntityCount { get; private set; }
    public int PrefixCount => _prefixDict.Count + _exactMatches.Count;

    public CtyParser(ILogger<CtyParser> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        var path = Path.Combine(env.WebRootPath, "cty.dat");
        if (File.Exists(path))
        {
            Load(path);
            _logger.LogInformation("CtyParser loaded {Entities} entities, {Prefixes} prefixes from cty.dat",
                EntityCount, PrefixCount);
        }
        else
        {
            _logger.LogWarning("cty.dat not found at {Path} — callsign distance lookup will be unavailable", path);
        }
    }

    private void Load(string path)
    {
        var lines = File.ReadAllLines(path);
        CtyEntity? currentEntity = null;
        var prefixAccumulator = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!char.IsWhiteSpace(line[0]))
            {
                // Header line — flush previous entity
                if (currentEntity != null && prefixAccumulator.Length > 0)
                    ProcessPrefixes(currentEntity, prefixAccumulator);

                currentEntity = ParseHeader(line);
                if (currentEntity != null) EntityCount++;
                prefixAccumulator = "";
            }
            else
            {
                // Prefix continuation line
                prefixAccumulator += line.Trim();
            }
        }

        // Flush last entity
        if (currentEntity != null && prefixAccumulator.Length > 0)
            ProcessPrefixes(currentEntity, prefixAccumulator);

        _maxPrefixLength = _prefixDict.Keys.Count > 0
            ? _prefixDict.Keys.Max(k => k.Length)
            : 0;
    }

    private CtyEntity? ParseHeader(string line)
    {
        var parts = line.Split(':');
        if (parts.Length < 8) return null;

        try
        {
            var entity = new CtyEntity
            {
                Name = parts[0].Trim(),
                CqZone = int.Parse(parts[1].Trim()),
                ItuZone = int.Parse(parts[2].Trim()),
                Continent = parts[3].Trim(),
                Latitude = double.Parse(parts[4].Trim()),
                // cty.dat: positive=West, negative=East — negate to standard
                Longitude = -double.Parse(parts[5].Trim()),
                GmtOffset = double.Parse(parts[6].Trim()),
                PrimaryPrefix = parts[7].Trim().TrimStart('*')
            };
            return entity;
        }
        catch
        {
            return null;
        }
    }

    private void ProcessPrefixes(CtyEntity entity, string prefixText)
    {
        // Remove trailing semicolon
        prefixText = prefixText.TrimEnd(';').Trim();
        if (string.IsNullOrEmpty(prefixText)) return;

        var entries = prefixText.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (string.IsNullOrEmpty(entry)) continue;

            // Parse overrides
            double lat = entity.Latitude;
            double lon = entity.Longitude;

            // <lat/lon> override — note: lon in override is also inverted
            var latLonMatch = Regex.Match(entry, @"<([^/]+)/([^>]+)>");
            if (latLonMatch.Success)
            {
                if (double.TryParse(latLonMatch.Groups[1].Value, out var oLat) &&
                    double.TryParse(latLonMatch.Groups[2].Value, out var oLon))
                {
                    lat = oLat;
                    lon = -oLon; // negate: cty.dat positive=West
                }
            }

            // Strip all override markers to get bare prefix
            var bare = entry;
            bare = Regex.Replace(bare, @"\(\d+\)", "");       // (CQ)
            bare = Regex.Replace(bare, @"\[\d+\]", "");       // [ITU]
            bare = Regex.Replace(bare, @"<[^>]+>", "");       // <lat/lon>
            bare = Regex.Replace(bare, @"\{[A-Z]{2}\}", "");  // {continent}
            bare = Regex.Replace(bare, @"~[^~]+~", "");       // ~offset~
            bare = bare.Trim();

            if (string.IsNullOrEmpty(bare)) continue;

            var location = (lat, lon, entity.Name);

            if (bare.StartsWith('='))
            {
                // Exact match callsign
                var call = bare[1..];
                _exactMatches.TryAdd(call, location);
            }
            else
            {
                // Prefix match — keep the longest (most specific) if duplicate
                if (!_prefixDict.ContainsKey(bare))
                    _prefixDict[bare] = location;
            }
        }
    }

    public (double Lat, double Lon, string Entity)? LookupCallsign(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return null;

        callsign = callsign.ToUpperInvariant().Trim();

        // 1. Exact match
        if (_exactMatches.TryGetValue(callsign, out var exact))
            return exact;

        // 2. Strip portable suffixes: /P, /M, /QRP, /MM, etc.
        var bareCall = StripSuffix(callsign);

        // 3. Longest prefix match
        var maxLen = Math.Min(bareCall.Length, _maxPrefixLength);
        for (int len = maxLen; len >= 1; len--)
        {
            if (_prefixDict.TryGetValue(bareCall[..len], out var pfx))
                return pfx;
        }

        return null;
    }

    private static string StripSuffix(string call)
    {
        // Handle /P, /M, /MM, /AM, /QRP etc. at end
        var slashIdx = call.LastIndexOf('/');
        if (slashIdx > 0)
        {
            var suffix = call[(slashIdx + 1)..];
            // If the part after / is short (1-3 chars), it's likely a suffix — strip it
            // If it's longer, it might be a prefix (e.g., W1/G3ABC) — strip the prefix part instead
            if (suffix.Length <= 3)
                return call[..slashIdx];
            else
                return suffix; // The callsign is after the slash
        }
        return call;
    }

    // --- Geo Math ---

    public static (double Lat, double Lon)? GridToLatLon(string? grid)
    {
        if (string.IsNullOrWhiteSpace(grid) || grid.Length < 4) return null;
        grid = grid.ToUpperInvariant();

        if (grid[0] < 'A' || grid[0] > 'R' || grid[1] < 'A' || grid[1] > 'R') return null;
        if (grid[2] < '0' || grid[2] > '9' || grid[3] < '0' || grid[3] > '9') return null;

        double lon = (grid[0] - 'A') * 20.0 - 180.0;
        double lat = (grid[1] - 'A') * 10.0 - 90.0;
        lon += (grid[2] - '0') * 2.0;
        lat += (grid[3] - '0') * 1.0;

        if (grid.Length >= 6 && grid[4] >= 'A' && grid[4] <= 'X' && grid[5] >= 'A' && grid[5] <= 'X')
        {
            lon += (grid[4] - 'A') * (2.0 / 24.0);
            lat += (grid[5] - 'A') * (1.0 / 24.0);
            lon += 1.0 / 24.0;  // center of subsquare
            lat += 0.5 / 24.0;
        }
        else
        {
            lon += 1.0;   // center of square
            lat += 0.5;
        }

        return (lat, lon);
    }

    public static string LatLonToGrid(double lat, double lon)
    {
        lon += 180.0;
        lat += 90.0;

        // Clamp
        lon = Math.Max(0, Math.Min(lon, 359.999));
        lat = Math.Max(0, Math.Min(lat, 179.999));

        var f1 = (char)('A' + (int)(lon / 20));
        var f2 = (char)('A' + (int)(lat / 10));
        var s1 = (int)((lon % 20) / 2);
        var s2 = (int)((lat % 10) / 1);
        var sub1 = (char)('a' + (int)(((lon % 20) % 2) / (2.0 / 24)));
        var sub2 = (char)('a' + (int)(((lat % 10) % 1) / (1.0 / 24)));

        return $"{f1}{f2}{s1}{s2}{sub1}{sub2}";
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRad(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2)) -
                Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(dLon);
        var brng = Math.Atan2(y, x);
        return (ToDeg(brng) + 360) % 360;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
