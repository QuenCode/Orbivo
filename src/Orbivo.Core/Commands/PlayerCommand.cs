namespace Orbivo.Core.Commands;

/// <summary>
/// A command from the operator UI to the player (CLAUDE.md rule 3: UI → player only through commands).
/// Written into a <c>Channel&lt;PlayerCommand&gt;</c>, read by the player control thread.
/// </summary>
public abstract record PlayerCommand;

/// <summary>Opens a video file. The headset stays black until <see cref="PlayCommand"/> (F6).</summary>
/// <param name="Path">Full path of the video file.</param>
/// <param name="Play">True to start playback as soon as the video is ready (playlist: next video while playing).</param>
public sealed record OpenMediaCommand(string Path, bool Play = false) : PlayerCommand;

/// <summary>Starts or resumes playback. After the end of the video it starts again from the beginning.</summary>
public sealed record PlayCommand : PlayerCommand;

/// <summary>Pauses playback; the headset keeps showing the current frame with head tracking.</summary>
public sealed record PauseCommand : PlayerCommand;

/// <summary>Stops playback, shows black and goes back to the beginning of the video.</summary>
public sealed record StopCommand : PlayerCommand;

/// <summary>
/// Jumps to <paramref name="Position"/> (time from the start of the video). The headset keeps the last frame until the
/// new one is decoded; the transport state stays (Ended becomes Paused). Seeks in quick succession are merged.
/// </summary>
/// <param name="Position">Target time; clamped to the video.</param>
public sealed record SeekCommand(TimeSpan Position) : PlayerCommand;

/// <summary>Plays the current video in a loop (F3): at the end it jumps back to the start instead of stopping.</summary>
public sealed record SetLoopCommand(bool Enabled) : PlayerCommand;

/// <summary>Switches the headset to black at once, or back to the picture (F5). Independent of the transport state.</summary>
public sealed record SetBlackoutCommand(bool Black) : PlayerCommand;

/// <summary>Fades the headset to black or back to the picture (F5). Independent of the transport state.</summary>
/// <param name="ToBlack">True: fade out to black. False: fade in to the picture.</param>
/// <param name="Duration">Time for a full fade; a fade from halfway takes half of it.</param>
public sealed record FadeCommand(bool ToBlack, TimeSpan Duration) : PlayerCommand;
