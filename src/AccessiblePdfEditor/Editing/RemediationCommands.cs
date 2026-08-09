using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Editing;

// =====================================================================================
//  RemediationCommands.cs
//
//  The commands that repair a document's accessibility.
//
//  This is the heart of what makes this an EDITOR rather than a reader. Each of these
//  fixes a specific way a PDF fails a blind reader, and each is undoable and describes
//  itself out loud.
//
//  They are ordered roughly by how much difference they make:
//
//    alt text          — turns a hole in the document into a sentence
//    field labels      — turns "edit box" into "date of birth"
//    heading level     — repairs the outline the whole document is navigated by
//    table headers     — turns a bare number into "March, Revenue, 4200"
//    artifact marking  — stops a running header being read on all two hundred pages
//    language          — stops a French document being read in an English voice
//    title             — makes the document announce its name instead of its filename
//
//  Every one of them captures the previous value at construction time, because by the time
//  undo runs the old value is gone from the model and nothing else remembers it.
// =====================================================================================

#region Setting alternate text — the highest-value repair there is

/// <summary>Sets or clears a figure's alternate text.</summary>
public sealed class SetAlternateTextCommand : EditCommand
{
    private readonly FigureElement _figure;
    private readonly string? _newText;
    private readonly string? _previousText;
    private readonly bool _wasMarkedDecorative;

    public SetAlternateTextCommand(FigureElement figure, string? alternateText)
    {
        _figure = figure ?? throw new ArgumentNullException(nameof(figure));
        _newText = string.IsNullOrWhiteSpace(alternateText) ? null : alternateText.Trim();

        // Captured now. After Apply runs, the figure no longer knows what it used to say.
        _previousText = figure.AlternateText;
        _wasMarkedDecorative = figure.IsMarkedDecorative;
    }

    public override EditKind Kind => EditKind.AlternateText;

    /// <summary>
    /// Adding an /Alt attribute changes nothing else in the file, so it is as safe as an edit gets.
    /// </summary>
    public override EditConfidence Confidence => EditConfidence.Safe;

    public override DocumentElement AffectedElement => _figure;

    public override string Description =>
        _newText is null
            ? $"cleared the description of the figure on page {_figure.PageNumber}"
            : $"described the figure on page {_figure.PageNumber} as \"{Shorten(_newText)}\"";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _figure.SetAlternateText(_newText);

        return EditResult.Ok(_newText is null
            ? "Description cleared."
            : $"Figure described as: {_newText}");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        if (_wasMarkedDecorative)
            _figure.MarkDecorative();
        else
            _figure.SetAlternateText(_previousText);

        return EditResult.Ok("Description restored.");
    }

    internal static string Shorten(string text) =>
        text.Length <= 50 ? text : string.Concat(text.AsSpan(0, 47).TrimEnd(), "…");
}

/// <summary>
/// Marks a figure as decorative, meaning it carries nothing worth describing.
///
/// A separate command from clearing the alt text, because they are opposite statements: one says
/// "there is nothing here to describe", the other says "nobody has described this yet". Collapsing
/// them would let a user accidentally declare a chart decorative when they meant to skip it for now.
/// </summary>
public sealed class MarkFigureDecorativeCommand : EditCommand
{
    private readonly FigureElement _figure;
    private readonly string? _previousText;
    private readonly bool _wasMarkedDecorative;

    public MarkFigureDecorativeCommand(FigureElement figure)
    {
        _figure = figure ?? throw new ArgumentNullException(nameof(figure));
        _previousText = figure.AlternateText;
        _wasMarkedDecorative = figure.IsMarkedDecorative;
    }

    public override EditKind Kind => EditKind.AlternateText;

    public override DocumentElement AffectedElement => _figure;

    public override string Description =>
        $"marked the figure on page {_figure.PageNumber} as decorative";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _figure.MarkDecorative();
        return EditResult.Ok("Marked as decorative. It will be skipped when reading.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        if (_wasMarkedDecorative)
            _figure.MarkDecorative();
        else
            _figure.SetAlternateText(_previousText);

        return EditResult.Ok("No longer marked as decorative.");
    }
}

