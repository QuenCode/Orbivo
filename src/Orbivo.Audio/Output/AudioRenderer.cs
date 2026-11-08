using Orbivo.Audio.Spatial;
using Orbivo.Core.Audio;

namespace Orbivo.Audio.Output;

/// <summary>Transport state of the sound on the audio output thread.</summary>
internal enum RendererState
{
    /// <summary>Nothing plays; the device gets silence. Ring flushes are acknowledged here.</summary>
    Silent,

    /// <summary>Sound plays (also while it fades in, and while it dips for a ring flush).</summary>
    Playing,

    /// <summary>Pause: fading out, the clock stops at the end of the fade.</summary>
    FadingToPause,

    /// <summary>Hold (seek, stop, new file): fading out the old sound, the clock already stands.</summary>
    FadingToHold,
}

/// <summary>
/// Produces the sound of one device stream period by period (docs/ARCHITECTURE.md, "Audio"): reads the ring buffer at
/// the media position that has to be heard, applies transport fades and the output gain, and drives the audio clock.
/// Audio output thread only; never blocks, never allocates (CLAUDE.md rule 2). Deterministic, so it is tested without
/// a device.
/// </summary>
/// <remarks>
/// Stream frames (written to the device, from 0 per stream) and media frames (media position at the stream rate) move
/// together while sound plays: each written frame is the next media frame, whether the ring had data for it or not.
/// The clock therefore follows the device, never the decoder: a late decoder means a dropout, not a sync error.
/// <para>
/// Transport changes take effect with a <see cref="TransportRamp"/> of 5 ms. The clock starts when the first frame of
/// a fade-in is heard and stops when the last frame of a pause fade-out is heard; a hold stops it at once.
/// </para>
/// <para>
/// Sound fixed to the picture (M7) goes through a <see cref="SpatialStage"/>, which delays it by one block. The ring is
/// then read that many frames ahead, so the frame written for media frame <c>m</c> is still the sound of <c>m</c>.
/// Switching between fixed and plain stereo while sound plays dips through silence like a ring flush.
/// </para>
/// </remarks>
internal sealed class AudioRenderer
{
    private readonly AudioRingBuffer _ring;
    private readonly AudioPlaybackClock _clock;
    private readonly long _timestampFrequency;

    private AudioClockTracker _tracker;
    private TransportRamp _ramp;
    private GainSmoother _gain;
    private int _sampleRate;

    private long _written;
    private long _mediaFrame;
    private long _heldMediaTicks;
    private bool _pendingPlay;
    private int _holdSequence;

    // Sound fixed to the picture (M7).
    private SpatialStage? _stage;
    private float[] _primeBuffer = [];
    private StereoPlacement _placement = StereoPlacement.HeadLocked;
    private bool _spatialActive;
    private bool _placementPending;
    private bool _needsPrime;

    /// <param name="ring">Decoded sound (2 channels).</param>
    /// <param name="clock">The clock this renderer publishes.</param>
    /// <param name="sampleRate">Rate of the first device stream.</param>
    /// <param name="timestampFrequency">Stopwatch ticks per second.</param>
    public AudioRenderer(AudioRingBuffer ring, AudioPlaybackClock clock, int sampleRate, long timestampFrequency)
    {
        if (ring.Channels != 2)
        {
            throw new ArgumentException("The renderer plays stereo.", nameof(ring));
        }

        _ring = ring;
        _clock = clock;
        _timestampFrequency = timestampFrequency;
        _sampleRate = sampleRate;
        _tracker = new AudioClockTracker(sampleRate, timestampFrequency);
        _ramp = new TransportRamp(sampleRate);
        _gain = new GainSmoother(sampleRate, 0f);
        Publish();
    }

    public RendererState State { get; private set; }

    public int SampleRate => _sampleRate;

    /// <summary>Stream frames written so far (the stream frame of the next frame).</summary>
    public long WrittenFrames => _written;

