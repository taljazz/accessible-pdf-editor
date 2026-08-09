using System.Net;
using System.Text;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Rendering;

// =====================================================================================
//  DocumentHtmlWriter.cs
//
//  Writes the document as semantic HTML, so that a screen reader can read it the way it
//  reads a web page — in BROWSE MODE.
//
//  WHY THIS EXISTS
//
//  The text box surface gave the user their review cursor, Say All, braille and find. What
//  it could not give them was STRUCTURE. A text box is one flat string; a heading in it is
//  only a heading because this program wrote the word "Heading" into the text. So the
//  user's own commands — NVDA's H for the next heading, T for the next table, and above all
//  Control+Alt+arrows to move around a table — did nothing, and every piece of navigation
//  had to be reinvented here with keys of this program's own devising.
//
//  That was the wrong shape. From NVDA's source, browse mode is created here:
//
//      # source/NVDAObjects/IAccessible/ia2Web.py
//      class Document(Ia2Web):
//          def _get_shouldCreateTreeInterceptor(self):
//              return controlTypes.State.READONLY in self.states
//
//  There is no check on which application is hosting the document. The only condition is
//  that the document is read-only. And a browse-mode document is a virtual buffer:
//
//      # source/virtualBuffers/__init__.py
//      class VirtualBuffer(browseMode.BrowseModeDocumentTreeInterceptor):
//          def _getTableCellAt(self, tableID, startPos, row, column): ...
//
//  which is exactly the _getTableCellAt that a Windows grid control can never supply,
//  because that one has to return a textInfos.TextInfo and a control has no text info.
//  Put the document in a web view and the user gets table navigation, quick navigation,
//  the element list, and Say All — all of it from their own screen reader, configured to
//  their own preferences, using the keys they already use everywhere else.
//
//  TWO RULES THAT MUST NOT BE BROKEN
//
//  1. Never emit role="application", and never put a document inside an element that has
//     it. NVDA turns browse mode OFF for it, and every gain here is lost:
//
//         class Application(Document):
//             shouldCreateTreeInterceptor = False
//
//  2. Never write role names into the text. The text box needed "Heading 2: Introduction"
//     because it had no other way to convey the role. Here the markup carries it, and the
//     screen reader says it. Writing it as well would make the reader say it twice.
//
//  Everything from the document is escaped on the way in. A PDF is a hostile input: it can
//  contain anything at all, and none of it is markup as far as this program is concerned.
// =====================================================================================

#region The result — HTML, plus what to look for once it is loaded

/// <summary>The document as a web page, with the identifiers needed to talk about it afterwards.</summary>
public sealed record DocumentHtml(string Html, IReadOnlyDictionary<int, string> AnchorsByElementId)
{
    /// <summary>The HTML id given to an element, or null when it produced no markup.</summary>
    public string? AnchorFor(DocumentElement element) =>
        AnchorsByElementId.TryGetValue(element.Id, out string? anchor) ? anchor : null;
}

#endregion

#region DocumentHtmlWriter

/// <summary>Renders a document model as a semantic, read-only HTML page.</summary>
public static class DocumentHtmlWriter
{
    #region Entry point

    /// <summary>
    /// Writes the whole document as a web page.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="showRepairButtons">
    /// Whether to write a button where the document has a fault this program can repair — an image
    /// with no description, a form field with no label.
    ///
    /// This is how remediation reaches a browse-mode reader at all. In browse mode the screen
    /// reader moves a cursor of its own through a copy of the page, and this program is not told
    /// where that cursor is; a command like "describe the image I am on" has nothing to act on. A
    /// button, though, is a real thing in the document: NVDA's B key finds it, Enter activates it,
    /// and the activation arrives here. So the faults become the navigation.
    /// </param>
    public static DocumentHtml Write(PdfDocumentModel document, bool showRepairButtons = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder(16384);
        var anchors = new Dictionary<int, string>();
        var writer = new Writer(builder, anchors, showRepairButtons, document.PageCount);

        string language = Escape(document.Metadata.Language is { Length: > 0 } lang ? lang : "en");
        string title = Escape(document.Metadata.Title is { Length: > 0 } t ? t : document.FileName);

        builder.Append("<!doctype html>\n<html lang=\"").Append(language).Append("\">\n<head>\n");
        builder.Append("<meta charset=\"utf-8\">\n");

        // Nothing in this page may reach the network or run script that this program did not
        // write. The document being displayed is untrusted input.
        builder.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"")
               .Append("default-src 'none'; img-src data:; style-src 'unsafe-inline'; ")
               .Append("script-src 'unsafe-inline'; form-action 'none';\">\n");