#endregion

#region Setting a form field's accessible label

/// <summary>
/// Writes a form field's tooltip, which is the name a screen reader announces for it.
///
/// This is the repair that turns an unfillable form into a fillable one. A field with no /TU
/// announces itself as "edit box" and nothing more; the user is asked to type something without
/// being told what. Writing a label fixes it permanently, for every reader, in the file itself.
/// </summary>
public sealed class SetFieldLabelCommand : EditCommand
{
    private readonly PdfFormField _field;
    private readonly string? _newLabel;
    private readonly string? _previousToolTip;
    private readonly bool _wasAlreadyPendingWrite;

    public SetFieldLabelCommand(PdfFormField field, string? label)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _newLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        _previousToolTip = field.ToolTip;
        _wasAlreadyPendingWrite = field.IsLabelModified;
    }

    public override EditKind Kind => EditKind.FormFieldLabel;

    public override DocumentElement AffectedElement => _field;

    public override string Description =>
        _newLabel is null
            ? $"cleared the label of the field on page {_field.PageNumber}"
            : $"labelled the field on page {_field.PageNumber} as \"{_newLabel}\"";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _field.SetAccessibleLabel(_newLabel);

        return EditResult.Ok(_newLabel is null
            ? "Label cleared."
            : $"Field labelled: {_newLabel}. This is saved into the document, so it will work for " +
              "everyone who opens it afterwards.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _field.RestoreAccessibleLabel(_previousToolTip, _wasAlreadyPendingWrite);
        return EditResult.Ok("Label restored.");
    }
}

#endregion

#region Setting a form field's value

/// <summary>
/// Sets a form field's value. The ordinary act of filling in a form, made undoable.
/// </summary>
public sealed class SetFieldValueCommand : EditCommand
{
    private readonly PdfFormField _field;
    private readonly string _previousValue;
    private readonly bool _wasModified;
    private string _newValue;

    public SetFieldValueCommand(PdfFormField field, string value)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _newValue = value ?? string.Empty;

        _previousValue = CaptureValue(field);
        _wasModified = field.IsModified;
    }

    public override EditKind Kind => EditKind.FormFieldValue;

    public override DocumentElement AffectedElement => _field;

    public override string Description =>
        _newValue.Length == 0
            ? $"cleared {_field.Label}"
            : $"set {_field.Label} to \"{SetAlternateTextCommand.Shorten(_newValue)}\"";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        var validation = _field.TrySetValue(_newValue);

        return validation.Accepted
            ? EditResult.Ok(validation.Message)
            : EditResult.Failed(validation.Message);
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _field.ApplyLoadedValue(_previousValue);

        // A field that was already edited before this command must stay edited after undoing it,
        // or the save path would skip it and silently lose the earlier change.
        if (_wasModified)
            _field.TrySetValue(_previousValue);

        return EditResult.Ok($"{_field.Label} restored to {(_previousValue.Length == 0 ? "blank" : _previousValue)}.");
    }

    /// <summary>
    /// Typing into a field produces one command per keystroke from the UI. Merging them means one
    /// undo step for one typed value, rather than fifteen presses of Ctrl+Z each announcing a
    /// near-identical change.
    /// </summary>
    public override bool CanMergeWith(EditCommand later) =>
        later is SetFieldValueCommand other && ReferenceEquals(other._field, _field);

    public override void MergeWith(EditCommand later)
    {
        if (later is SetFieldValueCommand other)
            _newValue = other._newValue;
    }

    /// <summary>
    /// Reads a field's current value in the form that can be given back to TrySetValue. Each field
    /// type stores its value differently, so this asks each for the representation it accepts.
    /// </summary>
    private static string CaptureValue(PdfFormField field) => field switch
    {
        TextFormField text => text.Value,
        CheckBoxFormField box => box.IsChecked ? box.CheckedStateName : box.UncheckedStateName,
        RadioGroupFormField radio => radio.SelectedExportValue ?? string.Empty,
        ChoiceFormField choice => choice.CustomText
            ?? string.Join(";", choice.SelectedExportValues),
        _ => string.Empty,
    };
}

