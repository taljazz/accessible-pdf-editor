using OpenTK.Audio.OpenAL;

namespace AccessiblePdfEditor.Accessibility;

// =====================================================================================
//  AudioCueService.cs
//
//  Short non-speech sounds — earcons — that tell the user something without spending time
//  saying it.
//
//  These matter more in an editor than in a reader. Speech is slow and strictly serial: if
//  every event has to be spoken, the user spends their day listening to confirmations.
//  A rising two-note chime for "saved", a low buzz for "rejected", a soft tick when
//  crossing a heading — each conveys in 80 milliseconds what would take two seconds to say,
//  and none of them interrupt speech, because they come out of a different channel entirely.
//
//  The design constraint that shaped this file: cues must OVERLAP. Arrowing quickly through
//  a list fires ticks faster than any one tick finishes, and a system that queues them falls
//  progressively further behind until the sound has nothing to do with where the cursor is.
//  OpenAL gives every cue its own source and mixes them, which is precisely why it is worth
//  the dependency over the simpler built-in sound APIs.
// =====================================================================================

#region AudioCue — the vocabulary of sounds
// Each value is a MEANING, not a sound. The mapping from meaning to pitch lives in one place
// below, so the sound design can be adjusted as a whole and stays internally consistent —
// everything that means "no" is low and falling, everything that means "yes" rises.

/// <summary>A short non-speech sound with a specific meaning.</summary>
public enum AudioCue
{
    /// <summary>Moved to another item. The most frequent cue by far, so the quietest and shortest.</summary>
    Navigation = 0,

    /// <summary>Landed on a heading while moving through the document.</summary>
    Heading,

    /// <summary>Landed on a link.</summary>
    Link,

    /// <summary>Landed on a form field.</summary>
    FormField,

    /// <summary>Landed on a figure.</summary>
    Figure,

    /// <summary>Landed on a table.</summary>
    Table,

    /// <summary>Crossed a page boundary.</summary>
    PageTurn,

    /// <summary>
    /// Reached the start or end of the document, or of the current structure. Distinct from
    /// Navigation because "nothing happened" and "something happened" must never sound alike —
    /// that is how a user ends up pressing a key twenty times without realising they have stopped.
    /// </summary>
    Boundary,

    /// <summary>An action succeeded.</summary>
    Success,

    /// <summary>A value was accepted.</summary>
    ValueAccepted,

    /// <summary>A value was rejected, or an action failed.</summary>
    Rejected,

    /// <summary>Something went wrong that the user needs to hear about.</summary>
    Error,

    /// <summary>Something is worth noting but is not an error.</summary>
    Warning,

    /// <summary>The document was saved.</summary>
    Saved,

    /// <summary>An edit was undone.</summary>
    Undone,

    /// <summary>An edit was redone.</summary>
    Redone,

    /// <summary>An accessibility problem was found or landed on.</summary>
    IssueFound,

    /// <summary>An accessibility problem was repaired. Deliberately the most satisfying sound here.</summary>
    IssueFixed,

    /// <summary>A long operation started.</summary>
    WorkStarted,

    /// <summary>A long operation finished.</summary>
    WorkFinished,
}

#endregion

#region IAudioCueService — the contract

/// <summary>Plays short non-speech sounds.</summary>
public interface IAudioCueService : IDisposable
{
    /// <summary>Whether an audio device was opened. False means every call is silently ignored.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether cues are switched on. Independent of availability.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Cue volume, from 0 to 1.</summary>
    float Volume { get; set; }

    /// <summary>Plays a cue. Returns immediately; the sound plays on the audio device's own thread.</summary>
    void Play(AudioCue cue);

    /// <summary>
    /// Plays a single tone at a given pitch.
    ///
    /// Unlike <see cref="Play"/>, this carries a CONTINUOUS value rather than a fixed meaning. It
    /// exists so that a position can be heard: mapping where something is to how high it sounds
    /// lets a blind user track a moving pointer in real time, which no amount of speech can do —
    /// speech is far too slow, and by the time a coordinate had been read out the pointer would
    /// have moved.
    /// </summary>
    void PlayTone(double frequency, int milliseconds, double amplitude = 0.5);

