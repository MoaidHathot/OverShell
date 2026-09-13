using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OverShell.App.Terminal.WindowsTerminal;

/// <summary>
/// Reproduces the cell and underline metrics Windows Terminal's AtlasEngine derives from a
/// font, so an underline drawn over the native control lands exactly where the renderer
/// would draw its own.
/// <para>
/// The arithmetic is <c>AtlasEngine::_resolveFontMetrics</c> line for line — same
/// DirectWrite metrics, same <c>roundf</c> at the same points (half away from zero, which
/// is not .NET's default). It also yields the cell size, which the caller compares with
/// what the terminal actually reports: agreement on both width and height is strong
/// evidence the same font was resolved; disagreement means "do not trust the underline".
/// </para>
/// </summary>
internal static class AtlasFontMetrics
{
    public sealed record Result(string Family, int CellWidth, int CellHeight, int UnderlineTop, int UnderlineThickness, int Baseline);

    private const string FallbackFamily = "Consolas";
    private const int NormalWeight = 400;
    private const int NormalStretch = 5;
    private const int NormalStyle = 0;

    private static readonly ConcurrentDictionary<(string Family, float SizePt, int Dpi), Result?> Cache = new();
    private static readonly Lock FactoryGate = new();
    private static IDWriteFactory? _factory;
    private static IDWriteFontCollection? _systemFonts;

