using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace AccessiblePdfEditor.Ingestion;

// =====================================================================================
//  FormFieldReader.cs
//
//  Reads a document's AcroForm into the application's form field model.
//
//  Most of this file is about ONE problem: giving every field a name.
//
//  A PDF field carries an optional tooltip (/TU), and that tooltip is what a screen reader
//  announces. When it is present everything works. When it is absent — and in forms
//  produced by scanning, by Word export, or by anyone not thinking about accessibility, it
//  usually is — the user hears "edit box, blank" and is expected to type something into it.
//
//  So when there is no tooltip, the label is recovered from the page itself, by looking
//  where a sighted person would look: immediately to the left of the field on the same
//  line, or directly above it. That is not guesswork for its own sake; it reconstructs the
//  visual relationship the form's designer relied on and never wrote down. The result is
//  always reported as a recovered guess rather than passed off as the document's own, and
//  writing it back as a real /TU is one of the repairs this editor offers.
// =====================================================================================

#region FormFieldReader

/// <summary>Reads AcroForm fields into the document model, recovering labels where none exist.</summary>
internal static class FormFieldReader
{
    #region Entry point

    /// <summary>
    /// Reads every field of a form and attaches it to the page it appears on.
    /// </summary>
    public static void ReadInto(
        AcroForm form, PigDocument pig, PdfDocumentModel document, List<string> warnings)
    {
        foreach (var field in form.Fields)
            ReadField(field, prefix: null, document, warnings);
    }

    /// <summary>
    /// Reads one field and its descendants. The prefix carries the dotted path down the tree, which
    /// is how a PDF field's fully qualified name is formed and how the save layer finds it again.
    /// </summary>
    private static void ReadField(
        AcroFieldBase field,
        string? prefix,
        PdfDocumentModel document,
        List<string> warnings)
    {
        string partial = field.Information?.PartialName ?? string.Empty;
        string fullName = string.IsNullOrEmpty(prefix)
            ? partial
            : string.IsNullOrEmpty(partial) ? prefix : $"{prefix}.{partial}";

        try
        {
            // Radio groups and checkbox groups are non-terminal fields whose children are widgets
            // rather than fields in their own right. They are converted whole, so the recursion
            // must not descend into them.
            switch (field)
            {
                case AcroRadioButtonsField radioGroup:
                    Attach(BuildRadioGroup(radioGroup, fullName), field, document);
                    return;

                case AcroCheckboxesField checkboxes:
                    Attach(BuildGroupedCheckBox(checkboxes, fullName), field, document);
                    return;

                case AcroNonTerminalField container:
                    foreach (var child in container.Children)
                        ReadField(child, fullName, document, warnings);
                    return;
            }

            var built = BuildTerminalField(field, fullName);
            if (built is not null)
                Attach(built, field, document);
        }
        catch (Exception ex)
        {
            warnings.Add($"Form field '{fullName}' could not be read and has been skipped: {ex.Message}");
        }
    }

    #endregion

    #region Building each field type

    private static PdfFormField? BuildTerminalField(AcroFieldBase field, string fullName)
    {
        int page = field.PageNumber ?? 0;

        return field switch
        {
            AcroTextField text => BuildText(text, fullName, page),
            AcroCheckboxField checkbox => BuildCheckBox(checkbox, fullName, page),
            AcroComboBoxField combo => BuildCombo(combo, fullName, page),
            AcroListBoxField list => BuildListBox(list, fullName, page),
            AcroPushButtonField button => BuildPushButton(button, fullName, page),
            AcroSignatureField signature => BuildSignature(signature, fullName, page),
            AcroRadioButtonField single => BuildLoneRadioButton(single, fullName, page),
            _ => null,
        };
    }

    private static TextFormField BuildText(AcroTextField source, string fullName, int page)
    {
        bool isComb = source.Flags.HasFlag(AcroTextFieldFlags.Comb);
        string? toolTip = source.Information?.AlternateName;

        var field = new TextFormField(page, fullName, source.Value)
        {
            IsMultiline = source.IsMultiline,
            MaxLength = source.MaxLength,
            ToolTip = Clean(toolTip),
            MappingName = Clean(source.Information?.MappingName),
        };

        field.Format = TextFormField.InferFormat(field.ToolTip ?? fullName, fullName, isComb);

        ApplyCommonFlags(field, source.Flags.HasFlag(AcroTextFieldFlags.Required),
            source.Flags.HasFlag(AcroTextFieldFlags.ReadOnly),
            source.Flags.HasFlag(AcroTextFieldFlags.NoExport));

        if (source.Flags.HasFlag(AcroTextFieldFlags.Password))
            MarkPassword(field);

        return field;
    }