#endregion

#region Correcting document structure

/// <summary>
/// Changes a heading's level, or promotes a paragraph to a heading.
///
/// Repairs the outline the whole document is navigated by. On an untagged document the levels are
/// this program's guesses, and a guess that put every heading at level 1 leaves the user with a
/// flat list where there should be a hierarchy.
/// </summary>
public sealed class SetHeadingLevelCommand : EditCommand
{
    private readonly HeadingElement _heading;
    private readonly HeadingLevel _newLevel;
    private readonly HeadingLevel _previousLevel;

    public SetHeadingLevelCommand(HeadingElement heading, HeadingLevel level)
    {
        _heading = heading ?? throw new ArgumentNullException(nameof(heading));
        _newLevel = level;
        _previousLevel = heading.Level;
    }

    public override EditKind Kind => EditKind.StructureType;

    /// <summary>
    /// Changing a level in an already-tagged document rewrites part of the structure tree, which is
    /// a heavier operation than setting an attribute.
    /// </summary>
    public override EditConfidence Confidence =>
        _heading.IsFromRealTags ? EditConfidence.Rewrites : EditConfidence.Additive;

    public override DocumentElement AffectedElement => _heading;

    public override string Description =>
        $"made \"{SetAlternateTextCommand.Shorten(_heading.Text)}\" a level {(int)_newLevel} heading";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _heading.Level = _newLevel;
        return EditResult.Ok($"Now a level {(int)_newLevel} heading.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _heading.Level = _previousLevel;

        return EditResult.Ok(_previousLevel == HeadingLevel.None
            ? "Heading level cleared."
            : $"Back to a level {(int)_previousLevel} heading.");
    }
}

/// <summary>
/// Marks a table cell as a header, and says which axis it governs.
///
/// Without header cells, every value in a table is announced bare — "4200" with nothing to say
/// what it measures or when. Marking the headers is what lets each cell announce itself as
/// "March, Revenue, 4200".
/// </summary>
public sealed class SetTableCellRoleCommand : EditCommand
{
    private readonly TableCellElement _cell;
    private readonly TableCellRole _newRole;
    private readonly TableCellRole _previousRole;

    public SetTableCellRoleCommand(TableCellElement cell, TableCellRole role)
    {
        _cell = cell ?? throw new ArgumentNullException(nameof(cell));
        _newRole = role;
        _previousRole = cell.CellRole;
    }

    public override EditKind Kind => EditKind.TableHeaders;

    public override EditConfidence Confidence => EditConfidence.Additive;

    public override DocumentElement AffectedElement => _cell;

    public override string Description
    {
        get
        {
            string what = _newRole switch
            {
                TableCellRole.ColumnHeader => "a column header",
                TableCellRole.RowHeader => "a row header",
                TableCellRole.Header => "a header",
                _ => "a data cell",
            };

            return $"made \"{SetAlternateTextCommand.Shorten(_cell.Text)}\" {what}";
        }
    }

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _cell.CellRole = _newRole;
        return EditResult.Ok($"Cell is now {DescribeRole(_newRole)}.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _cell.CellRole = _previousRole;
        return EditResult.Ok($"Cell is back to {DescribeRole(_previousRole)}.");
    }

    private static string DescribeRole(TableCellRole role) => role switch
    {
        TableCellRole.ColumnHeader => "a column header",
        TableCellRole.RowHeader => "a row header",
        TableCellRole.Header => "a header",
        _ => "a data cell",
    };
}

/// <summary>
/// Marks a whole row's cells as headers in one action.
///
/// Exists because marking a header row one cell at a time is the single most repetitive task in
/// table remediation, and a repair nobody completes is not a repair.
/// </summary>
public sealed class MarkHeaderRowCommand : EditCommand
{
    private readonly TableRowElement _row;
    private readonly List<(TableCellElement Cell, TableCellRole Previous)> _previous = [];

