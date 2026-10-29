namespace PackCheck.App.Infrastructure;

/// <summary>What this PC can actually give a server: sliders never go past the installed RAM or the logical cores.</summary>
public static class SystemLimits
{
    /// <summary>Installed memory in GB (one decimal), as Windows reports it to .NET.</summary>
    public static double RamGb { get; } = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1);

    /// <summary>Whole gigabytes a server may get: the installed RAM rounded down, at most 256 GB.</summary>
    public static int MaxRamGb { get; } = Math.Clamp((int)Math.Floor(RamGb), 1, 256);

    /// <summary>Logical processors of this PC, at most 64.</summary>
    public static int MaxCores { get; } = Math.Clamp(Environment.ProcessorCount, 1, 64);
}