    private static CheckBoxFormField BuildCheckBox(AcroCheckboxField source, string fullName, int page)
    {
        var (onName, offName) = ReadToggleStateNames(source.Dictionary);

        var field = new CheckBoxFormField(page, fullName, source.IsChecked)
        {
            ToolTip = Clean(source.Information?.AlternateName),
            MappingName = Clean(source.Information?.MappingName),
            CheckedStateName = onName,
            UncheckedStateName = offName,
        };

        ApplyCommonFlags(field, source.Flags.HasFlag(AcroButtonFieldFlags.Required),
            source.Flags.HasFlag(AcroButtonFieldFlags.ReadOnly),
            source.Flags.HasFlag(AcroButtonFieldFlags.NoExport));

        return field;
    }

    /// <summary>
    /// Builds a checkbox from a group of widgets sharing one name. Several widgets can represent the
    /// same logical checkbox appearing on more than one page, so they collapse into a single field.
    /// </summary>
    private static CheckBoxFormField BuildGroupedCheckBox(AcroCheckboxesField source, string fullName)
    {
        var first = source.Children.OfType<AcroCheckboxField>().FirstOrDefault();
        int page = first?.PageNumber ?? source.PageNumber ?? 0;

        var (onName, offName) = ReadToggleStateNames(first?.Dictionary ?? source.Dictionary);

        var field = new CheckBoxFormField(page, fullName, first?.IsChecked ?? false)
        {
            ToolTip = Clean(source.Information?.AlternateName ?? first?.Information?.AlternateName),
            MappingName = Clean(source.Information?.MappingName),
            CheckedStateName = onName,
            UncheckedStateName = offName,
        };

        if (first is not null)
        {
            ApplyCommonFlags(field, first.Flags.HasFlag(AcroButtonFieldFlags.Required),
                first.Flags.HasFlag(AcroButtonFieldFlags.ReadOnly),
                first.Flags.HasFlag(AcroButtonFieldFlags.NoExport));
        }

        return field;
    }

