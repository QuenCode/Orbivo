using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

public sealed class AudioRingBufferTests
{
    [Fact]
    public void Capacity_IsRoundedUpToAPowerOfTwo()
    {
        using var ring = new AudioRingBuffer(2, 1000);
        Assert.Equal(1024, ring.CapacityFrames);
        Assert.Equal(1024, ring.FreeFrames);
    }

    [Fact]
    public void ReadPosition_FollowsTheStartAndTheFramesRead()
    {
        using var ring = new AudioRingBuffer(2, 16);
        int generation = ring.RequestFlush();
        Assert.True(ring.FlushPending);
        ring.AcknowledgeFlush();
        Assert.True(ring.IsFlushed(generation));

        ring.Start(48_000, 48_000);
        Assert.Equal(4, ring.Write([1, -1, 2, -2, 3, -3, 4, -4]));
        Assert.True(ring.HasStart);
        Assert.Equal(48_000, ring.ReadPosition);
        Assert.Equal(48_000, ring.SampleRate);
        Assert.Equal(48_004, ring.WritePosition);

        Span<float> buffer = stackalloc float[4];
        Assert.Equal(2, ring.Read(buffer, 2));
        Assert.Equal([1f, -1f, 2f, -2f], buffer.ToArray());
        Assert.Equal(48_002, ring.ReadPosition);

        Assert.Equal(1, ring.Skip(1));
        Assert.Equal(48_003, ring.ReadPosition);
        Assert.Equal(1, ring.Read(buffer, 4));
        Assert.Equal(4f, buffer[0]);
        Assert.Equal(0, ring.AvailableFrames);
    }

    [Fact]
    public void Generation_WithFewerChannels_KeepsItsFramesApart_AndWraps()
    {
        using var ring = new AudioRingBuffer(16, 4);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000, 4);
        Assert.Equal(4, ring.FrameChannels);
        Assert.Equal(3, ring.Write([0, 1, 2, 3, 10, 11, 12, 13, 20, 21, 22, 23]));

        Span<float> buffer = stackalloc float[8];
        Assert.Equal(2, ring.Read(buffer, 2));
        Assert.Equal([0f, 1f, 2f, 3f, 10f, 11f, 12f, 13f], buffer.ToArray());

