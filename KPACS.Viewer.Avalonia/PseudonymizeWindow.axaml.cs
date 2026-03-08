using Avalonia.Controls;
using Avalonia.Interactivity;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

public partial class PseudonymizeWindow : Window
{
    public PseudonymizeWindow()
    {
        InitializeComponent();
        PatientIdBox.Text = $"PX-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    public PseudonymizeRequest? Request { get; private set; }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        Request = new PseudonymizeRequest
        {
            PatientName = PatientNameBox.Text?.Trim() ?? string.Empty,
            PatientId = PatientIdBox.Text?.Trim() ?? string.Empty,
            AccessionNumber = AccessionBox.Text?.Trim(),
            ReferringPhysician = ReferringPhysicianBox.Text?.Trim(),
        };

        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Request = null;
        Close(false);
    }
}