    /// <summary>Stops every sound currently playing.</summary>
    void StopAll();
}

#endregion

#region Tone — one note of a cue

/// <summary>A single note: a frequency, a duration and a relative loudness.</summary>
public readonly record struct Tone(double Frequency, int Milliseconds, double Amplitude = 1.0);

#endregion

#region AudioCueServiceBase — owns sound design and synthesis
// Both the meaning-to-notes mapping and the PCM synthesis live here rather than in the OpenAL
// class, so the sound design is testable without an audio device and so a future implementation
// on a different audio backend inherits identical sounds rather than approximations of them.

/// <summary>
/// Base class for cue services. Owns the mapping from cue to notes and the synthesis of those
/// notes into audio samples; subclasses supply only playback.
/// </summary>
public abstract class AudioCueServiceBase : IAudioCueService
{
    #region Configuration

    /// <summary>Sample rate for generated audio. 44.1 kHz is universally supported.</summary>
    protected const int SampleRate = 44_100;

    private float _volume = 0.55f;

    /// <inheritdoc />
    public abstract bool IsAvailable { get; }

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    #endregion

    #region The sound design — meaning to notes, in one place
    // Frequencies are picked from a C major scale so that cues heard in quick succession do not
    // clash. The rules behind the choices:
    //
    //   rising    = something advanced or succeeded
    //   falling   = something was refused or ended
    //   flat tick = movement, nothing notable
    //   low       = a problem
    //   high      = a structure worth noticing
    //
    // Durations are kept short. Anything longer than about 120 milliseconds starts to feel like a
    // delay rather than a confirmation when it fires on every keypress.

    /// <summary>The notes that make up a cue.</summary>
    protected virtual IReadOnlyList<Tone> GetTones(AudioCue cue) => cue switch
    {
        // Movement: barely there. It must be possible to hear these all day.
        AudioCue.Navigation => [new Tone(880, 22, 0.25)],

        // Structures: each has its own pitch so they can be told apart without speech, which is
        // what makes fast skimming possible — the user hears "heading, heading, table" while the
        // speech is still catching up.
        AudioCue.Heading => [new Tone(1046.5, 40, 0.4)],
        AudioCue.Link => [new Tone(1318.5, 32, 0.35)],
        AudioCue.FormField => [new Tone(987.8, 38, 0.4)],
        AudioCue.Figure => [new Tone(739.99, 40, 0.38)],
        AudioCue.Table => [new Tone(659.26, 44, 0.4)],

        AudioCue.PageTurn => [new Tone(523.25, 30, 0.35), new Tone(659.26, 45, 0.35)],

        // Boundary: two flat notes at the same pitch. Deliberately unlike everything else, because
        // it means "you did not move" and must never be mistaken for movement.
        AudioCue.Boundary => [new Tone(392, 55, 0.5), new Tone(392, 55, 0.5)],

        // Outcomes.
        AudioCue.Success => [new Tone(523.25, 55, 0.5), new Tone(659.26, 55, 0.5), new Tone(783.99, 80, 0.5)],
        AudioCue.ValueAccepted => [new Tone(659.26, 45, 0.45), new Tone(880, 65, 0.45)],
        AudioCue.Rejected => [new Tone(311.13, 70, 0.55), new Tone(233.08, 100, 0.55)],
        AudioCue.Error => [new Tone(196, 110, 0.6), new Tone(146.83, 150, 0.6)],
        AudioCue.Warning => [new Tone(415.3, 70, 0.5), new Tone(415.3, 70, 0.5)],

        AudioCue.Saved => [new Tone(783.99, 50, 0.5), new Tone(1046.5, 90, 0.5)],

        // Undo falls, redo rises: the same two notes in opposite order, so the pair is
        // immediately understandable as one action and its reverse.
        AudioCue.Undone => [new Tone(659.26, 45, 0.45), new Tone(493.88, 60, 0.45)],
        AudioCue.Redone => [new Tone(493.88, 45, 0.45), new Tone(659.26, 60, 0.45)],

        AudioCue.IssueFound => [new Tone(349.23, 60, 0.45), new Tone(293.66, 75, 0.45)],

        // Fixing an accessibility problem gets the fullest sound in the program. Remediation is
        // slow, repetitive work, and the point at which something is genuinely repaired deserves
        // to feel like an achievement rather than another tick.
        AudioCue.IssueFixed =>
        [
            new Tone(523.25, 50, 0.5), new Tone(659.26, 50, 0.5),
            new Tone(783.99, 50, 0.5), new Tone(1046.5, 110, 0.55),
        ],

        AudioCue.WorkStarted => [new Tone(440, 40, 0.35), new Tone(554.37, 40, 0.35)],
        AudioCue.WorkFinished => [new Tone(554.37, 40, 0.35), new Tone(440, 55, 0.35)],

        _ => [new Tone(880, 25, 0.3)],
    };