    public MarkHeaderRowCommand(TableRowElement row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));

        foreach (var cell in row.Cells)
            _previous.Add((cell, cell.CellRole));
    }

    public override EditKind Kind => EditKind.TableHeaders;

    public override EditConfidence Confidence => EditConfidence.Additive;

    public override DocumentElement AffectedElement => _row;

    public override string Description =>
        $"made row {_row.RowNumber} a header row, {_previous.Count} cells";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        foreach (var (cell, _) in _previous)
            cell.CellRole = TableCellRole.ColumnHeader;

        return EditResult.Ok(
            $"Row {_row.RowNumber} is now a header row. Its {_previous.Count} cells will be " +
            "announced with the values beneath them.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        foreach (var (cell, previous) in _previous)
            cell.CellRole = previous;

        return EditResult.Ok($"Row {_row.RowNumber} is no longer a header row.");
    }
}

#endregion

#region Document-level accessibility settings

/// <summary>
/// Sets the document's language.
///
/// One of the cheapest and most valuable repairs available. Without it a screen reader reads the
/// document in whatever voice it is currently using, so a French document read by an English voice
/// is not merely accented but genuinely unintelligible.
/// </summary>
public sealed class SetDocumentLanguageCommand : EditCommand
{
    private readonly string? _newLanguage;
    private readonly string? _previousLanguage;

    public SetDocumentLanguageCommand(PdfDocumentModel document, string? language)
    {
        _newLanguage = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        _previousLanguage = document.Metadata.Language;
    }

    public override EditKind Kind => EditKind.Language;

    public override string Description =>
        _newLanguage is null
            ? "cleared the document language"
            : $"set the document language to {DescribeLanguage(_newLanguage)}";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        document.Metadata.Language = _newLanguage;

        return EditResult.Ok(_newLanguage is null
            ? "Document language cleared."
            : $"Document language set to {DescribeLanguage(_newLanguage)}. " +
              "A screen reader will now use the right voice for it.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        document.Metadata.Language = _previousLanguage;
        return EditResult.Ok("Document language restored.");
    }

    /// <summary>
    /// Turns a language tag into its name. "en-GB" spoken as a tag means nothing to most people;
    /// "English, United Kingdom" is what they would recognise.
    /// </summary>
    public static string DescribeLanguage(string tag)
    {
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(tag);
            return culture.EnglishName;
        }
        catch
        {
            return tag;
        }
    }
}

/// <summary>
/// Sets the document's title and asks viewers to display it.
///
/// The two go together and are useless apart. A title alone does nothing; the display flag is what
/// makes a reader announce "Annual Report 2026" on opening instead of
/// "AR26-final-v3-USE-THIS.pdf". Setting one without the other is the commonest way this repair is
/// got wrong, so this command does both.
/// </summary>
public sealed class SetDocumentTitleCommand : EditCommand
{
    private readonly string? _newTitle;
    private readonly string? _previousTitle;
    private readonly bool _previouslyDisplayed;

    public SetDocumentTitleCommand(PdfDocumentModel document, string? title)
    {
        _newTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        _previousTitle = document.Metadata.Title;
        _previouslyDisplayed = document.Metadata.DisplaysDocumentTitle;
    }

    public override EditKind Kind => EditKind.Metadata;

    public override string Description =>
        _newTitle is null
            ? "cleared the document title"
            : $"titled the document \"{_newTitle}\"";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        document.Metadata.Title = _newTitle;
        document.Root.Title = _newTitle ?? document.FileName;

        if (_newTitle is not null)
            document.Metadata.DisplaysDocumentTitle = true;

        return EditResult.Ok(_newTitle is null
            ? "Document title cleared."
            : $"Document titled: {_newTitle}. Readers will now announce this instead of the filename.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        document.Metadata.Title = _previousTitle;
        document.Metadata.DisplaysDocumentTitle = _previouslyDisplayed;
        document.Root.Title = _previousTitle ?? document.FileName;

        return EditResult.Ok("Document title restored.");
    }
}

#endregion

#region Marking page furniture

