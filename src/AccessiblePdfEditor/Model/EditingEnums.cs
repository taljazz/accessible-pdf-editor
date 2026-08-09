namespace AccessiblePdfEditor.Model;

// =====================================================================================
//  EditingEnums.cs
//
//  The vocabulary of CHANGING a document: what kind of edit was made, how confident we are
//  that it can be written back safely, how a save turned out, and how serious an
//  accessibility problem is.
//
//  A PDF is a fixed-layout format that was never designed to be edited in place, so several
//  of these enums exist specifically to be honest about what an edit can and cannot do,
//  rather than to let the app pretend every change is equally safe.
// =====================================================================================

#region Edit kinds — what a command changed, for grouping and for speech
// Every edit command reports one of these. The undo history is spoken using it ("undo, set alt
// text"), the save summary groups by it ("3 alt texts, 1 heading, 2 field values"), and the
// auditor uses it to decide which of its findings a given edit might have resolved.

/// <summary>The category of change an edit command makes.</summary>
public enum EditKind
{
    /// <summary>No change.</summary>
    None = 0,

    /// <summary>A form field's value was set.</summary>
    FormFieldValue,

    /// <summary>A form field's accessible label (PDF /TU) was set.</summary>
    FormFieldLabel,

    /// <summary>A form field was created.</summary>
    FormFieldCreated,

    /// <summary>Alternate text was set on a figure.</summary>
    AlternateText,

    /// <summary>The actual-text replacement for a piece of content was set.</summary>
    ActualText,

    /// <summary>An abbreviation's expanded form was set.</summary>
    ExpansionText,

    /// <summary>An element's structure type changed — for instance a paragraph became a heading.</summary>
    StructureType,

    /// <summary>An element moved within the structure tree, changing reading order.</summary>
    ReadingOrder,

    /// <summary>Content was marked as an artifact, or stopped being one.</summary>
    ArtifactMarking,

    /// <summary>Table header cells and their scopes were set.</summary>
    TableHeaders,

    /// <summary>A structure tree was created for a document that had none.</summary>
    StructureTreeCreated,

    /// <summary>An annotation was added.</summary>
    AnnotationAdded,

    /// <summary>An annotation's text was changed.</summary>
    AnnotationEdited,

    /// <summary>An annotation was deleted.</summary>
    AnnotationDeleted,

    /// <summary>New page content was authored.</summary>
    ContentAuthored,

    /// <summary>A page was added, removed, moved or rotated.</summary>
    PageOperation,

    /// <summary>Document metadata — title, author, subject, keywords — was changed.</summary>
    Metadata,

    /// <summary>The document language was set.</summary>
    Language,

    /// <summary>A viewer preference was changed, such as showing the title instead of the filename.</summary>
    ViewerPreference,
}

#endregion

#region Edit confidence — how safely a change can be written back
// This is the enum that keeps the app honest. PDF write-back ranges from completely safe
// (setting a string in a dictionary) to genuinely risky (rebuilding a structure tree in a file
// produced by an unknown tool). The user is told which they are about to do, and the save path
// treats them differently: anything below Safe forces a backup and says so out loud.

/// <summary>How confidently an edit can be written back into the file without side effects.</summary>
public enum EditConfidence
{
    /// <summary>
    /// A self-contained change to a value the format defines exactly — a field value, a tooltip,
    /// a title, the document language. No risk to anything else in the file.
    /// </summary>
    Safe = 0,

    /// <summary>
    /// A structural addition that does not disturb existing objects — adding an annotation, adding
    /// a structure element to a tree that already exists. Low risk, but the file grows structures
    /// that its original producer did not write.
    /// </summary>
    Additive,

    /// <summary>
    /// Rebuilds or substantially rewrites part of the file, such as creating a structure tree for a
    /// document that had none, or reordering pages that carry structure. A backup is always taken
    /// and the user is told before it happens.
    /// </summary>
    Rewrites,

