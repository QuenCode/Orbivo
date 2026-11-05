namespace Orbivo.App;

/// <summary>
/// Command line options.
/// </summary>
/// <param name="NoXr">
/// <c>--no-xr</c>: render into a desktop window instead of the headset (debug viewport, from M1).
/// </param>
/// <param name="ImagePath">
/// <c>--image &lt;path&gt;</c>: equirect 360 mono still image for the headset or the debug viewport. Relative to the current directory.
/// </param>
/// <param name="UnknownArguments">Arguments that were not recognized; they are logged as a warning.</param>
public sealed record StartupOptions(bool NoXr, string? ImagePath, IReadOnlyList<string> UnknownArguments)
{
    public const string NoXrSwitch = "--no-xr";
    public const string ImageOption = "--image";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool noXr = false;
        string? imagePath = null;
        var unknown = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, NoXrSwitch, StringComparison.OrdinalIgnoreCase))
            {
                noXr = true;
            }
            else if (string.Equals(arg, ImageOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    imagePath = args[++i];
                }
                else
                {
                    unknown.Add($"{ImageOption} (path missing)");
                }
            }
            else
            {
                unknown.Add(arg);
            }
        }

        return new StartupOptions(noXr, imagePath, unknown);
    }
}