    private static RadioGroupFormField BuildRadioGroup(AcroRadioButtonsField source, string fullName)
    {
        int page = source.Children.FirstOrDefault()?.PageNumber ?? source.PageNumber ?? 0;

        var field = new RadioGroupFormField(page, fullName)
        {
            ToolTip = Clean(source.Information?.AlternateName),
            MappingName = Clean(source.Information?.MappingName),
        };

        // The group's own /V names the chosen option. This is read directly rather than trusting
        // PdfPig's per-button IsSelected, which reports true for EVERY button in a group whose /V
        // is /Off — that is, for every group where nothing has been chosen. Believing it would make
        // an untouched form announce every radio option as already selected, which is both wrong
        // and actively misleading on a form someone is about to submit.
        string? groupValue = ReadNameValue(source.Dictionary);
        string? selected = null;

        foreach (var child in source.Children.OfType<AcroRadioButtonField>())
        {
            var (onName, _) = ReadToggleStateNames(child.Dictionary);

            // The kid's own on-state name is the export value. Falling back to the field's current
            // value would make every option in the group share one value, which would make the
            // group unselectable.
            string exportValue = onName;

            var option = new RadioOption(exportValue, label: null, child.PageNumber ?? page);

            if (child.Bounds is { } bounds)
            {
                option.Bounds = new PageRegion(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
            }

            field.AddOption(option);

            if (groupValue is not null
                && !groupValue.Equals("Off", StringComparison.Ordinal)
                && groupValue.Equals(exportValue, StringComparison.Ordinal))
            {
                selected = exportValue;
            }
        }

        ApplyCommonFlags(field, source.Flags.HasFlag(AcroButtonFieldFlags.Required),
            source.Flags.HasFlag(AcroButtonFieldFlags.ReadOnly),
            source.Flags.HasFlag(AcroButtonFieldFlags.NoExport));

        if (selected is not null)
            SetInitialValue(field, selected);

        return field;
    }

    /// <summary>
    /// Builds a group from a radio button that appears outside one. Malformed forms produce these,
    /// and a lone radio button with no group is still something the user must be able to reach.
    /// </summary>
    private static RadioGroupFormField BuildLoneRadioButton(AcroRadioButtonField source, string fullName, int page)
    {
        var (onName, _) = ReadToggleStateNames(source.Dictionary);

        var field = new RadioGroupFormField(page, fullName)
        {
            ToolTip = Clean(source.Information?.AlternateName),
        };

        field.AddOption(new RadioOption(onName, label: null, page));

        if (source.IsSelected)
            SetInitialValue(field, onName);

        return field;
    }

    private static ChoiceFormField BuildCombo(AcroComboBoxField source, string fullName, int page)
    {
        var field = new ChoiceFormField(page, fullName, isComboBox: true)
        {
            ToolTip = Clean(source.Information?.AlternateName),
            MappingName = Clean(source.Information?.MappingName),
            AllowsCustomText = source.Flags.HasFlag(AcroChoiceFieldFlags.Edit),
            AllowsMultipleSelection = source.Flags.HasFlag(AcroChoiceFieldFlags.MultiSelect),
        };

        PopulateChoiceOptions(field, source.Options, source.SelectedOptions);

        ApplyCommonFlags(field, source.Flags.HasFlag(AcroChoiceFieldFlags.Required),
            source.Flags.HasFlag(AcroChoiceFieldFlags.ReadOnly),
            source.Flags.HasFlag(AcroChoiceFieldFlags.NoExport));

        return field;
    }

    private static ChoiceFormField BuildListBox(AcroListBoxField source, string fullName, int page)
    {
        var field = new ChoiceFormField(page, fullName, isComboBox: false)
        {
            ToolTip = Clean(source.Information?.AlternateName),
            MappingName = Clean(source.Information?.MappingName),
            AllowsMultipleSelection = source.SupportsMultiSelect,
        };

        PopulateChoiceOptions(field, source.Options, source.SelectedOptions);

        ApplyCommonFlags(field, source.Flags.HasFlag(AcroChoiceFieldFlags.Required),
            source.Flags.HasFlag(AcroChoiceFieldFlags.ReadOnly),
            source.Flags.HasFlag(AcroChoiceFieldFlags.NoExport));

        return field;
    }

    private static void PopulateChoiceOptions(
        ChoiceFormField field,
        IReadOnlyList<AcroChoiceOption> options,
        IReadOnlyList<string> selected)
    {
        foreach (var option in options)
        {
            // PDF stores an option either as one string, or as a pair of [export value, display
            // text]. Where both exist the display text is the only part meant for a person, and
            // reading the export value instead is how a country list ends up read as codes.
            string? exportValue = option.HasExportValue ? option.ExportValue : option.Name;
            string? displayText = option.HasExportValue ? option.Name : null;

            // An option with neither is malformed. Skipping it is right: an unnamed entry in a list
            // is something the user could select without any way to know what they chose.
            if (string.IsNullOrEmpty(exportValue))
                continue;

            field.AddOption(new ChoiceOption(exportValue, displayText));
        }

        if (selected.Count > 0)
            SetInitialValue(field, string.Join(";", selected));
    }

    private static PushButtonFormField BuildPushButton(AcroPushButtonField source, string fullName, int page)
    {
        var (action, target) = ReadButtonAction(source.Dictionary);

        var field = new PushButtonFormField(page, fullName, action)
        {
            ToolTip = Clean(source.Information?.AlternateName),
            ActionTarget = target,
            Caption = ReadButtonCaption(source.Dictionary),
        };

        ApplyCommonFlags(field, required: false,
            source.Flags.HasFlag(AcroButtonFieldFlags.ReadOnly),
            source.Flags.HasFlag(AcroButtonFieldFlags.NoExport));

        return field;
    }

    private static SignatureFormField BuildSignature(AcroSignatureField source, string fullName, int page)
    {
        var field = new SignatureFormField(page, fullName)
        {
            ToolTip = Clean(source.Information?.AlternateName),
        };

        ReadSignatureDetails(source.Dictionary, field);
        return field;
    }

    #endregion

    #region Reading the raw dictionary
    // PdfPig surfaces the common properties but not everything, so several facts are read straight
    // out of the field dictionary. All of it is guarded: a malformed value costs one attribute, not
    // the whole field.

    /// <summary>
    /// Reads a toggle's on and off state names from its appearance dictionary.
    ///
    /// A checkbox's "checked" name is whatever the form's designer chose — /Yes, /On, /1, /Oui.
    /// Assuming /Yes produces a file that looks right here and arrives at its destination empty,
    /// which is the worst kind of bug: silent, and only discovered by the person waiting for the
    /// form.
    /// </summary>
    private static (string On, string Off) ReadToggleStateNames(DictionaryToken? dictionary)
    {
        const string defaultOn = "Yes";
        const string defaultOff = "Off";

        if (dictionary is null)
            return (defaultOn, defaultOff);

        try
        {
            if (!dictionary.TryGet(NameToken.Ap, out DictionaryToken? appearances) || appearances is null)
                return (defaultOn, defaultOff);

            if (!appearances.TryGet(NameToken.N, out DictionaryToken? normal) || normal is null)
                return (defaultOn, defaultOff);

            string? on = null;

            foreach (string key in normal.Data.Keys)
            {
                if (key.Equals("Off", StringComparison.Ordinal))
                    continue;

                on = key;
                break;
            }

            return (on ?? defaultOn, defaultOff);
        }
        catch
        {
            return (defaultOn, defaultOff);
        }
    }

    /// <summary>
    /// Reads a field's /V when it is stored as a name, which is how button-type fields record their
    /// current state. Returns null when /V is absent or is some other kind of value.
    /// </summary>
    private static string? ReadNameValue(DictionaryToken? dictionary)
    {
        if (dictionary is null)
            return null;

        try
        {
            if (dictionary.TryGet(NameToken.V, out NameToken? value) && value is not null)
                return value.Data;
        }
        catch
        {
            // A field with no readable value simply has nothing chosen.
        }

        return null;
    }

    private static string? ReadButtonCaption(DictionaryToken? dictionary)
    {
        if (dictionary is null)
            return null;

        try
        {
            // The caption lives in the widget's appearance characteristics, under /MK /CA.
            if (dictionary.TryGet(NameToken.Create("MK"), out DictionaryToken? characteristics)
                && characteristics is not null
                && characteristics.TryGet(NameToken.Create("CA"), out StringToken? caption))
            {
                return Clean(caption?.Data);
            }
        }
        catch
        {
            // Optional.
        }

        return null;
    }

    /// <summary>Reads what a push button's action would do, from its /A action dictionary.</summary>
    private static (ButtonAction Action, string? Target) ReadButtonAction(DictionaryToken? dictionary)
    {
        if (dictionary is null)
            return (ButtonAction.None, null);

        try
        {
            if (!dictionary.TryGet(NameToken.A, out DictionaryToken? action) || action is null)
                return (ButtonAction.None, null);

            if (!action.TryGet(NameToken.S, out NameToken? subtype) || subtype is null)
                return (ButtonAction.None, null);

            switch (subtype.Data)
            {
                case "SubmitForm":
                    return (ButtonAction.SubmitForm, ReadActionUri(action));

                case "ResetForm":
                    return (ButtonAction.ResetForm, null);

                case "ImportData":
                    return (ButtonAction.ImportData, null);

                case "URI":
                    return (ButtonAction.OpenUrl, ReadActionUri(action));

                case "JavaScript":
                    return (ButtonAction.RunJavaScript, null);

                case "GoTo":
                    return (ButtonAction.GoToDestination, null);

                case "Named":
                    if (action.TryGet(NameToken.Create("N"), out NameToken? named)
                        && named?.Data == "Print")
                    {
                        return (ButtonAction.Print, null);
                    }

                    return (ButtonAction.None, named?.Data);

                default:
                    return (ButtonAction.None, subtype.Data);
            }
        }
        catch
        {
            return (ButtonAction.None, null);
        }
    }

    private static string? ReadActionUri(DictionaryToken action)
    {
        // A submit action stores its destination in a file specification (/F), which may be a plain
        // string or a dictionary. A URI action stores it in /URI.
        if (action.TryGet(NameToken.Create("URI"), out StringToken? uri))
            return Clean(uri?.Data);

        if (action.TryGet(NameToken.F, out StringToken? file))
            return Clean(file?.Data);

        if (action.TryGet(NameToken.F, out DictionaryToken? fileSpec)
            && fileSpec is not null
            && fileSpec.TryGet(NameToken.Create("F"), out StringToken? path))
        {
            return Clean(path?.Data);
        }

        return null;
    }

    private static void ReadSignatureDetails(DictionaryToken? dictionary, SignatureFormField field)
    {
        if (dictionary is null)
            return;

        try
        {
            if (!dictionary.TryGet(NameToken.V, out DictionaryToken? signature) || signature is null)
                return;

            field.MarkSigned();

            if (signature.TryGet(NameToken.Create("Name"), out StringToken? name))
                field.SignerName = Clean(name?.Data);

            if (signature.TryGet(NameToken.Create("Reason"), out StringToken? reason))
                field.SigningReason = Clean(reason?.Data);

            if (signature.TryGet(NameToken.Create("Location"), out StringToken? location))
                field.SigningLocation = Clean(location?.Data);

            if (signature.TryGet(NameToken.M, out StringToken? when)
                && TryParsePdfDate(when?.Data, out var parsed))
            {
                field.SignedAt = parsed;
            }
        }
        catch
        {
            // A signature we cannot fully read is still reported as present, which is the fact that
            // matters most.
        }
    }

    /// <summary>
    /// Parses a PDF date string, which has the form D:YYYYMMDDHHmmSSOHH'mm'. Only the leading parts
    /// are required, so anything from a year to a full timestamp has to be accepted.
    /// </summary>
    private static bool TryParsePdfDate(string? value, out DateTimeOffset result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();
        if (text.StartsWith("D:", StringComparison.Ordinal))
            text = text[2..];

        if (text.Length < 4 || !int.TryParse(text.AsSpan(0, 4), out int year))
            return false;

        int month = ReadPart(text, 4, 2, 1);
        int day = ReadPart(text, 6, 2, 1);
        int hour = ReadPart(text, 8, 2, 0);
        int minute = ReadPart(text, 10, 2, 0);
        int second = ReadPart(text, 12, 2, 0);

        try
        {
            result = new DateTimeOffset(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 28),
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59),
                TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }

        static int ReadPart(string text, int offset, int length, int fallback) =>
            text.Length >= offset + length && int.TryParse(text.AsSpan(offset, length), out int parsed)
                ? parsed
                : fallback;
    }

