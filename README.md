# Accessible PDF Editor

A PDF reader and accessibility remediation tool built for blind and partially sighted users,
in C# on .NET 10 with Windows Forms.

It does three things: reads PDFs aloud with full structure navigation, fills in forms, and
**repairs a document's accessibility** so that the fixes are saved into the file and work for
everyone who opens it afterwards.

---

## What it can and cannot do

This is stated first because being honest about it is part of the design.

### It can

| | |
|---|---|
| **Read** | The document is presented to the screen reader as a real **document**, so it reads in browse mode and every command the user already knows works — quick navigation, `Ctrl+Alt+arrow` table navigation, the element list, Say All. Structure comes from the document's own tags where they exist, and is inferred from page layout — including tables — where they do not, which is most PDFs. |
| **Fill in forms** | Every AcroForm field type, with per-type validation, spoken format guidance, and recovered labels for fields the document never named. Clear the whole form (undoable), or save a flattened copy. |
| **Sign** | Place a visible signature from an image of your handwriting, your typed name, or one drawn on an accessible signature pad. |
| **Repair accessibility** | Image descriptions, form field names, heading levels, table headers, page furniture, reading order, document language and title. And the big one: **give an untagged document a real structure tree**, so the headings and tables this program worked out from the layout are carried by the file itself and every other reader gets them too. |
| **Annotate** | Read existing comments, highlights and replies, with the text each one covers — and write them. A comment attaches to whatever you are **on**, not to a place you point at, so commenting needs no sight and no mouse. Edit, reply, delete, all undoable. |
| **Save safely** | Every save is verified against the original before it replaces it, and refuses rather than losing anything. |

### It cannot

**Rewrite text that is already printed on a page.** PDF is a fixed-layout format: text is
positioned glyph by glyph with no notion of paragraphs that reflow. Editing it reliably is a
rendering engine, not a feature, and tools that claim to do it frequently mangle the layout.

What is offered instead is an **announced-text override** — changing what a screen reader says for
a span of existing text without touching the ink. That fixes OCR errors, expands acronyms and
repairs ligatures, which is what the ability to "edit text" is usually wanted for.

Not built, and stated rather than hidden:

- **Cryptographic signing.** PDFsharp ships `PdfSharpDefaultSigner`, which takes an X509
  certificate, so it is achievable; only the *visible* signature is implemented, and the editor
  says which of the two it applied every time.
- **Creating new form fields** — achievable via raw dictionaries, not implemented.
- **Import-data and Print buttons** — recognised and described, then honestly refused. A button
  that claims to have imported your data and did not is worse than one that admits it cannot.
- **JavaScript buttons** — deliberately refused, and say so rather than doing nothing.
- **XFA forms** — not handled.

---

## Signing without sight

Signing is one of the few genuinely visual acts left in document work, and it is where most
"accessible" PDF tools give up. Three routes are offered, deliberately in this order:

1. **An image of your handwritten signature** — a scan or photo. Your real signature, and a file
   picker needs no pointing device at all. This is the default and the recommendation.
2. **Your typed name** — drawn into the field as text. Also needs no pointer.
3. **Draw it now** — for people who want to, and who asked for it.

The third is the interesting one. A blind person can very often write their own signature — it is
muscle memory, not something you look at — but a mouse gives none of the feedback a pen on paper
does. So the signature pad:

- **moves the physical mouse cursor** to the left-centre of the drawing area on opening, where a
  signature starts on paper, and says so. You do not have to find a surface you cannot see;
- **makes position audible** — pitch rises as the pointer moves right and as it moves up, so you
  hear where you are while drawing. Speech cannot do this: by the time a coordinate had been read
  out, the pointer would have moved;
- **sounds different at the edge**, so running off the pad is obvious immediately;
- **works with no mouse at all** — Space puts the pen down, arrow keys draw, Shift moves further;
- **refuses a signature that is not one.** A stray click leaves a dot. A sighted user sees that
  instantly; this measures what was drawn, says how much of the area it covers, and asks again.

Before anything is applied, the editor states plainly that this is a **visible** signature — a
picture of one, which is what most e-signing actually is and is usually sufficient — and that it is
**not cryptographic**, so it does not prove the document is unchanged and anyone with the file could
extract the image. A sighted user can inspect a signature's properties to learn that. This says it.

