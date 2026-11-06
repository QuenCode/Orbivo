using FFmpeg.AutoGen;

namespace Orbivo.Media;

/// <summary>Turns FFmpeg error codes into exceptions with readable messages.</summary>
internal static unsafe class FFmpegError
{
    public static readonly int TryAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>Throws if <paramref name="result"/> is negative; returns it otherwise.</summary>
    public static int Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new FFmpegException(operation, result);
        }

        return result;
    }

    public static string Describe(int error)
    {
        const int BufferSize = 256;
        byte* buffer = stackalloc byte[BufferSize];
        if (ffmpeg.av_strerror(error, buffer, BufferSize) < 0)
        {
            return $"FFmpeg error {error}";
        }

        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer) ?? $"FFmpeg error {error}";
    }
}

/// <summary>An FFmpeg call failed.</summary>
public sealed class FFmpegException : Exception
{
    public FFmpegException(string operation, int errorCode)
        : base($"{operation} failed: {FFmpegError.Describe(errorCode)} ({errorCode}).")
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}

/// <summary>
/// A video cannot be played. <see cref="Exception.Message"/> is English for the log,
/// <see cref="OperatorMessage"/> German for the operator UI.
/// </summary>
public sealed class MediaOpenException : Exception
{
    public MediaOpenException(string message, string operatorMessage, Exception? innerException = null)
        : base(message, innerException)
    {
        OperatorMessage = operatorMessage;
    }

    public string OperatorMessage { get; }
}
