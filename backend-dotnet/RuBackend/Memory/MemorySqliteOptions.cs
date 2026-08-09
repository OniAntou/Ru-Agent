namespace RuBackend.Memory;

public sealed class MemorySqliteOptions
{
    public string DatabasePath { get; init; } = "memory.db";
    public string LegacyTextsPath { get; init; } = "memory_texts.json";
    public int RetrieveScanLimit { get; init; } = 2000;
    public double RetrieveMinScore { get; init; } = 0.18;
    public int RetrieveLookbackDays { get; init; } = 90;
    public int DefaultContextMaxChars { get; init; } = 1200;
}
