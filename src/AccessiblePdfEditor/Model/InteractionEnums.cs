namespace AccessiblePdfEditor.Model;

// =====================================================================================
//  InteractionEnums.cs
//
//  Everything describing what the user is DOING rather than what the document contains:
//  the form-field vocabulary, how far a keypress moves, how much the app says, and how an
//  attempted interaction turned out.
//
//  The document's own vocabulary lives in DocumentEnums.cs.
// =====================================================================================

#region Form field kinds — the interactive controls a PDF can carry
// Maps to the PDF field types /Tx, /Btn and /Ch, split out into the distinct behaviours a user
// actually meets. The PDF format collapses checkbox, radio and push button into one /Btn type
// distinguished only by flag bits, which is useless to announce; we split them at load time so
// every field can say what it really is.

/// <summary>The kind of interactive form field, split by behaviour rather than by PDF field type.</summary>
public enum FormFieldKind
{
    /// <summary>A field type we could not identify. Presented read-only.</summary>
    Unknown = 0,

    /// <summary>A single-line text box (PDF /Tx).</summary>
    Text,

    /// <summary>A text box that accepts line breaks (PDF /Tx with the Multiline flag).</summary>
    MultilineText,

    /// <summary>A single checkbox that toggles on and off (PDF /Btn).</summary>
    CheckBox,

    /// <summary>
    /// A group of mutually exclusive radio buttons (PDF /Btn with the Radio flag). Modelled as one
    /// field with several options, because that is the unit a user chooses from.
    /// </summary>
    RadioGroup,

    /// <summary>A drop-down list (PDF /Ch with the Combo flag).</summary>
    ComboBox,

    /// <summary>A drop-down list the user may also type into (PDF /Ch, Combo plus Edit flags).</summary>
    EditableComboBox,

    /// <summary>A scrolling list, single or multiple selection (PDF /Ch without the Combo flag).</summary>
    ListBox,

    /// <summary>A button that performs an action rather than holding a value (PDF /Btn, Pushbutton flag).</summary>
    PushButton,

    /// <summary>A digital signature field (PDF /Sig). We report its state but never sign.</summary>
    Signature,
}

#endregion

#region Field state — flags tracked per field for the whole editing session
// [Flags] because these genuinely co-occur: a field is routinely Required and Empty and Invalid
// at the same time, and the announcement has to mention all three. Tracking them on the field
// rather than recomputing lets the form-fill UI answer "how many required fields are still
// empty?" instantly, which is the question that matters when filling a long form.

/// <summary>Conditions that can hold for a form field, combined as flags.</summary>
[Flags]
public enum FieldStates
{
    /// <summary>Nothing special: an optional, writable, empty, valid field.</summary>
    None = 0,

    /// <summary>The document marks this field as required (PDF /Ff bit 2).</summary>
    Required = 1 << 0,

    /// <summary>The document marks this field read-only (PDF /Ff bit 1). Announced, never edited.</summary>
    ReadOnly = 1 << 1,

    /// <summary>The field currently holds a value.</summary>
    HasValue = 1 << 2,

    /// <summary>The value was changed in this session and is not yet saved.</summary>
    Modified = 1 << 3,

    /// <summary>The current value failed this field's own validation.</summary>
    Invalid = 1 << 4,

    /// <summary>
    /// The field has no usable label — no tooltip, no alternate name, and nothing recoverable from
    /// the page. A serious accessibility fault: the user is asked to fill in something unnamed.
    /// The auditor reports it and the remediation workflow offers to write one.
    /// </summary>
    Unlabelled = 1 << 5,

    /// <summary>Contents are masked when displayed (PDF /Ff Password flag).</summary>
    Password = 1 << 6,

    /// <summary>The field's value is excluded when the form is submitted (PDF /Ff NoExport flag).</summary>
    NoExport = 1 << 7,

    /// <summary>A signature field that already carries a signature.</summary>
    Signed = 1 << 8,

    /// <summary>Created in this session and not yet written to the file.</summary>
    NewlyCreated = 1 << 9,

    /// <summary>
    /// The field's accessible label was changed in this session and is not yet saved.
    ///
    /// Tracked separately from <see cref="Modified"/>, which covers the field's VALUE. They are
    /// different edits with different consequences: changing a value fills in a form, changing a
    /// label repairs the document for everyone who opens it afterwards. A user can do either
    /// without the other, and the save path has to write both.
    /// </summary>
    LabelModified = 1 << 10,
}

#endregion

#region Text field formats — what a text field will accept
// PDF stores input restrictions as JavaScript format actions, which we cannot execute and would
// not want to. Instead we recognise the common formats and enforce them ourselves, so a blind
// user is told "that is not a valid date" immediately on leaving the field rather than being
// silently rejected later by whoever receives the form.

/// <summary>The value format a text field expects, used for validation and for spoken guidance.</summary>
public enum TextFieldFormat
{
    /// <summary>Any text.</summary>
    PlainText = 0,

    /// <summary>Digits, with an optional sign and decimal part.</summary>
    Number,

    /// <summary>A currency amount.</summary>
    Currency,

    /// <summary>Digits only, no sign or separator — a reference or account number.</summary>
    DigitsOnly,

    /// <summary>A calendar date.</summary>
    Date,

    /// <summary>A time of day.</summary>
    Time,

    /// <summary>An email address.</summary>
    Email,

    /// <summary>A telephone number.</summary>
    Telephone,

    /// <summary>A postal or ZIP code.</summary>
    PostalCode,

    /// <summary>
    /// A comb field: a fixed number of single-character boxes (PDF /Ff Comb flag). The length is
    /// exact, not merely a maximum, so it is validated differently.
    /// </summary>
    Comb,
}

#endregion

