using Orbivo.Core.Concurrency;

namespace Orbivo.Core.Tests.Concurrency;

public sealed class TripleBufferIndexTests
{
    [Fact]
    public void NothingPublished_ReaderGetsNothing()
    {
        var buffer = new TripleBufferIndex();
        Assert.False(buffer.TryAcquireNewest());
        Assert.NotEqual(buffer.WriteIndex, buffer.ReadIndex);
    }

    [Fact]
    public void Reader_GetsTheNewestSlot_AndOnlyOnce()
    {
        var buffer = new TripleBufferIndex();
        int first = buffer.WriteIndex;
        buffer.Publish();
        int second = buffer.WriteIndex;
        buffer.Publish();

        Assert.True(buffer.TryAcquireNewest());
        Assert.Equal(second, buffer.ReadIndex);
        Assert.False(buffer.TryAcquireNewest());
        Assert.Equal(second, buffer.ReadIndex);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WriterAndReader_NeverShareASlot()
    {
        var buffer = new TripleBufferIndex();
        for (int i = 0; i < 100; i++)
        {
            Assert.NotEqual(buffer.WriteIndex, buffer.ReadIndex);
            buffer.Publish();
            Assert.NotEqual(buffer.WriteIndex, buffer.ReadIndex);
            if (i % 3 == 0)
            {
                buffer.TryAcquireNewest();
            }
        }
    }

    [Fact]
    public void UnderLoad_TheReaderNeverSeesAHalfWrittenOrOlderSlot()
    {
        var buffer = new TripleBufferIndex();
        long[] first = new long[TripleBufferIndex.SlotCount];
        long[] second = new long[TripleBufferIndex.SlotCount];
        const long count = 200_000;
        bool done = false;

        var writer = new Thread(() =>
        {
            for (long value = 1; value <= count; value++)
            {
                int slot = buffer.WriteIndex;
                first[slot] = value;
                Thread.SpinWait(1);
                second[slot] = value;
                buffer.Publish();
            }

            Volatile.Write(ref done, true);
        });

        long last = 0;
        long reads = 0;
        writer.Start();
        while (true)
        {
            bool finished = Volatile.Read(ref done);
            if (buffer.TryAcquireNewest())
            {
                int slot = buffer.ReadIndex;
                long a = first[slot];
                Thread.SpinWait(1);
                long b = second[slot];
                Assert.Equal(a, b);
                Assert.True(a > last, $"slot value {a} after {last}");
                last = a;
                reads++;
            }
            else if (finished)
            {
                break;
            }
        }

        writer.Join();
        Assert.True(reads > 0);
        Assert.Equal(count, last);
    }
}