        builder.Append("<title>").Append(title).Append("</title>\n");
        builder.Append("<style>\n").Append(Stylesheet).Append("\n</style>\n</head>\n");

        // A plain <body> with no role. See rule 1 at the top of this file.
        builder.Append("<body>\n<main>\n");

        writer.WriteChildren(document.Root);

        builder.Append("</main>\n<script>\n").Append(Script).Append("\n</script>\n</body>\n</html>\n");

        return new DocumentHtml(builder.ToString(), anchors);
    }

    #endregion

    #region The walk

    /// <summary>
    /// Walks the element tree and writes each element as the markup that carries its meaning.
    ///
    /// The tree is walked rather than the flat reading order, because nesting is the whole point:
    /// a cell has to be inside its row, inside its table, or none of the table semantics survive.
    /// </summary>
    private sealed class Writer(
        StringBuilder html,
        Dictionary<int, string> anchors,
        bool showRepairButtons,
        int pageCount)
    {
        private int _listDepth;

        /// <summary>
        /// The page being built. Named as a member rather than used as the captured constructor
        /// parameter so that the nested field writer can reach it: a primary constructor parameter
        /// is in scope only inside the class that declares it.
        /// </summary>
        private StringBuilder Html => html;

        public void WriteChildren(DocumentElement parent)
        {
            foreach (var child in parent.Children)
                Write(child);
        }

        private void Write(DocumentElement element)
        {
            switch (element)
            {
                case DocumentRootElement:
                    WriteChildren(element);
                    break;

                // A page becomes a labelled region, which is a landmark. That gives the user
                // NVDA's D key for "next landmark" as a way of moving a page at a time — the
                // structure the document actually has, expressed in a structure the reader knows.
                // The label carries the page total as well as the number, because the reader
                // announces this element's name when the user arrives and that is the moment
                // "3 of 6" is worth knowing. Information belongs in the document, not in a
                // separate spoken channel this program controls.
                case PageElement page:
                    html.Append("<section aria-label=\"Page ").Append(page.PageNumber)
                        .Append(pageCount > 1 ? $" of {pageCount}" : string.Empty).Append("\"")
                        .Append(Anchor(element)).Append(">\n");
                    WriteChildren(element);
                    html.Append("</section>\n");
                    break;

                case SectionElement section:
                    html.Append("<section").Append(Anchor(element));
                    if (section.SectionTitle is { Length: > 0 } name)
                        html.Append(" aria-label=\"").Append(Escape(name)).Append('"');
                    html.Append(">\n");
                    WriteChildren(element);
                    html.Append("</section>\n");
                    break;

                case HeadingElement heading:
                    WriteHeading(heading);
                    break;

                case ParagraphElement paragraph:
                    WriteTextBlock("p", paragraph);
                    break;

                case BlockQuoteElement quote:
                    WriteTextBlock("blockquote", quote);
                    break;

                case CodeElement code:
                    // Whitespace in code is meaning, and <pre> is the only element that keeps it.
                    html.Append("<pre").Append(Anchor(element)).Append("><code>")
                        .Append(Escape(code.Text)).Append("</code></pre>\n");
                    break;

                case NoteElement note:
                    html.Append("<aside").Append(Anchor(element)).Append(" aria-label=\"Note\"><p>")
                        .Append(Escape(note.Text)).Append("</p></aside>\n");
                    break;

                // Page furniture — running heads, folios. A sighted reader's eye skips them; hiding
                // them from the reader is the same courtesy, not a loss of information.
                case ArtifactElement:
                    break;

                // A caption reached on its own, outside a figure or table, is just a paragraph.
                case CaptionElement caption:
                    WriteTextBlock("p", caption, "caption");
                    break;

                case TableElement table:
                    WriteTable(table);
                    break;

                case ListElement list:
                    WriteList(list);
                    break;

                case ListItemElement item:
                    WriteListItem(item);
                    break;

                case FigureElement figure:
                    WriteFigure(figure);
                    break;

                case PdfFormField field:
                    WriteFormField(field);
                    break;

                case LinkElement link:
                    WriteLink(link);
                    break;

                case AnnotationElement annotation:
                    WriteAnnotation(annotation);
                    break;

                case AttachmentElement attachment:
                    html.Append("<p><button type=\"button\" class=\"act\"")
                        .Append(Anchor(element, focusable: false)).Append(" data-act=\"attachment\">")
                        .Append("Attachment: ").Append(Escape(attachment.FileName))
                        .Append(", ").Append(Escape(attachment.SpokenSize))
                        .Append(". Activate to save it to disk.</button></p>\n");
                    break;

                default:
                    if (element.Text.Trim().Length > 0)
                        html.Append("<p").Append(Anchor(element)).Append('>')
                            .Append(Escape(element.Text)).Append("</p>\n");
                    else
                        WriteChildren(element);
                    break;
            }
        }

        #endregion

        #region Text, headings and links

        private void WriteHeading(HeadingElement heading)
        {
            // Level None means the classifier found a heading but could not tell how deep. h2 is
            // the safe answer: it keeps it inside the document's outline rather than claiming to
            // be the document's own title.
            int level = heading.Level == HeadingLevel.None ? 2 : (int)heading.Level;
            level = Math.Clamp(level, 1, 6);

            html.Append("<h").Append(level).Append(Anchor(heading)).Append('>')
                .Append(Escape(heading.Text)).Append("</h").Append(level).Append(">\n");
        }

        private void WriteTextBlock(string tag, DocumentElement element, string? cssClass = null)
        {
            string text = element.Text.Trim();
            if (text.Length == 0)
                return;

            html.Append('<').Append(tag).Append(Anchor(element));

            if (cssClass is not null)
                html.Append(" class=\"").Append(cssClass).Append('"');

            // A run of text in another language is marked, so the reader switches voice for it.
            if (element.Language is { Length: > 0 } language)
                html.Append(" lang=\"").Append(Escape(language)).Append('"');

            if (element.Direction == TextDirection.RightToLeft)
                html.Append(" dir=\"rtl\"");

            html.Append('>').Append(Escape(text)).Append("</").Append(tag).Append(">\n");
        }

        /// <summary>
        /// Writes a link. The href is a placeholder: following it is this program's decision, not
        /// the page's, because a PDF link can point anywhere and some of them must be confirmed
        /// with the user before anything is opened. What the href IS for is making the element a
        /// real link, so the reader announces it as one and finds it with the K key.
        /// </summary>
        private void WriteLink(LinkElement link)
        {
            string text = link.Text.Trim();
            if (text.Length == 0)
                text = link.TargetKind == LinkTargetKind.InternalDestination && link.TargetPage is { } page
                    ? $"Go to page {page}"
                    : link.Target;

            html.Append("<p><a href=\"#\" class=\"act\"").Append(Anchor(link, focusable: false))
                .Append(" data-act=\"link\">").Append(Escape(text)).Append("</a>");

            // The destination is written out, because a link whose text is "click here" tells a
            // listener nothing, and this is the one place the real target can be heard.
            if (link.TargetKind is LinkTargetKind.WebUrl or LinkTargetKind.Email
                && link.Target.Length > 0 && !string.Equals(text, link.Target, StringComparison.Ordinal))
            {
                html.Append(" <span class=\"target\">(").Append(Escape(link.Target)).Append(")</span>");
            }

            html.Append("</p>\n");
        }

        private void WriteAnnotation(AnnotationElement annotation)
        {
            html.Append("<aside class=\"comment\"").Append(Anchor(annotation))
                .Append(" aria-label=\"Comment\">\n<p>");

            if (annotation.Author is { Length: > 0 } author)
                html.Append(Escape(author)).Append(": ");

            html.Append(Escape(annotation.Text)).Append("</p>\n");

            if (annotation.AnchoredText is { Length: > 0 } anchored)
                html.Append("<p class=\"quoted\">On the text: ").Append(Escape(anchored)).Append("</p>\n");

            html.Append("</aside>\n");
        }

        #endregion

        #region Tables — the reason for all of this

        /// <summary>
        /// Writes a real table, with real header cells.
        ///
        /// Note what is NOT written: no "Table: 4 rows, 3 columns" line, and no pipe-separated row
        /// text. Both were workarounds for a text box that could not express a table. A screen
        /// reader announces the dimensions itself on entering a &lt;table&gt;, and announces each
        /// cell with its headers as the user moves. Writing them as well would say everything
        /// twice.
        /// </summary>
        private void WriteTable(TableElement table)
        {
            html.Append("<table").Append(Anchor(table)).Append(">\n");

            // The caption is the table's accessible name, and it is what the reader says when the
            // user arrives. Falling back to the summary is worth it: a table whose name is
            // "Table" tells the user only what they already knew.
            var caption = table.Children.OfType<CaptionElement>().FirstOrDefault();
            string? captionText = caption?.Text.Trim() is { Length: > 0 } c ? c : table.Summary;

            if (captionText is { Length: > 0 })
                html.Append("<caption>").Append(Escape(captionText)).Append("</caption>\n");

            var rows = table.Rows;

            // A header row goes in <thead>. That is not decoration: it is what lets a reader keep
            // announcing column headers once the user has scrolled far below them.
            bool hasHeaderRow = rows.Count > 0 && rows[0].IsHeaderRow;

            if (hasHeaderRow)
            {
                html.Append("<thead>\n");
                WriteRow(rows[0], forceHeaderCells: true);
                html.Append("</thead>\n");
            }

            html.Append("<tbody>\n");

            for (int i = hasHeaderRow ? 1 : 0; i < rows.Count; i++)
                WriteRow(rows[i], forceHeaderCells: false);

            html.Append("</tbody>\n</table>\n");
        }

        private void WriteRow(TableRowElement row, bool forceHeaderCells)
        {
            html.Append("<tr").Append(Anchor(row)).Append(">\n");

            foreach (var cell in row.Cells)
            {
                bool isHeader = forceHeaderCells || cell.CellRole != TableCellRole.Data;

                // The scope is the whole point of a header cell. Without it a reader knows the
                // cell is emphasised but not what it governs, so it cannot say "February, Amount,
                // 398.50" — which is the sentence that makes a table usable by ear.
                string tag = isHeader ? "th" : "td";
                html.Append('<').Append(tag).Append(Anchor(cell));

                if (isHeader)
                {
                    string scope = cell.CellRole switch
                    {
                        TableCellRole.RowHeader => "row",
                        TableCellRole.ColumnHeader => "col",

                        // An unscoped header in the first row labels its column; anywhere else it
                        // labels its row. That is what the shape of a table almost always means,
                        // and guessing well beats emitting no scope at all.
                        _ => forceHeaderCells ? "col" : "row",
                    };

                    html.Append(" scope=\"").Append(scope).Append('"');
                }

                if (cell.ColumnSpan > 1)
                    html.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');

                if (cell.RowSpan > 1)
                    html.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');

                html.Append('>').Append(Escape(cell.Text)).Append("</").Append(tag).Append(">\n");
            }

            html.Append("</tr>\n");
        }

        #endregion

        #region Lists

        private void WriteList(ListElement list)
        {
            bool ordered = list.MarkerKind is not (ListMarkerKind.None or ListMarkerKind.Bullet);
            string tag = ordered ? "ol" : "ul";

            html.Append('<').Append(tag).Append(Anchor(list));

            if (ordered)
            {
                // The numbering style is carried across, so an outline that reads "a, b, c" in the
                // document does not become "1, 2, 3" here and stop matching what a colleague sees.
                string type = list.MarkerKind switch
                {
                    ListMarkerKind.LowerAlpha => "a",
                    ListMarkerKind.UpperAlpha => "A",
                    ListMarkerKind.LowerRoman => "i",
                    ListMarkerKind.UpperRoman => "I",
                    _ => "1",
                };

                html.Append(" type=\"").Append(type).Append('"');
            }

            html.Append(">\n");

            _listDepth++;
            WriteChildren(list);
            _listDepth--;

            html.Append("</").Append(tag).Append(">\n");
        }

        private void WriteListItem(ListItemElement item)
        {
            // A stray list item, from a document whose list structure did not survive. Wrapping it
            // keeps the markup valid rather than letting an <li> float outside any list.
            bool needsWrapper = _listDepth == 0;

            if (needsWrapper)
                html.Append("<ul>\n");

            html.Append("<li").Append(Anchor(item)).Append('>');
            html.Append(Escape(item.Text.Trim()));

            // Nested lists live inside their item, which is what makes a reader announce the
            // nesting level as the user moves down into it.
            foreach (var child in item.Children.Where(c => c.Kind != ElementKind.ListItem))
            {
                if (child is ListElement nested)
                {
                    html.Append('\n');
                    WriteList(nested);
                }
            }

            html.Append("</li>\n");

            if (needsWrapper)
                html.Append("</ul>\n");
        }

        #endregion

        #region Figures — including the ones with nothing to say

        private void WriteFigure(FigureElement figure)
        {
            // A figure the document says is decorative is hidden, which is what "decorative" means.
            // It is still in the model, and the remediation pane can still reach it, so nothing is
            // lost except the interruption.
            if (figure.IsMarkedDecorative)
            {
                html.Append("<img src=\"").Append(BlankPixel).Append("\" alt=\"\" role=\"presentation\"")
                    .Append(Anchor(figure)).Append(">\n");
                return;
            }

            if (figure.AlternateText is { Length: > 0 } alt)
            {
                html.Append("<figure").Append(Anchor(figure)).Append(">\n<img src=\"").Append(BlankPixel)
                    .Append("\" alt=\"").Append(Escape(alt)).Append("\">\n");

                if (figure.Caption?.Text.Trim() is { Length: > 0 } caption)
                    html.Append("<figcaption>").Append(Escape(caption)).Append("</figcaption>\n");

                html.Append("</figure>\n");
                return;
            }

            // No description. This is the fault the whole editor exists to fix, so it is written
            // as a button rather than as a lament: the user can find it with the reader's own B
            // key and repair it on the spot.
            if (showRepairButtons)
            {
                html.Append("<p><button type=\"button\" class=\"fix\"").Append(Anchor(figure, focusable: false))
                    .Append(" data-act=\"describe\">Image with no description")
                    .Append(figure.Caption?.Text.Trim() is { Length: > 0 } nearby
                        ? ", captioned " + Escape(nearby)
                        : string.Empty)
                    .Append(". Activate to describe it.</button></p>\n");
            }
            else
            {
                html.Append("<p").Append(Anchor(figure)).Append(">Image with no description.</p>\n");
            }
        }

        #endregion

        #region Form fields — real controls, so the reader treats them as controls

        /// <summary>
        /// Writes a field as the HTML control that matches it, using the visitor the model already
        /// defines. Real controls are what make the reader switch to focus mode by itself, echo
        /// what the user types, and announce "required" and "read-only" without being told.
        /// </summary>
        private void WriteFormField(PdfFormField field) => field.Accept(new FieldWriter(this, field));

        private sealed class FieldWriter(Writer writer, PdfFormField field) : IFormFieldVisitor<bool>
        {
            private StringBuilder Html => writer.Html;

            public bool VisitText(TextFormField f)
            {
                string id = writer.Anchor(f, asAttribute: false);
                bool multiline = f.FieldKind == FormFieldKind.MultilineText;

                OpenControl(id);

                if (multiline)
                {
                    Html.Append("<textarea id=\"").Append(id).Append('"').Append(CommonAttributes())
                        .Append(" rows=\"4\">").Append(Escape(f.Value)).Append("</textarea>");
                }
                else
                {
                    Html.Append("<input type=\"text\" id=\"").Append(id).Append('"')
                        .Append(CommonAttributes())
                        .Append(" value=\"").Append(Escape(f.Value)).Append("\">");
                }

                CloseControl();
                return true;
            }

            public bool VisitCheckBox(CheckBoxFormField f)
            {
                string id = writer.Anchor(f, asAttribute: false);

                Html.Append("<p class=\"field\"><input type=\"checkbox\" id=\"").Append(id).Append('"')
                    .Append(CommonAttributes())
                    .Append(f.IsChecked ? " checked" : string.Empty).Append('>')
                    .Append("<label for=\"").Append(id).Append("\">").Append(LabelText())
                    .Append("</label></p>\n");

                return true;
            }

            public bool VisitRadioGroup(RadioGroupFormField f)
            {
                // A fieldset with a legend is how a group of radio buttons gets a name. Without it
                // the reader announces each option but never what the question was.
                string groupName = "g" + f.Id;

                Html.Append("<fieldset").Append(writer.Anchor(f)).Append(">\n<legend>")
                    .Append(LabelText()).Append("</legend>\n");

                for (int i = 0; i < f.Options.Count; i++)
                {
                    var option = f.Options[i];
                    string optionId = groupName + "_" + i;
                    bool selected = string.Equals(f.SelectedExportValue, option.ExportValue, StringComparison.Ordinal);

                    Html.Append("<p><input type=\"radio\" name=\"").Append(groupName)
                        .Append("\" id=\"").Append(optionId).Append('"')
                        .Append(" data-el=\"").Append(f.Id).Append('"')
                        .Append(" data-value=\"").Append(Escape(option.ExportValue)).Append('"')
                        .Append(f.IsReadOnly ? " disabled" : string.Empty)
                        .Append(selected ? " checked" : string.Empty).Append('>')
                        .Append("<label for=\"").Append(optionId).Append("\">")
                        .Append(Escape(option.SpokenLabel)).Append("</label></p>\n");
                }

                Html.Append("</fieldset>\n");
                return true;
            }

            public bool VisitChoice(ChoiceFormField f)
            {
                string id = writer.Anchor(f, asAttribute: false);

                OpenControl(id);

                Html.Append("<select id=\"").Append(id).Append('"').Append(CommonAttributes())
                    .Append(f.AllowsMultipleSelection ? " multiple size=\"4\"" : string.Empty).Append(">\n");

                // An empty option first, so that "not filled in" is a thing the user can choose and
                // hear, rather than the first real option looking as though it were already picked.
                if (!f.AllowsMultipleSelection)
                    Html.Append("<option value=\"\"").Append(f.HasValue ? string.Empty : " selected")
                        .Append(">(not filled in)</option>\n");

                foreach (var option in f.Options)
                {
                    bool selected = f.SelectedExportValues.Contains(option.ExportValue, StringComparer.Ordinal);

                    Html.Append("<option value=\"").Append(Escape(option.ExportValue)).Append('"')
                        .Append(selected ? " selected" : string.Empty).Append('>')
                        .Append(Escape(option.SpokenText)).Append("</option>\n");
                }

                Html.Append("</select>");
                CloseControl();
                return true;
            }

            public bool VisitPushButton(PushButtonFormField f)
            {
                Html.Append("<p><button type=\"button\" class=\"act\"").Append(writer.Anchor(f, focusable: false))
                    .Append(" data-act=\"button\"")
                    .Append(f.CanActivate ? string.Empty : " disabled").Append('>')
                    .Append(LabelText()).Append("</button></p>\n");

                return true;
            }

            public bool VisitSignature(SignatureFormField f)
            {
                string state = f.IsSigned
                    ? "signed" + (f.SignerName is { Length: > 0 } n ? " by " + Escape(n) : string.Empty)
                    : f.HasPendingSignature
                        ? "a signature is waiting to be saved"
                        : "not signed";

                Html.Append("<p><button type=\"button\" class=\"act\"").Append(writer.Anchor(f, focusable: false))
                    .Append(" data-act=\"sign\">Signature field: ").Append(LabelText())
                    .Append(", ").Append(state)
                    .Append(f.IsSigned ? "." : ". Activate to sign it.")
                    .Append("</button></p>\n");

                return true;
            }

            #region Shared field markup

            private void OpenControl(string id)
            {
                Html.Append("<p class=\"field\"><label for=\"").Append(id).Append("\">")
                    .Append(LabelText()).Append("</label>");
            }

            private void CloseControl() => Html.Append("</p>\n");

            /// <summary>
            /// The field's label, with a warning where the document did not supply one. A field
            /// called "Text1" is a fault, and saying so where the field is heard is more use than
            /// listing it in a report the user has to go and find.
            /// </summary>
            private string LabelText()
            {
                string label = Escape(field.Label);

                return field.IsUnlabelled
                    ? label + " <span class=\"warn\">(this field has no label in the document)</span>"
                    : label;
            }

            /// <summary>
            /// required and readonly are written as real HTML attributes rather than as words in
            /// the label, so the reader announces them in its own wording and the user's own
            /// verbosity setting decides whether they are spoken at all.
            /// </summary>
            private string CommonAttributes()
            {
                var attributes = new StringBuilder(64);
                attributes.Append(" data-el=\"").Append(field.Id).Append('"');

                if (field.IsRequired)
                    attributes.Append(" required aria-required=\"true\"");

                if (field.IsReadOnly)
                    attributes.Append(" readonly aria-readonly=\"true\"");

                if (field.IsInvalid)
                    attributes.Append(" aria-invalid=\"true\"");

                if (field.InputGuidance is { Length: > 0 } guidance)
                    attributes.Append(" title=\"").Append(Escape(guidance)).Append('"');

                return attributes.ToString();
            }

            #endregion
        }

        #endregion

        #region Identifiers

        /// <summary>
        /// Gives an element an id, so that this program can move to it, and so that anything the
        /// user does to it arrives back here naming the element it happened to.
        /// </summary>
        /// <param name="asAttribute">
        /// False returns the bare id, for a control that needs it in a label's for attribute.
        /// </param>
        /// <param name="focusable">
        /// Whether to make the element focusable programmatically.
        ///
        /// This is what lets navigation actually MOVE the reader. In browse mode the screen reader
        /// keeps a cursor of its own, and scrolling the page does not move it — the announcement
        /// would happen and the user would still be where they were, which is precisely the bug
        /// that made table navigation look broken in the text view. Moving focus does move it.
        ///
        /// tabindex="-1" allows that without adding anything to the Tab order, so tabbing still
        /// visits only the controls a user can actually operate.
        ///
        /// It must NOT be applied to links and buttons: those are focusable already, and setting
        /// tabindex="-1" on them would REMOVE them from the Tab order. Hence the parameter rather
        /// than doing it unconditionally.
        /// </param>
        private string Anchor(DocumentElement element, bool asAttribute = true, bool focusable = true)
        {
            string id = "e" + element.Id;
            anchors[element.Id] = id;

            if (!asAttribute)
                return id;

            return $" id=\"{id}\" data-el=\"{element.Id}\"" + (focusable ? " tabindex=\"-1\"" : string.Empty);
        }

        #endregion
    }

    #region Escaping

    /// <summary>
    /// Escapes text from the document for HTML. Every piece of text that came out of a PDF goes
    /// through here: a PDF is untrusted input and none of what it contains is markup.
    /// </summary>
    internal static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : WebUtility.HtmlEncode(value);

    #endregion

    #region Page furniture

    /// <summary>
    /// A transparent pixel. The figures are named by their alternative text rather than shown; the
    /// page picture pane is where the actual page is looked at. An img element with no src at all
    /// is reported as broken by some readers, which would be a fault this program invented.
    /// </summary>
    private const string BlankPixel =
        "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    /// <summary>
    /// Styling for whoever is looking at the screen. It follows the system colours so that Windows
    /// high contrast themes work, and it never hides anything from the reader: display:none and
    /// visibility:hidden remove an element from the accessibility tree as well as from the screen.
    /// </summary>
    private const string Stylesheet = """
        :root { color-scheme: light dark; }
        body { font-family: "Segoe UI", system-ui, sans-serif; font-size: 1rem;
               line-height: 1.5; margin: 0; padding: 1rem 1.5rem; max-width: 60rem; }
        main { outline: none; }
        section { margin-bottom: 1.5rem; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.2em 0 0.4em; }
        p { margin: 0 0 0.7em; }
        table { border-collapse: collapse; margin: 1em 0; }
        caption { text-align: left; font-weight: 600; padding-bottom: 0.3em; }
        th, td { border: 1px solid GrayText; padding: 0.35em 0.6em; text-align: left; vertical-align: top; }
        th { font-weight: 700; }
        figure { margin: 1em 0; }
        figure img { max-width: 100%; min-width: 2rem; min-height: 2rem; border: 1px dashed GrayText; }
        figcaption { font-style: italic; }
        aside { border-left: 4px solid Highlight; padding: 0.4em 0 0.4em 0.8em; margin: 1em 0; }
        .quoted { font-style: italic; }
        .target { color: GrayText; }
        .caption { font-style: italic; }
        fieldset { margin: 1em 0; padding: 0.6em 1em; border: 1px solid GrayText; }
        legend { font-weight: 600; }
        p.field { margin: 0.8em 0; }
        p.field label { display: inline-block; min-width: 12rem; padding-right: 0.6em; }
        input[type="text"], textarea, select { font: inherit; padding: 0.25em; min-width: 18rem; }
        input[type="checkbox"], input[type="radio"] { margin-right: 0.5em; }
        button.fix, button.act { font: inherit; padding: 0.35em 0.7em; cursor: pointer; }
        button.fix { border: 2px solid Highlight; }
        .warn { color: GrayText; }
        :focus-visible { outline: 3px solid Highlight; outline-offset: 2px; }
        """;

    /// <summary>
    /// Reports what the user does back to the program.
    ///
    /// Note what this does NOT do: it does not move focus, scroll, or announce anything. In browse
    /// mode the screen reader is moving a cursor of its own through its own copy of this page, and
    /// a page that moved focus underneath it would drag the user somewhere they did not ask to go.
    /// The page reports; the program decides.
    /// </summary>
    private const string Script = """
        (function () {
          var post = function (message) {
            if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
          };

          document.addEventListener('click', function (event) {
            var target = event.target.closest('[data-act]');
            if (!target) return;
            event.preventDefault();
            post({ kind: 'activate', action: target.getAttribute('data-act'),
                   element: parseInt(target.getAttribute('data-el'), 10) });
          });

          // 'change' rather than 'input': it fires when the user has finished, so a half-typed
          // date does not reach the undo history one keystroke at a time.
          document.addEventListener('change', function (event) {
            var control = event.target;
            var id = control.getAttribute('data-el');
            if (!id) return;

            var value;
            if (control.type === 'checkbox') value = control.checked;
            else if (control.type === 'radio') value = control.getAttribute('data-value');
            else if (control.multiple) value = Array.from(control.selectedOptions).map(function (o) { return o.value; });
            else value = control.value;

            post({ kind: 'value', element: parseInt(id, 10), value: value });
          });

          document.addEventListener('focusin', function (event) {
            var id = event.target.getAttribute && event.target.getAttribute('data-el');
            if (id) post({ kind: 'focus', element: parseInt(id, 10) });
          });

          // The program's own shortcuts have to keep working while the user is reading here, or
          // the browse view would be a room with no door: no Control+S, no Control+F, no F6 back
          // to the text. Only modified keys and function keys are forwarded — single letters are
          // left alone, because in browse mode those belong to the screen reader, and taking H
          // back for this program is exactly the mistake this whole pane exists to undo.
          var EDITING = { 65: 1, 67: 1, 86: 1, 88: 1, 90: 1, 89: 1 };  // A C V X Z Y

          document.addEventListener('keydown', function (event) {
            var tag = event.target && event.target.tagName;
            var editing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
            var isFunctionKey = event.keyCode >= 112 && event.keyCode <= 123;

            // Page Up and Page Down mean "previous page" and "next page" in every PDF reader, so
            // they are forwarded even unmodified. A screen reader in browse mode claims them first
            // for its own cursor and this listener will never see them — NVDA binds them in
            // cursorManager.py — which is why Control+Page Up and Control+Page Down exist as well.
            // Left alone inside a form control, where they belong to the control.
            var isPaging = (event.keyCode === 33 || event.keyCode === 34) && !editing;

            if (!event.ctrlKey && !event.altKey && !isFunctionKey && !isPaging) return;

            // Someone typing into a field keeps their own select-all, copy, paste and undo.
            if (editing && event.ctrlKey && !event.altKey && EDITING[event.keyCode]) return;

            post({ kind: 'key', code: event.keyCode, ctrl: event.ctrlKey,
                   shift: event.shiftKey, alt: event.altKey });

            event.preventDefault();
          });
        })();
        """;

    #endregion
}

#endregion
