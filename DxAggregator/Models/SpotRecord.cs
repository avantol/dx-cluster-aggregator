namespace DxAggregator.Models;

public class SpotRecord
{
    public long Id { get; set; }
    public string DxCall { get; set; } = string.Empty;
    public double Frequency { get; set; }
    public string Band { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Spotter { get; set; } = string.Empty;
    public int? Snr { get; set; }
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? DxccEntity { get; set; }
    public int? DxccCode { get; set; }
    public string? Grid { get; set; }
    public double? DistanceKm { get; set; }
    public double? Bearing { get; set; }
    public bool? LotwUser { get; set; }
    public int DesirabilityScore { get; set; }
    public string? Comment { get; set; }

    /// <summary>
    /// Derives the amateur radio band from a frequency in kHz.
    /// </summary>
    public static string FrequencyToBand(double freqKhz)
    {
        return freqKhz switch
        {
            >= 1800 and < 2000 => "160m",
            >= 3500 and < 4000 => "80m",
            >= 5300 and < 5410 => "60m",
            >= 7000 and < 7300 => "40m",
            >= 10100 and < 10150 => "30m",
            >= 14000 and < 14350 => "20m",
            >= 18068 and < 18168 => "17m",
            >= 21000 and < 21450 => "15m",
            >= 24890 and < 24990 => "12m",
            >= 28000 and < 29700 => "10m",
            >= 50000 and < 54000 => "6m",
            >= 144000 and < 148000 => "2m",
            >= 420000 and < 450000 => "70cm",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Infers mode from frequency within known digital sub-bands.
    /// Returns null if mode cannot be determined from frequency alone.
    /// </summary>
    public static string? InferModeFromFrequency(double freqKhz)
    {
        // Common FT8 frequencies (dial frequencies)
        double[] ft8Freqs = { 1840, 3573, 5357, 7074, 10136, 14074, 18100, 21074, 24915, 28074, 50313 };
        foreach (var f in ft8Freqs)
        {
            if (Math.Abs(freqKhz - f) < 3)
                return "FT8";
        }

        // Common FT4 frequencies
        double[] ft4Freqs = { 3575.5, 7047.5, 10140, 14080, 18104, 21140, 24919, 28180 };
        foreach (var f in ft4Freqs)
        {
            if (Math.Abs(freqKhz - f) < 3)
                return "FT4";
        }

        return null;
    }
}
