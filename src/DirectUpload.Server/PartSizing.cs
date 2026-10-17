namespace DirectUpload.Server;

/// <summary>How a file is split into multipart-upload parts.</summary>
/// <param name="PartSize">Size of every part except possibly the last.</param>
/// <param name="PartCount">Number of parts (at least 1, at most 10,000).</param>
public readonly record struct PartPlan(long PartSize, int PartCount)
{
    /// <summary>Size of the last part for a file of <paramref name="fileSize"/> bytes.</summary>
    public long LastPartSize(long fileSize) => fileSize - (PartCount - 1) * PartSize;
}

/// <summary>S3 multipart limits and the part-size calculation that respects them.</summary>
public static class PartSizing
{
    /// <summary>One mebibyte.</summary>
    public const long MiB = 1024 * 1024;

    /// <summary>S3 minimum part size (all parts except the last).</summary>
    public const long MinPartSize = 5 * MiB;

    /// <summary>S3 maximum part size.</summary>
    public const long MaxPartSize = 5L * 1024 * MiB;

    /// <summary>S3 maximum number of parts per upload.</summary>
    public const int MaxParts = 10_000;

    /// <summary>S3 maximum object size (5 TiB).</summary>
    public const long MaxObjectSize = 5L * 1024 * 1024 * MiB;

    /// <summary>
    /// Plans the parts for a file. The preferred part size is clamped to the S3 minimum and grown
    /// (in whole MiB) when the file would otherwise need more than 10,000 parts. A zero-byte file
    /// becomes a single empty part.
    /// </summary>
    public static PartPlan Plan(long fileSize, long preferredPartSize)
    {
        if (fileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(fileSize), "File size cannot be negative.");
        if (fileSize > MaxObjectSize)
            throw new ArgumentOutOfRangeException(nameof(fileSize), "File exceeds the S3 maximum object size of 5 TiB.");

        var partSize = Math.Clamp(preferredPartSize, MinPartSize, MaxPartSize);
        if (CeilDiv(fileSize, partSize) > MaxParts)
            partSize = CeilDiv(CeilDiv(fileSize, MaxParts), MiB) * MiB;

        var partCount = fileSize == 0 ? 1 : (int)CeilDiv(fileSize, partSize);
        return new PartPlan(partSize, partCount);
    }

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;
}