        // Past the end of the slots: frames 4 and 5 wrap around.
        Assert.Equal(3, ring.Write([30, 31, 32, 33, 40, 41, 42, 43, 50, 51, 52, 53]));
        Span<float> rest = stackalloc float[16];
        Assert.Equal(4, ring.Read(rest, 4));
        Assert.Equal([20f, 21f, 22f, 23f, 30f, 31f, 32f, 33f, 40f, 41f, 42f, 43f, 50f, 51f, 52f, 53f], rest.ToArray());
    }

    [Fact]
    public void EachGeneration_HasItsOwnChannelCount()
    {
        using var ring = new AudioRingBuffer(16, 8);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000, 2);
        Assert.Equal(1, ring.Write([1, -1]));

        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(100, 48_000, 9);
        Assert.Equal(9, ring.FrameChannels);
        float[] frame = [.. Enumerable.Range(0, 9).Select(i => (float)i)];
        Assert.Equal(1, ring.Write(frame));
        Assert.Equal(1, ring.WriteSilence(1));

        var buffer = new float[18];
        Assert.Equal(100, ring.ReadPosition);
        Assert.Equal(2, ring.Read(buffer, 2));
        Assert.Equal(frame, buffer[..9]);
        Assert.All(buffer[9..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Start_WithMoreChannelsThanTheSlots_Throws()
    {
        using var ring = new AudioRingBuffer(2, 8);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Start(0, 48_000, 4));
    }

    [Fact]
    public void Write_StopsWhenFull_AndWrapsAround()
    {
        using var ring = new AudioRingBuffer(1, 4);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000);
        Assert.Equal(4, ring.Write([1, 2, 3, 4, 5, 6]));
        Assert.Equal(0, ring.FreeFrames);

        Span<float> buffer = stackalloc float[3];
        Assert.Equal(3, ring.Read(buffer, 3));
        Assert.Equal(3, ring.Write([5, 6, 7]));
        Span<float> rest = stackalloc float[4];
        Assert.Equal(4, ring.Read(rest, 4));
        Assert.Equal([4f, 5f, 6f, 7f], rest.ToArray());
    }

    [Fact]
    public void Flush_DropsOldData_AndTheNewGenerationStartsElsewhere()
    {
        using var ring = new AudioRingBuffer(1, 8);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000);
        ring.Write([1, 2, 3]);

        int generation = ring.RequestFlush();
        Assert.False(ring.IsFlushed(generation));

        // Until the consumer acknowledges, the old data stays readable (for the fade-out).
        Assert.True(ring.HasStart);
        Assert.Equal(3, ring.AvailableFrames);
        Span<float> old = stackalloc float[1];
        Assert.Equal(1, ring.Read(old, 1));
        Assert.Equal(1f, old[0]);

        ring.AcknowledgeFlush();
        Assert.Equal(generation, ring.Generation);
        Assert.False(ring.HasStart);

        ring.Start(1000, 48_000);
        ring.Write([9]);
        Assert.Equal(1000, ring.ReadPosition);
        Span<float> buffer = stackalloc float[1];
        Assert.Equal(1, ring.Read(buffer, 1));
        Assert.Equal(9f, buffer[0]);
    }

    [Fact]
    public void EndOfData_OnlyAfterTheEndIsMarkedAndAllIsRead()
    {
        using var ring = new AudioRingBuffer(1, 8);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000);
        ring.Write([1, 2]);
        Assert.False(ring.EndOfData);
        ring.MarkEnd();
        Assert.False(ring.EndOfData);
        ring.Skip(2);
        Assert.True(ring.EndOfData);

        ring.RequestFlush();
        ring.AcknowledgeFlush();
        Assert.False(ring.EndOfData);
    }

    [Fact]
    public void WriteSilence_WritesZeros()
    {
        using var ring = new AudioRingBuffer(2, 8);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000);
        ring.Write([5, 5, 5, 5]);
        Assert.Equal(2, ring.WriteSilence(2));
        Span<float> buffer = stackalloc float[8];
        buffer.Fill(9);
        Assert.Equal(4, ring.Read(buffer, 4));
        Assert.Equal([5f, 5f, 5f, 5f, 0f, 0f, 0f, 0f], buffer.ToArray());
    }

    [Fact]
    public async Task ProducerAndConsumer_OnTwoThreads_TransferEveryFrameInOrder()
    {
        const int total = 2_000_000;
        using var ring = new AudioRingBuffer(2, 1024);
        ring.RequestFlush();
        ring.AcknowledgeFlush();
        ring.Start(0, 48_000);

        Task producer = Task.Run(() =>
        {
            var chunk = new float[2 * 333];
            int next = 0;
            while (next < total)
            {
                int frames = Math.Min(333, total - next);
                for (int i = 0; i < frames; i++)
                {
                    chunk[2 * i] = next + i;
                    chunk[(2 * i) + 1] = -(next + i);
                }

                int offset = 0;
                while (offset < frames)
                {
                    int written = ring.Write(chunk.AsSpan(2 * offset, 2 * (frames - offset)));
                    offset += written;
                    if (written == 0)
                    {
                        ring.WaitForConsumer(TimeSpan.FromMilliseconds(1));
                    }
                }

                next += frames;
            }
        }, TestContext.Current.CancellationToken);

        var buffer = new float[2 * 500];
        long expected = 0;
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (expected < total && timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            Assert.Equal(expected, ring.ReadPosition);
            int read = ring.Read(buffer, 500);
            for (int i = 0; i < read; i++)
            {
                Assert.Equal(expected + i, buffer[2 * i]);
                Assert.Equal(-(expected + i), buffer[(2 * i) + 1]);
            }

            expected += read;
        }

        await producer;
        Assert.Equal(total, expected);
    }
}