#region Navigation granularity — how far one keypress moves
// The heart of reading by keyboard. A sighted reader's eye jumps between structures without
// thinking; this enum is how that becomes deliberate and available from the keyboard. The order
// runs from smallest to largest for the character/word/line/sentence/paragraph run, then lists
// the structural jumps, which are filters over the element list rather than steps through text.

/// <summary>The unit a navigation command moves by.</summary>
public enum NavigationGranularity
{
    /// <summary>One character. Used for spelling out a word or checking a typed value.</summary>
    Character = 0,

    /// <summary>One word.</summary>
    Word,

    /// <summary>One laid-out line, as it falls on the page.</summary>
    Line,

    /// <summary>One sentence. Usually the most comfortable unit for listening to prose.</summary>
    Sentence,

    /// <summary>One paragraph or other block element.</summary>
    Paragraph,

    /// <summary>The next or previous element of any kind, in reading order.</summary>
    Element,

    /// <summary>The next or previous heading, at any level.</summary>
    Heading,

    /// <summary>The next or previous heading at one specific level.</summary>
    HeadingAtLevel,

    /// <summary>The next or previous list.</summary>
    List,

    /// <summary>The next or previous item within the current list.</summary>
    ListItem,

    /// <summary>The next or previous table.</summary>
    Table,

    /// <summary>One cell within the current table.</summary>
    TableCell,

    /// <summary>The next or previous figure.</summary>
    Figure,

    /// <summary>The next or previous link.</summary>
    Link,

    /// <summary>The next or previous form field.</summary>
    FormField,

    /// <summary>The next or previous form field that still needs a value.</summary>
    UnfilledFormField,

    /// <summary>The next or previous annotation.</summary>
    Annotation,

    /// <summary>The next or previous accessibility problem found by the auditor.</summary>
    AccessibilityIssue,

    /// <summary>One page.</summary>
    Page,

    /// <summary>The next or previous search match.</summary>
    SearchMatch,
}

#endregion

#region Movement direction — which way a navigation command goes
// Separate from granularity so that every granularity works in both directions without doubling
// the enum, and so "first" and "last" are expressible without a special case at each call site.

/// <summary>Which way through the document a navigation command moves.</summary>
public enum MoveDirection
{
    /// <summary>Towards the end of the document.</summary>
    Next = 0,

    /// <summary>Towards the start of the document.</summary>
    Previous,

    /// <summary>Jump to the first item of the requested granularity.</summary>
    First,

    /// <summary>Jump to the last item of the requested granularity.</summary>
    Last,
}

#endregion

#region Verbosity — how much the app says
// A first-time listener wants roles and hints spelled out; a daily user wants the text and
// nothing else, because repeated boilerplate makes reading slower. Every element's description
// method takes this and decides for itself what to include, so the setting reaches everything
// without a single global "if" spread through the code.

/// <summary>How much detail spoken announcements include.</summary>
public enum VerbosityLevel
{
    /// <summary>Content only. Roles and hints are omitted unless nothing else would be said.</summary>
    Terse = 0,

    /// <summary>Role plus content. The default.</summary>
    Normal,

    /// <summary>Role, content, position, state and usage hints. Best while learning the app.</summary>
    Detailed,
}

#endregion

#region Announcement priority — whether speech waits its turn or cuts in
// Maps onto the polite/assertive distinction screen readers already use. Getting this wrong is
// the classic self-voicing bug: either everything talks over everything else, or an error is
// queued behind a paragraph and heard far too late to be useful.

/// <summary>Whether an announcement queues behind current speech or interrupts it.</summary>
public enum AnnouncementPriority
{
    /// <summary>Queue behind whatever is being said. Used for ordinary reading.</summary>
    Polite = 0,

    /// <summary>Interrupt at once. Used for errors and for anything the user just asked for.</summary>
    Assertive,
}

#endregion

#region Interaction outcome — the result of activating something
// Returned by every InteractiveElement.Activate call. The caller turns it into speech and an
// earcon, so a link, a checkbox and a button all report success and failure the same way.

/// <summary>What happened when the user activated an interactive element.</summary>
public enum InteractionOutcome
{
    /// <summary>Nothing happened.</summary>
    None = 0,

    /// <summary>The action completed.</summary>
    Succeeded,

    /// <summary>The value changed as a result.</summary>
    ValueChanged,

    /// <summary>The view moved somewhere else in this document.</summary>
    NavigatedWithinDocument,

    /// <summary>Something outside the document was opened, after the user confirmed it.</summary>
    OpenedExternally,

    /// <summary>The element cannot be activated — a read-only field, or a signature.</summary>
    NotAvailable,

    /// <summary>The user was asked to confirm and declined.</summary>
    Cancelled,

    /// <summary>The action failed. The accompanying message explains why.</summary>
    Failed,
}

#endregion

#region Reading mode — how continuous reading treats the document
// Structured reading follows the tag tree or the inferred structure and skips page furniture,
// which is what someone reading a report wants. Layout reading follows the page exactly, which
// is what someone checking a form or a table against a printed copy wants. Both are needed.

/// <summary>How the reader linearises a document for continuous reading.</summary>
public enum ReadingMode
{
    /// <summary>
    /// Follow logical structure, skip artifacts such as running heads and page numbers, and
    /// announce roles. The default, and the closest thing to how a sighted reader skims.
    /// </summary>
    Structured = 0,

    /// <summary>
    /// Follow the page's own layout line by line, including furniture, and announce nothing extra.
    /// Useful for checking a document against a printed copy.
    /// </summary>
    Layout,

    /// <summary>
    /// Raw extracted text with no interpretation at all. The fallback when structure is so broken
    /// that any interpretation would mislead.
    /// </summary>
    Raw,
}

#endregion
