namespace Orbivo.App;

/// <summary>
/// Command line options.
/// </summary>
/// <param name="NoXr">
/// <c>--no-xr</c>: render into a desktop window instead of the headset (debug viewport, from M1).
/// </param>
/// <param name="UnknownArguments">Arguments that were not recognized; they are logged as a warning.</param>
public sealed record StartupOptions(bool NoXr, IReadOnlyList<string> UnknownArguments)
{
    public const string NoXrSwitch = "--no-xr";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool noXr = false;
        var unknown = new List<string>();
        foreach (string arg in args)
        {
            if (string.Equals(arg, NoXrSwitch, StringComparison.OrdinalIgnoreCase))
            {
                noXr = true;
            }
            else
            {
                unknown.Add(arg);
            }
        }

        return new StartupOptions(noXr, unknown);
    }
}
