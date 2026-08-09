using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  ListSelectionDialog.cs
//
//  A dialog that presents a list and returns what the user chose.
//
//  Used for bookmarks, links, comments, accessibility findings and heading levels. One
//  dialog for all of them, for the same reason as the text prompt: consistency is worth
//  more than bespoke design when you cannot see the window.
//
//  It is built around a real Windows ListBox rather than a custom surface, and that is the
//  whole trick. A ListBox gives the user, without this program writing any of it:
//
//    "3 of 17" announced automatically as they arrow through
//    first-letter navigation to jump to an entry
//    Home and End
//    their braille display tracking the selection
//
//  A hand-rolled list would have to reimplement every one of those, worse.
// =====================================================================================

#region ListSelectionDialog

/// <summary>Presents a list of items and returns the chosen one.</summary>
/// <typeparam name="T">The type of item in the list.</typeparam>
public sealed class ListSelectionDialog<T> : AccessibleFormBase where T : class
{
    #region State

    private readonly string _title;
    private readonly string _purpose;
    private readonly IReadOnlyList<T> _items;
    private readonly Func<T, string> _describe;
    private readonly Func<T, string>? _describeInDetail;
    private readonly string? _actionButtonText;

    private ListBox _list = null!;
    private Label _detailLabel = null!;

    /// <summary>What the user chose. Null when they cancelled.</summary>
    public T? Selected { get; private set; }

    public ListSelectionDialog(
        ISpeechService speech,
        IAudioCueService cues,
        string title,
        string purpose,
        IReadOnlyList<T> items,
        Func<T, string> describe,
        Func<T, string>? describeInDetail = null,
        string? actionButtonText = null)
        : base(speech, cues)
    {
        _title = title;
        _purpose = purpose;
        _items = items;
        _describe = describe;
        _describeInDetail = describeInDetail;
        _actionButtonText = actionButtonText;

        Size = new Size(720, 480);
        FormBorderStyle = FormBorderStyle.Sizable;
    }

    #endregion

    #region Identity

    protected override string WindowTitle => _title;

    protected override string WindowPurpose => _purpose;

    protected override string BuildOpeningAnnouncement()
    {
        // The count comes first. It is what tells the listener whether to step through the list or
        // look for another way at it, and they cannot get it any other way.
        string count = _items.Count switch
        {
            0 => "The list is empty.",
            1 => "1 item.",
            _ => $"{_items.Count} items.",
        };

        return $"{_title}. {count} {_purpose} " +
               "Use the arrow keys to move through the list, Enter to choose, Escape to close.";
    }

    #endregion

    #region Layout

    protected override void BuildContent()
    {
        _list = CreateListBox(_title, _purpose, tabIndex: 0);
        _list.Dock = DockStyle.Fill;

        foreach (var item in _items)
            _list.Items.Add(new ItemWrapper(item, _describe(item)));

        if (_items.Count > 0)
            _list.SelectedIndex = 0;

        _list.SelectedIndexChanged += (_, _) => AnnounceDetail();
        _list.DoubleClick += (_, _) => Accept();
        _list.KeyDown += OnListKeyDown;

        _detailLabel = CreateLabel(string.Empty, tabIndex: 1);
        _detailLabel.Dock = DockStyle.Bottom;
        _detailLabel.MaximumSize = new Size(680, 0);
        _detailLabel.AutoSize = true;
        _detailLabel.AccessibleName = "Details of the selected item";
        _detailLabel.Padding = new Padding(8);

        var accept = CreateButton(
            _actionButtonText ?? "&Go to",
            (_, _) => Accept(),
            "Use the selected item",
            tabIndex: 2);

        var cancel = CreateButton("&Close", (_, _) => Cancel(), "Close without choosing", tabIndex: 3);

        Controls.Add(_list);
        Controls.Add(_detailLabel);
        Controls.Add(CreateButtonRow(accept, cancel));

        SetDefaultButton(accept);
        SetCancelButton(cancel);

        UpdateDetail();
    }

    protected override void FocusFirstControl() => _list.Focus();

    #endregion

    #region Selection

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
            return;

        Accept();
        e.Handled = e.SuppressKeyPress = true;
    }

    /// <summary>
    /// Shows and speaks the detail for the selected item.
    ///
    /// Spoken politely, so it queues behind the list box's own "3 of 17, such and such" rather than
    /// cutting it off. Getting that wrong makes fast arrowing through a list unintelligible.
    /// </summary>
    private void AnnounceDetail()
    {
        UpdateDetail();

        if (_describeInDetail is null || CurrentItem is not { } item)
            return;

        Announce(_describeInDetail(item));
    }

    private void UpdateDetail()
    {
        _detailLabel.Text = _describeInDetail is not null && CurrentItem is { } item
            ? _describeInDetail(item)
            : string.Empty;
    }

    private T? CurrentItem => _list.SelectedItem is ItemWrapper wrapper ? wrapper.Item : null;

    private void Accept()
    {
        if (CurrentItem is not { } item)
        {
            Play(AudioCue.Boundary);
            Announce("Nothing is selected.", AnnouncementPriority.Assertive);
            return;
        }

        Selected = item;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        Selected = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override string BuildKeyHelp() =>
        "Arrow keys move through the list. Typing a letter jumps to the next entry starting with " +
        "it. Home and End go to the first and last. Enter chooses. Escape closes.";

    #endregion

    #region Item wrapper
    // The ListBox shows whatever ToString returns, so the item is wrapped with its description
    // rather than requiring every list type to override ToString for this one purpose.

    private sealed class ItemWrapper(T item, string text)
    {
        public T Item { get; } = item;

        public override string ToString() => text;
    }

    #endregion

    #region Convenience

    /// <summary>Shows the dialog and returns the chosen item, or null.</summary>
    public static T? Choose(
        IWin32Window owner,
        ISpeechService speech,
        IAudioCueService cues,
        string title,
        string purpose,
        IReadOnlyList<T> items,
        Func<T, string> describe,
        Func<T, string>? describeInDetail = null,
        string? actionButtonText = null)
    {
        using var dialog = new ListSelectionDialog<T>(
            speech, cues, title, purpose, items, describe, describeInDetail, actionButtonText);

        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Selected : null;
    }

    #endregion
}

#endregion
