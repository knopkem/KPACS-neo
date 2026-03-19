using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private static readonly string[] s_vascularValidationFailureModes =
    [
        "Low contrast or delayed enhancement can pull the centerline toward the wall.",
        "Calcified or thrombus-adjacent segments can bias neck diameter estimation.",
        "Metal artifacts can distort orthogonal lumen appearance and CPR continuity.",
        "Incomplete renal-to-iliac coverage invalidates landing-zone planning.",
        "Tortuous or branching anatomy may require manual guide seeds and marker review.",
    ];

    private VascularValidationSnapshot _vascularValidationSnapshot = VascularValidationSnapshot.CreateDefault();

    private void RecordVascularPerformanceMetric(string key, double elapsedMs)
    {
        if (_isApplyingMeasurementSession || elapsedMs < 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
        {
            return;
        }

        _vascularValidationSnapshot = _vascularValidationSnapshot.RecordPerformance(key, elapsedMs);
        ScheduleMeasurementSessionSave();
        if (_reportPanelVisible || _reportPanelPinned)
        {
            RefreshReportPanel(forceVisible: _reportPanelPinned);
        }
    }

    private void CycleVascularValidationChecklistItem(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _vascularValidationSnapshot = _vascularValidationSnapshot.CycleChecklistState(key);
        ScheduleMeasurementSessionSave();
        RefreshReportPanel(forceVisible: _reportPanelPinned || _reportPanelVisible);
    }

    private string BuildVascularValidationSummary()
    {
        VascularValidationSnapshot snapshot = _vascularValidationSnapshot.EnsureDefaults();
        int passCount = snapshot.ChecklistItems.Count(item => item.State == ValidationCheckState.Pass);
        int failCount = snapshot.ChecklistItems.Count(item => item.State == ValidationCheckState.Fail);
        int pendingCount = snapshot.ChecklistItems.Count(item => item.State == ValidationCheckState.Pending);
        int overBudgetCount = snapshot.PerformanceBudgets.Sum(budget => budget.OverBudgetCount > 0 ? 1 : 0);
        return $"{passCount} pass · {failCount} fail · {pendingCount} pending · {overBudgetCount} guardrail warnings";
    }

    private string BuildVascularValidationDetails()
    {
        VascularValidationSnapshot snapshot = _vascularValidationSnapshot.EnsureDefaults();
        List<string> parts = [];
        foreach (VascularPerformanceBudget budget in snapshot.PerformanceBudgets)
        {
            string latencyText = budget.LastObservedLatencyMs is double last
                ? $"{last:0.0} ms"
                : "n/a";
            string status = budget.LastObservedLatencyMs is double observed && observed > budget.TargetLatencyMs
                ? "over"
                : "ok";
            parts.Add($"{budget.Title}: {latencyText} (target ≤ {budget.TargetLatencyMs:0} ms, {status})");
        }

        return string.Join(" · ", parts);
    }

    private string BuildVascularValidationHint()
    {
        return string.Join(" ", s_vascularValidationFailureModes);
    }

    private Control CreateValidationReportEntryCard(ReportEntry entry)
    {
        VascularValidationSnapshot snapshot = entry.SourceWindow._vascularValidationSnapshot.EnsureDefaults();
        var checklistPanel = new StackPanel { Spacing = 6 };
        foreach (VascularValidationChecklistItem item in snapshot.ChecklistItems)
        {
            Button stateButton = new()
            {
                Content = item.State.ToString(),
                MinWidth = 64,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            stateButton.Click += (_, _) => entry.SourceWindow.CycleVascularValidationChecklistItem(item.Key);

            checklistPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#141A2230")),
                BorderBrush = new SolidColorBrush(Color.Parse(GetValidationStateColor(item.State))),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(8, 6),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = item.Title,
                                    Foreground = new SolidColorBrush(Color.Parse("#FFF6FBFF")),
                                    FontSize = 10,
                                    FontWeight = FontWeight.SemiBold,
                                    TextWrapping = TextWrapping.Wrap,
                                },
                                new TextBlock
                                {
                                    Text = item.Description,
                                    Foreground = new SolidColorBrush(Color.Parse("#FFAEC2D3")),
                                    FontSize = 9,
                                    TextWrapping = TextWrapping.Wrap,
                                }
                            }
                        },
                        stateButton,
                    }
                }
            });
            Grid.SetColumn(stateButton, 1);
        }

        var budgetPanel = new StackPanel { Spacing = 4 };
        foreach (VascularPerformanceBudget budget in snapshot.PerformanceBudgets)
        {
            string observedText = budget.LastObservedLatencyMs is double last ? $"last {last:0.0} ms" : "last n/a";
            string worstText = budget.WorstObservedLatencyMs is double worst ? $" · worst {worst:0.0} ms" : string.Empty;
            string overText = budget.OverBudgetCount > 0 ? $" · over {budget.OverBudgetCount}x" : string.Empty;
            budgetPanel.Children.Add(new TextBlock
            {
                Text = $"{budget.Title}: target ≤ {budget.TargetLatencyMs:0} ms · {observedText}{worstText}{overText}",
                Foreground = new SolidColorBrush(Color.Parse(budget.OverBudgetCount > 0 ? "#FFF1D57A" : "#FFD3E5F5")),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(entry.IsSelected ? "#243D5368" : "#181F2630")),
            BorderBrush = new SolidColorBrush(Color.Parse(entry.AccentHex)),
            BorderThickness = new Avalonia.Thickness(entry.IsSelected ? 1.4 : 1.0),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = entry.Title,
                        Foreground = new SolidColorBrush(Color.Parse("#FFF6FBFF")),
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CreateBadgeRow(entry),
                    new TextBlock
                    {
                        Text = entry.Details,
                        Foreground = new SolidColorBrush(Color.Parse("#FFD3E5F5")),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    checklistPanel,
                    budgetPanel,
                    new TextBox
                    {
                        Text = entry.Hint,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        Foreground = new SolidColorBrush(Color.Parse("#FF94ABC1")),
                        Background = Brushes.Transparent,
                        BorderThickness = new Avalonia.Thickness(0),
                        Padding = new Avalonia.Thickness(0),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                    }
                }
            }
        };
    }

    private static string GetValidationStateColor(ValidationCheckState state) => state switch
    {
        ValidationCheckState.Pass => "#FF7FDFA2",
        ValidationCheckState.Fail => "#FFFF8A8A",
        _ => "#FF73C7FF",
    };

    private Stopwatch StartVascularStopwatch() => Stopwatch.StartNew();
}