### How the mark is written

The ink is **flattened into the page** and the empty signature field is removed, rather than being
put in the field's appearance. That matters for two reasons:

- A signature field that is not cryptographically signed is **still a signature field**, and Adobe
  Acrobat paints its own "click to sign" panel over it. The person who signed cannot see that their
  mark has been covered up. Removing the field leaves nothing to paint over.
- The flattened ink is wrapped in a **tagged `/Figure` with real alternate text** — *"Signature of
  Thomas Anderson, added 3 August 2026, visual mark, not cryptographically verified"* — so a screen
  reader announces our wording. Without it a signature is just ink: nothing to announce, and the
  person who signed cannot confirm it is announced at all. It would be a poor showing for this
  program of all programs to produce output it could not itself read.

A test loads the signed file back **through the editor's own reader** and asserts the signature
comes back as a described figure, not as one needing a description.

If flattening fails for any reason, it falls back to the appearance-stream route — which keeps the
field, so the Acrobat caveat applies — and says which happened rather than failing outright.

Because the field is consumed, that spot cannot be signed again. The confirmation says so.

The field's position is also announced in millimetres from the page corner, which matters if you
end up signing somewhere else.

---

## The safety mechanism worth knowing about

PDFsharp — the library used for writing — **cannot see structure trees stored in compressed object
streams**, which is what Word, Acrobat and InDesign all produce. Opening and saving such a file
deletes every heading, list and image description in it, while leaving the page text, the page
count and the "this document is tagged" flag all intact.

Testing reproduced this on 24 out of 24 real tagged PDFs. Nothing about the result looks wrong.
The only person who would ever find out is the blind reader who opens the document afterwards.

For an accessibility editor that is the worst possible bug — the tool stripping accessibility from
exactly the documents that had it. So:

1. **Before any save**, the file is inspected with an independent parser and compared against what
   the writing library can see. A disagreement means saving would delete something.
2. **After writing**, a fingerprint of the result is compared against one taken at open. If
   anything went down that the user did not ask to lose, the save is rolled back.
3. **The original is never replaced** by a file that shrank behind the user's back.

Verified against the PDFs on the development machine:

```
21 of 40 sampled documents are tagged; 4 would lose their tags on save and are protected
PASS  saving a document that would lose its tags is refused by default
```

---

## How it is accessible

Two approaches exist for making a Windows program work with a screen reader, and this uses both,
in a deliberate order of priority.

