using DirectUpload.Server;

namespace DirectUpload.Server.Tests;

public class PartSizingTests
{
    private const long MiB = PartSizing.MiB;

    [Fact]
    public void Twelve_MiB_with_5_MiB_parts_is_three_parts_last_two_MiB()
    {
        var plan = PartSizing.Plan(12 * MiB, 5 * MiB);

        Assert.Equal(new PartPlan(5 * MiB, 3), plan);
        Assert.Equal(2 * MiB, plan.LastPartSize(12 * MiB));
    }

    [Fact]
    public void Zero_byte_file_is_one_empty_part()
    {
        var plan = PartSizing.Plan(0, 5 * MiB);

        Assert.Equal(1, plan.PartCount);
        Assert.Equal(0, plan.LastPartSize(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5 * MiB)]
    public void File_up_to_one_part_is_exactly_one_part(long size)
    {
        var plan = PartSizing.Plan(size, 5 * MiB);

        Assert.Equal(1, plan.PartCount);
        Assert.Equal(size, plan.LastPartSize(size));
    }

    [Fact]
    public void Exact_multiple_of_part_size_has_full_last_part()
    {
        var plan = PartSizing.Plan(15 * MiB, 5 * MiB);

        Assert.Equal(3, plan.PartCount);
        Assert.Equal(5 * MiB, plan.LastPartSize(15 * MiB));
    }

    [Fact]
    public void Part_size_below_S3_minimum_is_raised_to_5_MiB()
    {
        Assert.Equal(5 * MiB, PartSizing.Plan(100 * MiB, 1 * MiB).PartSize);
    }

    [Theory]
    [InlineData(100L * 1024 * MiB)]            // 100 GiB with 5 MiB parts would be 20,480 parts
    [InlineData(5L * 1024 * 1024 * MiB)]       // 5 TiB, the S3 object maximum
    [InlineData(10_000 * 5 * MiB + 1)]         // one byte over 10,000 minimum-size parts
    public void Large_files_grow_the_part_size_to_stay_within_10000_parts(long size)
    {
        var plan = PartSizing.Plan(size, 5 * MiB);

        Assert.InRange(plan.PartCount, 1, PartSizing.MaxParts);
        Assert.InRange(plan.PartSize, PartSizing.MinPartSize, PartSizing.MaxPartSize);
        Assert.Equal(0, plan.PartSize % MiB);
        Assert.True((long)plan.PartCount * plan.PartSize >= size);
        Assert.InRange(plan.LastPartSize(size), 1, plan.PartSize);
    }

    [Fact]
    public void Exactly_10000_minimum_parts_keeps_the_part_size()
    {
        Assert.Equal(new PartPlan(5 * MiB, 10_000), PartSizing.Plan(10_000 * 5 * MiB, 5 * MiB));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5L * 1024 * 1024 * MiB + 1)]
    public void Out_of_range_sizes_throw(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartSizing.Plan(size, 5 * MiB));
    }
}