    /// <summary>Frames of silence played while sound should have played (the decoder was late).</summary>
    public long UnderrunFrames { get; private set; }

    /// <summary>True if the ring holds sound of another sample rate than the stream (the decoder must reopen).</summary>
    public bool RateMismatch { get; private set; }

    public AudioClockTracker Tracker => _tracker;

    /// <summary>A play is waiting for the end of a fade-out or for the next period.</summary>
    public bool PlayPending => _pendingPlay;

    /// <summary>The sound is fixed to the picture right now (stage present and chosen).</summary>
    public bool SpatialActive => _spatialActive;

    /// <summary>The placement the player asked for last.</summary>
    public StereoPlacement Placement => _placement;

    /// <summary>The spatial stage for the current rate, or null if Steam Audio is not available.</summary>
    public SpatialStage? Stage => _stage;

    /// <summary>
    /// Sets the spatial stage for the current device rate (null: plain stereo only). The renderer owns it from now on
    /// and disposes the previous one. Between periods, right after <see cref="Attach"/>.
    /// </summary>
    public void SetStage(SpatialStage? stage)
    {
        if (!ReferenceEquals(stage, _stage))
        {
            _stage?.Dispose();
            _stage = stage;
        }

        if (stage is not null)
        {
            stage.WidthDegrees = _placement.WidthDegrees;
            if (_primeBuffer.Length < 2 * stage.LatencyFrames)
            {
                _primeBuffer = new float[2 * stage.LatencyFrames];
            }
        }

        _spatialActive = WantsSpatial;
        _placementPending = false;
        _needsPrime = _spatialActive;
    }

    /// <summary>
    /// How the stereo of the current video is heard. A new width applies from the next block; switching between fixed
    /// and plain stereo while sound plays dips through 5 ms of silence. Between periods.
    /// </summary>
    public void SetPlacement(StereoPlacement placement)
    {
        _placement = placement;
        if (_stage is not null)
        {
            _stage.WidthDegrees = placement.WidthDegrees;
        }

        bool wanted = WantsSpatial;
        if (wanted == _spatialActive)
        {
            _placementPending = false;
            return;
        }

        if (State == RendererState.Silent)
        {
            _spatialActive = wanted;
            _needsPrime = wanted;
            _placementPending = false;
        }
        else
        {
            _placementPending = true;
        }
    }

    private bool WantsSpatial => _placement.FixedToPicture && _stage is not null && _stage.SampleRate == _sampleRate;

    /// <summary>
    /// A new device stream (other device, the first one after the silent stand-in, or a new rate). Stream frames
    /// restart at 0. Sound that played continues at the media time heard now, with a fade-in.
    /// </summary>
    public void Attach(int sampleRate, long now)
    {
        long heardMedia = _tracker.Current.At(now);
        RendererState previous = State;
        _sampleRate = sampleRate;
        _tracker = new AudioClockTracker(sampleRate, _timestampFrequency);
        _ramp = new TransportRamp(sampleRate);
        _gain = new GainSmoother(sampleRate, _gain.Value);
        _written = 0;
        State = RendererState.Silent;
        switch (previous)
        {
            case RendererState.Playing:
                _heldMediaTicks = heardMedia;
                _pendingPlay = true;
                break;
            case RendererState.FadingToPause:
                _heldMediaTicks = heardMedia;
                break;
        }

        _tracker.Stand(_heldMediaTicks);

        // The stage belongs to the old rate until SetStage brings one for the new rate.
        _spatialActive = WantsSpatial;
        _placementPending = false;
        _needsPrime = _spatialActive;
        Publish();
    }

    // ---------------------------------------------------------------- transport (between periods)

