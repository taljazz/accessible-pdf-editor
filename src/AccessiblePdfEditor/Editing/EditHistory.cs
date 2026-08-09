using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Editing;

// =====================================================================================
//  EditHistory.cs
//
//  The undo and redo stack, built so it can be listened to rather than looked at.
//
//  Undo in a visual program is cheap: press Ctrl+Z, watch the page, see what came back.
//  Undo without sight is a leap of faith unless the program says what it just reversed.
//  So every operation here returns a sentence — "undone: set the description of figure 3
//  on page 4" — and the whole history can be read out on request, which is the equivalent
//  of glancing back over what you have done.
//
//  The other thing this class provides is an honest answer to "have I finished?". A
//  remediation session involves dozens of small repairs, and knowing that 14 changes are
//  pending, of which 9 are alt texts, is what tells the user whether they are done.
// =====================================================================================

#region HistoryOperationResult

/// <summary>The outcome of an undo or redo.</summary>
public readonly record struct HistoryOperationResult(bool Succeeded, string Message, EditCommand? Command)
{
    public static HistoryOperationResult Nothing(string message) => new(false, message, null);

    public static HistoryOperationResult Done(string message, EditCommand command) =>
        new(true, message, command);
}

#endregion

#region EditHistory

/// <summary>The stack of changes made to a document, with undo and redo.</summary>
public sealed class EditHistory
{
    #region State
    // Two stacks rather than a list with an index: the shape enforces the rule that making a new
    // change after undoing discards the redo branch, which is what every editor does and what a
    // user expects even when they cannot see it happen.

    private readonly List<EditCommand> _done = [];
    private readonly List<EditCommand> _undone = [];
    private readonly PdfDocumentModel _document;

    /// <summary>
    /// How long after a change a related one may still merge into it. Typing pauses of more than a
    /// couple of seconds mark a new thought, and therefore a new undo step.
    /// </summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(2);

    public EditHistory(PdfDocumentModel document)
    {
        _document = document;
    }

    #endregion

    #region Position

    /// <summary>Whether there is anything to undo.</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>Whether there is anything to redo.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>The number of changes currently applied.</summary>
    public int AppliedCount => _done.Count;

    /// <summary>Where the history currently sits.</summary>
    public HistoryPosition Position => (_done.Count, _undone.Count) switch
    {
        (0, 0) => HistoryPosition.Empty,
        (0, _) => HistoryPosition.AtOldest,
        (_, 0) => HistoryPosition.AtLatest,
        _ => HistoryPosition.InMiddle,
    };

    /// <summary>The changes that have been made, oldest first.</summary>
    public IReadOnlyList<EditCommand> AppliedChanges => _done;

    /// <summary>
    /// The most cautious confidence level among the applied changes. The save path uses it to
    /// decide how emphatically to warn: a session of nothing but field values needs no ceremony,
    /// one that rebuilt a structure tree does.
    /// </summary>
    public EditConfidence HighestRisk =>
        _done.Count == 0
            ? EditConfidence.Safe
            : _done.Max(c => c.Confidence);

    #endregion

    #region Making changes

    /// <summary>
    /// Applies a change and adds it to the history.
    ///
    /// A successful change discards the redo branch, because the document has now diverged from
    /// whatever was undone and those changes can no longer be replayed onto it.
    /// </summary>
    public EditResult Do(EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var result = command.Apply(_document);

        if (!result.Succeeded)
            return result;

        _undone.Clear();

        // Absorb into the previous change where they are really one action — typing a value, or
        // adjusting the same setting twice in a row.
        if (_done.Count > 0)
        {
            var previous = _done[^1];

            if (previous.CanMergeWith(command)
                && command.MadeAt - previous.MadeAt <= MergeWindow)
            {
                previous.MergeWith(command);
                return result;
            }
        }

        _done.Add(command);
        return result;
    }

    #endregion

    #region Undo and redo

    /// <summary>Reverses the most recent change and says what it was.</summary>
    public HistoryOperationResult Undo()
    {
        if (_done.Count == 0)
            return HistoryOperationResult.Nothing("There is nothing to undo.");

        var command = _done[^1];
        var result = command.Revert(_document);

        if (!result.Succeeded)
            return HistoryOperationResult.Nothing(result.Message);

        _done.RemoveAt(_done.Count - 1);
        _undone.Add(command);

        // Naming what was reversed is the whole point. "Undone" on its own leaves the user to work
        // out what changed, which is exactly the information they cannot get by looking.
        string remaining = _done.Count == 0
            ? "No changes remain."
            : $"{_done.Count} {(_done.Count == 1 ? "change" : "changes")} remain.";

        return HistoryOperationResult.Done($"Undone: {command.Description}. {remaining}", command);
    }

