using Avalonia.Media;

namespace KPACS.Viewer.Models;

public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed class ToastNotificationItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Icon { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IBrush Background { get; init; } = Brushes.DimGray;
    public IBrush BorderBrush { get; init; } = Brushes.Gray;
    public IBrush Foreground { get; init; } = Brushes.White;
}