    /// <summary>The clock stands at <paramref name="mediaTicks"/> now; sound fades out. <see cref="Play"/> continues there.</summary>
    /// <param name="mediaTicks">Media time to hold.</param>
    /// <param name="holdSequence">Number of the hold request (<see cref="AudioPlaybackClock.RequestHold"/>).</param>
    public void Hold(long mediaTicks, int holdSequence)
    {
        _holdSequence = holdSequence;
        _heldMediaTicks = mediaTicks;
        _pendingPlay = false;
        _tracker.Stand(mediaTicks);
        if (State is RendererState.Playing or RendererState.FadingToPause)
        {
            _ramp.FadeOut();
            State = RendererState.FadingToHold;
        }

        Publish();
    }

    /// <summary>Starts (with a fade-in at the next frame) or takes back a pause that is still fading.</summary>
    public void Play()
    {
        switch (State)
        {
            case RendererState.FadingToPause:
                _tracker.CancelEnd();
                _ramp.FadeIn();
                State = RendererState.Playing;
                Publish();
                break;
            case RendererState.Silent:
            case RendererState.FadingToHold:
                _pendingPlay = true;
                break;
        }
    }

    /// <summary>Fades out; the clock stops at the media time of the last frame of the fade.</summary>
    public void Pause()
    {
        _pendingPlay = false;
        if (State != RendererState.Playing)
        {
            return;
        }

        _ramp.FadeOut();
        long end = _written + _ramp.RemainingFrames;
        _tracker.EndSegment(end);
        _heldMediaTicks = _tracker.MediaTicksAt(end);
        State = RendererState.FadingToPause;
        Publish();
    }

    // ---------------------------------------------------------------- periods

    /// <summary>
    /// Fills <paramref name="stereo"/> with the next <paramref name="frames"/> stream frames (interleaved L/R).
    /// </summary>
    /// <param name="stereo">Destination, at least 2 × <paramref name="frames"/> samples.</param>
    /// <param name="frames">Frames to produce.</param>
    /// <param name="firstHeardTimestamp">Estimate when the first of these frames is heard (Stopwatch).</param>
    /// <param name="targetGain">Output gain (volume, mute, fade), smoothed here.</param>
    public void Render(Span<float> stereo, int frames, long firstHeardTimestamp, float targetGain)
    {
        int done = 0;
        RateMismatch = _ring.HasStart && _ring.SampleRate != _sampleRate;
        while (done < frames)
        {
            if (State == RendererState.Silent)
            {
                // Nothing is heard: the gain may jump, so a start does not fade through an old volume.
                _gain = new GainSmoother(_sampleRate, targetGain);
                if (_ring.FlushPending)
                {
                    _ring.AcknowledgeFlush();
                }

                if (_placementPending)
                {
                    _spatialActive = WantsSpatial;
                    _placementPending = false;
                }

                if (_pendingPlay)
                {
                    long heard = firstHeardTimestamp + (long)((double)done * _timestampFrequency / _sampleRate);
                    Start(heard);
                    continue;
                }

                stereo.Slice(2 * done, 2 * (frames - done)).Clear();
                _written += frames - done;
                done = frames;
                break;
            }

            if (_ramp.IsSilent && State is RendererState.FadingToPause or RendererState.FadingToHold)
            {
                State = RendererState.Silent;
                continue;
            }

            // A flush without a hold (the decoder starts over by itself) or a switch between fixed and plain stereo: dip
            // for it, then go on at the same media time.
            if (State == RendererState.Playing && (_ring.FlushPending || _placementPending))
            {
                if (_ramp.IsSilent)
                {
                    if (_ring.FlushPending)
                    {
                        _ring.AcknowledgeFlush();
                    }

                    if (_placementPending)
                    {
                        _spatialActive = WantsSpatial;
                        _placementPending = false;
                    }

                    // After a flush or a switch, the delay line holds sound that does not follow on.
                    _needsPrime = _spatialActive;
                    _ramp.FadeIn();
                }
                else
                {
                    _ramp.FadeOut();
                }
            }

            int chunk = frames - done;
            if (_ramp.RemainingFrames > 0)
            {
                chunk = Math.Min(chunk, _ramp.RemainingFrames);
            }

            Span<float> target = stereo.Slice(2 * done, 2 * chunk);
            if (_spatialActive)
            {
                FetchSpatial(target, chunk);
            }
            else
            {
                Fetch(target, chunk, _mediaFrame);
            }
            for (int i = 0; i < chunk; i++)
            {
                float gain = _ramp.Next() * _gain.Next(targetGain);
                target[2 * i] *= gain;
                target[(2 * i) + 1] *= gain;
            }

            done += chunk;
            _written += chunk;
            _mediaFrame += chunk;

            if (_ramp.IsSilent && State is RendererState.FadingToPause or RendererState.FadingToHold)
            {
                State = RendererState.Silent;
            }
        }

        Publish();
    }