    #endregion

    #region Attaching to a page, and recovering a label

    private static void Attach(PdfFormField field, AcroFieldBase source, PdfDocumentModel document)
    {
        if (source.Bounds is { } bounds)
        {
            field.Bounds = new PageRegion(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
        }

        var page = document.GetPage(field.PageNumber)
            ?? document.Pages.FirstOrDefault();

        if (page is null)
            return;

        field.PageNumber = page.PageNumber;

        // The field has to be in the tree before its label can be recovered: recovery searches the
        // page's own text elements, and it needs the field's page to find them.
        page.AddChild(field);

        // Only recover a label when the document supplied nothing. A tooltip is the author's own
        // statement and is never second-guessed.
        if (string.IsNullOrWhiteSpace(field.ToolTip) && !field.Bounds.IsEmpty)
        {
            field.RecoveredLabel = RecoverLabel(field);

            if (field is RadioGroupFormField radioGroup)
                RecoverRadioOptionLabels(radioGroup);
        }
    }

    /// <summary>
    /// Finds the text that visually labels a field.
    ///
    /// Two positions are checked, in the order a sighted person's eye would take them: immediately
    /// to the LEFT on the same line, then directly ABOVE. Those two cover very nearly every form
    /// layout in existence, and checking left first matters because a field sitting under a section
    /// heading and beside its own caption must take the caption.
    /// </summary>
    private static string? RecoverLabel(PdfFormField field)
    {
        var page = FindPageElement(field);
        if (page is null)
            return null;

        var candidates = page.SelfAndDescendants()
            .OfType<TextElement>()
            .Where(t => t.Kind != ElementKind.Artifact && t.Text.Trim().Length > 0)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var bounds = field.Bounds;
        double lineHeight = Math.Max(bounds.Height, 8);

        // To the left, on the same line, within a reasonable reach. Anything further away is
        // another column rather than this field's label.
        TextElement? best = null;
        double bestDistance = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var box = candidate.Bounds;

            if (box.Right > bounds.Left + 2)
                continue;

            if (!box.SharesLineWith(bounds, tolerance: 0.3))
                continue;

            double distance = bounds.Left - box.Right;
            if (distance < 0 || distance > lineHeight * 12)
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best is not null)
            return TidyLabel(best.Text);

        // Directly above, overlapping horizontally. The other common layout, used for wide fields
        // and for anything stacked in a column.
        bestDistance = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var box = candidate.Bounds;

            if (box.Bottom < bounds.Top - 2)
                continue;

            bool overlapsHorizontally = box.Left < bounds.Right && box.Right > bounds.Left;
            if (!overlapsHorizontally)
                continue;

            double distance = box.Bottom - bounds.Top;
            if (distance < 0 || distance > lineHeight * 3)
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null ? TidyLabel(best.Text) : null;
    }