    #endregion

    #region Synthesis
    // A raw sine wave that starts and stops abruptly produces an audible click at each end,
    // because the waveform jumps discontinuously to and from zero. The short fade applied here is
    // what makes these sound like notes rather than like faults.

    /// <summary>Renders a cue's notes into 16-bit mono PCM samples.</summary>
    protected short[] Synthesise(IReadOnlyList<Tone> tones)
    {
        int totalSamples = 0;
        foreach (var tone in tones)
            totalSamples += tone.Milliseconds * SampleRate / 1000;

        var samples = new short[totalSamples];
        int offset = 0;

        foreach (var tone in tones)
        {
            int count = tone.Milliseconds * SampleRate / 1000;

            // 4 milliseconds of fade at each end, or a quarter of the note if it is very short.
            int fade = Math.Min(SampleRate * 4 / 1000, count / 4);
            double angularStep = 2.0 * Math.PI * tone.Frequency / SampleRate;
            double peak = tone.Amplitude * Volume * short.MaxValue * 0.8;

            for (int i = 0; i < count; i++)
            {
                double envelope = 1.0;
                if (fade > 0)
                {
                    if (i < fade) envelope = (double)i / fade;
                    else if (i >= count - fade) envelope = (double)(count - i) / fade;
                }

                samples[offset + i] = (short)(Math.Sin(angularStep * i) * peak * envelope);
            }

            offset += count;
        }

        return samples;
    }

    #endregion

    #region Playback

    /// <inheritdoc />
    public void Play(AudioCue cue)
    {
        if (!IsEnabled || !IsAvailable || Volume <= 0f)
            return;

        try
        {
            PlayCore(cue, GetTones(cue));
        }
        catch
        {
            // A cue that will not play is never worth interrupting the user's work for. The
            // information it carried is always also available in speech.
        }
    }

    /// <inheritdoc />
    public void PlayTone(double frequency, int milliseconds, double amplitude = 0.5)
    {
        if (!IsEnabled || !IsAvailable || Volume <= 0f)
            return;

        // Clamped to what a person can comfortably hear and to a length that will not queue up
        // behind itself when these fire many times a second, which is exactly how they are used.
        double safeFrequency = Math.Clamp(frequency, 80, 6000);
        int safeLength = Math.Clamp(milliseconds, 10, 400);

        try
        {
            PlayToneCore(new Tone(safeFrequency, safeLength, Math.Clamp(amplitude, 0, 1)));
        }
        catch
        {
            // A tone that will not play is never worth interrupting the user's work for.
        }
    }

    /// <summary>Plays a cue's notes. Called only when cues are enabled and available.</summary>
    protected abstract void PlayCore(AudioCue cue, IReadOnlyList<Tone> tones);

    /// <summary>
    /// Plays a single tone that is not one of the fixed cues, and so cannot be cached: its pitch
    /// changes every time. Does nothing by default.
    /// </summary>
    protected virtual void PlayToneCore(Tone tone) { }

    /// <inheritdoc />
    public abstract void StopAll();

    #endregion

    #region Disposal

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }

    #endregion
}

#endregion

#region OpenAlAudioCueService — the real one

/// <summary>
/// Plays cues through OpenAL, giving each one its own source so that rapid cues overlap and mix
/// rather than queueing.
/// </summary>
public sealed class OpenAlAudioCueService : AudioCueServiceBase
{
    #region Device setup
    // Everything here is best-effort. A machine with no sound card, a remote desktop session with
    // audio redirection off, or a missing OpenAL32.dll all end in IsAvailable being false, and the
    // editor carries on in silence. The one thing it must not do is fail to start.

