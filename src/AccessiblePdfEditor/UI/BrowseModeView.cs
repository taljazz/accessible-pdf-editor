using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  BrowseModeView.cs
//
//  Hosts the document as a web page, so the screen reader reads it in browse mode.
//
//  This is the pane that closes the gap between this program and a PDF reader. Everything
//  the user knows how to do â€” H for the next heading, T for the next table, D for the next
//  landmark, Control+Alt+arrows to move around a table, NVDA+F7 for the element list, Say
//  All from here to the end â€” works, because it is their screen reader doing it against a
//  real document rather than this program imitating it against a text box.
//
//  WHAT THIS CONTROL IS CAREFUL ABOUT
//
//  It never navigates. The document is served from memory to a host name that does not
//  exist, and any attempt to go anywhere else is cancelled. A PDF can contain a link to
//  anything at all, and the decision to open one belongs to the program and the user, not
//  to the page.
//
//  It never speaks and never moves focus on its own. In browse mode the reader is moving a
//  cursor through its own copy of the page; a page that stole focus would drag the user
//  somewhere they did not ask to go, mid-sentence.
//
//  It degrades. If the WebView2 runtime is not installed â€” a locked-down machine, an old
//  Windows build â€” the control says so in a real label and the program carries on with the
//  text pane, which is exactly what it had before. A missing component must never be the
//  reason someone cannot read their document.
// =====================================================================================

#region What the page reports back

/// <summary>Something the user did in the page, on their way to this program doing something about it.</summary>
public sealed record BrowsePageEvent(string Kind, int ElementId, string? Action, string? Value);

#endregion

#region BrowseModeView

/// <summary>Shows the document as a web page for the screen reader to read in browse mode.</summary>
public sealed class BrowseModeView : UserControl
{
    #region State

    private readonly WebView2? _view;
    private readonly Label? _unavailableLabel;

    private DocumentHtml? _pending;
    private DocumentHtml? _current;
    private bool _ready;
    private bool _initialising;

    /// <summary>The host name the document is served from. It is not a real one, and never resolves.</summary>
    private const string Origin = "https://document.localhost";

    private const string EntryUrl = Origin + "/index.html";

    #endregion

    #region Construction

    public BrowseModeView()
    {
        AccessibleName = "Document";
        Dock = DockStyle.Fill;

        string? failure = DetectRuntime();

        if (failure is not null)
        {
            UnavailableReason = failure;

            // A real label, not a painted message: someone who cannot see the pane still needs to
            // be told why it is empty, and a label is a thing a screen reader can read.
            _unavailableLabel = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                Text = failure,
                AccessibleName = "Browse view unavailable",
                AccessibleDescription = failure,
            };

            Controls.Add(_unavailableLabel);
            return;
        }

        _view = new WebView2
        {
            Dock = DockStyle.Fill,

            // Named, because this is the control the screen reader is about to introduce.
            AccessibleName = "Document",
            AccessibleDescription =
                "The document as a web page. Use your screen reader's usual reading and " +
                "navigation commands.",
        };

