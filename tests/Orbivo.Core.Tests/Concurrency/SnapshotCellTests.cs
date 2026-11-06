using Orbivo.Core.Concurrency;

namespace Orbivo.Core.Tests.Concurrency;

public sealed class SnapshotCellTests
{
    [Fact]
    public void Read_ReturnsTheInitialAndThenTheLatestValue()
    {
        var cell = new SnapshotCell<Pair>(new Pair(1, -1));
        Assert.Equal(new Pair(1, -1), cell.Read());
        for (long i = 2; i < 20; i++)
        {
            cell.Write(new Pair(i, -i));
            Assert.Equal(new Pair(i, -i), cell.Read());
        }
    }

    [Fact]
    public async Task ConcurrentReads_NeverSeeATornValue()
    {
        var cell = new SnapshotCell<Pair>(new Pair(0, 0));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        long reads = 0;
        Task reader = Task.Run(() =>
        {
            long last = 0;
            while (!cancel.IsCancellationRequested)
            {
                Pair value = cell.Read();
                Assert.Equal(value.A, -value.B);
                Assert.True(value.A >= last, "values went backwards");
                last = value.A;
                reads++;
            }
        }, TestContext.Current.CancellationToken);

        long next = 1;
        while (!cancel.IsCancellationRequested)
        {
            cell.Write(new Pair(next, -next));
            next++;
        }

        await reader;
        Assert.True(reads > 1000);
    }

    [Fact]
    public void ReadAndWrite_DoNotAllocate()
    {
        var cell = new SnapshotCell<Pair>(new Pair(0, 0));
        cell.Write(new Pair(1, 1));
        _ = cell.Read();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            cell.Write(new Pair(i, i));
            _ = cell.Read();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private readonly record struct Pair(long A, long B);
}