    private readonly ALDevice _device;
    private readonly ALContext _context;
    private readonly bool _ready;

    /// <summary>
    /// Sources are pooled and reused. Generating one per cue would leak them at the rate the user
    /// presses arrow keys, and OpenAL implementations cap the number of sources fairly low.
    /// </summary>
    private readonly List<int> _sources = [];

    private readonly Dictionary<AudioCue, int> _bufferCache = [];
    private readonly Lock _gate = new();

    private const int MaxSources = 12;

    public OpenAlAudioCueService()
    {
        try
        {
            _device = ALC.OpenDevice(null);
            if (_device == ALDevice.Null)
                return;

            _context = ALC.CreateContext(_device, (int[])null!);
            if (_context == ALContext.Null)
            {
                ALC.CloseDevice(_device);
                _device = ALDevice.Null;
                return;
            }

            if (!ALC.MakeContextCurrent(_context))
            {
                ALC.DestroyContext(_context);
                ALC.CloseDevice(_device);
                _device = ALDevice.Null;
                _context = ALContext.Null;
                return;
            }

            _ready = true;
        }
        catch (DllNotFoundException)
        {
            _ready = false;
        }
        catch
        {
            _ready = false;
        }
    }

    public override bool IsAvailable => _ready;

    #endregion

    #region Playback
    // Buffers are generated once per cue and cached, because a cue's audio never changes for a
    // given volume. Volume changes clear the cache, which is simpler and less error-prone than
    // re-rendering every buffer at the moment the user turns the volume up.

    protected override void PlayCore(AudioCue cue, IReadOnlyList<Tone> tones)
    {
        lock (_gate)
        {
            if (!_ready)
                return;

            int buffer = GetOrCreateBuffer(cue, tones);
            if (buffer == 0)
                return;

            int source = AcquireSource();
            if (source == 0)
                return;

            AL.Source(source, ALSourcei.Buffer, buffer);
            AL.Source(source, ALSourcef.Gain, 1.0f);
            AL.SourcePlay(source);
        }
    }

    private int GetOrCreateBuffer(AudioCue cue, IReadOnlyList<Tone> tones)
    {
        if (_bufferCache.TryGetValue(cue, out int cached))
            return cached;

        short[] samples = Synthesise(tones);
        if (samples.Length == 0)
            return 0;

        int buffer = AL.GenBuffer();
        AL.BufferData(buffer, ALFormat.Mono16, samples, SampleRate);

        if (AL.GetError() != ALError.NoError)
        {
            AL.DeleteBuffer(buffer);
            return 0;
        }

        _bufferCache[cue] = buffer;
        return buffer;
    }

    /// <summary>
    /// Finds a source that has finished playing, or creates one up to the pool limit. When every
    /// source is busy the oldest is stolen — at that point cues are firing faster than they can be
    /// heard anyway, and dropping the newest would make the sound lag behind the cursor, which is
    /// the failure this whole pooling scheme exists to avoid.
    /// </summary>
    private int AcquireSource()
    {
        foreach (int source in _sources)
        {
            var state = (ALSourceState)AL.GetSource(source, ALGetSourcei.SourceState);
            if (state is not (ALSourceState.Playing or ALSourceState.Paused))
                return source;
        }

        if (_sources.Count < MaxSources)
        {
            int created = AL.GenSource();
            if (AL.GetError() != ALError.NoError)
                return 0;

            _sources.Add(created);
            return created;
        }

        int stolen = _sources[0];
        _sources.RemoveAt(0);
        _sources.Add(stolen);
        AL.SourceStop(stolen);
        return stolen;
    }