/// <summary>
/// Marks text as page furniture, so it is skipped when reading straight through.
///
/// The repair that makes a long document bearable. An unmarked running header is read at every
/// page boundary; over two hundred pages that is two hundred interruptions carrying no information.
/// </summary>
public sealed class MarkAsArtifactCommand : EditCommand
{
    private readonly TextElement _element;
    private readonly string _artifactType;
    private ArtifactElement? _replacement;
    private int _indexAmongSiblings = -1;

    public MarkAsArtifactCommand(TextElement element, string artifactType = "page furniture")
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _artifactType = artifactType;
    }

    public override EditKind Kind => EditKind.ArtifactMarking;

    public override EditConfidence Confidence => EditConfidence.Additive;

    public override DocumentElement AffectedElement => (DocumentElement?)_replacement ?? _element;

    public override string Description =>
        $"marked \"{SetAlternateTextCommand.Shorten(_element.Text)}\" as {_artifactType}";

    /// <summary>
    /// Replaces the element in the tree rather than setting a flag on it, because artifact-ness is
    /// carried by the element type. The original is kept so undo can put it back in the same place.
    /// </summary>
    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        var parent = _element.Parent;
        if (parent is null)
            return EditResult.Failed("That text is not attached to a page.");

        _indexAmongSiblings = _element.IndexAmongSiblings;

        _replacement = new ArtifactElement(_element.PageNumber, _element.RawText, _artifactType)
        {
            FontSize = _element.FontSize,
            FontName = _element.FontName,
            IsBold = _element.IsBold,
            IsItalic = _element.IsItalic,
            ClassificationReason = "marked as page furniture by you",
        };

        _replacement.Bounds = _element.Bounds;

        parent.RemoveChild(_element);
        parent.AddChild(_replacement);

        if (_indexAmongSiblings >= 0)
            parent.MoveChild(_replacement, _indexAmongSiblings);

        document.RebuildReadingOrder();

        return EditResult.Ok($"Marked as {_artifactType}. It will be skipped when reading straight through.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        if (_replacement?.Parent is not { } parent)
            return EditResult.Failed("That change could not be undone.");

        parent.RemoveChild(_replacement);
        parent.AddChild(_element);

        if (_indexAmongSiblings >= 0)
            parent.MoveChild(_element, _indexAmongSiblings);

        document.RebuildReadingOrder();
        _replacement = null;

        return EditResult.Ok("No longer marked as page furniture.");
    }
}

#endregion

#region Reading order

/// <summary>
/// Moves an element earlier or later in the reading order.
///
/// Repairs the case where a document's content is announced in an order that makes no sense — a
/// caption before its figure, a sidebar interrupting a sentence. Invisible to a sighted reader,
/// who sees the layout, and completely disorienting to a listener, who only has the order.
/// </summary>
public sealed class MoveElementCommand : EditCommand
{
    private readonly DocumentElement _element;
    private readonly int _newIndex;
    private readonly int _previousIndex;

    public MoveElementCommand(DocumentElement element, int newIndex)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _newIndex = newIndex;
        _previousIndex = element.IndexAmongSiblings;
    }

    public override EditKind Kind => EditKind.ReadingOrder;

    public override EditConfidence Confidence => EditConfidence.Rewrites;

    public override DocumentElement AffectedElement => _element;

    public override string Description =>
        $"moved \"{SetAlternateTextCommand.Shorten(_element.FullText)}\" " +
        $"from position {_previousIndex + 1} to position {_newIndex + 1}";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        if (_element.Parent is not { } parent)
            return EditResult.Failed("That element is not attached to anything.");

        if (!parent.MoveChild(_element, _newIndex))
            return EditResult.Failed("That element could not be moved.");

        document.RebuildReadingOrder();
        return EditResult.Ok($"Moved to position {_newIndex + 1}.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        if (_element.Parent is not { } parent)
            return EditResult.Failed("That element is not attached to anything.");

        parent.MoveChild(_element, _previousIndex);
        document.RebuildReadingOrder();

        return EditResult.Ok($"Moved back to position {_previousIndex + 1}.");
    }
}

#endregion
