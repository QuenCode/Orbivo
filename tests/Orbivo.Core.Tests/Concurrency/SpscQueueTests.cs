using Orbivo.Core.Concurrency;

namespace Orbivo.Core.Tests.Concurrency;

public sealed class SpscQueueTests
{
    [Fact]
    public void Capacity_IsRoundedUpToAPowerOfTwo()
    {
        Assert.Equal(8, new SpscQueue<int>(6).Capacity);
        Assert.Equal(1, new SpscQueue<int>(1).Capacity);
    }

    [Fact]
    public void Elements_ComeOutInOrder_AndTheQueueRejectsWhenFull()
    {
        var queue = new SpscQueue<int>(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(queue.TryEnqueue(i));
        }

        Assert.False(queue.TryEnqueue(99));
        Assert.Equal(4, queue.Count);

        Assert.True(queue.TryPeek(out int peeked));
        Assert.Equal(0, peeked);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(queue.TryDequeue(out int value));
            Assert.Equal(i, value);
        }

        Assert.False(queue.TryDequeue(out _));
        Assert.False(queue.TryPeek(out _));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void WrapsAroundManyTimes()
    {
        var queue = new SpscQueue<long>(2);
        for (long i = 0; i < 1000; i++)
        {
            Assert.True(queue.TryEnqueue(i));
            Assert.True(queue.TryDequeue(out long value));
            Assert.Equal(i, value);
        }
    }

    [Fact]
    public void TwoThreads_TransferEveryElementOnceAndInOrder()
    {
        const int Count = 200_000;
        var queue = new SpscQueue<(int Value, int Check)>(8);
        var producer = new Thread(() =>
        {
            for (int i = 0; i < Count; i++)
            {
                while (!queue.TryEnqueue((i, ~i)))
                {
                    Thread.SpinWait(1);
                }
            }
        });

        producer.Start();
        int expected = 0;
        while (expected < Count)
        {
            if (queue.TryDequeue(out (int Value, int Check) item))
            {
                Assert.Equal(expected, item.Value);
                Assert.Equal(~expected, item.Check);
                expected++;
            }
        }

        producer.Join();
        Assert.Equal(0, queue.Count);
    }
}
