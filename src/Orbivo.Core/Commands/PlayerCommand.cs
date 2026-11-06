namespace Orbivo.Core.Commands;

/// <summary>
/// A command from the operator UI to the player (CLAUDE.md rule 3: UI → player only through commands).
/// Written into a <c>Channel&lt;PlayerCommand&gt;</c>, read by the player control thread.
/// </summary>
public abstract record PlayerCommand;

/// <summary>Opens a video file. The headset stays black until <see cref="PlayCommand"/> (F6).</summary>
/// <param name="Path">Full path of the video file.</param>
public sealed record OpenMediaCommand(string Path) : PlayerCommand;

/// <summary>Starts or resumes playback. After the end of the video it starts again from the beginning.</summary>
public sealed record PlayCommand : PlayerCommand;

/// <summary>Pauses playback; the headset keeps showing the current frame with head tracking.</summary>
public sealed record PauseCommand : PlayerCommand;

/// <summary>Stops playback, shows black and goes back to the beginning of the video.</summary>
public sealed record StopCommand : PlayerCommand;