    /// <summary>
    /// Recovers a readable label for each option in a radio group, from the text beside each
    /// button. Without this the user chooses between export values such as "Opt1" and "Opt2",
    /// which tell them nothing about what they are selecting.
    /// </summary>
    private static void RecoverRadioOptionLabels(RadioGroupFormField group)
    {
        var page = FindPageElement(group);
        if (page is null)
            return;

        var candidates = page.SelfAndDescendants()
            .OfType<TextElement>()
            .Where(t => t.Kind != ElementKind.Artifact && t.Text.Trim().Length > 0)
            .ToList();

        foreach (var option in group.Options)
        {
            if (option.Bounds.IsEmpty)
                continue;

            // A radio button's label sits to its right, unlike a field's, because the button is a
            // marker preceding its option text.
            TextElement? best = null;
            double bestDistance = double.MaxValue;
            double reach = Math.Max(option.Bounds.Height, 8) * 14;

            foreach (var candidate in candidates)
            {
                var box = candidate.Bounds;

                if (box.Left < option.Bounds.Right - 2)
                    continue;

                if (!box.SharesLineWith(option.Bounds, tolerance: 0.3))
                    continue;

                double distance = box.Left - option.Bounds.Right;
                if (distance < 0 || distance > reach)
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best is not null)
                option.Label = TidyLabel(best.Text);
        }
    }

