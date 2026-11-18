using System.Windows;
using System.Windows.Controls;
using Convert.Core;

namespace Convert;

/// <summary>Builds the "Advanced" rows for a target. Mirrors SettingsView in ContentView.swift.</summary>
internal static class SettingsBuilder
{
    private const double LabelWidth = 110;

    public static void Build(Panel panel, Target t, ConvSettings s, Action changed)
    {
        void Row(string label, UIElement content)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(content, 1);
            g.Children.Add(l);
            g.Children.Add(content);
            panel.Children.Add(g);
        }

        void Menu<T>(string label, T current, IEnumerable<(T Value, string Text)> options, Action<T> set)
        {
            var list = options.ToList();
            var box = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Text" };
            box.ItemsSource = list.Select(o => new { o.Value, o.Text }).ToList();
            var index = list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current));
            box.SelectedIndex = Math.Max(0, index);
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                set(list[box.SelectedIndex].Value);
                changed();
            };
            Row(label, box);
        }

        void Check(string text, bool current, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = current };
            c.Checked += (_, _) => { set(true); changed(); };
            c.Unchecked += (_, _) => { set(false); changed(); };
            Row("", c);
        }

        void Slider(string label, double current, double min, double max, double step, string? left, string? right, Func<double, string>? value, Action<double> set)
        {
            var sl = new Slider
            {
                Minimum = min, Maximum = max, Value = current, Width = 200, VerticalAlignment = VerticalAlignment.Center,
                SmallChange = step, LargeChange = step * 2, TickFrequency = step, IsSnapToTickEnabled = step > 0.01,
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock Caption(string text)
            {
                var tb = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                return tb;
            }
            if (left != null) { var c = Caption(left); c.Margin = new Thickness(0, 0, 8, 0); sp.Children.Add(c); }
            sp.Children.Add(sl);
            if (right != null) sp.Children.Add(Caption(right));
            TextBlock? readout = value != null ? Caption(value(current)) : null;
            if (readout != null) sp.Children.Add(readout);
            sl.ValueChanged += (_, e) =>
            {
                set(e.NewValue);
                if (readout != null) readout.Text = value!(e.NewValue);
                changed();
            };
            Row(label, sp);
        }

        void ImageQuality() => Slider("Quality", s.ImageQuality, 0.3, 1, 0.05, null, null,
            v => $"{Math.Round(v * 100)}%", v => s.ImageQuality = Math.Round(v, 2));

        switch (t.Group)
        {
            case OutGroup.Audio:
                if (t.IsLossyAudio)
                    Menu("Bitrate", s.AudioBitrate, t.BitrateOptions.Select(b => (b, b == 0 ? "VBR – best (~245 kbps)" : $"{b} kbps")), v => s.AudioBitrate = v);
                if (t.IsLosslessAudio)
                    Menu("Bit depth", s.BitDepth, [(0, "Match source"), (16, "16-bit"), (24, "24-bit")], v => s.BitDepth = v);
                if (t.SampleRates.Length > 0)
                    Menu("Sample rate", s.SampleRate, t.SampleRates.Select(r => (r, r == 0 ? "Same as source" : RateLabel(r))), v => s.SampleRate = v);
                if (t.HasChannelOption)
                    Menu("Channels", s.Channels, [(0, "Same as source"), (2, "Stereo"), (1, "Mono")], v => s.Channels = v);
                break;

            case OutGroup.Video when t.Id == "gif":
                Menu("Width", s.GifWidth, new[] { 320, 480, 640, 800, 1080 }.Select(w => (w, $"{w} px")), v => s.GifWidth = v);
                Menu("Frame rate", s.GifFps, new[] { 10, 12, 15, 20, 25, 30 }.Select(f => (f, $"{f} fps")), v => s.GifFps = v);
                break;

            case OutGroup.Video:
                if (t.Id != "mov-prores")
                    Slider("Quality", s.VideoQuality, 0, 1, 0, "Smaller", "Better", null, v => s.VideoQuality = Math.Round(v, 3));
                Menu("Resolution", s.VideoHeight, [(0, "Same as source"), (2160, "4K (2160p)"), (1440, "1440p"), (1080, "1080p"), (720, "720p"), (480, "480p")], v => s.VideoHeight = v);
                Menu("Frame rate", s.Fps, [(0, "Same as source"), (60, "60 fps"), (30, "30 fps"), (25, "25 fps"), (24, "24 fps")], v => s.Fps = v);
                Check("Include audio", s.KeepAudio, v => s.KeepAudio = v);
                if (t.HasHardwareOption)
                    Check("Hardware encoding (much faster)", s.Hardware, v => s.Hardware = v);
                break;

            case OutGroup.Image:
                if (t.IsLossyImage) ImageQuality();
                Menu("Max size", s.MaxSize, [(0, "Same as source"), (4096, "4096 px"), (2048, "2048 px"), (1920, "1920 px"), (1280, "1280 px"), (1080, "1080 px"), (512, "512 px")], v => s.MaxSize = v);
                break;

            case OutGroup.Document when t.Id == "pdf-doc":
                Menu("Paper", s.PaperA4, [(true, "A4"), (false, "US Letter")], v => s.PaperA4 = v);
                break;

            case OutGroup.PdfPages when t.Id != "pdf-txt":
                Menu("Resolution", s.PdfDpi, [(72, "72 dpi (screen)"), (150, "150 dpi"), (300, "300 dpi (print)")], v => s.PdfDpi = v);
                if (t.Id == "pdf-jpg") ImageQuality();
                break;
        }
    }

    private static string RateLabel(int hz)
    {
        var k = hz / 1000.0;
        return k == Math.Round(k) ? $"{k:0} kHz" : $"{k:0.0} kHz";
    }
}