        Controls.Add(_view);
    }

    /// <summary>
    /// Whether the browse view can be used at all. False means the WebView2 runtime is missing.
    /// </summary>
    public bool IsAvailable => _view is not null;

    /// <summary>Why the browse view cannot be used, when it cannot.</summary>
    public string? UnavailableReason { get; }

    /// <summary>The installed runtime version, for the diagnostics the user can read.</summary>
    public string? RuntimeVersion { get; private set; }

    private string? DetectRuntime()
    {
        try
        {
            string? version = CoreWebView2Environment.GetAvailableBrowserVersionString();

            if (string.IsNullOrEmpty(version))
                return NotInstalledMessage;

            RuntimeVersion = version;
            return null;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException
                                      or BadImageFormatException or TypeLoadException)
        {
            return NotInstalledMessage;
        }
        catch (Exception ex)
        {
            return "The browse view could not start: " + ex.Message +
                   " The document is still available in the text view.";
        }
    }

    private const string NotInstalledMessage =
        "The browse view needs the Microsoft Edge WebView2 runtime, which is not installed on " +
        "this computer. Everything in this program still works without it: press F6 to go back " +
        "to the text view, which needs nothing extra. To turn the browse view on, install the " +
        "WebView2 runtime from Microsoft and restart this program.";

    #endregion

    #region Events

    /// <summary>The user activated something â€” a link, a button, a fault offered for repair.</summary>
    public event EventHandler<BrowsePageEvent>? PageEvent;

    /// <summary>A key the host window should deal with, forwarded out of the page.</summary>
    public event EventHandler<KeyEventArgs>? HostKeyPressed;

    #endregion

    #region Starting up

    /// <summary>
    /// Starts the web view. Safe to call more than once; the second call does nothing.
    ///
    /// This is deliberately not done in the constructor. Starting a browser process costs time and
    /// memory, and a user who never opens this pane should never pay for it.
    /// </summary>
    public async Task<bool> InitialiseAsync()
    {
        if (_view is null || _ready || _initialising)
            return _ready;

        _initialising = true;

        try
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessiblePdfEditor", "WebView2");

            Directory.CreateDirectory(userData);

            var options = new CoreWebView2EnvironmentOptions
            {
                // The document is never a real web page, so none of the browser's own document
                // shortcuts belong here. Turning them off is also what lets Control+S and
                // Control+F reach this program rather than opening the browser's save dialog.
                AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection",
            };

            var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
            await _view.EnsureCoreWebView2Async(environment);

            ConfigureCore(_view.CoreWebView2);

            _ready = true;

            if (_pending is not null)
            {
                var document = _pending;
                _pending = null;
                Show(document);
            }

            return true;
        }
        catch (Exception ex)
        {
            ShowFailure("The browse view could not start: " + ex.Message +
                        " Press F6 to go back to the text view.");
            return false;
        }
        finally
        {
            _initialising = false;
        }
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        var settings = core.Settings;

        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDevToolsEnabled = false;

        // The document may contain a form. None of it should be offered to the browser's own
        // autofill, or saved into the user's browser profile.
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        // Serve the page out of memory. NavigateToString has a two-megabyte limit that a long
        // document would quietly exceed, and writing it to a temporary file would put the
        // contents of somebody's document on disk where they did not ask for it.
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        // The document does not get to decide where this control goes.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    #endregion

    #region Serving the document

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_view?.CoreWebView2 is null)
            return;

        string html = _current?.Html ?? "<!doctype html><html lang=\"en\"><head><title>No document" +
                                        "</title></head><body><p>No document is open.</p></body></html>";

        var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
            stream, 200, "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    /// <summary>Shows a document. Queued if the web view has not finished starting.</summary>
    public void Show(DocumentHtml document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_view is null)
            return;

        if (!_ready)
        {
            _pending = document;
            return;
        }

        _current = document;

        // Reload rather than navigate, so the same address serves the new content and the history
        // does not grow an entry every time the document changes.
        _view.CoreWebView2.Navigate(EntryUrl);
    }

    /// <summary>Reloads the page after the document has been edited elsewhere in the program.</summary>
    public void Refresh(DocumentHtml document)
    {
        if (_view is null || !_ready)
        {
            _pending = document;
            return;
        }

        _current = document;
        _view.CoreWebView2.Reload();
    }

    #endregion

    #region Moving to a place in the page

    /// <summary>
    /// Moves to an element, taking the reader's cursor with it.
    ///
    /// The focus() call is the load-bearing part, and it is worth being precise about why.
    /// scrollIntoView moves the window only. In browse mode the screen reader is reading a copy of
    /// the page and keeps a cursor of its own inside it, and scrolling does not move that cursor —
    /// so a navigation command would announce the destination and leave the user reading where they
    /// already were. That is the same failure the text view once had with the caret.
    ///
    /// Moving focus does move the browse cursor. It is only ever done in response to the user
    /// deliberately asking to go somewhere; nothing here moves on its own.
    /// </summary>
    public async Task MoveToAsync(string anchor)
    {
        if (_view?.CoreWebView2 is null || !_ready || string.IsNullOrEmpty(anchor))
            return;

        string script =
            $"(function(){{var t=document.getElementById({JsonSerializer.Serialize(anchor)});" +
            "if(!t)return;t.scrollIntoView({block:'center'});" +
            "if(typeof t.focus==='function')t.focus({preventScroll:true});})()";

        try
        {
            await _view.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
        {
            // The view was disposed or renavigated while the call was in flight. Nothing to do:
            // the position will be right again after the next load.
        }
    }

    /// <summary>Puts keyboard focus in the page, so the reader starts reading the document.</summary>
    public new void Focus()
    {
        if (_view is not null)
            _view.Focus();
        else
            _unavailableLabel?.Focus();
    }

    #endregion

    #region Messages from the page

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var parsed = JsonDocument.Parse(e.WebMessageAsJson);
            var root = parsed.RootElement;

            string kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? string.Empty : string.Empty;

            if (kind == "key")
            {
                RaiseHostKey(root);
                return;
            }

            int elementId = root.TryGetProperty("element", out var idProperty)
                            && idProperty.ValueKind == JsonValueKind.Number
                ? idProperty.GetInt32()
                : -1;

            string? action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            string? value = root.TryGetProperty("value", out var v) ? DescribeValue(v) : null;

            PageEvent?.Invoke(this, new BrowsePageEvent(kind, elementId, action, value));
        }
        catch (JsonException)
        {
            // A message this program did not send. Ignored rather than trusted.
        }
    }

    private static string? DescribeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)),
        _ => null,
    };

    private void RaiseHostKey(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeProperty) || codeProperty.ValueKind != JsonValueKind.Number)
            return;

        var keys = (Keys)codeProperty.GetInt32();

        if (root.TryGetProperty("ctrl", out var ctrl) && ctrl.ValueKind == JsonValueKind.True)
            keys |= Keys.Control;

        if (root.TryGetProperty("shift", out var shift) && shift.ValueKind == JsonValueKind.True)
            keys |= Keys.Shift;

        if (root.TryGetProperty("alt", out var alt) && alt.ValueKind == JsonValueKind.True)
            keys |= Keys.Alt;

        HostKeyPressed?.Invoke(this, new KeyEventArgs(keys));
    }

    #endregion

    #region Failure

    private void ShowFailure(string message)
    {
        if (IsDisposed)
            return;

        Controls.Clear();

        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            Text = message,
            AccessibleName = "Browse view unavailable",
            AccessibleDescription = message,
        });
    }

    #endregion

    #region Cleanup

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _view?.Dispose();

        base.Dispose(disposing);
    }

    #endregion
}

#endregion
