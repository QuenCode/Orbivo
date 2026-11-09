using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

public sealed class AmbisonicsLayoutTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 9)]
    [InlineData(3, 16)]
    public void GetChannelCount_IsOrderPlusOneSquared(int order, int expected)
    {
        Assert.Equal(expected, AmbisonicsLayout.GetChannelCount(order));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(9, 2)]
    [InlineData(16, 3)]
    public void TryGetOrder_RecognizesSupportedOrders(int channelCount, int expectedOrder)
    {
        Assert.True(AmbisonicsLayout.TryGetOrder(channelCount, out int order));
        Assert.Equal(expectedOrder, order);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(25)]
    public void TryGetOrder_RejectsOtherChannelCounts(int channelCount)
    {
        Assert.False(AmbisonicsLayout.TryGetOrder(channelCount, out _));
    }

    [Theory]
    // First order in ACN: W, Y, Z, X
    [InlineData(0, 0, 0)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 1, 3)]
    [InlineData(2, -2, 4)]
    [InlineData(3, 3, 15)]
    public void GetAcnIndex_FollowsAcnOrdering(int degree, int index, int expectedAcn)
    {
        Assert.Equal(expectedAcn, AmbisonicsLayout.GetAcnIndex(degree, index));
    }

    [Fact]
    public void GetAcnIndex_RejectsIndexOutsideDegree()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmbisonicsLayout.GetAcnIndex(1, 2));
    }
}
