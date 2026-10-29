namespace OverShell.Core.Tabs;

public sealed record FloatingPresentation(string Id, string TabId, string? OwnerTabId, bool Focused);
public sealed record PresentationChange(string TabId, string Mode);

/// <summary>UI-thread presentation operations. Hiding releases a presentation, never a terminal session.</summary>
public interface ITabPresentations
{
    IReadOnlyList<FloatingPresentation> Floating { get; }
    TabActionResult ShowFloating(string tabId, string? ownerTabId = null, NavigationPoint? returnTo = null, bool activate = true);
    TabActionResult ToggleFloating(string tabId);
    TabActionResult HideFloating(string tabId, bool returnFocus = true);
    TabActionResult ShowNormally(string tabId);
    event Action<PresentationChange>? Changed;
}
