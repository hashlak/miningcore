using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ShareCreditTests
{
    private const double Network = 7000d;

    [Theory]
    [InlineData(0.02, 0.02)]                     // an ordinary share keeps its difficulty
    [InlineData(7000, 7000)]                     // exactly the network difficulty
    [InlineData(7000.5, 7000)]                   // just above
    [InlineData(1e30, 7000)]                     // d=1000000000000000000000000000000
    [InlineData(double.MaxValue, 7000)]          // the largest "d=" that parses
    [InlineData(double.PositiveInfinity, 7000)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    public void Difficulty_never_exceeds_the_network(double stratum, double expected)
    {
        Assert.Equal(expected, ShareCredit.Difficulty(stratum, Network));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Difficulty_credits_nothing_on_a_bad_network_difficulty(double network)
    {
        Assert.Equal(0d, ShareCredit.Difficulty(1, network));
    }

    [Theory]
    [InlineData(1e30)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    public void Score_is_at_most_one_and_never_overflows_decimal(double difficulty)
    {
        Assert.Equal(1m, ShareCredit.Score(difficulty, Network));
    }

    [Fact]
    public void Score_is_the_fraction_of_a_block()
    {
        Assert.Equal(0.5m, ShareCredit.Score(3500, Network));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Score_is_zero_on_a_bad_network_difficulty(double network)
    {
        Assert.Equal(0m, ShareCredit.Score(1, network));
    }

    [Fact]
    public void Score_is_zero_on_a_nan_difficulty()
    {
        Assert.Equal(0m, ShareCredit.Score(double.NaN, Network));
    }

    [Theory]
    [InlineData(100, 500d, 100)]     // below maxDiff
    [InlineData(500, 500d, 500)]     // at maxDiff
    [InlineData(1e30, 500d, 500)]    // above maxDiff
    public void StaticDifficulty_stays_within_maxDiff(double requested, double maxDiff, double expected)
    {
        Assert.Equal(expected, ShareCredit.StaticDifficulty(requested, maxDiff));
    }

    [Fact]
    public void StaticDifficulty_is_unchanged_on_a_port_without_maxDiff()
    {
        Assert.Equal(1e30, ShareCredit.StaticDifficulty(1e30, null));
    }
}