    /// <param name="familyList">The comma-separated <c>font.face</c> value handed to the control.</param>
    /// <param name="renderedFamily">The family the terminal reports it resolved, when known. Takes precedence.</param>
    /// <param name="fontSizePt">Point size handed to the control.</param>
    /// <param name="dpi">The DPI the control renders at.</param>
    public static Result? Resolve(string familyList, string? renderedFamily, float fontSizePt, int dpi)
    {
        // The same resolution order as the renderer: first family in the list that exists,
        // else Consolas. A reported rendered family short-circuits that.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(renderedFamily))
        {
            candidates.Add(renderedFamily.Trim());
        }

        candidates.AddRange(familyList.Split(',').Select(f => f.Trim().Trim('"', '\'')).Where(f => f.Length > 0));
        candidates.Add(FallbackFamily);

        foreach (var family in candidates)
        {
            var result = Cache.GetOrAdd((family, fontSizePt, dpi), static key => Measure(key.Family, key.SizePt, key.Dpi));
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static Result? Measure(string family, float fontSizePt, int dpi)
    {
        try
        {
            IDWriteFontCollection fonts;
            lock (FactoryGate)
            {
                if (_systemFonts is null)
                {
                    var iid = typeof(IDWriteFactory).GUID;
                    if (DWriteCreateFactory(0 /* shared */, ref iid, out var unknown) < 0 || unknown is not IDWriteFactory factory)
                    {
                        return null;
                    }

                    _factory = factory;
                    if (_factory.GetSystemFontCollection(out _systemFonts, 0) < 0 || _systemFonts is null)
                    {
                        return null;
                    }
                }

                fonts = _systemFonts;
            }

            if (fonts.FindFamilyName(family, out var index, out var exists) < 0 || exists == 0)
            {
                return null;
            }

            if (fonts.GetFontFamily(index, out var fontFamily) < 0 ||
                fontFamily.GetFirstMatchingFont(NormalWeight, NormalStretch, NormalStyle, out var font) < 0 ||
                font.CreateFontFace(out var face) < 0)
            {
                return null;
            }

            face.GetMetrics(out var m);

            // Point sizes are at 72 DPI; the renderer wants pixels at the display's DPI.
            var fontSizeInPx = fontSizePt / 72.0f * dpi;
            var designUnitsPerPx = fontSizeInPx / m.DesignUnitsPerEm;
            var ascent = m.Ascent * designUnitsPerPx;
            var descent = m.Descent * designUnitsPerPx;
            var lineGap = m.LineGap * designUnitsPerPx;
            var underlinePosition = -m.UnderlinePosition * designUnitsPerPx;
            var underlineThickness = m.UnderlineThickness * designUnitsPerPx;
            var advanceHeight = ascent + descent + lineGap;

            // The advance of "0", as CSS defines the ch unit; 0.5em when the font has none.
            var advanceWidth = 0.5f * fontSizeInPx;
            uint zero = '0';
            var glyphIndex = new ushort[1];
            if (face.GetGlyphIndices([zero], 1, glyphIndex) >= 0 && glyphIndex[0] != 0)
            {
                var glyphMetrics = new DwriteGlyphMetrics[1];
                if (face.GetDesignGlyphMetrics(glyphIndex, 1, glyphMetrics, 0) >= 0)
                {
                    advanceWidth = glyphMetrics[0].AdvanceWidth * designUnitsPerPx;
                }
            }

            // No cellWidth/cellHeight override reaches the control, so the adjusted sizes
            // are the natural ones.
            var adjustedWidth = MathF.Max(1.0f, RoundF(advanceWidth));
            var adjustedHeight = MathF.Max(1.0f, RoundF(advanceHeight));

            var baseline = RoundF(ascent + ((lineGap + adjustedHeight - advanceHeight) / 2.0f));
            var underlinePos = RoundF(baseline + underlinePosition);
            var underlineWidth = MathF.Max(1.0f, RoundF(underlineThickness));

            var cellWidth = (int)adjustedWidth;
            var cellHeight = (int)adjustedHeight;
            var thickness = (int)underlineWidth;

            // The renderer clips to the cell; so must the overlay.
            var top = Math.Clamp((int)underlinePos, 0, Math.Max(0, cellHeight - thickness));

            return new Result(family, cellWidth, cellHeight, top, thickness, (int)baseline);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>C's <c>roundf</c>: halves go away from zero. <see cref="MathF.Round(float)"/> alone goes to even.</summary>
    private static float RoundF(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    // ------------------------------------------------------------ DirectWrite

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DwriteFontMetrics
    {
        public ushort DesignUnitsPerEm;
        public ushort Ascent;
        public ushort Descent;
        public short LineGap;
        public ushort CapHeight;
        public ushort XHeight;
        public short UnderlinePosition;
        public ushort UnderlineThickness;
        public short StrikethroughPosition;
        public ushort StrikethroughThickness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwriteGlyphMetrics
    {
        public int LeftSideBearing;
        public uint AdvanceWidth;
        public int RightSideBearing;
        public int TopSideBearing;
        public uint AdvanceHeight;
        public int BottomSideBearing;
        public int VerticalOriginY;
    }

    // Only the vtable slots that are called carry real signatures; the rest are placeholders
    // that keep the slot numbering right. COM interface inheritance is not honoured by the
    // interop layer, so IDWriteFontFamily re-declares IDWriteFontList's slots.

    [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig] int GetSystemFontCollection(out IDWriteFontCollection fontCollection, int checkForUpdates);
    }

    [ComImport, Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig] uint GetFontFamilyCount();
        [PreserveSig] int GetFontFamily(uint index, out IDWriteFontFamily fontFamily);
        [PreserveSig] int FindFamilyName([MarshalAs(UnmanagedType.LPWStr)] string familyName, out uint index, out int exists);
    }

    [ComImport, Guid("da20d8ef-812a-4c43-9802-62ec4abd7add"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        [PreserveSig] void Slot3_GetFontCollection();
        [PreserveSig] void Slot4_GetFontCount();
        [PreserveSig] void Slot5_GetFont();
        [PreserveSig] void Slot6_GetFamilyNames();
        [PreserveSig] int GetFirstMatchingFont(int weight, int stretch, int style, out IDWriteFont matchingFont);
    }

    [ComImport, Guid("acd16696-8c14-4f5d-877e-fe3fc1d32737"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFont
    {
        [PreserveSig] void Slot3_GetFontFamily();
        [PreserveSig] void Slot4_GetWeight();
        [PreserveSig] void Slot5_GetStretch();
        [PreserveSig] void Slot6_GetStyle();
        [PreserveSig] void Slot7_IsSymbolFont();
        [PreserveSig] void Slot8_GetFaceNames();
        [PreserveSig] void Slot9_GetInformationalStrings();
        [PreserveSig] void Slot10_GetSimulations();
        [PreserveSig] void Slot11_GetMetrics();
        [PreserveSig] void Slot12_HasCharacter();
        [PreserveSig] int CreateFontFace(out IDWriteFontFace fontFace);
    }

    // Arrays in ComImport interfaces default to SAFEARRAY marshalling — DirectWrite wants
    // raw pointers, hence the explicit LPArray on every array parameter.
    [ComImport, Guid("5f49804d-7024-4d43-bfa9-d25984f53849"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFace
    {
        [PreserveSig] void Slot3_GetType();
        [PreserveSig] void Slot4_GetFiles();
        [PreserveSig] void Slot5_GetIndex();
        [PreserveSig] void Slot6_GetSimulations();
        [PreserveSig] void Slot7_IsSymbolFont();
        [PreserveSig] void GetMetrics(out DwriteFontMetrics fontFaceMetrics);
        [PreserveSig] void Slot9_GetGlyphCount();

        [PreserveSig]
        int GetDesignGlyphMetrics(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ushort[] glyphIndices,
            uint glyphCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DwriteGlyphMetrics[] glyphMetrics,
            int isSideways);

        [PreserveSig]
        int GetGlyphIndices(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] codePoints,
            uint codePointCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ushort[] glyphIndices);
    }
}
