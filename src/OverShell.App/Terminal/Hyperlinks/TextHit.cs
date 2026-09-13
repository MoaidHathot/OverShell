using System.Windows;
using System.Windows.Media;

namespace OverShell.App.Terminal.Hyperlinks;

/// <summary>
/// The text under a pointer, exactly as the terminal's own buffer reports it.
/// </summary>
/// <param name="Line">
/// The logical line under the pointer: the buffer rows that wrapped into each other,
/// concatenated with nothing in between. Which rows wrap is the buffer's own flag, read
/// back through UI Automation (DESIGN.md §7.10), not a guess.
/// </param>
/// <param name="Offset">Text offset of the pointer's cell within <paramref name="Line"/>.</param>
/// <param name="Rows">The individual rows that make up <paramref name="Line"/>, each exactly as the buffer returned it.</param>
/// <param name="HitRow">Index into <paramref name="Rows"/> of the row under the pointer.</param>
/// <param name="HitRowBounds">On-screen rectangle of that row, physical pixels.</param>
/// <param name="TerminalBounds">On-screen rectangle of the whole terminal, physical pixels.</param>
/// <param name="TruncatedAbove">The walk up stopped at its cap before reaching the line's start.</param>
/// <param name="TruncatedBelow">The walk down stopped at its cap before reaching the line's end.</param>
internal sealed record TextHit(
    string Line,
    int Offset,
    IReadOnlyList<string> Rows,
    int HitRow,
    Rect HitRowBounds,
    Rect TerminalBounds,
    bool TruncatedAbove,
    bool TruncatedBelow);

/// <summary>A link found in a <see cref="TextHit.Line"/>: the target plus the span of text that carries it.</summary>
internal sealed record LinkMatch(string Url, int Start, int Length);

/// <summary>
/// A link under the pointer with everything needed to draw and open it.
/// </summary>
/// <param name="Text">What was under the pointer.</param>
/// <param name="Match">The link and its span in <see cref="TextHit.Line"/>.</param>
/// <param name="Rects">Screen rectangles of the cells the link occupies, one per row, physical pixels. As reported by the terminal.</param>
/// <param name="Foreground">The colour the link's text is rendered in, when it is uniform.</param>
/// <param name="FontFamily">The font family the terminal resolved for rendering, when it reports one.</param>
internal sealed record LinkHit(
    TextHit Text,
    LinkMatch Match,
    IReadOnlyList<Rect> Rects,
    Color? Foreground,
    string? FontFamily);

/// <summary>
/// Result of one probe: the text under the pointer, the link there if any, and the
/// terminal's cell lattice.
/// </summary>
/// <param name="Text">The text under the pointer; null when the pointer is below the last written row.</param>
/// <param name="Link">The link under the pointer, if any.</param>
/// <param name="CellGrid">
/// Origin of the terminal and the size of one cell, physical pixels — from the row
/// rectangle the terminal reported. Known even when <paramref name="Text"/> is not.
/// </param>
internal sealed record ProbeResult(TextHit? Text, LinkHit? Link, (Point Origin, Size Cell)? CellGrid);
