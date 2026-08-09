using System.Runtime.InteropServices;

namespace AccessiblePdfEditor.Accessibility;

// =====================================================================================
//  TolkNative.cs
//
//  The raw P/Invoke surface of Tolk.dll, the screen-reader abstraction library.
//
//  Tolk detects whichever screen reader is actually running and routes speech and braille
//  to it: NVDA, JAWS, Window-Eyes, SuperNova, System Access, ZoomText, or SAPI when none of
//  them is present. That last fallback matters more than it sounds — it means this editor
//  still speaks on a machine with no screen reader installed, which is what makes it usable
//  by a sighted colleague helping to check a document.
//
//  Nothing in the application calls these directly. TolkSpeechService wraps them, because
//  every one of them can throw if the DLL is missing and none of them should be able to
//  take the program down.
// =====================================================================================

#region Native entry points

/// <summary>
/// Direct bindings to Tolk.dll. Internal by design: everything goes through
/// <see cref="TolkSpeechService"/>, which adds the error handling these raw calls lack.
/// </summary>
internal static partial class TolkNative
{
    private const string DllName = "Tolk.dll";

    /// <summary>Initialises Tolk and loads whichever screen-reader client is available.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void Tolk_Load();

    /// <summary>Releases Tolk and its screen-reader client.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void Tolk_Unload();

    /// <summary>
    /// Allows Tolk to fall back to SAPI when no screen reader is running. Must be called before
    /// <see cref="Tolk_Load"/> to have any effect.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool useSapi);

    /// <summary>Speaks text, optionally cutting off whatever is currently being spoken.</summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Tolk_Output(string text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

    /// <summary>
    /// Sends text to a braille display only, without speaking it. Used for the status line, where
    /// a braille reader wants a persistent record but a speech user does not want it read aloud.
    /// </summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Tolk_Braille(string text);

    /// <summary>Stops speech immediately.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Tolk_Silence();

    /// <summary>Whether Tolk has been loaded.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Tolk_IsLoaded();

    /// <summary>The name of the detected screen reader, or a null pointer when none was found.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr Tolk_DetectScreenReader();

    /// <summary>Whether the detected client can speak.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Tolk_HasSpeech();

    /// <summary>Whether the detected client can drive a braille display.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Tolk_HasBraille();
}

#endregion
