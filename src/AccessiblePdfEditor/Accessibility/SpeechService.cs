using System.Runtime.InteropServices;
using System.Text;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Accessibility;

// =====================================================================================
//  SpeechService.cs
//
//  The speech layer: the contract, an abstract base that owns the tricky part, and two
//  implementations.
//
//  The tricky part is announcement assembly, and getting it wrong is the classic bug in
//  every self-voicing program. A single screen may have four things to say — where you
//  are, what is here, what state it is in, how to use it. If each one interrupts the last,
//  the user hears only the fourth. If none of them interrupts, pressing a key produces a
//  queue that takes ten seconds to drain and the program feels unusable.
//
//  The rule that works, and the one implemented here: ONE interrupt per keystroke. The
//  first thing said after a key is pressed cuts off whatever came before; everything else
//  said in response to that same key queues behind it. So a screen says all of itself, in
//  order, and pressing another key cuts the whole thing short.
//
//  That rule lives in SpeechServiceBase so no implementation can get it wrong, and the
//  concrete classes are left with nothing to do but move characters.
// =====================================================================================

#region ISpeechService — the contract

/// <summary>Speaks text to the user through whichever screen reader is running.</summary>
public interface ISpeechService : IDisposable
{
    /// <summary>Whether a real screen reader or speech engine was found.</summary>
    bool IsSpeechAvailable { get; }

    /// <summary>The name of the detected screen reader, or null when none was found.</summary>
    string? DetectedScreenReader { get; }

    /// <summary>Whether a braille display is connected.</summary>
    bool IsBrailleAvailable { get; }

    /// <summary>
    /// Says something. Polite announcements queue behind whatever is currently being said;
    /// assertive ones cut in immediately.
    /// </summary>
    void Speak(string text, AnnouncementPriority priority = AnnouncementPriority.Polite);

    /// <summary>
    /// Sends text to a braille display without speaking it. Silently does nothing when no display
    /// is connected.
    /// </summary>
    void Braille(string text);

    /// <summary>
    /// Marks the next utterance as the start of a fresh announcement, so it interrupts. Called once
    /// per keystroke by the application shell; this is what makes the one-interrupt-per-key rule
    /// work.
    /// </summary>
    void BeginNewAnnouncement();

    /// <summary>Stops speech at once.</summary>
    void Silence();

    /// <summary>Everything said since the current announcement began, joined together.</summary>
    string LastAnnouncement { get; }

    /// <summary>Says the whole current announcement again from the beginning.</summary>
    void RepeatLast();

    /// <summary>
    /// Raised for every utterance so the window can show on screen what is being said. Useful for a
    /// sighted person helping out, and the only feedback at all when no speech engine is available.
    /// </summary>
    event Action<string>? Spoken;
}

#endregion

#region SpeechServiceBase — owns announcement assembly so no subclass can get it wrong

/// <summary>
/// Base class for speech services. Owns announcement assembly, the repeat buffer and text
/// preparation; subclasses supply only the means of getting characters to a speech engine.
/// </summary>
public abstract class SpeechServiceBase : ISpeechService
{
    #region State

    private readonly List<string> _currentAnnouncement = [];
    private readonly Lock _gate = new();
    private bool _nextUtteranceStartsAnnouncement = true;
    private bool _disposed;

    /// <inheritdoc />
    public abstract bool IsSpeechAvailable { get; }

    /// <inheritdoc />
    public virtual string? DetectedScreenReader => null;

    /// <inheritdoc />
    public virtual bool IsBrailleAvailable => false;

    /// <inheritdoc />
    public string LastAnnouncement { get; private set; } = string.Empty;

    /// <inheritdoc />
    public event Action<string>? Spoken;

    #endregion

    #region The announcement rule — one interrupt per keystroke
    // Speak is not virtual. Subclasses override OutputCore, which receives an already-prepared
    // string and a flag saying whether to interrupt. Everything about WHEN to interrupt is decided
    // here, once, for every implementation.

