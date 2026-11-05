using System.Globalization;
using System.Text.Json;
using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private void InitializeFontZoom()
    {
        _commands.Register("terminal.zoomIn", "Increase terminal font size", "Terminal", () => AdjustFontSize(1), () => TargetTab?.FontSize is not null);
        _commands.Register("terminal.zoomOut", "Decrease terminal font size", "Terminal", () => AdjustFontSize(-1), () => TargetTab?.FontSize is not null);
        _commands.Register("terminal.zoomReset", "Reset terminal font size", "Terminal", () =>
        {
            if (TargetTab is { } tab) ShowFontSizeResult(tab, tab.SetFontSize(null));
        }, () => TargetTab?.FontSize is not null, "Restore this tab's profile font size");
        _commands.Register("terminal.setFontSize", "Set terminal font size", "Terminal", invocation =>
        {
            if (ResolveTarget(invocation) is not { } tab) return true;
            var value = invocation.Arguments["size"];
            var size = 0d;
            if (!invocation.Arguments.ContainsKey("size") || value is not null &&
                (value.GetValueKind() != JsonValueKind.Number || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out size)))
                ShowStatusMessage("Provide size in points, or null to reset");
            else ShowFontSizeResult(tab, tab.SetFontSize(value is null ? null : size));
            return true;
        }, description: "Parameterized command: size in points, null resets; target may be a tab ID or alias");
    }

    private void AdjustFontSize(int direction)
    {
        if (TargetTab is not { FontSize: { } size } tab) return;
        ShowFontSizeResult(tab, tab.SetFontSize(TerminalFontSize.Adjust(size, direction * _settings.Terminal.EffectiveFontSizeStep)));
    }

    private void ShowFontSizeResult(TerminalTab tab, TabActionResult result) =>
        ShowStatusMessage(result.Success ? $"{tab.Label}: {tab.FontSize:0.#} pt" : result.Error!);
}
