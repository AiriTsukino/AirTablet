namespace AirTablet.Services;

internal enum ControlCenterWidgetKind
{
    Stat,
    Toggle,
    MacroPad,
}

internal enum ControlCenterWidgetSize
{
    Compact,
    Wide,
}

internal sealed record ControlCenterWidgetSnapshot(
    string Value,
    string Detail = "",
    bool? IsActive = null,
    bool IsAvailable = true);

internal sealed record ControlCenterMacroButton(
    string Id,
    string Title,
    string ImagePath = "");

internal sealed record ControlCenterMacroPadSnapshot(
    IReadOnlyList<ControlCenterMacroButton?> Slots,
    IReadOnlyList<ControlCenterMacroButton> Available);

internal sealed record ControlCenterWidget(
    string Id,
    string AppId,
    string Title,
    string Description,
    ControlCenterWidgetKind Kind,
    ControlCenterWidgetSize Size,
    Func<ControlCenterWidgetSnapshot> Read,
    Action<bool>? SetToggle = null,
    Func<ControlCenterMacroPadSnapshot>? ReadMacroPad = null,
    Action<string>? ActivateMacro = null,
    Action<int, string?>? AssignMacro = null,
    string RepeatableGroup = "",
    Action? Removed = null);