    /// <inheritdoc />
    public void BeginNewAnnouncement()
    {
        lock (_gate)
            _nextUtteranceStartsAnnouncement = true;
    }

    /// <inheritdoc />
    public void Speak(string text, AnnouncementPriority priority = AnnouncementPriority.Polite)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return;

        string prepared = PrepareForSpeech(text);
        if (prepared.Length == 0)
            return;

        bool startsAnnouncement;

        lock (_gate)
        {
            // An assertive utterance always starts a new announcement. An error must not be heard
            // at the end of a paragraph that was already in flight.
            startsAnnouncement = priority == AnnouncementPriority.Assertive
                || _nextUtteranceStartsAnnouncement;

            _nextUtteranceStartsAnnouncement = false;

            if (startsAnnouncement)
                _currentAnnouncement.Clear();

            _currentAnnouncement.Add(prepared);

            // The repeat buffer holds the WHOLE announcement, not just the last fragment, so that
            // asking to hear it again gives back everything the screen said rather than its tail.
            LastAnnouncement = string.Join(" ", _currentAnnouncement);
        }

        SafeOutput(prepared, startsAnnouncement);
        Spoken?.Invoke(prepared);
    }

    /// <inheritdoc />
    public void RepeatLast()
    {
        if (_disposed || LastAnnouncement.Length == 0)
            return;

        SafeOutput(LastAnnouncement, interrupt: true);
    }

    /// <inheritdoc />
    public void Braille(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return;

        try { BrailleCore(text.Trim()); }
        catch { /* A braille display that fails must never stop the program. */ }
    }

    /// <inheritdoc />
    public void Silence()
    {
        if (_disposed)
            return;

        try { SilenceCore(); }
        catch { /* Nothing useful to do if silencing fails. */ }
    }

    /// <summary>
    /// Calls <see cref="OutputCore"/> and swallows anything it throws. A speech engine that has
    /// gone away — a screen reader restarted mid-session, a USB display unplugged — must degrade
    /// to silence, not to a crash that loses the user's unsaved edits.
    /// </summary>
    private void SafeOutput(string text, bool interrupt)
    {
        try { OutputCore(text, interrupt); }
        catch { /* Deliberately swallowed: see above. */ }
    }

    #endregion

    #region What subclasses supply

    /// <summary>Sends prepared text to the speech engine.</summary>
    protected abstract void OutputCore(string text, bool interrupt);

    /// <summary>Sends text to a braille display. Does nothing by default.</summary>
    protected virtual void BrailleCore(string text) { }

    /// <summary>Stops speech. Does nothing by default.</summary>
    protected virtual void SilenceCore() { }

    #endregion

    #region Text preparation — making extracted PDF text speakable
    // Overridable, but the default does the work that every implementation needs. Text pulled out
    // of a PDF is full of things that read badly aloud, and none of them are the user's fault.

    /// <summary>
    /// Cleans text before it is spoken. Subclasses may extend this, but should call the base
    /// implementation: the substitutions here fix problems present in almost every real PDF.
    /// </summary>
    protected virtual string PrepareForSpeech(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            char replacement = c switch
            {
                // Typographic quotes and dashes. Some engines read these as their Unicode names.
                '‘' or '’' or '‛' => '\'',
                '“' or '”' or '‟' => '"',
                '–' or '—' or '−' => '-',
                ' ' or ' ' or ' ' => ' ',

                // A soft hyphen is an invisible line-break hint that extraction leaves behind.
                // Spoken, it becomes an audible hyphen in the middle of a word.
                '­' => '\0',

                // Zero-width characters: invisible on the page, and read as nothing useful.
                '​' or '‌' or '‍' or '﻿' => '\0',

                // The replacement character means extraction failed for this glyph. Saying
                // "unknown" is honest; letting the engine read it as "black diamond question
                // mark" is not.
                '�' => '\0',

                _ => c,
            };

            if (replacement == '\0')
                continue;

            if (char.IsWhiteSpace(replacement))
            {
                if (lastWasSpace || builder.Length == 0)
                    continue;

                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(replacement);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the speech engine. Subclasses that hold native resources override this.</summary>
    protected virtual void Dispose(bool disposing)
    {
        _disposed = true;
    }

    /// <summary>Whether <see cref="Dispose()"/> has been called.</summary>
    protected bool IsDisposed => _disposed;

    #endregion
}

#endregion

#region TolkSpeechService — the real one

/// <summary>Speaks through Tolk, which routes to whichever screen reader is running.</summary>
public sealed class TolkSpeechService : SpeechServiceBase
{
    #region Construction — every native call guarded
    // Tolk.dll may be missing, may be the wrong architecture, or may fail to find a client. None
    // of those is a reason for the editor not to start: it falls back to writing announcements to
    // the transcript panel, which a sighted helper can read.

    private readonly bool _isLoaded;

    public TolkSpeechService()
    {
        try
        {
            // Must precede Tolk_Load. Without it there is no speech at all on a machine with no
            // screen reader running, which is exactly the case where the fallback matters.
            TolkNative.Tolk_TrySAPI(true);
            TolkNative.Tolk_Load();

            _isLoaded = TolkNative.Tolk_IsLoaded();

            if (_isLoaded)
            {
                HasSpeech = TolkNative.Tolk_HasSpeech();
                HasBraille = TolkNative.Tolk_HasBraille();
                DetectedScreenReader = ReadDetectedName();
            }
        }
        catch (DllNotFoundException)
        {
            _isLoaded = false;
        }
        catch (BadImageFormatException)
        {
            // Tolk.dll is 64-bit. This is what a 32-bit host process gets, and it is worth
            // distinguishing in a log from the DLL simply being absent.
            _isLoaded = false;
        }
        catch
        {
            _isLoaded = false;
        }
    }

    private static string? ReadDetectedName()
    {
        IntPtr pointer = TolkNative.Tolk_DetectScreenReader();
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
    }

    #endregion

    #region Capabilities

    private bool HasSpeech { get; }

    private bool HasBraille { get; }

    public override bool IsSpeechAvailable => _isLoaded && HasSpeech;

    public override bool IsBrailleAvailable => _isLoaded && HasBraille;

    public override string? DetectedScreenReader { get; }

    #endregion

    #region Output

    protected override void OutputCore(string text, bool interrupt)
    {
        if (IsSpeechAvailable)
            TolkNative.Tolk_Output(text, interrupt);
    }

    protected override void BrailleCore(string text)
    {
        if (IsBrailleAvailable)
            TolkNative.Tolk_Braille(text);
    }

    protected override void SilenceCore()
    {
        if (IsSpeechAvailable)
            TolkNative.Tolk_Silence();
    }

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        if (!IsDisposed && _isLoaded)
        {
            try { TolkNative.Tolk_Unload(); }
            catch { /* Shutting down; nothing left to salvage. */ }
        }

        base.Dispose(disposing);
    }

    #endregion
}

#endregion

#region NullSpeechService — records instead of speaking

/// <summary>
/// A speech service that records what would have been said instead of saying it.
///
/// This exists so the whole application can be tested without a screen reader, a sound card or a
/// window. Tests assert on what the program WOULD have announced, which turns "is this accessible"
/// from a manual check into something the build can verify.
/// </summary>
public sealed class NullSpeechService : SpeechServiceBase
{
    private readonly List<string> _utterances = [];

    /// <summary>Everything spoken since construction, in order.</summary>
    public IReadOnlyList<string> Utterances => _utterances;

    /// <summary>Reports as available so that code paths guarded on speech are exercised in tests.</summary>
    public override bool IsSpeechAvailable => true;

    public override string? DetectedScreenReader => "none (test harness)";

    protected override void OutputCore(string text, bool interrupt) => _utterances.Add(text);

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear() => _utterances.Clear();

    /// <summary>True when any utterance contains the given text, ignoring case.</summary>
    public bool Said(string fragment) =>
        _utterances.Any(u => u.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

#endregion
