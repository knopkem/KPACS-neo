using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private static readonly string[] s_neuroAnatomyOptions =
    [
        "Auto",
        "Cerebrum",
        "Frontal lobe",
        "Parietal lobe",
        "Temporal lobe",
        "Occipital lobe",
        "Cerebellum",
        "Brainstem",
        "Pituitary region",
        "Ventricular system",
        "Skull base",
        "Meninges",
        "Unassigned",
    ];

    private static readonly string[] s_thoraxAnatomyOptions =
    [
        "Auto",
        "Right lung upper region",
        "Right lung mid region",
        "Right lung lower region",
        "Left lung upper region",
        "Left lung mid region",
        "Left lung lower region",
        "Right hilum",
        "Left hilum",
        "Mediastinum",
        "Pleura",
        "Chest wall",
        "Heart",
        "Pericardium",
        "Aorta",
        "Trachea",
        "Esophagus",
        "Diaphragm",
        "Thorax",
        "Unassigned",
    ];

    private static readonly string[] s_abdomenAnatomyOptions =
    [
        "Auto",
        "Liver",
        "Gallbladder",
        "Spleen",
        "Pancreas",
        "Stomach",
        "Duodenum",
        "Small bowel",
        "Colon",
        "Appendix",
        "Mesenterium",
        "Retroperitoneum",
        "Right kidney",
        "Left kidney",
        "Right adrenal gland",
        "Left adrenal gland",
        "Abdominal wall",
        "Abdomen",
        "Unassigned",
    ];

    private static readonly string[] s_pelvisAnatomyOptions =
    [
        "Auto",
        "Urinary bladder",
        "Prostate",
        "Uterus",
        "Right ovary / adnexa",
        "Left ovary / adnexa",
        "Rectum",
        "Sigmoid colon",
        "Pelvic sidewall",
        "Sacrum",
        "Pelvis",
        "Unassigned",
    ];

    private static readonly string[] s_shoulderAnatomyOptions =
    [
        "Auto",
        "Shoulder joint",
        "Rotator cuff",
        "Supraspinatus tendon",
        "Infraspinatus tendon",
        "Subscapularis tendon",
        "Teres minor",
        "Biceps tendon",
        "Deltoid muscle",
        "Acromion",
        "Clavicle",
        "Scapula",
        "Glenoid",
        "Humeral head",
        "Proximal humerus",
        "Shoulder musculature",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_kneeAnatomyOptions =
    [
        "Auto",
        "Knee joint",
        "Distal femur",
        "Patella",
        "Proximal tibia",
        "Proximal fibula",
        "Medial meniscus",
        "Lateral meniscus",
        "Anterior cruciate ligament",
        "Posterior cruciate ligament",
        "Medial collateral ligament",
        "Lateral collateral ligament",
        "Quadriceps tendon",
        "Patellar tendon",
        "Popliteal fossa",
        "Knee musculature",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_ankleAnatomyOptions =
    [
        "Auto",
        "Ankle joint",
        "Distal tibia",
        "Distal fibula",
        "Talus",
        "Calcaneus",
        "Navicular",
        "Cuboid",
        "Cuneiforms",
        "Tibiotalar joint",
        "Subtalar joint",
        "Achilles tendon",
        "Peroneal tendons",
        "Posterior tibial tendon",
        "Deltoid ligament",
        "Lateral ankle ligaments",
        "Plantar fascia",
        "Ankle musculature",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_musculoskeletalGenericAnatomyOptions =
    [
        "Auto",
        "Bone",
        "Joint",
        "Muscle",
        "Tendon",
        "Ligament",
        "Cartilage",
        "Marrow",
        "Soft tissues",
        "Unassigned",
    ];

    private static readonly string[] s_reportReviewStateOptions =
    [
        "Auto",
        "Confirmed",
        "Needs review",
    ];

    private static readonly string[] s_reportRegionOptions =
    [
        "Neuro",
        "Upper thorax",
        "Lower thorax",
        "Upper abdomen",
        "Lower abdomen",
        "Pelvis",
        "Shoulder",
        "Knee",
        "Ankle",
    ];

    private Point _reportPanelOffset;
    private bool _reportPanelPinned;
    private bool _reportPanelVisible;
    private readonly Dictionary<Guid, string> _reportRegionOverrides = [];
    private readonly Dictionary<Guid, string> _reportAnatomyOverrides = [];
    private readonly Dictionary<Guid, string> _reportReviewStates = [];
    private readonly List<VolumeRoiAnatomyPriorRecord> _volumeRoiAnatomyPriors = [];
    private bool _volumeRoiAnatomyPriorsLoaded;
    private IPointer? _reportPanelDragPointer;
    private Point _reportPanelDragStart;
    private Point _reportPanelDragStartOffset;

    private static readonly IBrush s_reportComboForeground = new SolidColorBrush(Color.Parse("#FFF2F6FA"));
    private static readonly IBrush s_reportComboBackground = new SolidColorBrush(Color.Parse("#FF20262D"));
    private static readonly IBrush s_reportComboBorder = new SolidColorBrush(Color.Parse("#FF5D768E"));

    private void RefreshReportPanel(bool forceVisible = false)
    {
        if (forceVisible)
        {
            _reportPanelVisible = true;
        }

        if (!_reportPanelVisible || ReportPanel is null)
        {
            HideReportPanel();
            return;
        }

        List<ReportEntry> entries = BuildReportEntries();
        ReportPanelPinButton.IsChecked = _reportPanelPinned;
        ReportPanelSummaryText.Text = BuildReportSummary(entries);
        ReportPanelHintText.Text = entries.Count == 0
            ? "Create measurements, annotations, 3D ROIs, or RECIST suggestions to populate the report. Anatomy starts as a heuristic guess and can be reviewed here."
            : "Click the region label to adjust the detected scan region, then use the anatomy dropdown to confirm or correct the finding assignment."
                + (_reportPanelPinned ? " Panel is pinned." : string.Empty);

        PopulateReportItems(entries);
        ReportPanel.IsVisible = true;
        ApplyReportPanelOffset();
    }

    private void HideReportPanel()
    {
        if (ReportPanel is null)
        {
            return;
        }

        ReportPanel.IsVisible = false;
        ReportItemsPanel?.Children.Clear();
        if (ReportPanelSummaryText is not null)
        {
            ReportPanelSummaryText.Text = string.Empty;
        }

        if (ReportPanelHintText is not null)
        {
            ReportPanelHintText.Text = string.Empty;
        }
    }

    private List<ReportEntry> BuildReportEntries()
    {
        var entries = new List<ReportEntry>(_studyMeasurements.Count * 2);

        foreach (StudyMeasurement measurement in _studyMeasurements)
        {
            ViewportSlot? slot = FindSlotForMeasurement(measurement);
            entries.Add(BuildMeasurementReportEntry(measurement, slot));

            if (TryBuildRecistReportEntry(measurement, slot, out ReportEntry recistEntry))
            {
                entries.Add(recistEntry);
            }

            if (TryBuildRadiomicsReportEntry(measurement, slot, out ReportEntry radiomicsEntry))
            {
                entries.Add(radiomicsEntry);
            }
        }

        return entries
            .OrderByDescending(entry => entry.IsSelected)
            .ThenBy(entry => entry.SortOrder)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ReportEntry BuildMeasurementReportEntry(StudyMeasurement measurement, ViewportSlot? slot)
    {
        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        AnatomyGuess anatomy = ResolveMeasurementAnatomy(measurement, slot, region);
        string typeLabel = GetMeasurementTypeLabel(measurement);
        string title = typeLabel;
        if (measurement.Kind == MeasurementKind.Annotation && !string.IsNullOrWhiteSpace(measurement.AnnotationText))
        {
            title = measurement.AnnotationText.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(measurement.Tracking?.Label))
        {
            title = $"{typeLabel} · {measurement.Tracking.Label.Trim()}";
        }

        string details = BuildMeasurementDetails(measurement);
        return new ReportEntry(
            measurement.Id,
            typeLabel,
            title,
            BuildMeasurementOriginLabel(measurement, slot),
            region.Label,
            region.AutoLabel,
            region.IsManual,
            anatomy.DisplayLabel,
            details,
            CombineReportHints(region.Hint, anatomy.Hint),
            measurement.Id == _selectedMeasurementId,
            GetAccentColor(typeLabel),
            IsRegionEditable: true,
            anatomy.IsManual,
            IsAnatomyEditable: true,
            AnatomyOptions: BuildAnatomySelectionOptions(measurement, slot),
            ReviewState: GetReviewStateSelectionValue(measurement.Id),
            SortOrder: 0);
    }

    private bool TryBuildRecistReportEntry(StudyMeasurement measurement, ViewportSlot? slot, out ReportEntry entry)
    {
        entry = default!;
        MeasurementTrackingMetadata? tracking = measurement.Tracking;
        if (tracking is null)
        {
            return false;
        }

        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        AnatomyGuess anatomy = ResolveMeasurementAnatomy(measurement, slot, region);
        string confidence = tracking.Confidence > 0
            ? $"{tracking.Confidence:P0}"
            : "n/a";
        string review = tracking.ReviewState == MeasurementReviewState.None
            ? string.Empty
            : $" · {tracking.ReviewState}";
        string timepoint = string.IsNullOrWhiteSpace(tracking.TimepointLabel)
            ? string.Empty
            : $" · {tracking.TimepointLabel.Trim()}";
        string detailText = string.IsNullOrWhiteSpace(tracking.ConfidenceSummary)
            ? $"Confidence {confidence}{review}{timepoint}"
            : $"{tracking.ConfidenceSummary.Trim()} · {confidence}{review}{timepoint}";

        entry = new ReportEntry(
            measurement.Id,
            "RECIST",
            string.IsNullOrWhiteSpace(tracking.Label) ? "RECIST follow-up" : $"RECIST · {tracking.Label.Trim()}",
            BuildTrackingOriginLabel(measurement, slot),
            region.Label,
            region.AutoLabel,
            region.IsManual,
            anatomy.DisplayLabel,
            detailText,
            CombineReportHints(region.Hint, anatomy.Hint),
            measurement.Id == _selectedMeasurementId,
            "#FF89CFF0",
            IsRegionEditable: false,
            anatomy.IsManual,
            IsAnatomyEditable: false,
            AnatomyOptions: [],
            ReviewState: GetReviewStateSelectionValue(measurement.Id),
            SortOrder: 1);
        return true;
    }

    private bool TryBuildRadiomicsReportEntry(StudyMeasurement measurement, ViewportSlot? slot, out ReportEntry entry)
    {
        entry = default!;
        if (!IsInsightRelevantMeasurement(measurement))
        {
            return false;
        }

        if (!TryResolveMeasurementInsightContext(measurement, out ViewportSlot? resolvedSlot, out DicomViewPanel.RoiDistributionDetails distribution))
        {
            return false;
        }

        slot ??= resolvedSlot;
        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        AnatomyGuess anatomy = ResolveMeasurementAnatomy(measurement, slot, region);
        string supplement = GetMeasurementTextSupplement(measurement, []) ?? string.Empty;
        string supplementLine = supplement
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        string detailText = $"{distribution.QuantityLabel} μ {distribution.Mean:F1} · med {distribution.Median:F1} · σ {distribution.StandardDeviation:F1}";
        if (!string.IsNullOrWhiteSpace(supplementLine))
        {
            detailText = $"{detailText} · {supplementLine}";
        }

        entry = new ReportEntry(
            measurement.Id,
            "Radiomics",
            $"Radiomics · {GetMeasurementTypeLabel(measurement)}",
            BuildMeasurementOriginLabel(measurement, slot),
            region.Label,
            region.AutoLabel,
            region.IsManual,
            anatomy.DisplayLabel,
            detailText,
            CombineReportHints(region.Hint, anatomy.Hint),
            measurement.Id == _selectedMeasurementId,
            "#FFC9A4FF",
            IsRegionEditable: false,
            anatomy.IsManual,
            IsAnatomyEditable: false,
            AnatomyOptions: [],
            ReviewState: GetReviewStateSelectionValue(measurement.Id),
            SortOrder: 2);
        return true;
    }

    private void PopulateReportItems(IReadOnlyList<ReportEntry> entries)
    {
        ReportItemsPanel.Children.Clear();
        if (entries.Count == 0)
        {
            ReportItemsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#181F2630")),
                BorderBrush = new SolidColorBrush(Color.Parse("#334D5F74")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8),
                Child = new TextBlock
                {
                    Text = "No reportable objects yet.",
                    Foreground = new SolidColorBrush(Color.Parse("#FFD3E5F5")),
                    FontSize = 11,
                }
            });
            return;
        }

        foreach (ReportEntry entry in entries)
        {
            ReportItemsPanel.Children.Add(CreateReportEntryCard(entry));
        }
    }

    private Control CreateReportEntryCard(ReportEntry entry)
    {
        Color accent = Color.Parse(entry.AccentHex);
        Control jumpButton = CreateJumpButton(entry);
        var content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 3,
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
                            }
                        },
                        jumpButton,
                    }
                },
                new TextBlock
                {
                    Text = entry.Origin,
                    Foreground = new SolidColorBrush(Color.Parse("#FFAEC2D3")),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                },
                CreateRegionControl(entry),
                new TextBlock
                {
                    Text = $"Anatomy · {entry.Anatomy}",
                    Foreground = new SolidColorBrush(accent),
                    FontSize = 10,
                    FontWeight = FontWeight.Medium,
                    TextWrapping = TextWrapping.Wrap,
                },
                CreateAnatomyEditor(entry),
                CreateReviewStateEditor(entry),
                new TextBlock
                {
                    Text = entry.Details,
                    Foreground = new SolidColorBrush(Color.Parse("#FFD3E5F5")),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = !string.IsNullOrWhiteSpace(entry.Details),
                },
                new TextBlock
                {
                    Text = entry.Hint,
                    Foreground = new SolidColorBrush(Color.Parse("#FF94ABC1")),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = !string.IsNullOrWhiteSpace(entry.Hint),
                }
            }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(entry.IsSelected ? "#243D5368" : "#181F2630")),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(entry.IsSelected ? 1.4 : 1.0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = content,
        };
    }

    private Control CreateBadgeRow(ReportEntry entry)
    {
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6,
            LineSpacing = 4,
        };

        row.Children.Add(CreateBadge(entry.Category, entry.AccentHex, bold: true));
        if (entry.IsSelected)
        {
            row.Children.Add(CreateBadge("Selected", "#FFF1D57A", bold: false));
        }

        if (entry.IsAnatomyManual)
        {
            row.Children.Add(CreateBadge("Manual anatomy", "#FFB8F28F", bold: false));
        }

        if (entry.IsRegionManual)
        {
            row.Children.Add(CreateBadge("Manual region", "#FF88D7FF", bold: false));
        }

        if (!string.Equals(entry.ReviewState, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            row.Children.Add(CreateBadge(entry.ReviewState, GetReviewStateColor(entry.ReviewState), bold: false));
        }

        return row;
    }

    private Control CreateRegionControl(ReportEntry entry)
    {
        if (!entry.IsRegionEditable || entry.MeasurementId is not Guid measurementId)
        {
            return new TextBlock
            {
                Text = $"Region · {entry.Region}",
                Foreground = new SolidColorBrush(Color.Parse("#FF8FC6E8")),
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var button = new Button
        {
            Content = $"Region · {entry.Region}",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 180,
            MaxWidth = 260,
            Padding = new Thickness(8, 4),
            Background = new SolidColorBrush(Color.Parse("#16283848")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF4E7A9A")),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse("#FFD4ECFF")),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        button.ContextMenu = CreateRegionContextMenu(measurementId, entry.AutoRegion);
        button.Click += (_, _) => button.ContextMenu?.Open(button);

        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Region catalog",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB8CADA")),
                    FontSize = 9,
                    FontWeight = FontWeight.Medium,
                },
                button,
            }
        };
    }

    private ContextMenu CreateRegionContextMenu(Guid measurementId, string autoRegion)
    {
        var items = new List<Control>
        {
            CreateRegionMenuItem($"Auto ({autoRegion})", measurementId, "Auto"),
        };

        foreach (string option in s_reportRegionOptions)
        {
            items.Add(CreateRegionMenuItem(option, measurementId, option));
        }

        return new ContextMenu
        {
            ItemsSource = items,
        };
    }

    private MenuItem CreateRegionMenuItem(string header, Guid measurementId, string value)
    {
        var item = new MenuItem { Header = header, Tag = (measurementId, value) };
        item.Click += OnRegionMenuItemClick;
        return item;
    }

    private void OnRegionMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ValueTuple<Guid, string> payload })
        {
            ApplyManualRegionSelection(payload.Item1, payload.Item2);
        }
    }

    private Control CreateAnatomyEditor(ReportEntry entry)
    {
        if (!entry.IsAnatomyEditable || entry.MeasurementId is not Guid measurementId)
        {
            return new TextBlock { IsVisible = false };
        }

        var comboBox = new ComboBox
        {
            ItemsSource = entry.AnatomyOptions,
            SelectedItem = GetAnatomySelectionValue(measurementId),
            MinWidth = 180,
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ApplyReportComboBoxStyling(comboBox);

        comboBox.SelectionChanged += (_, _) =>
        {
            string selectedValue = comboBox.SelectedItem as string ?? "Auto";
            ApplyManualAnatomySelection(measurementId, selectedValue);
        };

        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Anatomy review",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB8CADA")),
                    FontSize = 9,
                    FontWeight = FontWeight.Medium,
                },
                comboBox,
            }
        };
    }

    private Control CreateReviewStateEditor(ReportEntry entry)
    {
        if (!entry.IsAnatomyEditable || entry.MeasurementId is not Guid measurementId)
        {
            return new TextBlock { IsVisible = false };
        }

        var comboBox = new ComboBox
        {
            ItemsSource = s_reportReviewStateOptions,
            SelectedItem = GetReviewStateSelectionValue(measurementId),
            MinWidth = 130,
            MaxWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ApplyReportComboBoxStyling(comboBox);

        comboBox.SelectionChanged += (_, _) =>
        {
            string selectedValue = comboBox.SelectedItem as string ?? "Auto";
            ApplyReviewStateSelection(measurementId, selectedValue);
        };

        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Review state",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB8CADA")),
                    FontSize = 9,
                    FontWeight = FontWeight.Medium,
                },
                comboBox,
            }
        };
    }

    private static Control CreateBadge(string text, string colorHex, bool bold)
    {
        Color color = Color.Parse(colorHex);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 2),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 9,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Medium,
            }
        };
    }

    private Control CreateJumpButton(ReportEntry entry)
    {
        var button = new Button
        {
            Content = "Show",
            MinWidth = 56,
            Height = 26,
            IsVisible = entry.MeasurementId is not null,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        button.Click += (_, _) =>
        {
            if (entry.MeasurementId is Guid measurementId)
            {
                FocusReportMeasurement(measurementId);
            }
        };

        Grid.SetColumn(button, 1);
        return button;
    }

    private void FocusReportMeasurement(Guid measurementId)
    {
        StudyMeasurement? measurement = _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null)
        {
            return;
        }

        _selectedMeasurementId = measurementId;
        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        if (slot is not null)
        {
            SetActiveSlot(slot, requestPriority: false);
            if (slot.Volume is not null && measurement.TryGetPatientCenter(out _))
            {
                FocusSlotOnMeasurement(slot, measurement);
            }
            else
            {
                LoadSlot(slot, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));
            }
        }

        RefreshMeasurementPanels();
    }

    private string BuildReportSummary(IReadOnlyList<ReportEntry> entries)
    {
        int manualCount = entries.Count(entry => entry.SortOrder == 0);
        int recistCount = entries.Count(entry => entry.Category == "RECIST");
        int radiomicsCount = entries.Count(entry => entry.Category == "Radiomics");
        int annotationCount = _studyMeasurements.Count(measurement => measurement.Kind == MeasurementKind.Annotation);
        int roiCount = _studyMeasurements.Count(measurement => measurement.Kind is MeasurementKind.RectangleRoi or MeasurementKind.EllipseRoi or MeasurementKind.PolygonRoi or MeasurementKind.VolumeRoi);
        int reviewedCount = entries.Count(entry => entry.SortOrder == 0 && entry.IsAnatomyManual);
        int confirmedCount = entries.Count(entry => entry.SortOrder == 0 && string.Equals(entry.ReviewState, "Confirmed", StringComparison.OrdinalIgnoreCase));
        int needsReviewCount = entries.Count(entry => entry.SortOrder == 0 && string.Equals(entry.ReviewState, "Needs review", StringComparison.OrdinalIgnoreCase));
        return $"{manualCount} findings · {reviewedCount} reviewed anatomy · {confirmedCount} confirmed · {needsReviewCount} needs review · {annotationCount} annotations · {roiCount} ROIs · {recistCount} RECIST · {radiomicsCount} radiomics";
    }

    private string BuildMeasurementOriginLabel(StudyMeasurement measurement, ViewportSlot? slot)
    {
        string studyLabel = ResolveStudyLabel(slot?.Series);
        string seriesLabel = slot?.Series is null
            ? "Series unavailable"
            : BuildSeriesLabel(slot.Series);

        string trackingLabel = measurement.Tracking is null || string.IsNullOrWhiteSpace(measurement.Tracking.TimepointLabel)
            ? string.Empty
            : $" · {measurement.Tracking.TimepointLabel.Trim()}";
        return $"{studyLabel} · {seriesLabel}{trackingLabel}";
    }

    private string BuildTrackingOriginLabel(StudyMeasurement measurement, ViewportSlot? slot)
    {
        MeasurementTrackingMetadata tracking = measurement.Tracking!;
        string origin = BuildMeasurementOriginLabel(measurement, slot);
        if (tracking.SourceMeasurementId is Guid sourceMeasurementId)
        {
            origin = $"{origin} · source {sourceMeasurementId.ToString("N")[..8]}";
        }

        return origin;
    }

    private string BuildMeasurementDetails(StudyMeasurement measurement)
    {
        return measurement.Kind switch
        {
            MeasurementKind.Annotation => string.IsNullOrWhiteSpace(measurement.AnnotationText)
                ? "Free-text annotation"
                : $"Text: {measurement.AnnotationText.Trim()}",
            MeasurementKind.Line => $"{measurement.Anchors.Length} anchor points · linear measurement",
            MeasurementKind.Angle => $"{measurement.Anchors.Length} anchor points · angular measurement",
            MeasurementKind.VolumeRoi => measurement.VolumeContours is { Length: > 0 } contours
                ? $"{contours.Length} contour slices · 3D ROI"
                : "3D ROI",
            _ => measurement.Anchors.Length > 0
                ? $"{measurement.Anchors.Length} anchor points"
                : string.Empty,
        };
    }

    private static void ApplyReportComboBoxStyling(ComboBox comboBox)
    {
        comboBox.Background = s_reportComboBackground;
        comboBox.Foreground = s_reportComboForeground;
        comboBox.BorderBrush = s_reportComboBorder;
        comboBox.BorderThickness = new Thickness(1);
        comboBox.Padding = new Thickness(8, 4);
        comboBox.FontSize = 11;
        comboBox.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock
        {
            Text = item,
            Foreground = s_reportComboForeground,
            FontSize = 11,
        });
    }

    private RegionGuess ResolveMeasurementRegion(StudyMeasurement measurement, ViewportSlot? slot)
    {
        if (measurement.Kind == MeasurementKind.VolumeRoi &&
            TryFindLearnedVolumeRoiPriorMatch(measurement, slot, out VolumeRoiAnatomyPriorMatch? learnedMatch))
        {
            RegionGuess learnedRegion = new(
                learnedMatch.Prior.RegionLabel,
                learnedMatch.Prior.RegionLabel,
                learnedMatch.Hint);

            if (_reportRegionOverrides.TryGetValue(measurement.Id, out string? manualLabel) &&
                !string.IsNullOrWhiteSpace(manualLabel))
            {
                return new RegionGuess(manualLabel.Trim(), learnedRegion.AutoLabel, $"Manual region override. Auto suggestion: {learnedRegion.AutoLabel}.", true);
            }

            return learnedRegion;
        }

        RegionGuess estimated = EstimateMeasurementRegion(measurement, slot);
        if (_reportRegionOverrides.TryGetValue(measurement.Id, out string? manualOverride) &&
            !string.IsNullOrWhiteSpace(manualOverride))
        {
            return new RegionGuess(manualOverride.Trim(), estimated.AutoLabel, $"Manual region override. Auto suggestion: {estimated.AutoLabel}.", true);
        }

        return estimated;
    }

    private AnatomyGuess ResolveMeasurementAnatomy(StudyMeasurement measurement, ViewportSlot? slot, RegionGuess? resolvedRegion = null)
    {
        RegionGuess region = resolvedRegion ?? ResolveMeasurementRegion(measurement, slot);

        if (measurement.Kind == MeasurementKind.VolumeRoi &&
            !_reportAnatomyOverrides.ContainsKey(measurement.Id) &&
            TryFindLearnedVolumeRoiPriorMatch(measurement, slot, out VolumeRoiAnatomyPriorMatch learnedMatch))
        {
            return new AnatomyGuess(learnedMatch.Prior.AnatomyLabel, learnedMatch.Hint);
        }

        AnatomyGuess estimated = EstimateMeasurementAnatomy(measurement, slot, region);
        if (_reportAnatomyOverrides.TryGetValue(measurement.Id, out string? manualLabel) &&
            !string.IsNullOrWhiteSpace(manualLabel))
        {
            return new AnatomyGuess(manualLabel.Trim(), $"Manually confirmed in report panel. Auto suggestion: {estimated.DisplayLabel}.", true);
        }

        return estimated;
    }

    private AnatomyGuess EstimateMeasurementAnatomy(StudyMeasurement measurement, ViewportSlot? slot, RegionGuess region)
    {
        AnatomyRegionProfile profile = MapRegionLabelToProfile(region.Label);
        bool headContext = profile == AnatomyRegionProfile.Neuro;
        bool chestContext = profile is AnatomyRegionProfile.Thorax or AnatomyRegionProfile.ThoraxAbdomen;
        bool abdomenContext = profile is AnatomyRegionProfile.Abdomen or AnatomyRegionProfile.AbdomenPelvis or AnatomyRegionProfile.ThoraxAbdomen;
        bool pelvisContext = profile == AnatomyRegionProfile.Pelvis;
        bool shoulderContext = profile == AnatomyRegionProfile.Shoulder;
        bool kneeContext = profile == AnatomyRegionProfile.Knee;
        bool ankleContext = profile == AnatomyRegionProfile.Ankle;
        string bodyPart = ResolveSoftBodyPartExamined(slot);

        if (!measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            if (shoulderContext)
            {
                return new AnatomyGuess("Shoulder joint", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven shoulder estimate."));
            }

            if (kneeContext)
            {
                return new AnatomyGuess("Knee joint", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven knee estimate."));
            }

            if (ankleContext)
            {
                return new AnatomyGuess("Ankle joint", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven ankle estimate."));
            }

            return headContext
                ? new AnatomyGuess("Cerebrum", "Descriptor-driven estimate.")
                : chestContext
                    ? new AnatomyGuess("Thorax", "Descriptor-driven estimate.")
                    : pelvisContext
                        ? new AnatomyGuess("Pelvis", "Descriptor-driven estimate.")
                    : abdomenContext
                        ? new AnatomyGuess("Abdomen", "Descriptor-driven estimate.")
                        : new AnatomyGuess("Unassigned", "No patient-space center available.");
        }

        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        if (bounds is null)
        {
            if (shoulderContext)
            {
                return new AnatomyGuess("Shoulder joint", BuildSoftBodyPartHint(bodyPart, "Study descriptors suggest shoulder anatomy."));
            }

            if (kneeContext)
            {
                return new AnatomyGuess("Knee joint", BuildSoftBodyPartHint(bodyPart, "Study descriptors suggest knee anatomy."));
            }

            if (ankleContext)
            {
                return new AnatomyGuess("Ankle joint", BuildSoftBodyPartHint(bodyPart, "Study descriptors suggest ankle anatomy."));
            }

            return headContext
                ? new AnatomyGuess("Cerebrum", "Study descriptors suggest neuro anatomy.")
                : chestContext
                    ? new AnatomyGuess("Thorax", "Study descriptors suggest thoracic anatomy.")
                    : pelvisContext
                        ? new AnatomyGuess("Pelvis", "Study descriptors suggest pelvic anatomy.")
                    : abdomenContext
                        ? new AnatomyGuess("Abdomen", "Study descriptors suggest abdominal anatomy.")
                        : new AnatomyGuess("Unassigned", "No aligned volume available for normalization.");
        }

        double normalizedX = Normalize(center.X, bounds.Value.MinX, bounds.Value.MaxX);
        double normalizedY = Normalize(center.Y, bounds.Value.MinY, bounds.Value.MaxY);
        double normalizedZ = Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ);
        string laterality = normalizedX >= 0.56 ? "left" : normalizedX <= 0.44 ? "right" : string.Empty;
        string vertical = normalizedZ >= 0.62 ? "upper" : normalizedZ <= 0.38 ? "lower" : "mid";
        bool central = normalizedX > 0.38 && normalizedX < 0.62;
        bool anterior = normalizedY < 0.44;
        bool posterior = normalizedY > 0.56;

        if (shoulderContext)
        {
            if (laterality == "right" || laterality == "left")
            {
                return new AnatomyGuess($"{Capitalize(laterality)} shoulder joint", BuildSoftBodyPartHint(bodyPart, "Shoulder-focused fallback using laterality."));
            }

            return new AnatomyGuess("Shoulder joint", BuildSoftBodyPartHint(bodyPart, "Shoulder-focused fallback."));
        }

        if (kneeContext)
        {
            if (anterior)
            {
                return new AnatomyGuess("Patellar tendon", BuildSoftBodyPartHint(bodyPart, "Anterior knee-focused fallback."));
            }

            if (posterior)
            {
                return new AnatomyGuess("Popliteal fossa", BuildSoftBodyPartHint(bodyPart, "Posterior knee-focused fallback."));
            }

            return new AnatomyGuess("Knee joint", BuildSoftBodyPartHint(bodyPart, "Knee-focused fallback."));
        }

        if (ankleContext)
        {
            if (posterior)
            {
                return new AnatomyGuess("Achilles tendon", BuildSoftBodyPartHint(bodyPart, "Posterior ankle-focused fallback."));
            }

            if (anterior)
            {
                return new AnatomyGuess("Ankle joint", BuildSoftBodyPartHint(bodyPart, "Anterior ankle-focused fallback."));
            }

            return new AnatomyGuess("Tibiotalar joint", BuildSoftBodyPartHint(bodyPart, "Ankle-focused fallback."));
        }

        if (headContext)
        {
            if (normalizedZ <= 0.34 && posterior)
            {
                return new AnatomyGuess("Cerebellum", "Inferior-posterior neuro position.");
            }

            return new AnatomyGuess("Cerebrum", "Supratentorial neuro position.");
        }

        if (chestContext)
        {
            if (!central)
            {
                string zone = vertical switch
                {
                    "upper" => "upper",
                    "lower" => "lower",
                    _ => "mid"
                };
                string side = string.IsNullOrWhiteSpace(laterality) ? "lung" : $"{laterality} lung";
                return new AnatomyGuess($"{side} {zone} region".Replace("  ", " ").Trim(), "Thoracic lateral compartment.");
            }

            return new AnatomyGuess("Mediastinum", "Thoracic central compartment.");
        }

        if (abdomenContext || string.Equals(slot?.Series?.Modality, "CT", StringComparison.OrdinalIgnoreCase) || string.Equals(slot?.Series?.Modality, "MR", StringComparison.OrdinalIgnoreCase))
        {
            if (laterality == "right" && vertical == "upper")
            {
                return new AnatomyGuess("Liver", "Right upper abdominal location.");
            }

            if (laterality == "left" && vertical == "upper" && anterior)
            {
                return new AnatomyGuess("Stomach", "Left upper anterior abdominal location.");
            }

            if (central && vertical != "upper")
            {
                return new AnatomyGuess("Mesenterium", "Central abdominal location.");
            }

            if (anterior || vertical == "lower")
            {
                return new AnatomyGuess("Intestines", "Anterior or lower abdominal location.");
            }

            return new AnatomyGuess("Abdomen", "Abdominal volume context.");
        }

        return new AnatomyGuess("Unassigned", $"No strong anatomical heuristic matched. Filter profile: {profile}.");
    }

    private RegionGuess EstimateMeasurementRegion(StudyMeasurement measurement, ViewportSlot? slot)
    {
        string context = BuildMeasurementAnatomyContext(slot);
        string bodyPart = ResolveSoftBodyPartExamined(slot);

        if (ContainsAny(context, "shoulder", "rotator", "glenoid", "clav", "acrom", "scap", "humeral head", "proximal humerus") ||
            ContainsAny(bodyPart, "shoulder", "acromioclavicular", "clavicle", "scapula"))
        {
            return new RegionGuess("Shoulder", "Shoulder", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven shoulder region."));
        }

        if (ContainsAny(context, "knee", "menisc", "patell", "cruciate", "collateral", "femorotib", "distal femur", "proximal tibia", "poplite") ||
            ContainsAny(bodyPart, "knee", "patella", "distal femur", "proximal tibia", "proximal fibula"))
        {
            return new RegionGuess("Knee", "Knee", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven knee region."));
        }

        if (ContainsAny(context, "ankle", "achilles", "tibiotalar", "subtalar", "malleol", "talus", "calcane", "navicular", "cuboid", "perone", "posterior tibial") ||
            ContainsAny(bodyPart, "ankle", "foot", "talus", "calcaneus", "heel"))
        {
            return new RegionGuess("Ankle", "Ankle", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven ankle region."));
        }

        if (ContainsAny(context, "brain", "head", "cran", "cereb", "neuro", "pituitary") || ContainsAny(bodyPart, "brain", "head", "skull"))
        {
            return new RegionGuess("Neuro", "Neuro", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven neuro region."));
        }

        if (ContainsAny(context, "pelvis", "pelvic", "bladder", "prostate", "uter", "ovar", "rect") || ContainsAny(bodyPart, "pelvis", "pelvic"))
        {
            return new RegionGuess("Pelvis", "Pelvis", BuildSoftBodyPartHint(bodyPart, "Descriptor-driven pelvic region."));
        }

        if (ContainsAny(context, "upper thorax", "upper chest", "apical lung"))
        {
            return new RegionGuess("Upper thorax", "Upper thorax", "Series descriptors suggest upper thorax.");
        }

        if (ContainsAny(context, "lower thorax", "basal lung", "lung base"))
        {
            return new RegionGuess("Lower thorax", "Lower thorax", "Series descriptors suggest lower thorax.");
        }

        if (ContainsAny(context, "upper abdomen", "liver", "hep", "stomach", "gastric", "pancrea", "spleen", "adrenal", "kidney", "renal"))
        {
            return new RegionGuess("Upper abdomen", "Upper abdomen", BuildSoftBodyPartHint(bodyPart, "Series descriptors suggest upper abdomen."));
        }

        if (ContainsAny(context, "lower abdomen", "appendix", "small bowel", "colon", "mesent", "retroperitone"))
        {
            return new RegionGuess("Lower abdomen", "Lower abdomen", BuildSoftBodyPartHint(bodyPart, "Series descriptors suggest lower abdomen."));
        }

        bool chestContext = ContainsAny(context, "chest", "thorax", "lung", "pulm", "mediast", "card", "heart", "pleura") || ContainsAny(bodyPart, "chest", "thorax", "lung");
        bool abdomenContext = ContainsAny(context, "abd", "bowel", "intestin", "mesent", "pancrea", "spleen", "renal", "kidney") || ContainsAny(bodyPart, "abdomen", "abdominal");

        if (chestContext && abdomenContext)
        {
            return EstimateBorderTorsoRegion(measurement, slot, bodyPart);
        }

        if (chestContext)
        {
            return EstimateThoraxRegion(measurement, slot, bodyPart);
        }

        if (abdomenContext)
        {
            return EstimateAbdomenRegion(measurement, slot, bodyPart);
        }

        AnatomyRegionProfile profile = ResolveAnatomyRegionProfile(measurement, slot, context);
        return profile switch
        {
            AnatomyRegionProfile.Neuro => new RegionGuess("Neuro", "Neuro", "Fallback region profile suggests neuro."),
            AnatomyRegionProfile.Thorax => EstimateThoraxRegion(measurement, slot, bodyPart),
            AnatomyRegionProfile.Abdomen => EstimateAbdomenRegion(measurement, slot, bodyPart),
            AnatomyRegionProfile.Pelvis => new RegionGuess("Pelvis", "Pelvis", "Fallback region profile suggests pelvis."),
            AnatomyRegionProfile.Shoulder => new RegionGuess("Shoulder", "Shoulder", "Fallback region profile suggests shoulder."),
            AnatomyRegionProfile.Knee => new RegionGuess("Knee", "Knee", "Fallback region profile suggests knee."),
            AnatomyRegionProfile.Ankle => new RegionGuess("Ankle", "Ankle", "Fallback region profile suggests ankle."),
            _ => new RegionGuess("Lower thorax", "Lower thorax", BuildSoftBodyPartHint(bodyPart, "Fallback region remained ambiguous.")),
        };
    }

    private IReadOnlyList<string> BuildAnatomySelectionOptions(StudyMeasurement measurement, ViewportSlot? slot)
    {
        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        List<string> options = region.Label switch
        {
            "Neuro" => [.. s_neuroAnatomyOptions],
            "Upper thorax" or "Lower thorax" => [.. s_thoraxAnatomyOptions],
            "Upper abdomen" or "Lower abdomen" => [.. s_abdomenAnatomyOptions],
            "Pelvis" => [.. s_pelvisAnatomyOptions],
            "Shoulder" => MergeAnatomyOptions(s_shoulderAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
            "Knee" => MergeAnatomyOptions(s_kneeAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
            "Ankle" => MergeAnatomyOptions(s_ankleAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
            _ => MergeAnatomyOptions(s_thoraxAnatomyOptions, s_abdomenAnatomyOptions, s_pelvisAnatomyOptions, s_musculoskeletalGenericAnatomyOptions),
        };

        if (_reportAnatomyOverrides.TryGetValue(measurement.Id, out string? manualLabel) &&
            !string.IsNullOrWhiteSpace(manualLabel) &&
            !options.Contains(manualLabel, StringComparer.OrdinalIgnoreCase))
        {
            options.Insert(1, manualLabel.Trim());
        }

        return options;
    }

    private string BuildMeasurementAnatomyContext(ViewportSlot? slot) =>
        string.Join(" ", new[]
        {
            slot?.Series?.Modality,
            slot?.Series?.BodyPart,
            FindLegacySeriesInfo(slot?.Series)?.BodyPart,
            slot?.Series?.SeriesDescription,
            _context.StudyDetails.Study.StudyDescription,
            _context.StudyDetails.Study.Modalities,
            _selectedPriorStudy?.StudyDescription,
            _selectedPriorStudy?.Modalities,
        }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private AnatomyRegionProfile ResolveAnatomyRegionProfile(StudyMeasurement measurement, ViewportSlot? slot, string context)
    {
        string bodyPart = ResolveSoftBodyPartExamined(slot);
        bool shoulderContext = ContainsAny(context, "shoulder", "rotator", "glenoid", "clav", "acrom", "scap", "humeral head", "proximal humerus");
        bool kneeContext = ContainsAny(context, "knee", "menisc", "patell", "cruciate", "collateral", "femorotib", "distal femur", "proximal tibia", "poplite");
        bool ankleContext = ContainsAny(context, "ankle", "achilles", "tibiotalar", "subtalar", "malleol", "talus", "calcane", "navicular", "cuboid", "perone", "posterior tibial");
        bool headContext = ContainsAny(context, "brain", "head", "cran", "cereb", "neuro", "pituitary");
        bool chestContext = ContainsAny(context, "chest", "thorax", "lung", "pulm", "mediast", "card", "heart", "pleura");
        bool abdomenContext = ContainsAny(context, "abd", "liver", "hep", "stomach", "gastric", "bowel", "intestin", "mesent", "pancrea", "spleen", "renal", "kidney");
        bool pelvisContext = ContainsAny(context, "pelvis", "pelvic", "bladder", "prostate", "uter", "ovar", "rect");

        if (shoulderContext)
        {
            return AnatomyRegionProfile.Shoulder;
        }

        if (kneeContext)
        {
            return AnatomyRegionProfile.Knee;
        }

        if (ankleContext)
        {
            return AnatomyRegionProfile.Ankle;
        }

        if (headContext)
        {
            return AnatomyRegionProfile.Neuro;
        }

        if (chestContext && abdomenContext)
        {
            return AnatomyRegionProfile.ThoraxAbdomen;
        }

        if (abdomenContext && pelvisContext)
        {
            return AnatomyRegionProfile.AbdomenPelvis;
        }

        if (chestContext)
        {
            return AnatomyRegionProfile.Thorax;
        }

        if (pelvisContext)
        {
            return AnatomyRegionProfile.Pelvis;
        }

        if (abdomenContext)
        {
            return AnatomyRegionProfile.Abdomen;
        }

        if (TryResolveSoftBodyPartProfile(bodyPart, out AnatomyRegionProfile bodyPartProfile) &&
            !headContext &&
            !chestContext &&
            !abdomenContext &&
            !pelvisContext)
        {
            return bodyPartProfile;
        }

        AnatomyProfileGuess estimated = EstimateProfileFromMeasurementPosition(measurement, slot);
        return estimated.Profile != AnatomyRegionProfile.Unknown
            ? estimated.Profile
            : TryResolveSoftBodyPartProfile(bodyPart, out bodyPartProfile)
                ? bodyPartProfile
                : estimated.Profile;
    }

    private AnatomyProfileGuess EstimateProfileFromMeasurementPosition(StudyMeasurement measurement, ViewportSlot? slot)
    {
        AnatomyGuess estimated = EstimateMeasurementAnatomyFromPosition(measurement, slot);
        AnatomyRegionProfile profile = estimated.Label switch
        {
            "Cerebrum" or "Cerebellum" => AnatomyRegionProfile.Neuro,
            "Mediastinum" or "Thorax" => AnatomyRegionProfile.Thorax,
            _ when estimated.Label.Contains("lung", StringComparison.OrdinalIgnoreCase) => AnatomyRegionProfile.Thorax,
            "Liver" or "Stomach" or "Mesenterium" or "Intestines" or "Abdomen" => AnatomyRegionProfile.Abdomen,
            "Shoulder joint" or "Right shoulder joint" or "Left shoulder joint" => AnatomyRegionProfile.Shoulder,
            "Knee joint" or "Patellar tendon" or "Popliteal fossa" => AnatomyRegionProfile.Knee,
            "Ankle joint" or "Tibiotalar joint" or "Achilles tendon" => AnatomyRegionProfile.Ankle,
            _ => AnatomyRegionProfile.Unknown,
        };

        return new AnatomyProfileGuess(profile, estimated.Hint);
    }

    private AnatomyGuess EstimateMeasurementAnatomyFromPosition(StudyMeasurement measurement, ViewportSlot? slot)
    {
        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        if (!measurement.TryGetPatientCenter(out SpatialVector3D center) || bounds is null)
        {
            return new AnatomyGuess("Unassigned", "No spatial fallback available.");
        }

        double normalizedX = Normalize(center.X, bounds.Value.MinX, bounds.Value.MaxX);
        double normalizedY = Normalize(center.Y, bounds.Value.MinY, bounds.Value.MaxY);
        double normalizedZ = Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ);
        string laterality = normalizedX >= 0.56 ? "left" : normalizedX <= 0.44 ? "right" : string.Empty;
        string vertical = normalizedZ >= 0.62 ? "upper" : normalizedZ <= 0.38 ? "lower" : "mid";
        bool central = normalizedX > 0.38 && normalizedX < 0.62;
        bool anterior = normalizedY < 0.44;
        bool posterior = normalizedY > 0.56;

        if (normalizedZ <= 0.28 && posterior)
        {
            return new AnatomyGuess("Cerebellum", "Inferior-posterior fallback position.");
        }

        if (normalizedZ >= 0.78)
        {
            return new AnatomyGuess("Cerebrum", "Superior fallback position.");
        }

        if (!central)
        {
            string zone = vertical switch
            {
                "upper" => "upper",
                "lower" => "lower",
                _ => "mid"
            };
            string side = string.IsNullOrWhiteSpace(laterality) ? "lung" : $"{laterality} lung";
            return new AnatomyGuess($"{side} {zone} region".Replace("  ", " ").Trim(), "Lateral fallback position.");
        }

        if (laterality == "right" && vertical == "upper")
        {
            return new AnatomyGuess("Liver", "Right upper fallback position.");
        }

        if (laterality == "left" && vertical == "upper" && anterior)
        {
            return new AnatomyGuess("Stomach", "Left upper anterior fallback position.");
        }

        if (central && vertical != "upper")
        {
            return new AnatomyGuess("Mesenterium", "Central abdominal fallback position.");
        }

        if (anterior || vertical == "lower")
        {
            return new AnatomyGuess("Intestines", "Anterior or lower fallback position.");
        }

        return new AnatomyGuess("Unassigned", "Fallback position remained ambiguous.");
    }

    private static List<string> MergeAnatomyOptions(params IReadOnlyList<string>[] groups)
    {
        var merged = new List<string>();
        foreach (IReadOnlyList<string> group in groups)
        {
            foreach (string option in group)
            {
                if (!merged.Contains(option, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(option);
                }
            }
        }

        return merged;
    }

    private string ResolveSoftBodyPartExamined(ViewportSlot? slot)
    {
        if (!string.IsNullOrWhiteSpace(slot?.Series?.BodyPart))
        {
            return slot.Series.BodyPart.Trim();
        }

        return FindLegacySeriesInfo(slot?.Series)?.BodyPart?.Trim() ?? string.Empty;
    }

    private KPACS.DCMClasses.Models.SeriesInfo? FindLegacySeriesInfo(SeriesRecord? series)
    {
        if (series is null)
        {
            return null;
        }

        return _context.StudyDetails.LegacyStudy?.Series.FirstOrDefault(candidate =>
            string.Equals(candidate.SerInstUid, series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveSoftBodyPartProfile(string bodyPart, out AnatomyRegionProfile profile)
    {
        if (ContainsAny(bodyPart, "shoulder", "acromioclavicular", "clavicle", "scapula"))
        {
            profile = AnatomyRegionProfile.Shoulder;
            return true;
        }

        if (ContainsAny(bodyPart, "knee", "patella", "distal femur", "proximal tibia", "proximal fibula"))
        {
            profile = AnatomyRegionProfile.Knee;
            return true;
        }

        if (ContainsAny(bodyPart, "ankle", "foot", "talus", "calcaneus", "heel"))
        {
            profile = AnatomyRegionProfile.Ankle;
            return true;
        }

        profile = AnatomyRegionProfile.Unknown;
        return false;
    }

    private static AnatomyRegionProfile MapRegionLabelToProfile(string regionLabel) => regionLabel switch
    {
        "Neuro" => AnatomyRegionProfile.Neuro,
        "Upper thorax" or "Lower thorax" => AnatomyRegionProfile.Thorax,
        "Upper abdomen" or "Lower abdomen" => AnatomyRegionProfile.Abdomen,
        "Pelvis" => AnatomyRegionProfile.Pelvis,
        "Shoulder" => AnatomyRegionProfile.Shoulder,
        "Knee" => AnatomyRegionProfile.Knee,
        "Ankle" => AnatomyRegionProfile.Ankle,
        _ => AnatomyRegionProfile.Unknown,
    };

    private RegionGuess EstimateThoraxRegion(StudyMeasurement measurement, ViewportSlot? slot, string bodyPart)
    {
        if (TryEstimateNormalizedZ(measurement, slot, out double normalizedZ) && normalizedZ >= 0.58)
        {
            return new RegionGuess("Upper thorax", "Upper thorax", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests upper thorax."));
        }

        return new RegionGuess("Lower thorax", "Lower thorax", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests lower thorax."));
    }

    private RegionGuess EstimateAbdomenRegion(StudyMeasurement measurement, ViewportSlot? slot, string bodyPart)
    {
        if (TryEstimateNormalizedZ(measurement, slot, out double normalizedZ) && normalizedZ >= 0.50)
        {
            return new RegionGuess("Upper abdomen", "Upper abdomen", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests upper abdomen."));
        }

        return new RegionGuess("Lower abdomen", "Lower abdomen", BuildSoftBodyPartHint(bodyPart, "Position fallback suggests lower abdomen."));
    }

    private RegionGuess EstimateBorderTorsoRegion(StudyMeasurement measurement, ViewportSlot? slot, string bodyPart)
    {
        if (TryEstimateNormalizedZ(measurement, slot, out double normalizedZ) && normalizedZ >= 0.52)
        {
            return new RegionGuess("Lower thorax", "Lower thorax", BuildSoftBodyPartHint(bodyPart, "Mixed descriptors with superior bias suggest lower thorax."));
        }

        return new RegionGuess("Upper abdomen", "Upper abdomen", BuildSoftBodyPartHint(bodyPart, "Mixed descriptors with inferior bias suggest upper abdomen."));
    }

    private bool TryEstimateNormalizedZ(StudyMeasurement measurement, ViewportSlot? slot, out double normalizedZ)
    {
        normalizedZ = 0.5;
        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        if (bounds is null || !measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            return false;
        }

        normalizedZ = Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ);
        return true;
    }

    private string GetRegionSelectionValue(Guid measurementId) =>
        _reportRegionOverrides.TryGetValue(measurementId, out string? regionLabel) && !string.IsNullOrWhiteSpace(regionLabel)
            ? regionLabel
            : "Auto";

    private void ApplyManualRegionSelection(Guid measurementId, string selectedValue)
    {
        if (string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selectedValue))
        {
            _reportRegionOverrides.Remove(measurementId);
        }
        else
        {
            _reportRegionOverrides[measurementId] = selectedValue.Trim();
            _reportReviewStates[measurementId] = "Confirmed";
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: true);
    }

    private static string BuildSoftBodyPartHint(string bodyPart, string fallbackHint)
    {
        return string.IsNullOrWhiteSpace(bodyPart)
            ? fallbackHint
            : $"{fallbackHint} Soft body-part hint: {bodyPart.Trim()}.";
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private string GetAnatomySelectionValue(Guid measurementId) =>
        _reportAnatomyOverrides.TryGetValue(measurementId, out string? manualLabel) &&
        !string.IsNullOrWhiteSpace(manualLabel)
            ? manualLabel
            : "Auto";

    private void ApplyManualAnatomySelection(Guid measurementId, string selectedValue)
    {
        if (string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selectedValue))
        {
            _reportAnatomyOverrides.Remove(measurementId);
        }
        else
        {
            _reportAnatomyOverrides[measurementId] = selectedValue.Trim();
            _reportReviewStates[measurementId] = "Confirmed";
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: true);

        if (!string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            _ = PersistVolumeRoiAnatomyPriorAsync(measurementId, selectedValue.Trim());
        }
    }

    private async Task LoadVolumeRoiAnatomyPriorsAsync()
    {
        if (Application.Current is not App app)
        {
            return;
        }

        List<VolumeRoiAnatomyPriorRecord> priors = await app.Repository.GetVolumeRoiAnatomyPriorsAsync(null, _priorLookupCancellation.Token);
        if (_priorLookupCancellation.IsCancellationRequested)
        {
            return;
        }

        _volumeRoiAnatomyPriors.Clear();
        _volumeRoiAnatomyPriors.AddRange(priors);
        _volumeRoiAnatomyPriorsLoaded = true;

        if (_reportPanelVisible || _reportPanelPinned)
        {
            RefreshReportPanel(forceVisible: _reportPanelPinned);
        }
    }

    private async Task PersistVolumeRoiAnatomyPriorAsync(Guid measurementId, string anatomyLabel)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        StudyMeasurement? measurement = _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId && candidate.Kind == MeasurementKind.VolumeRoi);
        if (measurement is null)
        {
            return;
        }

        ViewportSlot? slot = FindSlotForMeasurement(measurement) ?? _activeSlot;
        RegionGuess region = ResolveMeasurementRegion(measurement, slot);
        if (!TryBuildVolumeRoiPriorRecord(measurement, slot, anatomyLabel, region.Label, out VolumeRoiAnatomyPriorRecord prior))
        {
            return;
        }

        await app.Repository.UpsertVolumeRoiAnatomyPriorAsync(prior, _priorLookupCancellation.Token);
        if (_priorLookupCancellation.IsCancellationRequested)
        {
            return;
        }

        int existingIndex = _volumeRoiAnatomyPriors.FindIndex(candidate => string.Equals(candidate.Signature, prior.Signature, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            VolumeRoiAnatomyPriorRecord existing = _volumeRoiAnatomyPriors[existingIndex];
            _volumeRoiAnatomyPriors[existingIndex] = prior with { PriorKey = existing.PriorKey, UseCount = existing.UseCount + 1 };
        }
        else
        {
            _volumeRoiAnatomyPriors.Insert(0, prior);
        }

        _volumeRoiAnatomyPriorsLoaded = true;
    }

    private bool TryFindLearnedVolumeRoiPriorMatch(StudyMeasurement measurement, ViewportSlot? slot, out VolumeRoiAnatomyPriorMatch match)
    {
        match = null!;
        if (measurement.Kind != MeasurementKind.VolumeRoi || !_volumeRoiAnatomyPriorsLoaded || _volumeRoiAnatomyPriors.Count == 0)
        {
            return false;
        }

        if (!TryBuildVolumeRoiPriorProbe(measurement, slot, out VolumeRoiPriorProbe probe))
        {
            return false;
        }

        VolumeRoiAnatomyPriorMatch? bestMatch = null;
        foreach (VolumeRoiAnatomyPriorRecord prior in _volumeRoiAnatomyPriors)
        {
            double score = ScoreVolumeRoiPrior(probe, prior);
            if (score < 0.55)
            {
                continue;
            }

            string hint = $"Learned from similar labeled 3D ROI ({prior.AnatomyLabel}, {prior.RegionLabel}, match {score:P0}).";
            if (bestMatch is null || score > bestMatch.Score)
            {
                bestMatch = new VolumeRoiAnatomyPriorMatch(prior, score, hint);
            }
        }

        if (bestMatch is null)
        {
            return false;
        }

        match = bestMatch;
        return true;
    }

    private bool TryBuildVolumeRoiPriorRecord(StudyMeasurement measurement, ViewportSlot? slot, string anatomyLabel, string regionLabel, out VolumeRoiAnatomyPriorRecord prior)
    {
        prior = null!;
        if (!TryBuildVolumeRoiPriorProbe(measurement, slot, out VolumeRoiPriorProbe probe))
        {
            return false;
        }

        string signature = string.Join('|', new[]
        {
            _context.StudyDetails.Study.StudyInstanceUid,
            slot?.Series?.SeriesInstanceUid ?? string.Empty,
            anatomyLabel.Trim(),
            regionLabel.Trim(),
            probe.NormalizedCenterX.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedCenterY.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedCenterZ.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedSizeX.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedSizeY.ToString("F3", CultureInfo.InvariantCulture),
            probe.NormalizedSizeZ.ToString("F3", CultureInfo.InvariantCulture),
        });

        prior = new VolumeRoiAnatomyPriorRecord(
            0,
            signature,
            anatomyLabel.Trim(),
            regionLabel.Trim(),
            probe.Modality,
            probe.BodyPartExamined,
            probe.StudyDescription,
            probe.SeriesDescription,
            probe.NormalizedCenterX,
            probe.NormalizedCenterY,
            probe.NormalizedCenterZ,
            probe.NormalizedSizeX,
            probe.NormalizedSizeY,
            probe.NormalizedSizeZ,
            probe.EstimatedVolumeCubicMillimeters,
            _context.StudyDetails.Study.StudyInstanceUid,
            slot?.Series?.SeriesInstanceUid ?? string.Empty,
            DateTime.UtcNow,
            1);
        return true;
    }

    private bool TryBuildVolumeRoiPriorProbe(StudyMeasurement measurement, ViewportSlot? slot, out VolumeRoiPriorProbe probe)
    {
        probe = null!;
        if (measurement.Kind != MeasurementKind.VolumeRoi || measurement.VolumeContours is not { Length: > 0 })
        {
            return false;
        }

        SpatialBounds? bounds = GetSpatialBounds(slot?.Volume);
        SpatialVector3D[] points = measurement.VolumeContours
            .SelectMany(contour => contour.Anchors)
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (bounds is null || points.Length == 0 || !measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            return false;
        }

        double rangeX = Math.Max(bounds.Value.MaxX - bounds.Value.MinX, double.Epsilon);
        double rangeY = Math.Max(bounds.Value.MaxY - bounds.Value.MinY, double.Epsilon);
        double rangeZ = Math.Max(bounds.Value.MaxZ - bounds.Value.MinZ, double.Epsilon);
        double minX = points.Min(point => point.X);
        double maxX = points.Max(point => point.X);
        double minY = points.Min(point => point.Y);
        double maxY = points.Max(point => point.Y);
        double minZ = points.Min(point => point.Z);
        double maxZ = points.Max(point => point.Z);

        probe = new VolumeRoiPriorProbe(
            slot?.Series?.Modality?.Trim() ?? string.Empty,
            ResolveSoftBodyPartExamined(slot),
            _context.StudyDetails.Study.StudyDescription?.Trim() ?? string.Empty,
            slot?.Series?.SeriesDescription?.Trim() ?? string.Empty,
            Normalize(center.X, bounds.Value.MinX, bounds.Value.MaxX),
            Normalize(center.Y, bounds.Value.MinY, bounds.Value.MaxY),
            Normalize(center.Z, bounds.Value.MinZ, bounds.Value.MaxZ),
            Math.Clamp((maxX - minX) / rangeX, 0, 1),
            Math.Clamp((maxY - minY) / rangeY, 0, 1),
            Math.Clamp((maxZ - minZ) / rangeZ, 0, 1),
            EstimateMeasurementVolumeCubicMillimeters(measurement.VolumeContours));
        return true;
    }

    private double ScoreVolumeRoiPrior(VolumeRoiPriorProbe probe, VolumeRoiAnatomyPriorRecord prior)
    {
        double score = 0;

        if (!string.IsNullOrWhiteSpace(probe.Modality) && string.Equals(probe.Modality, prior.Modality, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.16;
        }

        score += 0.12 * ComputeBodyPartSimilarity(probe.BodyPartExamined, prior.BodyPartExamined);

        double centerDistance = Math.Sqrt(
            Math.Pow(probe.NormalizedCenterX - prior.NormalizedCenterX, 2) +
            Math.Pow(probe.NormalizedCenterY - prior.NormalizedCenterY, 2) +
            Math.Pow(probe.NormalizedCenterZ - prior.NormalizedCenterZ, 2));
        score += Math.Max(0, 0.46 - (centerDistance * 0.85));

        double sizeDistance = (
            Math.Abs(probe.NormalizedSizeX - prior.NormalizedSizeX) +
            Math.Abs(probe.NormalizedSizeY - prior.NormalizedSizeY) +
            Math.Abs(probe.NormalizedSizeZ - prior.NormalizedSizeZ)) / 3.0;
        score += Math.Max(0, 0.16 - (sizeDistance * 0.30));

        if (probe.EstimatedVolumeCubicMillimeters > 0 && prior.EstimatedVolumeCubicMillimeters > 0)
        {
            double ratio = Math.Max(probe.EstimatedVolumeCubicMillimeters, prior.EstimatedVolumeCubicMillimeters) /
                           Math.Max(1.0, Math.Min(probe.EstimatedVolumeCubicMillimeters, prior.EstimatedVolumeCubicMillimeters));
            score += Math.Max(0, 0.10 - (Math.Log(ratio) * 0.06));
        }

        score += 0.12 * ComputeContextTokenOverlap(probe.ContextText, $"{prior.StudyDescription} {prior.SeriesDescription} {prior.BodyPartExamined}");
        score += Math.Min(0.08, Math.Max(0, (prior.UseCount - 1) * 0.01));
        return score;
    }

    private static double ComputeContextTokenOverlap(string left, string right)
    {
        HashSet<string> leftTokens = TokenizeContext(left);
        HashSet<string> rightTokens = TokenizeContext(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        int matches = leftTokens.Count(token => rightTokens.Contains(token));
        return (double)matches / Math.Max(leftTokens.Count, rightTokens.Count);
    }

    private static double ComputeBodyPartSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return ComputeContextTokenOverlap(left, right);
    }

    private static HashSet<string> TokenizeContext(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        string[] tokens = value
            .Split([' ', '\\', '/', '-', '_', ',', ';', '.', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string GetReviewStateSelectionValue(Guid measurementId) =>
        _reportReviewStates.TryGetValue(measurementId, out string? state) && !string.IsNullOrWhiteSpace(state)
            ? state
            : "Auto";

    private void ApplyReviewStateSelection(Guid measurementId, string selectedValue)
    {
        if (string.Equals(selectedValue, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selectedValue))
        {
            _reportReviewStates.Remove(measurementId);
        }
        else
        {
            _reportReviewStates[measurementId] = selectedValue.Trim();
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: true);
    }

    private static string GetReviewStateColor(string reviewState) => reviewState switch
    {
        "Confirmed" => "#FF7FDFA2",
        "Needs review" => "#FFF5B267",
        _ => "#FFA8C0D8",
    };

    private SpatialBounds? GetSpatialBounds(SeriesVolume? volume)
    {
        if (volume is null)
        {
            return null;
        }

        SpatialVector3D[] corners =
        [
            volume.VoxelToPatient(0, 0, 0),
            volume.VoxelToPatient(volume.SizeX - 1, 0, 0),
            volume.VoxelToPatient(0, volume.SizeY - 1, 0),
            volume.VoxelToPatient(0, 0, volume.SizeZ - 1),
            volume.VoxelToPatient(volume.SizeX - 1, volume.SizeY - 1, 0),
            volume.VoxelToPatient(volume.SizeX - 1, 0, volume.SizeZ - 1),
            volume.VoxelToPatient(0, volume.SizeY - 1, volume.SizeZ - 1),
            volume.VoxelToPatient(volume.SizeX - 1, volume.SizeY - 1, volume.SizeZ - 1),
        ];

        return new SpatialBounds(
            corners.Min(point => point.X),
            corners.Max(point => point.X),
            corners.Min(point => point.Y),
            corners.Max(point => point.Y),
            corners.Min(point => point.Z),
            corners.Max(point => point.Z));
    }

    private void OnReportPanelPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _reportPanelPinned = ReportPanelPinButton.IsChecked == true;
        if (_reportPanelPinned)
        {
            _reportPanelVisible = true;
        }

        SaveViewerSettings();
        RefreshReportPanel(forceVisible: _reportPanelPinned);
        e.Handled = true;
    }

    private void OnReportPanelHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReportPanel.IsVisible || !e.GetCurrentPoint(ReportPanelDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _reportPanelDragPointer = e.Pointer;
        _reportPanelDragPointer.Capture(ReportPanelDragHandle);
        _reportPanelDragStart = e.GetPosition(ViewerContentHost);
        _reportPanelDragStartOffset = _reportPanelOffset;
        e.Handled = true;
    }

    private void OnReportPanelHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_reportPanelDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _reportPanelDragStart;
        _reportPanelOffset = new Point(
            _reportPanelDragStartOffset.X + delta.X,
            _reportPanelDragStartOffset.Y + delta.Y);
        ApplyReportPanelOffset();
        e.Handled = true;
    }

    private void OnReportPanelHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_reportPanelDragPointer, e.Pointer))
        {
            return;
        }

        _reportPanelDragPointer.Capture(null);
        _reportPanelDragPointer = null;
        ApplyReportPanelOffset();
        SaveViewerSettings();
        e.Handled = true;
    }

    private void ApplyReportPanelOffset()
    {
        if (ReportPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureReportPanelTransform();
        double panelWidth = ReportPanel.Bounds.Width;
        double panelHeight = ReportPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = ReportPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _reportPanelOffset.X;
            transform.Y = _reportPanelOffset.Y;
            return;
        }

        double defaultLeft = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = margin.Top;
        double defaultBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double clampedX = Math.Clamp(_reportPanelOffset.X, -defaultLeft, 0);
        double clampedY = Math.Clamp(_reportPanelOffset.Y, -defaultTop, defaultBottom);
        _reportPanelOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureReportPanelTransform()
    {
        if (ReportPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        ReportPanel.RenderTransform = transform;
        return transform;
    }

    private string ResolveStudyLabel(SeriesRecord? series)
    {
        if (series is null)
        {
            return _isShowingCurrentStudy ? "Current study" : "Comparison study";
        }

        bool isCurrentStudy = _context.StudyDetails.Series.Any(candidate =>
            string.Equals(candidate.SeriesInstanceUid, series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase));
        if (isCurrentStudy)
        {
            return "Current study";
        }

        if (_selectedPriorStudy is not null)
        {
            return $"Comparison study · {_selectedPriorStudy.DisplayLabel}";
        }

        return "Comparison study";
    }

    private static string BuildSeriesLabel(SeriesRecord series)
    {
        string description = series.SeriesDescription?.Trim() ?? string.Empty;
        string prefix = $"Series {series.SeriesNumber}";
        if (!string.IsNullOrWhiteSpace(series.Modality))
        {
            prefix = $"{prefix} · {series.Modality}";
        }

        return string.IsNullOrWhiteSpace(description)
            ? prefix
            : $"{prefix} · {description}";
    }

    private static string GetMeasurementTypeLabel(StudyMeasurement measurement) => measurement.Kind switch
    {
        MeasurementKind.Line => "Measurement",
        MeasurementKind.Angle => "Angle",
        MeasurementKind.Annotation => "Annotation",
        MeasurementKind.RectangleRoi => "Rectangle ROI",
        MeasurementKind.EllipseRoi => "Ellipse ROI",
        MeasurementKind.PolygonRoi => "Polygon ROI",
        MeasurementKind.VolumeRoi => "3D ROI",
        _ => "Finding",
    };

    private static string GetAccentColor(string typeLabel) => typeLabel switch
    {
        "Annotation" => "#FFF5B267",
        "Measurement" => "#FF7FC8F8",
        "Angle" => "#FF7FC8F8",
        "Rectangle ROI" => "#FF7FDFA2",
        "Ellipse ROI" => "#FF7FDFA2",
        "Polygon ROI" => "#FF7FDFA2",
        "3D ROI" => "#FF56D3C2",
        _ => "#FFA8C0D8",
    };

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CombineReportHints(string regionHint, string anatomyHint)
    {
        if (string.IsNullOrWhiteSpace(regionHint))
        {
            return anatomyHint;
        }

        if (string.IsNullOrWhiteSpace(anatomyHint))
        {
            return regionHint;
        }

        return string.Equals(regionHint, anatomyHint, StringComparison.Ordinal)
            ? regionHint
            : $"{regionHint} {anatomyHint}";
    }

    private static double Normalize(double value, double minimum, double maximum)
    {
        if (maximum - minimum <= double.Epsilon)
        {
            return 0.5;
        }

        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private sealed record ReportEntry(
        Guid? MeasurementId,
        string Category,
        string Title,
        string Origin,
        string Region,
        string AutoRegion,
        bool IsRegionManual,
        string Anatomy,
        string Details,
        string Hint,
        bool IsSelected,
        string AccentHex,
        bool IsRegionEditable,
        bool IsAnatomyManual,
        bool IsAnatomyEditable,
        IReadOnlyList<string> AnatomyOptions,
        string ReviewState,
        int SortOrder);

    private sealed record VolumeRoiPriorProbe(
        string Modality,
        string BodyPartExamined,
        string StudyDescription,
        string SeriesDescription,
        double NormalizedCenterX,
        double NormalizedCenterY,
        double NormalizedCenterZ,
        double NormalizedSizeX,
        double NormalizedSizeY,
        double NormalizedSizeZ,
        double EstimatedVolumeCubicMillimeters)
    {
        public string ContextText => $"{StudyDescription} {SeriesDescription} {BodyPartExamined}".Trim();
    }

    private sealed record RegionGuess(string Label, string AutoLabel, string Hint, bool IsManual = false);

    private sealed record AnatomyGuess(string Label, string Hint, bool IsManual = false)
    {
        public string DisplayLabel => IsManual || string.Equals(Label, "Unassigned", StringComparison.OrdinalIgnoreCase)
            ? Label
            : $"Likely {Label}";
    }

    private readonly record struct SpatialBounds(
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double MinZ,
        double MaxZ);

    private enum AnatomyRegionProfile
    {
        Unknown,
        Neuro,
        Thorax,
        Abdomen,
        Pelvis,
        Shoulder,
        Knee,
        Ankle,
        ThoraxAbdomen,
        AbdomenPelvis,
    }

    private sealed record AnatomyProfileGuess(AnatomyRegionProfile Profile, string Hint);
}