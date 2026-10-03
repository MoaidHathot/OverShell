using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OverShell.App.Diagnostics;

/// <summary>
/// The verify-first spikes from DESIGN.md §12.7, run inside the real application with
/// <c>OVERSHELL_SPIKES=1</c> and written to <c>%TEMP%\overshell-spikes.log</c>. None of
/// them injects input (§7.8): they open tabs, re-parent surfaces, open and close a
/// popup, and read state back — the same operations the features will perform.
/// </summary>
internal static class Spikes
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("OVERSHELL_SPIKES");

    private static readonly bool Enabled = Mode is "1" or "find";

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-spikes.log");

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    public static void Schedule(MainWindow window, TerminalTab firstTab)
    {
        if (!Enabled)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Mode == "find")
                {
                    await RunFindAsync(firstTab);
                }
                else
                {
                    await RunAsync(window, firstTab);
                }
            }
            catch (Exception e)
            {
                Log($"SPIKES FAILED: {e}");
            }
        };
        timer.Start();
    }

    private static async Task RunAsync(MainWindow window, TerminalTab first)
    {
        Log("=== spikes start ===");

        var hwnd1 = MainWindow.FindTerminalHwnd(first.View);
        Log($"tab1 hwnd=0x{hwnd1:X} visible={IsWindowVisible(hwnd1)}");

        // Spike 1 — UIA text from a HIDDEN tab.
        // Open a second tab, which hides the first, then read the first through UIA.
        var profile = first.Profile;
        var second = window.AddTab(profile, activate: true);
        await Task.Delay(2500);

        var hwnd2 = MainWindow.FindTerminalHwnd(second.View);
        Log($"tab2 opened hwnd=0x{hwnd2:X}; tab1 visible now={IsWindowVisible(hwnd1)} view.Visibility={first.View.Visibility}");

        var sw = Stopwatch.StartNew();
        var hiddenText = await Task.Run(() => ReadVisibleText(hwnd1));
        Log($"spike1 hidden-tab UIA text: {(hiddenText is null ? "FAILED (null)" : $"{hiddenText.Length} chars")} in {sw.Elapsed.TotalMilliseconds:F1} ms; first line='{FirstLine(hiddenText)}'");

        sw.Restart();
        var hiddenDoc = await Task.Run(() => ReadDocumentText(hwnd1));
        Log($"spike1 hidden-tab DocumentRange: {(hiddenDoc is null ? "FAILED" : $"{hiddenDoc.Length} chars")} in {sw.Elapsed.TotalMilliseconds:F1} ms");

        var hiddenBounds = await Task.Run(() => ReadBounds(hwnd1));
        Log($"spike1 hidden-tab BoundingRectangle: {hiddenBounds}");

        // Spike 2a — re-parent the (hidden) first tab's view to another panel in the same window.
        var host = (Panel)first.View.Parent;
        var side = new Grid { Width = 400, Height = 300 };
        var rootGrid = (Grid)((Border)window.Content).Child;
        rootGrid.Children.Add(side);
        Grid.SetRow(side, 1);

        host.Children.Remove(first.View);
        side.Children.Add(first.View);
        await Task.Delay(300);
        var hwnd1b = MainWindow.FindTerminalHwnd(first.View);
        var text2a = await Task.Run(() => ReadVisibleText(hwnd1b));
        Log($"spike2a re-parent same window: hwnd same={hwnd1 == hwnd1b} (0x{hwnd1b:X}) parent=0x{GetParent(hwnd1b):X} text={(text2a is null ? "FAILED" : $"{text2a.Length} chars")} first line='{FirstLine(text2a)}'");

        // Spike 2b — re-parent to a SECOND window (tear-off path), then back.
        var tearOff = new Window
        {
            Title = "OverShell spike tear-off",
            Width = 900,
            Height = 500,
            ShowActivated = false,
            Left = window.Left + 40,
            Top = window.Top + 40,
        };
        var tearGrid = new Grid();
        tearOff.Content = tearGrid;
        tearOff.Show();
        await Task.Delay(300);

        side.Children.Remove(first.View);
        tearGrid.Children.Add(first.View);
        first.View.Visibility = Visibility.Visible;
        await Task.Delay(800);

        var hwnd1c = MainWindow.FindTerminalHwnd(first.View);
        var tearHwnd = new WindowInteropHelper(tearOff).Handle;
        var parentAfter = GetParent(hwnd1c);
        var text2b = await Task.Run(() => ReadVisibleText(hwnd1c));
        Log($"spike2b re-parent to 2nd window: hwnd same={hwnd1 == hwnd1c} parent=0x{parentAfter:X} tearoff=0x{tearHwnd:X} parentIsTearOff={parentAfter == tearHwnd} visible={IsWindowVisible(hwnd1c)} text={(text2b is null ? "FAILED" : $"{text2b.Length} chars")} first line='{FirstLine(text2b)}' running={first.IsRunning}");

        // Write something to the moved terminal to prove the session is still wired up.
        first.SendText("echo spike-reparent-ok\r");
        await Task.Delay(1200);
        var text2c = await Task.Run(() => ReadVisibleText(hwnd1c));
        Log($"spike2b after echo: contains marker={text2c?.Contains("spike-reparent-ok") == true}");

        // Back home.
        tearGrid.Children.Remove(first.View);
        host.Children.Add(first.View);
        first.View.Visibility = Visibility.Hidden;
        tearOff.Close();
        rootGrid.Children.Remove(side);
        await Task.Delay(300);
        var hwnd1d = MainWindow.FindTerminalHwnd(first.View);
        var mainHwnd = new WindowInteropHelper(window).Handle;
        Log($"spike2c back to main: hwnd same={hwnd1 == hwnd1d} parent=0x{GetParent(hwnd1d):X} main=0x{mainHwnd:X} parentIsMain={GetParent(hwnd1d) == mainHwnd} running={first.IsRunning}");

        // Spike 3 — a Popup over the terminal takes and returns focus cleanly.
        var focusBefore = GetFocus();
        var box = new TextBox { Width = 300, Height = 28 };
        var popup = new Popup
        {
            Child = new Border { Background = System.Windows.Media.Brushes.DimGray, Padding = new Thickness(8), Child = box },
            PlacementTarget = second.View,
            Placement = PlacementMode.Center,
            StaysOpen = true,
        };
        popup.IsOpen = true;
        await Task.Delay(200);
        box.Focus();
        await Task.Delay(200);
        var focusInPopup = GetFocus();
        var popupHwnd = ((HwndSource?)PresentationSource.FromVisual(box))?.Handle ?? IntPtr.Zero;
        Log($"spike3 popup open: focus before=0x{focusBefore:X} (terminal2=0x{hwnd2:X}) focus in popup=0x{focusInPopup:X} popupHwnd=0x{popupHwnd:X} focusIsPopup={focusInPopup == popupHwnd} popupIsSeparateHwnd={popupHwnd != mainHwnd && popupHwnd != IntPtr.Zero} keyboardFocus={Keyboard.FocusedElement?.GetType().Name}");

        popup.IsOpen = false;
        await Task.Delay(100);
        second.Surface.Focus();
        await Task.Delay(200);
        var focusAfter = GetFocus();
        Log($"spike3 popup closed: focus=0x{focusAfter:X} backToTerminal={focusAfter == hwnd2}");

        // Spike 3b — the alternative: an owned, activated Window. WPF's own HwndSource then
        // holds Win32 focus and routes keys to the focused element the normal way.
        var box2 = new TextBox { Width = 300, Height = 28 };
        var palette = new Window
        {
            Owner = window,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Background = System.Windows.Media.Brushes.DimGray, Padding = new Thickness(8), Child = box2 },
        };
        palette.Show();
        box2.Focus();
        await Task.Delay(250);
        var paletteHwnd = new WindowInteropHelper(palette).Handle;
        var focusInPalette = GetFocus();
        Log($"spike3b owned window: focus=0x{focusInPalette:X} paletteHwnd=0x{paletteHwnd:X} focusIsPalette={focusInPalette == paletteHwnd} keyboardFocus={Keyboard.FocusedElement?.GetType().Name} mainActive={window.IsActive} paletteActive={palette.IsActive}");

        palette.Close();
        await Task.Delay(150);
        second.Surface.Focus();
        await Task.Delay(200);
        var focusAfter2 = GetFocus();
        Log($"spike3b closed: focus=0x{focusAfter2:X} backToTerminal={focusAfter2 == hwnd2} mainActive={window.IsActive}");

        // Spike 6 — small-tile reflow: shrink the second tab's grid and read the PTY size.
        var (c0, r0) = second.Grid;
        var view = second.View;
        var oldMargin = view.Margin;
        view.Margin = new Thickness(10, 6, 1400, 600);
        await Task.Delay(600);
        var (c1, r1) = second.Grid;
        view.Margin = oldMargin;
        await Task.Delay(600);
        var (c2, r2) = second.Grid;
        Log($"spike6 tile reflow: grid {c0}x{r0} -> shrunk {c1}x{r1} -> restored {c2}x{r2} (a live tile would resize the PTY: {(c1 < c0 ? "confirmed" : "not observed")})");

        // Clean up the second tab; leave the first as it was.
        window.CloseTab(second);
        await Task.Delay(300);
        Log($"cleanup: tabs={window.Tabs.Count} tab1 visible={IsWindowVisible(hwnd1)} running={first.IsRunning}");
        Log("=== spikes end ===");
    }

    /// <summary>
    /// Spike 7 (§12.14, find): what the UIA text provider offers for a search box - does
    /// <c>FindText</c> over <c>DocumentRange</c> reach scrollback, does <c>Select()</c>
    /// scroll the viewport and select the match as Windows Terminal's own search does
    /// (<c>Terminal::SelectNewRegion</c>), does <c>ScrollIntoView</c> work, and are the
    /// bounding rectangles of an off-screen match empty - which is how a visible match is
    /// told apart from a hidden one.
    /// </summary>
    private static async Task RunFindAsync(TerminalTab first)
    {
        Log("=== spike 7 (find) start ===");
        var hwnd = MainWindow.FindTerminalHwnd(first.View);
        Log($"tab1 hwnd=0x{hwnd:X} grid={first.Grid.Columns}x{first.Grid.Rows}");

        // 120 rows, NEEDLE on every 30th: two of them end up in scrollback.
        first.SendText("foreach ($i in 1..120) { \"find-spike row $i\" + $(if ($i % 30 -eq 0) { ' NEEDLE' } else { '' }) }\r");
        await Task.Delay(3000);

        await Task.Run(() =>
        {
            try
            {
                var element = AutomationElement.FromHandle(hwnd);
                if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern text)
                {
                    Log("  no TextPattern");
                    return;
                }

                Log($"  SupportedTextSelection={text.SupportedTextSelection}");
                var visibleBefore = text.GetVisibleRanges()[0].GetText(-1);
                Log($"  visible before: first='{FirstLine(visibleBefore)}' rows={visibleBefore.Split("\r\n").Length}");

                var sw = Stopwatch.StartNew();
                var doc = text.DocumentRange;
                var docText = doc.GetText(-1);
                Log($"  DocumentRange: {docText.Length} chars, rows={docText.Split("\r\n").Length}, contains 'row 30 NEEDLE'={docText.Contains("row 30 NEEDLE", StringComparison.Ordinal)} in {sw.Elapsed.TotalMilliseconds:F1} ms");

                sw.Restart();
                var search = doc.Clone();
                var found = new List<TextPatternRange>();
                for (var i = 0; i < 50; i++)
                {
                    var candidate = search.FindText("needle", false, true);
                    if (candidate is null)
                    {
                        break;
                    }

                    found.Add(candidate);
                    search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, candidate, TextPatternRangeEndpoint.End);
                }

                Log($"  FindText(ignoreCase) over DocumentRange: {found.Count} matches in {sw.Elapsed.TotalMilliseconds:F1} ms");
                foreach (var (range, index) in found.Select((r, i) => (r, i)))
                {
                    var rects = range.GetBoundingRectangles();
                    var row = range.Clone();
                    row.ExpandToEnclosingUnit(TextUnit.Line);
                    Log($"    match {index}: text='{range.GetText(-1).Replace("\r\n", "\\r\\n")}' rects={rects.Length}{(rects.Length > 0 ? $" first=[{rects[0].X},{rects[0].Y} {rects[0].Width}x{rects[0].Height}]" : string.Empty)} row='{row.GetText(-1).TrimEnd()}'");
                }

                // Backward search from the end - the natural "previous match" primitive.
                sw.Restart();
                var backward = doc.FindText("needle", true, true);
                Log($"  FindText(backward) from DocumentRange: {(backward is null ? "null" : $"'{backward.GetText(-1).TrimEnd()}'")} in {sw.Elapsed.TotalMilliseconds:F1} ms");

                if (found.Count >= 2)
                {
                    // Select the first (scrollback) match: does the viewport follow?
                    sw.Restart();
                    found[0].Select();
                    var elapsed = sw.Elapsed.TotalMilliseconds;
                    System.Threading.Thread.Sleep(300);
                    var visibleAfter = text.GetVisibleRanges()[0].GetText(-1);
                    var selection = text.GetSelection();
                    var rectsAfter = found[0].GetBoundingRectangles();
                    Log($"  Select(match 0) in {elapsed:F1} ms: visible first='{FirstLine(visibleAfter)}' contains row30={visibleAfter.Contains("row 30 NEEDLE", StringComparison.Ordinal)} selection={selection.Length}{(selection.Length > 0 ? $" '{selection[0].GetText(-1).TrimEnd()}'" : string.Empty)} rects now={rectsAfter.Length}");

                    // ScrollIntoView on the second match, aligned to top.
                    sw.Restart();
                    found[1].ScrollIntoView(true);
                    elapsed = sw.Elapsed.TotalMilliseconds;
                    System.Threading.Thread.Sleep(300);
                    var visibleScrolled = text.GetVisibleRanges()[0].GetText(-1);
                    Log($"  ScrollIntoView(match 1, top) in {elapsed:F1} ms: visible first='{FirstLine(visibleScrolled)}' contains row60={visibleScrolled.Contains("row 60 NEEDLE", StringComparison.Ordinal)} rects now={found[1].GetBoundingRectangles().Length}");

                    // Back to the bottom: scroll the last document line into view.
                    var last = doc.Clone();
                    last.MoveEndpointByRange(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.End);
                    var lastVisible = text.GetVisibleRanges()[0];
                    last.ScrollIntoView(false);
                    System.Threading.Thread.Sleep(300);
                    var visibleBottom = text.GetVisibleRanges()[0].GetText(-1);
                    Log($"  ScrollIntoView(end): visible first='{FirstLine(visibleBottom)}' back to start view={FirstLine(visibleBottom) == FirstLine(visibleBefore)}");

                    // Degenerate select clears the selection?
                    var degenerate = found[0].Clone();
                    degenerate.MoveEndpointByRange(TextPatternRangeEndpoint.End, degenerate, TextPatternRangeEndpoint.Start);
                    degenerate.Select();
                    System.Threading.Thread.Sleep(200);
                    Log($"  degenerate Select(): selection={text.GetSelection().Length}");

                    // Leave a VISIBLE match selected without any scroll, so a screenshot can show
                    // whether the renderer paints a selection made through UIA.
                    var visible = found.LastOrDefault(r => r.GetBoundingRectangles().Length > 0);
                    if (visible is not null)
                    {
                        var trimmed = visible.Clone();
                        trimmed.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
                        trimmed.Select();
                        System.Threading.Thread.Sleep(200);
                        var sel = text.GetSelection();
                        Log($"  Select(visible match, trimmed): selection='{(sel.Length > 0 ? sel[0].GetText(-1).TrimEnd() : string.Empty)}' rects={trimmed.GetBoundingRectangles().Length} first={(trimmed.GetBoundingRectangles().Length > 0 ? trimmed.GetBoundingRectangles()[0].ToString() : "-")}");
                    }
                }
            }
            catch (Exception e)
            {
                Log($"  spike7 error: {e.GetType().Name}: {e.Message}");
            }
        });

        first.RequestScreen();
        await Task.Delay(500);
        Log($"  after: screen rows={first.ScreenRows.Count} last='{first.ScreenRows.LastOrDefault(r => r.Trim().Length > 0)}'");
        Log("=== spike 7 (find) end ===");
    }

    private static string? ReadVisibleText(nint hwnd)    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern text)
            {
                return null;
            }

            var ranges = text.GetVisibleRanges();
            return ranges.Length > 0 ? ranges[0].GetText(-1) : string.Empty;
        }
        catch (Exception e)
        {
            Log($"  ReadVisibleText error: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static string? ReadDocumentText(nint hwnd)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            return element.GetCurrentPattern(TextPattern.Pattern) is TextPattern text
                ? text.DocumentRange.GetText(-1)
                : null;
        }
        catch (Exception e)
        {
            Log($"  ReadDocumentText error: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static string ReadBounds(nint hwnd)
    {
        try
        {
            var r = AutomationElement.FromHandle(hwnd).Current.BoundingRectangle;
            return r.IsEmpty ? "empty" : $"[{r.X},{r.Y} {r.Width}x{r.Height}]";
        }
        catch (Exception e)
        {
            return $"error {e.GetType().Name}";
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var nl = text.IndexOf("\r\n", StringComparison.Ordinal);
        var line = nl >= 0 ? text[..nl] : text;
        return line.TrimEnd();
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }
}