    /// <summary>
    /// The device reports that stream frame <paramref name="positionFrames"/> is heard at <paramref name="positionTimestamp"/>.
    /// </summary>
    public void UpdatePosition(long now, double positionFrames, long positionTimestamp)
    {
        _tracker.Update(now, positionFrames, positionTimestamp);
        Publish();
    }

    private void Start(long heardTimestamp)
    {
        _pendingPlay = false;
        _mediaFrame = (long)Math.Round((double)_heldMediaTicks * _sampleRate / TimeSpan.TicksPerSecond);
        _tracker.BeginSegment(_written, _heldMediaTicks, heardTimestamp);
        _ramp.Close();
        _ramp.FadeIn();
        _needsPrime = _spatialActive;
        State = RendererState.Playing;
    }

    /// <summary>
    /// The sound for media frames <see cref="_mediaFrame"/> … + <paramref name="frames"/>, fixed to the picture: the ring
    /// is read one block ahead and the stage gives back what went in one block earlier.
    /// </summary>
    private void FetchSpatial(Span<float> destination, int frames)
    {
        SpatialStage stage = _stage!;
        int latency = stage.LatencyFrames;
        if (_needsPrime || !stage.IsPrimed)
        {
            stage.Reset();
            Span<float> prime = _primeBuffer.AsSpan(0, 2 * latency);
            Fetch(prime, latency, _mediaFrame);
            stage.Prime(prime);
            _needsPrime = false;
        }

        Fetch(destination, frames, _mediaFrame + latency);
        stage.Exchange(destination, frames);
    }

    /// <summary>Copies the sound for media frames <paramref name="firstMediaFrame"/> … + <paramref name="frames"/>; silence where there is none.</summary>
    private void Fetch(Span<float> destination, int frames, long firstMediaFrame)
    {
        int done = 0;
        long media = firstMediaFrame;
        while (done < frames)
        {
            if (!_ring.HasStart || _ring.SampleRate != _sampleRate)
            {
                break;
            }

            long position = _ring.ReadPosition;
            if (position < media)
            {
                // Late data (before the position to play): drop it.
                if (_ring.Skip(media - position) == 0)
                {
                    break;
                }

                continue;
            }

            if (position > media)
            {
                // The data starts later (a gap in the track, or sound that starts after the picture): silence.
                int gap = (int)Math.Min(position - media, frames - done);
                destination.Slice(2 * done, 2 * gap).Clear();
                done += gap;
                media += gap;
                continue;
            }

            int read = _ring.Read(destination[(2 * done)..], frames - done);
            if (read == 0)
            {
                break;
            }

            done += read;
            media += read;
        }

        if (done < frames)
        {
            destination[(2 * done)..(2 * frames)].Clear();
            bool ended = _ring.HasStart && _ring.EndOfData;
            if (State == RendererState.Playing && !ended && !RateMismatch)
            {
                UnderrunFrames += frames - done;
            }
        }
    }

    private void Publish()
    {
        _clock.Publish(_tracker.Current, _holdSequence);
    }
}