    private static PageElement? FindPageElement(DocumentElement element) =>
        element as PageElement ?? element.NearestAncestor<PageElement>();

    /// <summary>
    /// Cleans a recovered label. Form captions end in colons and asterisks, both of which are
    /// visual conventions that read badly: "full name colon" and "full name asterisk" are not what
    /// the designer meant. The asterisk's meaning — that the field is required — is already
    /// announced from the field's own flags.
    /// </summary>
    private static string TidyLabel(string text)
    {
        string trimmed = text.Trim();

        while (trimmed.Length > 0 && (trimmed[^1] is ':' or '*' or '†' or '‡' or '_' or ' '))
            trimmed = trimmed[..^1].TrimEnd();

        while (trimmed.Length > 0 && trimmed[0] is '*' or '†' or '‡')
            trimmed = trimmed[1..].TrimStart();

        // A label longer than this is a paragraph that happens to sit beside the field, not a
        // caption. Announcing it would bury the field rather than name it.
        const int maximumLabelLength = 80;
        if (trimmed.Length > maximumLabelLength)
            return string.Empty;

        return trimmed;
    }

    #endregion

    #region State helpers
    // The field's own state flags are protected, which is correct — nothing outside a field should
    // be able to declare it invalid. The reader is part of constructing the field, so it goes
    // through the field's own public surface and lets the field maintain its own invariants.

    private static void ApplyCommonFlags(PdfFormField field, bool required, bool readOnly, bool noExport)
    {
        var states = FieldStates.None;

        if (required) states |= FieldStates.Required;
        if (readOnly) states |= FieldStates.ReadOnly;
        if (noExport) states |= FieldStates.NoExport;

        field.ApplyLoadedStates(states);
    }

    private static void MarkPassword(PdfFormField field) =>
        field.ApplyLoadedStates(FieldStates.Password);

    /// <summary>
    /// Sets a field's value as it was found in the file, without marking it as edited. A value that
    /// came out of the document is not a change the user made, and counting it as one would make
    /// every opened form report unsaved changes.
    /// </summary>
    private static void SetInitialValue(PdfFormField field, string value) =>
        field.ApplyLoadedValue(value);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    #endregion
}

#endregion
