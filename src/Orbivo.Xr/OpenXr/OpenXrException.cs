using Silk.NET.OpenXR;

namespace Orbivo.Xr.OpenXr;

/// <summary>An OpenXR call returned an error code.</summary>
public sealed class OpenXrException : Exception
{
    public OpenXrException(string call, Result result)
        : base($"{call} failed: {result} ({(int)result}).")
    {
        Call = call;
        Result = result;
    }

    public OpenXrException()
        : base("OpenXR call failed.")
    {
        Call = string.Empty;
    }

    public OpenXrException(string message)
        : base(message)
    {
        Call = string.Empty;
    }

    public OpenXrException(string message, Exception innerException)
        : base(message, innerException)
    {
        Call = string.Empty;
    }

    /// <summary>Name of the failed function, e.g. <c>xrCreateSession</c>.</summary>
    public string Call { get; }

    public Result Result { get; }

    /// <summary>
    /// True if waiting and trying again can help: runtime not running (Varjo Base closed), headset unplugged,
    /// instance or session lost (docs/ARCHITECTURE.md, "OpenXR-Lebenszyklus").
    /// </summary>
    public bool IsTransient => XrResults.IsTransient(Result);
}

/// <summary>Helpers for <see cref="Result"/> codes.</summary>
internal static class XrResults
{
    /// <summary>Throws <see cref="OpenXrException"/> for error codes (negative values). Success codes pass.</summary>
    public static void Check(this Result result, string call)
    {
        if (result < 0)
        {
            throw new OpenXrException(call, result);
        }
    }

    public static bool IsTransient(Result result)
    {
        return result is Result.ErrorRuntimeUnavailable
            or Result.ErrorRuntimeFailure
            or Result.ErrorFormFactorUnavailable
            or Result.ErrorInstanceLost
            or Result.ErrorSessionLost
            or Result.SessionLossPending;
    }
}