    /// <summary>
    /// Known to lose information. Flattening a form turns fields into ink and cannot be undone
    /// after saving. Only ever performed on an explicit, confirmed request.
    /// </summary>
    Lossy,
}

#endregion

#region Undo stack position — what undo and redo would do next
// Read straight out to the user when they ask, and used to enable or disable menu items. Having
// it as an enum rather than two booleans means the "nothing has been done yet" case and the
// "everything has been undone" case are distinguishable, and they are announced differently.

/// <summary>Where the edit history currently sits.</summary>
public enum HistoryPosition
{
    /// <summary>No edits have been made.</summary>
    Empty = 0,

    /// <summary>At the newest edit. Undo is possible, redo is not.</summary>
    AtLatest,

    /// <summary>Somewhere in the middle. Both undo and redo are possible.</summary>
    InMiddle,

    /// <summary>Everything has been undone. Redo is possible, undo is not.</summary>
    AtOldest,
}

#endregion

#region Save outcome — how writing the file turned out
// Saving a PDF can fail in ways that matter and that a blind user must not have to infer from a
// silent no-op: the file may be open elsewhere, read-only, or on a disconnected drive. Each of
// these becomes a distinct spoken message.

/// <summary>The result of attempting to write the document to disk.</summary>
public enum SaveOutcome
{
    /// <summary>Nothing needed saving.</summary>
    NoChanges = 0,

    /// <summary>Written successfully.</summary>
    Saved,

    /// <summary>Written successfully to a different path than the one it was opened from.</summary>
    SavedAsCopy,

    /// <summary>The user was asked to confirm an overwrite or a lossy operation and declined.</summary>
    Cancelled,

    /// <summary>The target file is read-only or locked by another program.</summary>
    TargetNotWritable,

    /// <summary>
    /// The document was written but verification found the result did not read back correctly,
    /// so the original was restored from the backup.
    /// </summary>
    RolledBack,

    /// <summary>Writing failed. The accompanying message explains why.</summary>
    Failed,
}

#endregion

#region Accessibility issue severity — how badly a problem hurts a reader
// Ordered from most to least serious so findings sort naturally. The distinction that matters is
// Blocker versus the rest: a Blocker means some content simply cannot be read at all, which is a
// different situation from content that is merely awkward to read.

/// <summary>How much an accessibility problem affects someone reading with a screen reader.</summary>
public enum IssueSeverity
{
    /// <summary>
    /// Content cannot be reached at all — an unlabelled required field, or a page of scanned
    /// images with no text behind them.
    /// </summary>
    Blocker = 0,

    /// <summary>
    /// Content is reachable but its meaning is lost — a figure with no alt text, a table with no
    /// header cells, headings that are only visually bold.
    /// </summary>
    Serious,

    /// <summary>
    /// Reading works but is harder than it should be — skipped heading levels, page furniture that
    /// was never marked as an artifact and so is read on every page.
    /// </summary>
    Moderate,

    /// <summary>
    /// Good practice that is missing — no document title, no declared language on a document that
    /// is otherwise fine.
    /// </summary>
    Advisory,
}

#endregion

#region Issue fixability — whether the editor can repair a finding, and how
// Drives the remediation workflow. The user can ask to be taken through only those problems the
// app can actually fix, which is a far better use of their time than walking a list where most
// entries end in "you will need a different tool".

/// <summary>Whether and how a reported accessibility problem can be repaired here.</summary>
public enum IssueFixability
{
    /// <summary>
    /// The editor can repair it with no further information — setting the language from the
    /// document's own text, or marking a repeated header as an artifact.
    /// </summary>
    AutomaticallyFixable = 0,

    /// <summary>
    /// The editor can repair it once the user supplies the missing meaning — the alt text for a
    /// figure, or the label for a field. This is the bulk of real remediation work.
    /// </summary>
    FixableWithInput,

    /// <summary>
    /// Reported for the record but not repairable here — a scanned page needs OCR, which this
    /// editor does not do.
    /// </summary>
    NotFixableHere,
}

#endregion
