namespace Orbivo.Core.Audio;

/// <summary>
/// Channel layout helpers for full-sphere Ambisonics in ACN channel ordering.
/// </summary>
public static class AmbisonicsLayout
{
    public const int MinSupportedOrder = 1;
    public const int MaxSupportedOrder = 3;

    /// <summary>Channels of the highest supported order (16): the most a track can carry into the ring buffer.</summary>
    public const int MaxChannels = (MaxSupportedOrder + 1) * (MaxSupportedOrder + 1);

    /// <summary>Number of channels for a full-sphere order: (order + 1)².</summary>
    public static int GetChannelCount(int order)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        return (order + 1) * (order + 1);
    }

    /// <summary>
    /// Maps a channel count to a supported order (4 → 1, 9 → 2, 16 → 3).
    /// The channel count alone is ambiguous (4 channels can also be quadraphonic);
    /// audio mode detection combines it with stream metadata.
    /// </summary>
    public static bool TryGetOrder(int channelCount, out int order)
    {
        for (int candidate = MinSupportedOrder; candidate <= MaxSupportedOrder; candidate++)
        {
            if (GetChannelCount(candidate) == channelCount)
            {
                order = candidate;
                return true;
            }
        }

        order = 0;
        return false;
    }

    /// <summary>Degree l of the ACN channel <paramref name="acn"/> (0 for W, 1 for Y, Z, X, ...).</summary>
    public static int GetDegree(int acn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(acn);
        return (int)Math.Sqrt(acn);
    }

    /// <summary>
    /// ACN index of the spherical harmonic with degree <paramref name="degree"/> and
    /// index <paramref name="index"/> (−degree ≤ index ≤ degree): ACN = degree² + degree + index.
    /// </summary>
    public static int GetAcnIndex(int degree, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
        if (index < -degree || index > degree)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between {-degree} and {degree}.");
        }

        return (degree * degree) + degree + index;
    }
}