    /// <summary>Re-applies the most recently undone change and says what it was.</summary>
    public HistoryOperationResult Redo()
    {
        if (_undone.Count == 0)
            return HistoryOperationResult.Nothing("There is nothing to redo.");

        var command = _undone[^1];
        var result = command.Apply(_document);

        if (!result.Succeeded)
            return HistoryOperationResult.Nothing(result.Message);

        _undone.RemoveAt(_undone.Count - 1);
        _done.Add(command);

        return HistoryOperationResult.Done($"Redone: {command.Description}.", command);
    }

    #endregion

    #region Reading the history aloud

    /// <summary>
    /// What undo would do next, phrased for a menu item or a spoken prompt. Answers "what happens
    /// if I press Ctrl+Z" before the user commits to finding out.
    /// </summary>
    public string DescribeNextUndo() =>
        _done.Count == 0 ? "Nothing to undo" : $"Undo {_done[^1].Description}";

    /// <summary>What redo would do next.</summary>
    public string DescribeNextRedo() =>
        _undone.Count == 0 ? "Nothing to redo" : $"Redo {_undone[^1].Description}";

    /// <summary>
    /// A summary of everything changed in this session, grouped by kind.
    ///
    /// Read out before saving and when the user asks what they have done. Grouped rather than
    /// listed, because after an hour of remediation a flat list of ninety changes is unusable,
    /// whereas "41 image descriptions, 12 field labels, 3 headings" is exactly the answer.
    /// </summary>
    public string SummariseChanges()
    {
        if (_done.Count == 0)
            return "No changes have been made.";

        var groups = _done
            .GroupBy(c => c.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {DescribeKind(g.Key, g.Count())}")
            .ToList();

        string total = _done.Count == 1 ? "1 change" : $"{_done.Count} changes";
        return $"{total}: {string.Join(", ", groups)}.";
    }

    /// <summary>The most recent changes, newest first, for reading through one at a time.</summary>
    public IEnumerable<string> ListRecentChanges(int maximum = 20) =>
        Enumerable.Reverse(_done)
            .Take(maximum)
            .Select((command, index) => $"{index + 1}. {command.Description}");

    /// <summary>
    /// Names a kind of change in words, pluralised. Written out rather than derived from the enum
    /// name, because "FormFieldValue" spoken aloud is not a phrase anybody uses.
    /// </summary>
    private static string DescribeKind(EditKind kind, int count)
    {
        bool plural = count != 1;

        return kind switch
        {
            EditKind.FormFieldValue => plural ? "field values" : "field value",
            EditKind.FormFieldLabel => plural ? "field labels" : "field label",
            EditKind.FormFieldCreated => plural ? "new fields" : "new field",
            EditKind.AlternateText => plural ? "image descriptions" : "image description",
            EditKind.ActualText => plural ? "text replacements" : "text replacement",
            EditKind.ExpansionText => plural ? "abbreviation expansions" : "abbreviation expansion",
            EditKind.StructureType => plural ? "structure changes" : "structure change",
            EditKind.ReadingOrder => plural ? "reading order changes" : "reading order change",
            EditKind.ArtifactMarking => plural ? "page furniture markings" : "page furniture marking",
            EditKind.TableHeaders => plural ? "table header changes" : "table header change",
            EditKind.StructureTreeCreated => "structure tree",
            EditKind.AnnotationAdded => plural ? "comments added" : "comment added",
            EditKind.AnnotationEdited => plural ? "comments edited" : "comment edited",
            EditKind.AnnotationDeleted => plural ? "comments deleted" : "comment deleted",
            EditKind.ContentAuthored => plural ? "pieces of new content" : "piece of new content",
            EditKind.PageOperation => plural ? "page changes" : "page change",
            EditKind.Metadata => plural ? "document details" : "document detail",
            EditKind.Language => "document language",
            EditKind.ViewerPreference => plural ? "viewer settings" : "viewer setting",
            _ => plural ? "changes" : "change",
        };
    }

    #endregion

    #region Clearing

    /// <summary>
    /// Forgets everything, without changing the document. Called after a successful save, at which
    /// point the changes are in the file and undoing them would put the document out of step with
    /// what is on disk.
    /// </summary>
    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
    }

    #endregion
}

#endregion
