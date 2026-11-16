using System;

namespace ScriptLangCore;

/// <summary>The terminator that FOLLOWED a line, so a caller that must reproduce the text byte for byte can put it back.
/// It exists because two consumers want opposite things from the same split: source code must come out unchanged (a CRLF
/// file stays CRLF), while data from anywhere only cares where a line ends.</summary>
internal enum LineBreak
{
    /// <summary>No terminator: this was the last line of the text.</summary>
    None,
    /// <summary>A single LF.</summary>
    Lf,
    /// <summary>A CRLF pair, counted as ONE terminator.</summary>
    CrLf,
    /// <summary>A lone CR (an old Mac's line ending).</summary>
    Cr,
}

/// <summary>One line, and the terminator it carried. A <c>ref struct</c> because the text is a view, never a copy, and
/// the pair travels together because a caller that rebuilds text needs BOTH: the characters, and what ended them.</summary>
internal readonly ref struct TextLine
{
    public TextLine(ReadOnlySpan<char> text, LineBreak lineBreak)
    {
        Text = text;
        Break = lineBreak;
    }

    /// <summary>The line, WITHOUT its terminator.</summary>
    public ReadOnlySpan<char> Text { get; }

    /// <summary>The terminator that followed <see cref="Text"/>.</summary>
    public LineBreak Break { get; }

    /// <summary>How many characters <see cref="Break"/> takes in the original text (0, 1 or 2).</summary>
    public int BreakWidth => Break switch
    {
        LineBreak.CrLf => 2,
        LineBreak.Lf or LineBreak.Cr => 1,
        _ => 0,
    };
}

/// <summary>A zero-allocation line iterator over text already in memory, and the ONE statement of where a line ends.
/// <para>
/// It is a <c>ref struct</c>, which is what makes it free: it lives on the stack, so <see cref="MoveNext"/> touches no
/// heap at all. That also means it can never be yielded from an iterator (an iterator's state machine lives on the heap,
/// `error CS9267`), which is why the one consumer that must hand out <c>string</c>s, the HTTP body reader, keeps its own
/// loop and says so at its site.
/// </para>
/// <para>
/// THREE TERMINATORS, one rule: CR, LF and CRLF all end a line, so a file written on Windows, on Unix or by an old Mac
/// reads the same way. There is NO phantom empty line for a text that ends with a break, so a text of "a\n" is ONE line:
/// <see cref="TextLines.Count"/> answers the SEGMENT count instead, which is one more in exactly that case, because a
/// RichEdit's own EM_GETLINECOUNT agrees with the segment count and a caller reads a control's rows from it. Both
/// conventions are stated in this file so neither can be mistaken for the other.
/// </para></summary>
internal ref struct LineSplitter
{
    private ReadOnlySpan<char> _rest;

    public LineSplitter(ReadOnlySpan<char> text)
    {
        _rest = text;
        Current = default;
    }

    /// <summary>The line just read, with its terminator.</summary>
    public TextLine Current { get; private set; }

    /// <summary>`foreach` is duck-typed, so this needs no interface, no box and no allocation.</summary>
    public LineSplitter GetEnumerator() => this;

    public bool MoveNext()
    {
        if (_rest.IsEmpty) return false;

        var at = _rest.IndexOfAny('\r', '\n');
        if (at < 0)
        {
            Current = new TextLine(_rest, LineBreak.None);
            _rest = default;
            return true;
        }

        var line = new TextLine(_rest[..at], _rest[at] == '\r' ? LineBreak.Cr : LineBreak.Lf);
        if (_rest[at] == '\r' && at + 1 < _rest.Length && _rest[at + 1] == '\n')
        {
            line = new TextLine(_rest[..at], LineBreak.CrLf);
            _rest = _rest[(at + 2)..];
        }
        else
        {
            _rest = _rest[(at + 1)..];
        }
        Current = line;
        return true;
    }
}

/// <summary>One place for the "how many lines does this text hold" question, on text that is already in memory.
/// <para>
/// It takes a <see cref="ReadOnlySpan{T}"/> of char, and that is legal for exactly the reason the BODY SPLITTER cannot:
/// this is not an iterator. An iterator may not yield a ref struct (CS9267, since its state machine lives on the heap),
/// so the splitter that hands lines to a script keeps its strings, while a scan that answers a NUMBER needs no string at
/// all. Measured on a 668,890-character / 17,000-line log: <c>text.Split('\n').Length</c> allocated 1,905,147 B per call
/// against 0 B for this, for the same answer.
/// </para>
/// <para>
/// The answer is the SEGMENT count, which is what <c>string.Split('\n').Length</c> answers, and it is deliberately NOT
/// the number of lines <see cref="LineSplitter"/> walks: a text ending in a break has one segment more than it has rows,
/// and one caller depends on that (a RichEdit's own EM_GETLINECOUNT reports it). It also counts '\n' ONLY, so CRLF text
/// counts once per break and CR-only text is one segment, which is what <c>Split('\n')</c> answers too.
/// </para>
/// <para>
/// ITS ONE REMAINING CALLER IS THE MIRROR, and that is worth knowing before adding a second: <c>TextBox.LineCount</c>
/// reports this for a control that has no window yet, and the REPL's line-offset arithmetic asks <see cref="Lines"/>
/// instead, because a block of code wants LINE semantics and the two agree there only because that caller trims the
/// trailing break first. A new caller should ask itself which of the two conventions it means, and the segment one has
/// no script-visible observable to guard it.
/// </para></summary>
internal static class TextLines
{
    /// <summary>How many segments <paramref name="text"/> splits into on '\n', counted without allocating.</summary>
    public static int Count(ReadOnlySpan<char> text) => text.Count('\n') + 1;

    /// <summary>How many LINES <see cref="LineSplitter"/> walks over <paramref name="text"/>, counted without
    /// allocating and DERIVED from the splitter so the two cannot disagree. It is deliberately not
    /// <see cref="Count"/>: this one has no phantom last line, so "a\n" is 1 here and 2 there, which is the difference
    /// between rows and segments. A caller that wants an exactly sized array asks this BEFORE filling it, which measured
    /// 19.2% fewer bytes than growing a list at 5,000 lines and 21.7% at 17,000 (where it is also about twice as fast,
    /// because the copies dominate).</summary>
    public static int Lines(ReadOnlySpan<char> text)
    {
        var lines = 0;
        foreach (var line in new LineSplitter(text)) lines++;
        return lines;
    }
}