    /// <summary>
    /// Plays a one-off tone whose pitch is different every time, so it cannot be cached.
    ///
    /// A small ring of buffers is reused rather than one being generated per tone. These fire many
    /// times a second while the user is tracking a position, and generating a buffer each time
    /// would exhaust the driver's buffer allocation within seconds of drawing.
    /// </summary>
    protected override void PlayToneCore(Tone tone)
    {
        lock (_gate)
        {
            if (!_ready)
                return;

            short[] samples = Synthesise([tone]);
            if (samples.Length == 0)
                return;

            int buffer = NextRingBuffer();
            if (buffer == 0)
                return;

            int source = AcquireSource();
            if (source == 0)
                return;

            // The source must be detached from its old buffer before that buffer is refilled, or
            // OpenAL refuses the write while it is still bound.
            AL.Source(source, ALSourcei.Buffer, 0);
            AL.BufferData(buffer, ALFormat.Mono16, samples, SampleRate);

            if (AL.GetError() != ALError.NoError)
                return;

            AL.Source(source, ALSourcei.Buffer, buffer);
            AL.Source(source, ALSourcef.Gain, 1.0f);
            AL.SourcePlay(source);
        }
    }

    private const int RingBufferCount = 8;

    private readonly int[] _ringBuffers = new int[RingBufferCount];
    private int _ringPosition;

    private int NextRingBuffer()
    {
        _ringPosition = (_ringPosition + 1) % RingBufferCount;

        if (_ringBuffers[_ringPosition] == 0)
        {
            int created = AL.GenBuffer();

            if (AL.GetError() != ALError.NoError)
                return 0;

            _ringBuffers[_ringPosition] = created;
        }

        return _ringBuffers[_ringPosition];
    }

    public override void StopAll()
    {
        lock (_gate)
        {
            if (!_ready)
                return;

            foreach (int source in _sources)
            {
                try { AL.SourceStop(source); }
                catch { /* Best effort. */ }
            }
        }
    }

    #endregion

    #region Volume changes invalidate the cache

    /// <summary>
    /// Re-renders cues at a new volume. Volume is baked into the samples during synthesis, so the
    /// cached buffers have to go when it changes.
    /// </summary>
    public void SetVolume(float volume)
    {
        lock (_gate)
        {
            float previous = Volume;
            Volume = volume;

            if (Math.Abs(previous - Volume) < 0.001f || !_ready)
                return;

            foreach (int buffer in _bufferCache.Values)
            {
                try { AL.DeleteBuffer(buffer); }
                catch { /* Best effort. */ }
            }

            _bufferCache.Clear();
        }
    }

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (!_ready)
                return;

            try
            {
                foreach (int source in _sources)
                {
                    AL.SourceStop(source);
                    AL.DeleteSource(source);
                }

                foreach (int buffer in _bufferCache.Values)
                    AL.DeleteBuffer(buffer);

                foreach (int buffer in _ringBuffers)
                {
                    if (buffer != 0)
                        AL.DeleteBuffer(buffer);
                }

                _sources.Clear();
                _bufferCache.Clear();

                ALC.MakeContextCurrent(ALContext.Null);
                ALC.DestroyContext(_context);
                ALC.CloseDevice(_device);
            }
            catch
            {
                // Shutting down. Nothing here is worth reporting to the user.
            }
        }

        base.Dispose(disposing);
    }

    #endregion
}

#endregion

#region SilentAudioCueService — records instead of playing

/// <summary>
/// A cue service that records which cues were requested instead of playing them. Lets tests assert
/// that, for example, reaching the end of a document produces a boundary cue rather than a
/// navigation one — a distinction that matters to the user and would otherwise go unverified.
/// </summary>
public sealed class SilentAudioCueService : AudioCueServiceBase
{
    private readonly List<AudioCue> _played = [];
    private readonly List<Tone> _tones = [];

    /// <summary>Every cue requested since construction, in order.</summary>
    public IReadOnlyList<AudioCue> Played => _played;

    /// <summary>Every individual tone requested, in order. Lets tests assert that pitch tracks
    /// position without needing an audio device.</summary>
    public IReadOnlyList<Tone> Tones => _tones;

    public override bool IsAvailable => true;

    protected override void PlayCore(AudioCue cue, IReadOnlyList<Tone> tones) => _played.Add(cue);

    protected override void PlayToneCore(Tone tone) => _tones.Add(tone);

    public override void StopAll() { }

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear()
    {
        _played.Clear();
        _tones.Clear();
    }
}

#endregion
