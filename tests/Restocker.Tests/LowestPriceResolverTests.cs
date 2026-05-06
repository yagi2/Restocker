using Restocker.Market;

namespace Restocker.Tests;

public class LowestPriceResolverTests
{
    [Fact]
    public void Pick_empty_returns_zero()
        => Assert.Equal(0, LowestPriceResolver.Pick(System.Array.Empty<long>()));

    [Fact]
    public void Pick_single_returns_that_value()
        => Assert.Equal(500, LowestPriceResolver.Pick(new long[] { 500 }));

    [Fact]
    public void Pick_no_outliers_returns_first()
    {
        // tightly clustered prices, gap < 1.5x, so the first stays as the lowest.
        var prices = new long[] { 100, 105, 110, 115, 120 };
        Assert.Equal(100, LowestPriceResolver.Pick(prices));
    }

    [Fact]
    public void Pick_single_low_outlier_skipped()
    {
        // 50 is a shill outlier; next jump 50 -> 100 is 2x ≥ 1.5x → drop the 50.
        var prices = new long[] { 50, 100, 105, 110 };
        Assert.Equal(100, LowestPriceResolver.Pick(prices));
    }

    [Fact]
    public void Pick_two_low_outliers_skipped()
    {
        // 10, 11 are shill bait; 11 -> 100 is ~9x. With maxOutlierGroupSize=2, both drop.
        var prices = new long[] { 10, 11, 100, 105 };
        Assert.Equal(100, LowestPriceResolver.Pick(prices));
    }

    [Fact]
    public void Pick_outlier_group_capped_at_two()
    {
        // Three suspicious lows in a row. The default cap is 2; only the first 2 are eligible
        // to be discarded, so once we look at i=1 (11 -> 12 ratio ~1.0 < 1.5) we don't break out
        // and outlierEnd stays at 0. The very first price wins.
        var prices = new long[] { 10, 11, 12, 100, 105 };
        Assert.Equal(10, LowestPriceResolver.Pick(prices));
    }

    [Fact]
    public void Pick_zero_priced_entries_skipped()
    {
        // 0 means 'no data'; treated as outlier and dropped.
        var prices = new long[] { 0, 100, 110 };
        Assert.Equal(100, LowestPriceResolver.Pick(prices));
    }

    [Fact]
    public void Pick_unsorted_overload_sorts_first()
    {
        var prices = new long[] { 200, 50, 100, 105 };
        // 50 is the outlier → next bracket starts at 100.
        Assert.Equal(100, LowestPriceResolver.Pick(prices));
    }

    [Fact]
    public void Pick_custom_threshold_can_disable_outlier_drop()
    {
        // gapRatioThreshold=10 effectively turns off outlier rejection on a 2x gap.
        var prices = new long[] { 50, 100, 105 };
        Assert.Equal(50, LowestPriceResolver.Pick(prices, gapRatioThreshold: 10));
    }
}