**Native UI Automation comes first.** Every control is a real Windows control with a proper
accessible name, role and value, and the document itself is presented as a real **document** — see
[The two reading views](#the-two-reading-views) below. That gives the user, for free:

- their screen reader's own review cursor
- Say All, at their speed and in their voice
- their braille display, tracking as they read
- text selection and copy, with the keys they already use
- their own punctuation and symbol settings
- and, in the browse view, **every quick-navigation and table-navigation command they already know**

**Self-voicing through Tolk comes second**, for what UI Automation has no way to express: "you have
moved from a paragraph to a level 2 heading", "3 of 12 required fields are still empty", "that
value was rejected because it is not a date".

### Tables in untagged documents

Tables are the structure where a listener is at the greatest disadvantage, and most PDFs carry no
tags to describe them. Without detection, an untagged table is extracted as separate column blocks
and read **down each column** — *"Month, January, February, March"*, then *"Amount, £412.00,
£412.00, £398.50"* — so the numbers arrive detached from the months they belong to and no amount of
careful listening puts them back together.

They are now inferred from alignment: several consecutive lines whose words begin at the same
handful of x positions. Header rows are recognised from bold, or from words sitting over numbers;
a label column is recognised as row headers. So a cell announces:

> row 2, column 2, **January, Amount, £412.00**

which is what a sighted reader takes from the grid at a glance.

### The two reading views

The first version of this program put the document in a read-only text box. That was a reasonable
choice and it bought a great deal — the review cursor, Say All, braille, find — but it had a defect
that could not be patched: **a text box has no structure.** To a screen reader it is one flat
string. A heading in it is a heading only because this program wrote the word "Heading" into the
text. So all navigation had to be reimplemented here, with keys of this program's own choosing, and
the user had to learn a second set of commands for one program. Table navigation could not be
reimplemented at all.

The fix was to stop imitating a document and provide one. **The browse view renders the document as
semantic HTML in an embedded WebView2**, which a screen reader reads in **browse mode** — the same
mode it uses on any web page.

This is not a guess about how NVDA behaves. Browse mode is created here:

```python
# source/NVDAObjects/IAccessible/ia2Web.py
class Document(Ia2Web):
    def _get_shouldCreateTreeInterceptor(self):
        return controlTypes.State.READONLY in self.states
```

There is **no check on which application hosts the document** — only that it is read-only. And a
browse-mode document is a virtual buffer, which is precisely the thing a control can never be:

```python
# source/virtualBuffers/__init__.py
class VirtualBuffer(browseMode.BrowseModeDocumentTreeInterceptor):
    def _getTableCellAt(self, tableID, startPos, row, column): ...
```

That `_getTableCellAt` is the method `Ctrl+Alt+arrow` needs, and it must return a
`textInfos.TextInfo`. A `DataGridView` has no text info, which is why those commands could never
work in a grid. A virtual buffer has one.

Inspecting the UI Automation tree — the substrate every screen reader reads — with a real PDF loaded
through the real control:

```
control type : ControlType.Document
read-only    : True            <-- NVDA's browse-mode trigger
patterns     : Value, Scroll, Text, ScrollItem

headings: 5   links: 1   edit fields: 5   check boxes: 1
radio buttons: 3   combo boxes: 1   groups: 8   e.g. "Page 1", "How should we contact you"

TABLES: 1 — 4 rows x 3 columns
  cell "£412.00" announces with headers: "January" + "Amount"
```

So in the browse view the user gets, from their own screen reader and with their own settings:

- `H`, `T`, `K`, `F`, `G`, `L`, `D` and the rest, as **they** have configured them
- `Ctrl+Alt+arrow` table navigation, announcing each cell with its headings
- the element list (`NVDA+F7`), Say All, find, and braille
- each page as a landmark, so `D` moves a page at a time

and this program claims **none** of those keys. Everything it writes into the page is markup rather
than words: `<h2>`, `<th scope="row">`, `<label for>`, `required`, `lang`. The role prefixes the text
box needed (*"Heading 2: Introduction"*, *"Table: 4 rows, 3 columns"*) are deliberately **absent**,
because the reader announces them itself and writing them too would say everything twice. A test
pins that.

**The text view remains**, one keystroke away on `Ctrl+Shift+B`, and is used automatically when the
WebView2 runtime is missing. Nothing that worked before has been taken away.

**Repair in browse mode works differently, and better.** In browse mode the reader moves a cursor
through a copy of the page that it holds itself, and nothing is reported back to the application —
so a command meaning "describe the image I am on" has nothing to act on. Instead, every fault the
editor can repair is written into the page **as a button**: *"Image with no description. Activate to
describe it."* `B` finds the next button, `Enter` activates it, and the ordinary edit command runs,
undoable like any other. The faults become the navigation.

**Moving has to move the cursor, not the scrollbar.** In browse mode the reader keeps a cursor
inside its own copy of the page, and `scrollIntoView` does not move it — a navigation command would
announce its destination and leave the user reading where they already were. Focus *does* move it,
so every block carries `tabindex="-1"` and deliberate navigation calls `focus()`. Links and buttons
are deliberately excluded: they are focusable already, and `tabindex="-1"` would take them **out** of
the Tab order. A test pins that distinction, because getting it backwards would make every link
unreachable by keyboard while looking identical on screen.

This is why `Page Up`/`Page Down` needed care. NVDA binds them to its own cursor:

```python
# source/cursorManager.py
"kb:pageUp":   "moveByPage_back",
"kb:pageDown": "moveByPage_forward",
```

so in browse mode they never reach the application. `control+pageUp` / `control+pageDown` are bound
nowhere in `cursorManager.py` or `browseMode.py`, so those pass through — hence both pairs are
bound, and the help says which one to reach for while reading. Each page's accessible name carries
its position (*"Page 3 of 6"*), so the reader announces where you landed without this program
speaking over it.

**Two rules the markup must never break**, both load-bearing and both silent when broken:

1. Never emit `role="application"` — NVDA switches browse mode **off** for it
   (`class Application(Document): shouldCreateTreeInterceptor = False`), and the screen would look
   identical.
2. Never write role names into the text, for the double-speaking reason above.

**What could not be verified.** No screen reader is installed on the development machine. Everything
above about the *tree* was observed directly; everything about what NVDA *does* with that tree comes
from reading NVDA's source. The reasoning is sound and the preconditions are met, but the first
real test is Denise pressing `Ctrl+Alt+RightArrow` in a table.

### Tables in a grid — the older path, still there

`Ctrl+Shift+T` still opens the current table in a real `DataGridView`. This was built before the
browse view and is kept for the text view, where a table has to be laid out as text.

| | Control type | Patterns exposed |
|---|---|---|
| read-only `TextBox` | `Edit` | Text, Value, Scroll |
| `DataGridView` | `DataGrid` | Grid, **Table** |
| a cell in it | `DataItem` | GridItem, **TableItem**, Value |

Arrow keys move between cells, `Ctrl+R` reads the current cell with its headings, `Escape` returns
to the document. NVDA's `Ctrl+Alt+arrow` commands do **not** work there, for the `TextInfo` reason
above — which is exactly why the browse view exists.

One detail worth recording: the stock row-header cell reports its accessible name as *"Row 2"*
whatever value it holds, so a reader would announce the row number instead of the label. A custom
cell fixes it, and a test pins that the header comes back as `"February"` rather than `"Row 2"`.

Tables with no headings get **numbered** columns rather than invented names — a made-up heading
would be announced with every cell as though the document had said it. `Ctrl+H` inside the grid
marks the first row as headings, which is an undoable edit to the document, offered at the moment
you discover the problem rather than back in a menu.

**Being wrong here is expensive**, so detection is deliberately conservative — set to miss a real table
rather than invent one. Announcing "table, 6 rows, 2 columns" over something that is really a list
sends a listener hunting for headers and relationships that do not exist, and they have no way to
discover it is fiction. The thresholds were tuned by scanning real technical manuals, where lists
with hanging indents turned out to be the dominant false positive: a numbered or bulleted list puts
its marker in one column and its text in another and satisfies every geometric test for a table.
Half the tests for this feature are negative cases for exactly that reason.

### Commenting without pointing at the page

Every other PDF tool starts commenting with a gesture: select text with the mouse, click the
highlighter, drag a box. All of it assumes you can see where you are putting things, and the
keyboard equivalents mostly do not exist.

Here a comment attaches to **whatever the reading position is on** — this paragraph, this table
cell, this form field. "Comment on this" is a complete instruction, and the page rectangle is
derived from the element rather than asked for. That is not a workaround for the absence of a
mouse; it is a better description of what the user means, and it produces a better comment, because
the anchor is a real element and the comment can therefore record *in words* what it is about:

> Added a comment on the paragraph beginning “Payments are due”: This total looks wrong.

which is what `Ctrl+Z` reads back if you undo it.

**In the browse view, comments carry their own buttons.** In browse mode the screen reader keeps its
cursor to itself, so a command meaning "the comment I am on" has nothing to act on — the same
problem the repair prompts have. Each comment therefore renders with *Change*, *Reply* and *Delete*
buttons, which `B` finds and `Enter` activates.

Deleting reads the comment back in full before asking, rather than saying "delete this comment?".
Someone who cannot see the page has no other way to check they are on the one they meant, and a
deleted remark is not recoverable once the file is saved.

**Comments are tagged into the structure tree**, which PDF/UA requires and a checker reports as a
fault when missing. The annotation is written with `/Contents` for the reader, `/T` for the author
and `/M` in PDF's own date format so other editors sort the thread — and then given an `/Annot`
structure element that reaches it through an `/OBJR` object reference.

The detail worth recording, because guessing it produces a file that opens, validates structurally
and leads nowhere — ISO 32000 clause 14.7.5:

> For an object identified as a content item by means of an object reference, the value is an
> indirect reference to the parent structure element. For a content stream containing
> marked-content sequences that are content items, the value is an array…

So a page's `/StructParents` maps to an **array** indexed by MCID, while an annotation's
`/StructParent` maps to a **single reference**. Write an array and the tag exists with nothing able
to reach it from the annotation. A test asserts the value is not an array.

This is safe in exactly the case that worries this project most: a structure tree PDFsharp cannot
see comes back as null, so nothing is written and nothing is damaged — and those documents are
refused before the save gets that far anyway.

Two consequences that had to be handled rather than discovered later:

- **Deleting a comment removes its tag too.** A structure element pointing at an annotation that no
  longer exists is a broken tree — a worse fault than the untagged annotation it replaced.
- **A deleted tag is a structure element going missing**, which is the single failure the save's
  fingerprint guard exists to catch. It rolled the entire save back until the deletion was declared
  to it. The count passed is the exact number of tags the writer actually removed, never an
  estimate: too low refuses a legitimate save, too high excuses the real loss of somebody's
  headings.

**Untagged documents are deliberately left alone.** Creating a structure tree means setting
`/MarkInfo /Marked true`, and a document that claims to be tagged while its text is not is worse
than an honest untagged one — checkers and readers believe the claim. The comment is still written
and still perfectly readable; tagging is a bonus, not a precondition.

### Tagging a document that has none

`Ctrl+Shift+N` writes everything the layout analysis worked out — headings, lists, tables, figures —
into a **new copy** of the file as a real structure tree. It is the largest repair here, and the
reason for most of the rest: without it, all that inference lives in memory and dies when the
document closes.

The hard part is not the tree, it is that a tree is a set of labels attached to nothing until each
element points at an MCID — and an MCID exists only where the **content stream** says so. The text
is already drawn, so it has to be marked where it sits. Two simpler designs were tried against a
real file first and both fail:

| tried | why it fails |
|---|---|
| wrap each `BT`/`ET` text object | the sample's whole first page is **one** `BT`/`ET` block holding sixteen show operators, so this gives one tag per page |
| match operators by their text | the operands are `<002B0052…>` — glyph indices into a subset font. There is nothing to match against |

What is reliable is **position**, because that is what the operators state outright. So the tagger
tracks the text matrix through `cm`/`q`/`Q`/`BT`/`Tm`/`Td`/`TD`/`T*`/`TL`, computes where every show
operator lands, and hands that to the caller to match against the elements found by layout. Position
is the common language between two independent readings of the same page, and a test asserts they
agree on the real sample.

The tree **mirrors the element tree** rather than being a flat list. A run of `/TD` with no `/Table`
and no `/TR` above it is not a table, it is eleven loose cells, and a reader would give the numbers
with no idea which row or column they came from.

**It refuses to lie.** If less than two thirds of a page's text lands inside a tag, the document is
not marked as tagged, and the user is told the coverage as a number. A file claiming
`/MarkInfo /Marked true` over mostly-untagged text is worse than one making no claim — a checker
passes it and a reader believes it. It is also always a **copy**: it rewrites every page's content
stream, and while that is tested not to alter the drawing, the person running it cannot look at the
result to check.

**The bug worth recording**, because it is the exact failure mode this whole design is exposed to.
The first working version inserted its marks between an operand and the operator that consumes it —
`<002B…> /H1 <</MCID 0>> BDC Tj` — leaving `Tj` with nothing to draw. The file still opened. The
pages still rendered. **38% of the document's text was simply gone**, and the only reason it was
caught is that the save's own fingerprint refused to write it. The test that was supposed to cover
this asserted that `BDC` came before `Tj` — which was still true. It now asserts `BDC` comes before
the *operand*.

### For people who can see the screen

There is a **page picture** pane (`Ctrl+Shift+R`) showing the page as it is actually printed,
beside the text. It is a supplement and stays one: it is **collapsed by default**, out of the tab
order until asked for, never takes focus by surprise, and every command works with it closed. The
text view leads and the picture follows — when the reading position moves, the page scrolls and the
current element is outlined. Never the other way round, or the primary user's position could be
moved by a pane they cannot see.

`F6` switches between the document and the picture. `Ctrl+plus` / `Ctrl+minus` zoom whichever pane
has focus.

A sighted keyboard user is not stranded in the browse view: the **Go** menu carries every navigation
command, and moving with it scrolls the browse view to match. `Ctrl+Shift+B` returns to the text
view, where the single-letter keys work for anyone not running a screen reader.

**The reason it was worth building:** the remediation workflow asks you to describe an image, and
until now *nobody* using this program could see the image — not the blind user, obviously, but not
a sighted colleague helping either, because the document was only ever shown as text. The alt-text
prompt now shows the figure, cropped out of the rendered page. That turns an impossible request
into an ordinary one.

Text size and high contrast are respected throughout: no hard-coded colours, system colours
everywhere, and the on-screen text scales from 9 to 32 point.

Getting this the wrong way round is the commonest mistake in accessible software written by sighted
developers. A self-voicing app demos well and is worse to live with, because it takes away tools the
user has spent years configuring and replaces them with the developer's guesses.

### Keys

**In the browse view this program claims no single-letter keys at all.** `H`, `T`, `K` and the rest
belong to the screen reader there, which is the point of it. The table below is the **text view**,
where this program has to provide navigation because a text box cannot. The letters deliberately
match NVDA's, so nothing has to be learned twice.

| Key | Moves to |
|---|---|
| `H` | next heading (`Shift+H` for previous — this applies to all of these) |
| `1`–`6` | next heading at that level |
| `K` | next link |
| `F` | next form field |
| `Shift+F` | next field still needing an answer |
| `T` / `G` / `L` / `P` | next table / graphic / list / paragraph |
| `Ctrl+Shift+T` | open the current table in a real grid |
| `A` | next comment |
| `Ctrl+Shift+M` | write a comment on whatever you are on |
| `Ctrl+Shift+K` | highlight what you are on, with a note |
| `F2` / `Ctrl+Shift+Y` / `Ctrl+Delete` | change / reply to / delete the comment you are on |
| `D` | next accessibility problem |
| `Enter` | activate whatever you are on |
| `Page Up`/`Down` | previous/next page |
| `Ctrl+Page Up`/`Down` | previous/next page — **also works in the browse view** |

| Shortcut | Does |
|---|---|
| `Ctrl+Shift+B` | switch between the browse view and the text view |
| `Ctrl+W` | say where you are |
| `Ctrl+Space` | repeat the last announcement |
| `Ctrl+Shift+A` | check accessibility |
| `Ctrl+Shift+F` | walk through the problems one at a time |
| `Ctrl+Z` | undo, and say what was undone |
| `F1` | read out the key list |
| `Shift+F1` | open the key list in a window you can browse |

`F1` and `Shift+F1` are deliberately different. Speech is transient: `F1` is right for a quick
reminder, but useless when you want to *explore* the key list — you cannot go back a line, skip to
the part about forms, or copy it somewhere. `Shift+F1` puts the same information in a read-only
text box, so the review cursor, Say All, find-in-text, braille tracking and `Ctrl+A`/`Ctrl+C` all
work on it. Every window has it, and each shows its own keys.

A test asserts that **every key the main window handles appears in the browsable help.** Key
documentation rots — a shortcut gets added, the help does not — and for someone who cannot see a
toolbar, an undocumented key is a feature that does not exist.

---

## Trying it out

There is a sample document at **`samples/Sample form (deliberately inaccessible).pdf`** — a
six-page benefits application form. It is deliberately imperfect: every fault the checker knows how
to find is planted in it on purpose, so the guided repair workflow has something real to walk
through and you can hear the difference before and after.

Regenerate it at any time with:

```bash
dotnet run --project tests/AccessiblePdfEditor.Tests -- --sample "samples/Sample form.pdf"
```

Opening it and pressing `Ctrl+Shift+A` reports **nine problems, all repairable**:

| Planted fault | What you hear |
|---|---|
| A required field with no label and no caption on the page | *Blocking problem* — the one that makes a form unusable |
| An untagged document | *Serious* — all structure is guesswork |
| A chart with no description | *Serious* — the guided workflow will ask you to describe it |
| No declared language | Read in whatever voice your reader happens to be using |
| A running footer on all six pages | Read out at every page boundary until you mark it |
| A label guessed from nearby text | Usually right, but the document never actually said so |
| A link reading "click here" | Tells a listener nothing |
| A heading jumping from level 2 to level 4 | The outline lies about the document's shape |
| No document title | Announced by filename |

Good things to try: `Ctrl+Shift+F` to walk the repairs one at a time; `F` and `Shift+F` to move
between form fields and to the next one still needing an answer; `Ctrl+Shift+G` to sign it; then
`Ctrl+Shift+A` again to hear the count come down.

The form also contains a drop-down whose stored values differ from its displayed ones
(`GB` / "United Kingdom"), a read-only reference number, a radio group whose options are unlabelled
in the file, and an untagged payments table — which is detected from its layout, so pressing `T`
finds it and its cells announce with their headers.

---

## Getting the app

A ready-to-run build is on the [releases page](https://github.com/taljazz/accessible-pdf-editor/releases):
**`AccessiblePdfEditor-v1.0.1-win-x64.zip`**, about 56 MB. Unzip it anywhere and run the exe inside.
Nothing to install — it carries its own copy of .NET and of the screen-reader bridge.

**Keep the folder together.** The DLLs beside the exe are the screen-reader bridge (Tolk, and the
NVDA and Dolphin clients) and the sound engine. Move the exe out on its own and it will still start,
still look completely fine, and not speak.

Windows will warn that it is from an unknown publisher, because it is not code-signed. That warning
is honest and you should read it: this is a hobby build from a public repository. Verify the
download against the SHA-256 published with the release before running it, and unblock the zip
before extracting — otherwise the extracted DLLs inherit the mark-of-the-web and may refuse to load.

```powershell
Get-FileHash AccessiblePdfEditor-v1.0.1-win-x64.zip -Algorithm SHA256
```

The one thing not bundled is the **Microsoft Edge WebView2 runtime**, which the browse view needs.
It ships with Windows 11 and with Edge, so it is almost certainly already there; if it is not, the
program says so and uses the text view instead.

## Building and running

```bash
dotnet build "AccessiblePdfEditor.slnx" -c Release
```

```bash
dotnet run --project src/AccessiblePdfEditor/AccessiblePdfEditor.csproj
```

```bash
dotnet run --project tests/AccessiblePdfEditor.Tests/AccessiblePdfEditor.Tests.csproj
```

To produce the build that goes on the releases page — one folder to zip, the managed code bundled
into the exe and the native libraries sitting beside it:

```bash
dotnet publish src/AccessiblePdfEditor/AccessiblePdfEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:AllowedReferenceRelatedFileExtensions=none
```

`IncludeNativeLibrariesForSelfExtract=false` is deliberate and is the whole reason this ships as a
zip rather than as one file. Set to **true**, the native libraries are bundled into the exe and
unpacked to a temporary directory at run time — which does work, and was measured working, but it
makes the program's ability to speak depend on an extraction step nobody can see. Set to **false**,
Tolk and the reader clients sit next to the exe and load from the application's own folder, which is
the ordinary Windows path and needs nothing to go right.

Leaving the flag out entirely would be the dangerous case: the libraries are then neither bundled
nor copied, and the result is a program that starts, looks perfectly fine, and cannot speak — the
worst failure this application can have, and one nothing on screen would reveal. So it is worth
checking after any change to the publish settings, on the actual artifact:

```powershell
(Get-Process AccessiblePdfEditor).Modules | Where-Object { $_.ModuleName -in 'Tolk.dll','OpenAL32.dll','WebView2Loader.dll' }
```

The test runner is a plain console program with no test-framework dependency, and exits non-zero on
failure so it can gate a build. **281 tests, 2050 checks, all passing; 0 warnings in Debug and
Release.**

The browse view needs the **Microsoft Edge WebView2 runtime**, which ships with Windows 11 and with
Edge. It is not bundled here. If it is missing the program says so in a real label, falls back to
the text view, and everything else works unchanged — a missing component must never be the reason
someone cannot read their document.

Most assertions are about what the program *says*, because for a screen-reader application the
spoken output is not a presentation detail on top of the real behaviour — it *is* the behaviour. A
table cell that computes its headers correctly but never mentions them is broken.

Some tests go further and assert on the **UI Automation tree** — the interfaces a screen reader
actually consumes. No screen reader is installed on the development machine, so "NVDA announces the
header" would be a guess; "the control exposes `TableItem`, and it returns the header `February`"
is evidence. That distinction is kept throughout.

One test class exists purely because of a bug every other test missed: navigation reported success
and announced the right thing while **the caret never moved**, so the review cursor stayed put and
the key appeared to do nothing. The service was right, the renderer was right, and the gap was
between them. `NavigationIntegrationTests` now walks every navigation key and insists each one can
place the caret — because for this application, navigation is not "the service returned an element",
it is "the screen reader is now reading from the right place".

Several tests run against real PDFs found on the machine rather than synthetic ones, because the
most important failure only appears in files produced by real tools.

---

## Structure

```
src/AccessiblePdfEditor/
  Accessibility/   speech (Tolk), earcons (OpenAL), and the announcement rules
  Model/           the document model — elements, form fields, and every enum
    Elements/      DocumentElement hierarchy: headings, lists, tables, figures, links…
    Forms/         PdfFormField hierarchy, with a visitor for double dispatch
  Ingestion/       loading, the two structure extractors (tagged, and layout inference),
                   and table detection from alignment
  Rendering/       DocumentHtmlWriter — the model as semantic HTML, which is what makes the
                   browse view a real document — plus page rasterisation for the visual pane
  Editing/         EditCommand hierarchy and the undo history that can be read aloud
  Auditing/        AuditRuleBase and one rule per way a PDF fails a reader
  Navigation/      granularity-based navigation and search
  Persistence/     saving, appearance streams, and the structure-loss guard
  Configuration/   settings
  UI/              AccessibleFormBase and the windows
tests/
```

### Design notes

- **The document is a document, not an imitation of one.** `DocumentHtmlWriter` emits `<h2>`,
  `<th scope="row">`, `<label for>` and `required` rather than writing "Heading 2:" and "required"
  into the text, because the screen reader announces roles and states itself. The single biggest
  correction in this codebase was realising that reimplementing browse mode was the wrong goal.
- **`DocumentElement.Describe` is a template method.** It fixes the shape of every spoken
  announcement in the application — role, content, state, position — while each subclass supplies
  its own parts. Adding an element type cannot accidentally invent a new announcement style.
- **`PdfFormField` resolves its label from four sources in a fixed order of trust**, and reports
  which one it used, so a guess is never passed off as the document's own statement.
- **The form-field visitor** gives real double dispatch: adding a field type makes the compiler
  point at every place that needs updating, rather than leaving a switch to fall through to a
  default that silently drops the user's answer.
- **The write layer uses raw dictionaries throughout**, because PDFsharp's typed setters for radio
  buttons, combo boxes and list boxes write corrupt values and report success.
- **Every enum carries real state.** `FieldStates` is `[Flags]` because a field is routinely
  required, empty and invalid at once, and the announcement has to mention all three.

---

## Dependencies

- **PdfPig 0.1.15** (Apache-2.0) — reading: text with positions, the tag tree, outlines, annotations
- **PDFsharp 6.2.4** (MIT) — writing: form values, appearance streams, metadata
- **Microsoft.Web.WebView2** — the browse view, which is what gives the screen reader a real
  document to read rather than an imitation of one. Degrades to the text view when the runtime is
  absent.
- **OpenTK 4.9.4** — OpenAL, for earcons that overlap rather than queue
- **Tolk** — the screen-reader bridge (NVDA and SAPI clients included)

Every library-specific workaround in this codebase was found by writing code that exercised the
library and reading back what it produced, not from documentation. Where a library misbehaves, the
comment at the call site says what was observed.

Five native DLLs are redistributed in this repository — Tolk and its bundled screen-reader clients,
and OpenAL Soft — because without them the program cannot speak. Their licences and provenance are
recorded in **[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)**, read from the binaries and package
metadata rather than from memory. One of them, Dolphin's `SAAPI64.dll`, carries no licence statement
of its own and is flagged there as unverified.

**No licence is declared for this project's own code**, which means default copyright applies and
nobody may reuse it. If you want it to be usable by others, add a `LICENSE` file — MIT would sit
comfortably with the dependencies above.
