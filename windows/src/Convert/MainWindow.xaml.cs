using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using Convert.Core;
using Microsoft.Win32;

namespace Convert;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private bool refreshing;
    private Target? builtSettingsFor;

    public MainWindow(MainViewModel vm)
    {
        this.vm = vm;
        InitializeComponent();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Progress)) UpdateProgress();
            else Refresh();
        };
        Refresh();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Started from Explorer's context menu, the window may open behind Explorer; bring it forward.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        vm.Cancel();
        base.OnClosing(e);
    }

    // State → UI

    private void Refresh()
    {
        refreshing = true;
        try
        {
            TitleText.Text = vm.Title;
            SubtitleText.Text = vm.Subtitle;
            KindGlyph.Text = vm.Glyph;
            var showKind = vm.PrimaryKind != null;
            KindBadge.Visibility = showKind ? Visibility.Visible : Visibility.Collapsed;
            AppIcon.Visibility = showKind ? Visibility.Collapsed : Visibility.Visible;

            var empty = vm.Files.Count == 0;
            var mixed = !empty && vm.Groups.Count == 0;
            DropZone.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            MixedNotice.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
            FormatPanel.Visibility = !empty && !mixed && vm.Target != null ? Visibility.Visible : Visibility.Collapsed;

            RefreshFormats();
            RefreshSettings();
            RefreshFooter();
        }
        finally { refreshing = false; }
    }

    private List<string> shownTargetIds = [];

    private void RefreshFormats()
    {
        var items = vm.TargetItems;
        var ids = items.Select(i => i.Target.Id).ToList();
        if (!ids.SequenceEqual(shownTargetIds))
        {
            shownTargetIds = ids;
            var view = new ListCollectionView(items);
            FormatBox.GroupStyle.Clear();
            if (vm.HasSeveralGroups)
            {
                view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(TargetItem.GroupLabel)));
                FormatBox.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeaderTemplate() });
            }
            FormatBox.ItemsSource = view;
        }
        FormatBox.SelectedItem = items.FirstOrDefault(i => i.Target == vm.Target);
        FormatBox.IsEnabled = vm.Phase == Phase.Idle;
    }

    private static DataTemplate GroupHeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.FontSizeProperty, 12.0);
        text.SetValue(MarginProperty, new Thickness(10, 8, 10, 4));
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return new DataTemplate { VisualTree = text };
    }

    private void RefreshSettings()
    {
        var t = vm.Target;
        AdvancedExpander.Visibility = t?.HasSettings == true ? Visibility.Visible : Visibility.Collapsed;
        AdvancedExpander.IsExpanded = vm.ShowAdvanced;
        SettingsPanel.IsEnabled = vm.Phase == Phase.Idle;
        if (t == builtSettingsFor) return;
        builtSettingsFor = t;
        SettingsPanel.Children.Clear();
        if (t != null) SettingsBuilder.Build(SettingsPanel, t, vm.Settings, vm.SettingsChanged);
    }

    private void RefreshFooter()
    {
        IdleFooter.Visibility = vm.Phase == Phase.Idle ? Visibility.Visible : Visibility.Collapsed;
        RunningFooter.Visibility = vm.Phase == Phase.Running ? Visibility.Visible : Visibility.Collapsed;
        DoneFooter.Visibility = vm.Phase == Phase.Done ? Visibility.Visible : Visibility.Collapsed;

        // Enter / Esc follow the visible buttons.
        ConvertButton.IsDefault = vm.Phase == Phase.Idle;
        CancelButton.IsCancel = vm.Phase == Phase.Idle;
        StopButton.IsCancel = vm.Phase == Phase.Running;
        DoneButton.IsDefault = DoneButton.IsCancel = vm.Phase == Phase.Done;

        var warning = vm.Warning;
        WarningRow.Visibility = warning != null ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = warning ?? "";
        TrashBox.Visibility = warning == null && vm.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrashBox.Content = vm.Files.Count > 1 ? "Move originals to Recycle Bin" : "Move original to Recycle Bin";
        TrashBox.IsChecked = vm.TrashOriginals;
        ConvertButton.IsEnabled = vm.CanConvert;

        RunningText.Text = vm.Stopped ? "Stopping…" : $"Converting {vm.CurrentName}…";
        StopButton.IsEnabled = !vm.Stopped;
        UpdateProgress();

        if (vm.Phase == Phase.Done) BuildResults();
        Taskbar.ProgressState = vm.Phase switch
        {
            Phase.Running => TaskbarItemProgressState.Normal,
            Phase.Done when vm.Errors.Count > 0 => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None,
        };
    }

    private void UpdateProgress()
    {
        Progress.Value = vm.Progress;
        Taskbar.ProgressValue = vm.Progress;
    }

    private void BuildResults()
    {
        ResultLines.Children.Clear();
        if (vm.Converted > 0)
            ResultLines.Children.Add(ResultLine("",$"Converted {vm.Converted} {(vm.Converted == 1 ? "file" : "files")}", "SystemFillColorSuccessBrush"));
        if (vm.Trashed > 0)
            ResultLines.Children.Add(ResultLine("", vm.Trashed == 1 ? "Moved the original to the Recycle Bin" : $"Moved {vm.Trashed} originals to the Recycle Bin",
                                                "TextFillColorSecondaryBrush", small: true));
        if (vm.Stopped)
            ResultLines.Children.Add(ResultLine("", "Stopped", "TextFillColorSecondaryBrush"));
        foreach (var e in vm.Errors)
            ResultLines.Children.Add(ResultLine("", e, "SystemFillColorCriticalBrush", small: true));
        ShowButton.IsEnabled = vm.Outputs.Count > 0;
    }

    private static FrameworkElement ResultLine(string glyph, string text, string brush, bool small = false)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], Margin = new Thickness(0, 2, 8, 0), FontSize = small ? 12 : 14 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var label = new TextBox
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, FontSize = small ? 12 : 14, MinHeight = 0, Focusable = true,
        };
        if (small) label.SetResourceReference(ForegroundProperty, brush == "SystemFillColorCriticalBrush" ? brush : "TextFillColorSecondaryBrush");
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    // Events

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || FormatBox.SelectedItem is not TargetItem item) return;
        vm.Target = item.Target;
    }

    private void Advanced_Toggled(object sender, RoutedEventArgs e)
    {
        if (!refreshing) vm.ShowAdvanced = AdvancedExpander.IsExpanded;
    }

    private void TrashBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!refreshing) vm.TrashOriginals = TrashBox.IsChecked == true;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e) => await vm.StartAsync();
    private void Stop_Click(object sender, RoutedEventArgs e) => vm.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Show_Click(object sender, RoutedEventArgs e) => Explorer.ShowFiles(vm.Outputs);

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Choose files to convert",
            Filter = "Supported files|" + string.Join(";", MediaKinds.AllExtensions.Select(x => "*." + x)) + "|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) vm.Add(dlg.FileNames);
    }

    // Drag and drop

    private void SetDropHighlight(bool on)
    {
        DropBorder.SetResourceReference(Shape_Stroke, on ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush");
        DropBorder.SetResourceReference(Shape_Fill, on ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush");
    }

    private static readonly DependencyProperty Shape_Stroke = System.Windows.Shapes.Shape.StrokeProperty;
    private static readonly DependencyProperty Shape_Fill = System.Windows.Shapes.Shape.FillProperty;

    protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); OnDragOver(e); }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) && vm.Phase != Phase.Running;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e) { base.OnDragLeave(e); SetDropHighlight(false); }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        SetDropHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var files = paths.SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p).Where(f => MediaKinds.Of(f) != null)
                : [p]);
            vm.Add(files);
        }
    }
}

internal static class Explorer
{
    /// <summary>Opens Explorer with the outputs selected (one window per folder, like Finder's "Show in Finder").</summary>
    public static void ShowFiles(IEnumerable<string> paths)
    {
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
        {
            var folder = ILCreateFromPathW(group.Key);
            if (folder == IntPtr.Zero) continue;
            var items = group.Select(ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
            try { SHOpenFolderAndSelectItems(folder, (uint)items.Length, items, 0); }
            finally
            {
                foreach (var i in items) ILFree(i);
                ILFree(folder);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ILCreateFromPathW(string path);
    [DllImport("shell32.dll")] private static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);
}
