using System.Globalization;
using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using WinUIShape = Microsoft.UI.Xaml.Shapes.Shape;
using WinUIPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{

    // =========================================================================
    // DonutChart & Multi-Series Chart (Module 1)
    // =========================================================================

    private struct ChartFrameSignature : IEquatable<ChartFrameSignature>
    {
        private ulong _lo;
        private ulong _hi;
        private bool _initialized;

        public static ChartFrameSignature Create() => new()
        {
            _lo = 14695981039346656037UL,
            _hi = 0x9E3779B97F4A7C15UL,
            _initialized = true
        };

        public void AddUInt64(ulong value)
        {
            _lo = (_lo ^ value) * 1099511628211UL;
            ulong mixed = value ^ (value >> 30);
            mixed *= 0xBF58476D1CE4E5B9UL;
            _hi = ((_hi << 27) | (_hi >> 37)) ^ mixed;
        }

        public void AddInt32(int value) => AddUInt64((ulong)(uint)value | 0x100000000UL);

        public void AddDouble(double value) =>
            AddUInt64(value == 0.0 ? 0UL : (ulong)BitConverter.DoubleToInt64Bits(value));

        public void AddColor(Color c) =>
            AddUInt64(((ulong)c.A << 24) | ((ulong)c.R << 16) | ((ulong)c.G << 8) | c.B);

        public void AddString(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                AddUInt64(0xFF00FF00UL);
                return;
            }

            ReadOnlySpan<char> span = text.AsSpan();
            AddInt32(span.Length);
            for (int i = 0; i < span.Length; i++)
            {
                AddUInt64(span[i]);
            }
        }

        public readonly bool Equals(ChartFrameSignature other) =>
            _initialized == other._initialized && _lo == other._lo && _hi == other._hi;

        public override readonly bool Equals(object? obj) =>
            obj is ChartFrameSignature other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(_lo, _hi, _initialized);
    }

    private sealed record DonutSliceData(string Label, double Value, Brush Brush);

    private FrameworkElement BuildDonutChart(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double defaultDiameter = Math.Round(96.0 * EffectiveAppearance.DensityMultiplier);
        double ringDiameter = Math.Clamp(spec.Size ?? spec.Height ?? spec.Width ?? defaultDiameter, 64, 180);
        double strokeThickness = Math.Clamp(Math.Round(ringDiameter * 0.16), 8, 28);
        double radius = (ringDiameter - strokeThickness) / 2.0;
        double cx = ringDiameter / 2.0;
        double cy = ringDiameter / 2.0;

        var ringHost = new Grid
        {
            Width = ringDiameter,
            Height = ringDiameter,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };

        var slicesCanvas = new Canvas
        {
            Width = ringDiameter,
            Height = ringDiameter
        };

        var trackRing = new WinUIPath
        {
            Width = ringDiameter,
            Height = ringDiameter,
            StrokeThickness = strokeThickness,
            Stroke = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)),
            Data = new EllipseGeometry
            {
                Center = new Point(cx, cy),
                RadiusX = radius,
                RadiusY = radius
            }
        };
        ringHost.Children.Add(trackRing);
        ringHost.Children.Add(slicesCanvas);

        var centerStack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = Math.Max(36, ringDiameter - (strokeThickness * 2.4))
        };
        var centerValTb = new TextBlock
        {
            FontFamily = DisplayFontFamily,
            FontSize = ringDiameter >= 120 ? MetricFontSize : SubtitleFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(centerValTb);
        var centerTitleTb = new TextBlock
        {
            FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
            Foreground = ResolveForegroundBrush("muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        centerStack.Children.Add(centerValTb);
        centerStack.Children.Add(centerTitleTb);
        ringHost.Children.Add(centerStack);

        var legendWrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 10,
            VerticalSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        List<DonutSliceData> ExtractSlices(CustomWidgetEvaluationContext evalCtx)
        {
            var list = new List<DonutSliceData>();
            IReadOnlyList<CustomWidgetRecord> records = ResolveNodeCollectionRecords(spec, evalCtx, out _);
            if (records.Count > 0)
            {
                string labelField = !string.IsNullOrWhiteSpace(spec.TitleField) ? spec.TitleField! : "label";
                string valField = !string.IsNullOrWhiteSpace(spec.ValueField) ? spec.ValueField! : "value";
                string colorField = !string.IsNullOrWhiteSpace(spec.ColorField) ? spec.ColorField! : "color";

                for (int i = 0; i < records.Count; i++)
                {
                    CustomWidgetRecord rec = records[i];
                    string rawVal = rec.GetField(valField);
                    if (string.IsNullOrWhiteSpace(rawVal)) rawVal = rec.GetField("count");
                    if (!double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
                    {
                        continue;
                    }

                    string lbl = rec.GetField(labelField);
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = rec.GetField("title");
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = rec.GetField("name");
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = $"#{i + 1}";

                    string cToken = rec.GetField(colorField);
                    string fallbackTone = DefaultChartSeriesPalette[i % DefaultChartSeriesPalette.Length];
                    Brush sliceBrush = ResolveAnyColorBrush(cToken, evalCtx, fallbackTone);
                    list.Add(new DonutSliceData(lbl, v, sliceBrush));
                }
            }
            else if (spec.Series.Count > 0)
            {
                for (int i = 0; i < spec.Series.Count; i++)
                {
                    CustomWidgetChartSeriesSpec s = spec.Series[i];
                    double v = s.Values is { Count: > 0 }
                        ? s.Values.Sum()
                        : (CustomWidgetExpressionEvaluator.EvaluateDouble(s.ValueField, evalCtx) ?? 0);
                    if (v <= 0) continue;
                    string fallbackTone = DefaultChartSeriesPalette[i % DefaultChartSeriesPalette.Length];
                    list.Add(new DonutSliceData(
                        CustomWidgetExpressionEvaluator.Interpolate(s.Name, evalCtx),
                        v,
                        ResolveAnyColorBrush(s.Color, evalCtx, fallbackTone)));
                }
            }

            return list;
        }

        ChartFrameSignature lastSlicesSignature = default;
        void RenderDonut(CustomWidgetEvaluationContext evalCtx)
        {
            List<DonutSliceData> slices = ExtractSlices(evalCtx);
            double total = slices.Sum(static s => s.Value);

            string resolvedCenterVal = !string.IsNullOrWhiteSpace(spec.CenterValue)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.CenterValue, evalCtx)
                : (total > 0 ? total.ToString("0.#", CultureInfo.InvariantCulture) : "0");
            string resolvedCenterTitle = !string.IsNullOrWhiteSpace(spec.CenterTitle)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.CenterTitle, evalCtx)
                : (!string.IsNullOrWhiteSpace(spec.Title) ? CustomWidgetExpressionEvaluator.Interpolate(spec.Title, evalCtx) : string.Empty);

            centerValTb.Text = resolvedCenterVal;
            centerTitleTb.Text = resolvedCenterTitle;
            centerTitleTb.Visibility = string.IsNullOrWhiteSpace(resolvedCenterTitle) ? Visibility.Collapsed : Visibility.Visible;

            ChartFrameSignature nextSignature = ChartFrameSignature.Create();
            nextSignature.AddInt32(slices.Count);
            foreach (DonutSliceData s in slices)
            {
                Color c = s.Brush is SolidColorBrush scb ? scb.Color : Colors.Transparent;
                nextSignature.AddString(s.Label);
                nextSignature.AddDouble(s.Value);
                nextSignature.AddColor(c);
            }

            if (lastSlicesSignature.Equals(nextSignature))
            {
                return;
            }

            lastSlicesSignature = nextSignature;
            slicesCanvas.Children.Clear();
            legendWrap.Children.Clear();

            if (total <= 0 || slices.Count == 0)
            {
                return;
            }

            double gapDeg = slices.Count > 1 ? 2.4 : 0.0;
            double cursorDeg = -90.0;
            foreach (DonutSliceData slice in slices)
            {
                double sweepDeg = (slice.Value / total) * 360.0;
                double drawSweep = Math.Max(0.8, sweepDeg - gapDeg);
                double startDeg = cursorDeg + (gapDeg * 0.5);
                cursorDeg += sweepDeg;

                WinUIPath segPath;
                if (slices.Count == 1 || drawSweep >= 359.5)
                {
                    segPath = new WinUIPath
                    {
                        Width = ringDiameter,
                        Height = ringDiameter,
                        StrokeThickness = strokeThickness,
                        Stroke = slice.Brush,
                        Data = new EllipseGeometry
                        {
                            Center = new Point(cx, cy),
                            RadiusX = radius,
                            RadiusY = radius
                        }
                    };
                }
                else
                {
                    double startRad = startDeg * Math.PI / 180.0;
                    double endRad = (startDeg + drawSweep) * Math.PI / 180.0;
                    var startPt = new Point(cx + (radius * Math.Cos(startRad)), cy + (radius * Math.Sin(startRad)));
                    var endPt = new Point(cx + (radius * Math.Cos(endRad)), cy + (radius * Math.Sin(endRad)));

                    var fig = new PathFigure
                    {
                        StartPoint = startPt,
                        IsClosed = false,
                        IsFilled = false
                    };
                    fig.Segments.Add(new ArcSegment
                    {
                        Point = endPt,
                        Size = new Size(radius, radius),
                        IsLargeArc = drawSweep > 180.0,
                        SweepDirection = SweepDirection.Clockwise
                    });
                    var geom = new PathGeometry();
                    geom.Figures.Add(fig);

                    segPath = new WinUIPath
                    {
                        Width = ringDiameter,
                        Height = ringDiameter,
                        StrokeThickness = strokeThickness,
                        StrokeStartLineCap = PenLineCap.Flat,
                        StrokeEndLineCap = PenLineCap.Flat,
                        Stroke = slice.Brush,
                        Data = geom
                    };
                }

                double pct = (slice.Value / total) * 100.0;
                ToolTipService.SetToolTip(segPath, $"{slice.Label}: {slice.Value:0.##} ({pct:0.#}%)");
                slicesCanvas.Children.Add(segPath);

                if (spec.ShowLegend)
                {
                    var itemRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    itemRow.Children.Add(new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(4),
                        Background = slice.Brush,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    itemRow.Children.Add(new TextBlock
                    {
                        Text = $"{slice.Label} ({pct:0}%)",
                        FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                        Foreground = ResolveForegroundBrush("secondary"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    legendWrap.Children.Add(itemRow);
                }
            }
        }

        RenderDonut(ctx);
        RegisterRetainedPatcher(RenderDonut);

        bool isHorizontal = string.Equals(spec.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        var layout = new StackPanel
        {
            Orientation = isHorizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.Children.Add(ringHost);
        if (spec.ShowLegend)
        {
            legendWrap.VerticalAlignment = VerticalAlignment.Center;
            layout.Children.Add(legendWrap);
        }

        return ApplyContainerVisualOverrides(layout, spec, ctx);
    }

    private sealed record ResolvedChartSeries(
        string Name,
        string Kind,
        Brush StrokeBrush,
        Color SolidColor,
        IReadOnlyList<double> Points);

    private FrameworkElement BuildChart(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double chartHeight = Math.Clamp(spec.Height ?? 148, 64, 420);
        string defaultKind = (spec.ChartKind ?? "line").Trim().ToLowerInvariant();

        var rootStack = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary")
            };
            Grid.SetColumn(titleTb, 0);
            titleRow.Children.Add(titleTb);

            if (!string.IsNullOrWhiteSpace(spec.Subtitle))
            {
                var subTb = new TextBlock
                {
                    Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, ctx),
                    FontSize = CaptionFontSize,
                    Foreground = ResolveForegroundBrush("muted")
                };
                Grid.SetColumn(subTb, 1);
                titleRow.Children.Add(subTb);
            }
            rootStack.Children.Add(titleRow);
        }

        var plotHost = new Grid
        {
            Height = chartHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        var plotCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        plotHost.Children.Add(plotCanvas);
        rootStack.Children.Add(plotHost);

        var xLabelsGrid = new Grid
        {
            Margin = new Thickness(0, -2, 0, 0),
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        xLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        xLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        xLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var xStartTb = new TextBlock { FontSize = 10.5, Foreground = ResolveForegroundBrush("muted"), HorizontalAlignment = HorizontalAlignment.Left };
        var xMidTb = new TextBlock { FontSize = 10.5, Foreground = ResolveForegroundBrush("muted"), HorizontalAlignment = HorizontalAlignment.Center };
        var xEndTb = new TextBlock { FontSize = 10.5, Foreground = ResolveForegroundBrush("muted"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(xStartTb, 0);
        Grid.SetColumn(xMidTb, 1);
        Grid.SetColumn(xEndTb, 2);
        xLabelsGrid.Children.Add(xStartTb);
        xLabelsGrid.Children.Add(xMidTb);
        xLabelsGrid.Children.Add(xEndTb);
        rootStack.Children.Add(xLabelsGrid);

        var legendRow = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 12,
            VerticalSpacing = 4,
            Visibility = spec.ShowLegend ? Visibility.Visible : Visibility.Collapsed
        };
        rootStack.Children.Add(legendRow);

        (List<ResolvedChartSeries> SeriesList, List<string> Categories) ExtractChartModel(CustomWidgetEvaluationContext evalCtx)
        {
            var categories = new List<string>();
            var resolvedSeries = new List<ResolvedChartSeries>();
            IReadOnlyList<CustomWidgetRecord> records = ResolveNodeCollectionRecords(spec, evalCtx, out _);

            string xField = !string.IsNullOrWhiteSpace(spec.XField) ? spec.XField! : "label";
            if (records.Count > 0)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    CustomWidgetRecord r = records[i];
                    string cat = r.GetField(xField);
                    if (string.IsNullOrWhiteSpace(cat)) cat = r.GetField("date");
                    if (string.IsNullOrWhiteSpace(cat)) cat = r.GetField("name");
                    if (string.IsNullOrWhiteSpace(cat)) cat = r.GetField("x");
                    categories.Add(string.IsNullOrWhiteSpace(cat) ? $"{i + 1}" : cat);
                }
            }

            IReadOnlyList<CustomWidgetChartSeriesSpec> seriesSpecs = spec.Series;
            if (seriesSpecs.Count == 0 && records.Count > 0)
            {
                string vf = !string.IsNullOrWhiteSpace(spec.ValueField) ? spec.ValueField! : "value";
                seriesSpecs = [new CustomWidgetChartSeriesSpec(spec.Title ?? vf, vf, spec.Color, defaultKind)];
            }
            else if (seriesSpecs.Count == 0 && !string.IsNullOrWhiteSpace(spec.Sparkline))
            {
                IReadOnlyList<double> hist = CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, evalCtx);
                seriesSpecs = [new CustomWidgetChartSeriesSpec(spec.Title ?? "Series", "value", spec.Color, defaultKind, hist)];
            }

            for (int si = 0; si < seriesSpecs.Count; si++)
            {
                CustomWidgetChartSeriesSpec sSpec = seriesSpecs[si];
                string fallbackTone = DefaultChartSeriesPalette[si % DefaultChartSeriesPalette.Length];
                Brush sBrush = ResolveAnyColorBrush(sSpec.Color, evalCtx, fallbackTone);
                Color solid = sBrush is SolidColorBrush scb
                    ? scb.Color
                    : ResolveSemanticBrush(fallbackTone).Color;

                var pts = new List<double>();
                if (sSpec.Values is { Count: > 0 })
                {
                    pts.AddRange(sSpec.Values);
                }
                else if (records.Count > 0)
                {
                    string vf = !string.IsNullOrWhiteSpace(sSpec.ValueField) ? sSpec.ValueField : "value";
                    foreach (CustomWidgetRecord r in records)
                    {
                        double.TryParse(r.GetField(vf), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv);
                        pts.Add(dv);
                    }
                }

                string sKind = !string.IsNullOrWhiteSpace(sSpec.Kind)
                    ? sSpec.Kind.Trim().ToLowerInvariant()
                    : (defaultKind == "line" && spec.ShowArea && seriesSpecs.Count == 1 && string.IsNullOrWhiteSpace(spec.ChartKind)
                        ? "area"
                        : defaultKind);

                resolvedSeries.Add(new ResolvedChartSeries(
                    CustomWidgetExpressionEvaluator.Interpolate(sSpec.Name, evalCtx),
                    sKind,
                    sBrush,
                    solid,
                    pts));
            }

            return (resolvedSeries, categories);
        }

        ChartFrameSignature lastPlotSignature = default;
        void RenderPlot(CustomWidgetEvaluationContext evalCtx)
        {
            double width = plotHost.ActualWidth > 16 ? plotHost.ActualWidth : Math.Max(180, (spec.Width ?? 280) - 24);
            double height = plotHost.ActualHeight > 16 ? plotHost.ActualHeight : chartHeight;
            const double padTop = 8;
            const double padBottom = 6;
            double usableH = Math.Max(16, height - padTop - padBottom);

            (List<ResolvedChartSeries> seriesList, List<string> categories) = ExtractChartModel(evalCtx);
            ChartFrameSignature nextSignature = ChartFrameSignature.Create();
            nextSignature.AddDouble(Math.Round(width, 1));
            nextSignature.AddDouble(Math.Round(height, 1));
            nextSignature.AddInt32(IsDark ? 1 : 0);
            nextSignature.AddInt32(categories.Count);
            foreach (string cat in categories)
            {
                nextSignature.AddString(cat);
            }
            nextSignature.AddInt32(seriesList.Count);
            foreach (ResolvedChartSeries s in seriesList)
            {
                nextSignature.AddString(s.Name);
                nextSignature.AddString(s.Kind);
                nextSignature.AddColor(s.SolidColor);
                nextSignature.AddInt32(s.Points.Count);
                for (int pi = 0; pi < s.Points.Count; pi++)
                {
                    nextSignature.AddDouble(s.Points[pi]);
                }
            }

            if (lastPlotSignature.Equals(nextSignature))
            {
                return;
            }

            lastPlotSignature = nextSignature;
            plotCanvas.Children.Clear();
            legendRow.Children.Clear();
            int maxPoints = seriesList.Count > 0 ? seriesList.Max(static s => s.Points.Count) : 0;

            if (categories.Count > 0)
            {
                xStartTb.Text = categories[0];
                xMidTb.Text = categories.Count >= 3 ? categories[categories.Count / 2] : string.Empty;
                xEndTb.Text = categories.Count >= 2 ? categories[^1] : string.Empty;
                xLabelsGrid.Visibility = Visibility.Visible;
            }
            else
            {
                xLabelsGrid.Visibility = Visibility.Collapsed;
            }

            if (spec.ShowGrid)
            {
                Brush gridBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0));
                for (int g = 0; g <= 3; g++)
                {
                    double gy = padTop + (usableH * g / 3.0);
                    plotCanvas.Children.Add(new Line
                    {
                        X1 = 0,
                        Y1 = gy,
                        X2 = width,
                        Y2 = gy,
                        Stroke = gridBrush,
                        StrokeThickness = 1
                    });
                }
            }

            if (maxPoints == 0 || seriesList.Count == 0)
            {
                return;
            }

            double minVal = 0;
            double maxVal = 1;
            bool firstVal = true;
            foreach (ResolvedChartSeries s in seriesList)
            {
                foreach (double v in s.Points)
                {
                    if (firstVal)
                    {
                        minVal = Math.Min(0, v);
                        maxVal = v;
                        firstVal = false;
                    }
                    else
                    {
                        if (v < minVal) minVal = v;
                        if (v > maxVal) maxVal = v;
                    }
                }
            }

            if (Math.Abs(maxVal - minVal) < 0.0001)
            {
                maxVal = minVal + 1.0;
            }

            double MapY(double v) =>
                padTop + usableH - (((v - minVal) / (maxVal - minVal)) * usableH);

            double baselineY = MapY(Math.Clamp(0, minVal, maxVal));

            List<ResolvedChartSeries> barSeries = seriesList.Where(static s => s.Kind == "bar").ToList();
            int barSeriesCount = Math.Max(1, barSeries.Count);

            for (int si = 0; si < seriesList.Count; si++)
            {
                ResolvedChartSeries s = seriesList[si];
                if (s.Points.Count == 0) continue;

                if (s.Kind == "bar")
                {
                    int barIdx = barSeries.IndexOf(s);
                    double slotWidth = width / Math.Max(1, maxPoints);
                    double groupWidth = slotWidth * 0.72;
                    double singleBarWidth = Math.Max(3.0, (groupWidth / barSeriesCount) - (barSeriesCount > 1 ? 2 : 0));

                    for (int i = 0; i < s.Points.Count; i++)
                    {
                        double v = s.Points[i];
                        double yVal = MapY(v);
                        double top = Math.Min(yVal, baselineY);
                        double barH = Math.Max(2.0, Math.Abs(baselineY - yVal));
                        double groupLeft = (i * slotWidth) + ((slotWidth - groupWidth) * 0.5);
                        double barLeft = groupLeft + (barIdx * (singleBarWidth + 2));

                        var rect = new Rectangle
                        {
                            Width = singleBarWidth,
                            Height = barH,
                            RadiusX = Math.Min(3, singleBarWidth * 0.4),
                            RadiusY = Math.Min(3, singleBarWidth * 0.4),
                            Fill = s.StrokeBrush
                        };
                        string catLabel = i < categories.Count ? categories[i] : $"#{i + 1}";
                        ToolTipService.SetToolTip(rect, $"{s.Name} · {catLabel}: {v:0.##}{spec.Unit}");
                        Canvas.SetLeft(rect, barLeft);
                        Canvas.SetTop(rect, top);
                        plotCanvas.Children.Add(rect);
                    }
                }
                else
                {
                    int count = s.Points.Count;
                    double stepX = count > 1 ? width / (count - 1) : width;
                    var linePts = new PointCollection();
                    var areaPts = new PointCollection { new Point(0, baselineY) };

                    for (int i = 0; i < count; i++)
                    {
                        double x = count > 1 ? i * stepX : width * 0.5;
                        double y = MapY(s.Points[i]);
                        var pt = new Point(x, y);
                        linePts.Add(pt);
                        areaPts.Add(pt);
                    }

                    if (count == 1)
                    {
                        linePts.Insert(0, new Point(0, linePts[0].Y));
                        linePts[1] = new Point(width, linePts[0].Y);
                        areaPts.Clear();
                        areaPts.Add(new Point(0, baselineY));
                        areaPts.Add(new Point(0, linePts[0].Y));
                        areaPts.Add(new Point(width, linePts[0].Y));
                    }
                    areaPts.Add(new Point(width, baselineY));

                    if (s.Kind == "area")
                    {
                        var areaGrad = new LinearGradientBrush
                        {
                            StartPoint = new Point(0, 0),
                            EndPoint = new Point(0, 1)
                        };
                        areaGrad.GradientStops.Add(new GradientStop
                        {
                            Color = Color.FromArgb((byte)(IsDark ? 56 : 40), s.SolidColor.R, s.SolidColor.G, s.SolidColor.B),
                            Offset = 0
                        });
                        areaGrad.GradientStops.Add(new GradientStop
                        {
                            Color = Color.FromArgb(0, s.SolidColor.R, s.SolidColor.G, s.SolidColor.B),
                            Offset = 1
                        });
                        plotCanvas.Children.Add(new Polygon
                        {
                            Points = areaPts,
                            Fill = areaGrad
                        });
                    }

                    plotCanvas.Children.Add(new Polyline
                    {
                        Points = linePts,
                        Stroke = s.StrokeBrush,
                        StrokeThickness = 2.0,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });

                    if (count <= 24)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            double x = count > 1 ? i * stepX : width * 0.5;
                            double y = MapY(s.Points[i]);
                            var dot = new Ellipse
                            {
                                Width = 6,
                                Height = 6,
                                Fill = s.StrokeBrush
                            };
                            string catLabel = i < categories.Count ? categories[i] : $"#{i + 1}";
                            ToolTipService.SetToolTip(dot, $"{s.Name} · {catLabel}: {s.Points[i]:0.##}{spec.Unit}");
                            Canvas.SetLeft(dot, x - 3);
                            Canvas.SetTop(dot, y - 3);
                            plotCanvas.Children.Add(dot);
                        }
                    }
                }

                if (spec.ShowLegend && (seriesList.Count > 1 || !string.IsNullOrWhiteSpace(s.Name)))
                {
                    var legItem = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5
                    };
                    legItem.Children.Add(new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(s.Kind == "bar" ? 2 : 4),
                        Background = s.StrokeBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    legItem.Children.Add(new TextBlock
                    {
                        Text = s.Name,
                        FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                        Foreground = ResolveForegroundBrush("secondary"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    legendRow.Children.Add(legItem);
                }
            }

            legendRow.Visibility = spec.ShowLegend && legendRow.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        RenderPlot(ctx);
        plotHost.SizeChanged += (_, _) => RenderPlot(_viewModel.CreateEvaluationContext());
        RegisterRetainedPatcher(RenderPlot);

        var card = new Border
        {
            CornerRadius = CornerMedium,
            Padding = ParseThickness(spec.Padding ?? "12,10,12,10"),
            Background = ResolveCardBackground("default"),
            BorderThickness = CardBorderThickness,
            BorderBrush = CardStrokeBrush,
            Child = rootStack
        };

        return ApplyContainerVisualOverrides(card, spec, ctx);
    }

    // =========================================================================
    // Declarative Vector Canvas (Module 2)
    // =========================================================================

    private FrameworkElement BuildCanvas(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        (double vbMinX, double vbMinY, double vbWidth, double vbHeight) = ParseCanvasViewBox(spec.ViewBox, spec.Width, spec.Height);

        var drawingCanvas = new Canvas
        {
            Width = vbWidth,
            Height = vbHeight,
            Background = GetOrCreateSolidBrush(Colors.Transparent),
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };

        ChartFrameSignature lastCanvasSignature = default;
        var shapeStates = new List<CanvasShapeState>();
        void PopulateCanvasShapes(CustomWidgetEvaluationContext evalCtx)
        {
            ChartFrameSignature nextSignature = BuildCanvasRenderSignature(spec, evalCtx);
            if (lastCanvasSignature.Equals(nextSignature))
            {
                return;
            }

            lastCanvasSignature = nextSignature;
            IReadOnlyList<CustomWidgetCanvasElementSpec> shapes = ResolveCanvasElements(spec, evalCtx);

            if (shapes.Count > 0 &&
                drawingCanvas.Children.Count == shapes.Count &&
                shapeStates.Count == shapes.Count &&
                TryReconcileCanvasShapesInPlace(drawingCanvas, shapeStates, shapes, spec, evalCtx))
            {
                return;
            }

            foreach (UIElement existingChild in drawingCanvas.Children)
            {
                if (existingChild is FrameworkElement existingFe)
                {
                    DeskBox.Helpers.FluentMotionHelper.StopCanvasAmbientMotion(existingFe);
                }
            }

            drawingCanvas.Children.Clear();
            shapeStates.Clear();

            foreach (CustomWidgetCanvasElementSpec el in shapes)
            {
                UIElement? built = BuildSingleCanvasShape(el, spec, evalCtx, out CanvasShapeState? state);
                if (built is not null && state is not null)
                {
                    drawingCanvas.Children.Add(built);
                    shapeStates.Add(state);
                }
            }
        }

        PopulateCanvasShapes(ctx);
        RegisterRetainedPatcher(PopulateCanvasShapes);

        FrameworkElement visualStage;
        if (!string.IsNullOrWhiteSpace(spec.ViewBox))
        {
            if (Math.Abs(vbMinX) > 0.01 || Math.Abs(vbMinY) > 0.01)
            {
                drawingCanvas.RenderTransform = new TranslateTransform { X = -vbMinX, Y = -vbMinY };
            }

            Stretch canvasStretch = (spec.Stretch?.Trim().ToLowerInvariant()) switch
            {
                "fill" => Stretch.Fill,
                "uniformtofill" or "cover" => Stretch.UniformToFill,
                "none" => Stretch.None,
                _ => Stretch.Uniform
            };

            visualStage = new Viewbox
            {
                Stretch = canvasStretch,
                Width = spec.Width ?? double.NaN,
                Height = spec.Height ?? 180,
                Child = drawingCanvas
            };
        }
        else
        {
            if (spec.Width.HasValue) drawingCanvas.Width = spec.Width.Value;
            if (spec.Height.HasValue) drawingCanvas.Height = spec.Height.Value;
            visualStage = drawingCanvas;
        }

        var hostBorder = new Border
        {
            CornerRadius = new CornerRadius(0),
            Background = GetOrCreateSolidBrush(Colors.Transparent),
            Clip = new RectangleGeometry(),
            Child = visualStage
        };
        hostBorder.SizeChanged += (_, e) =>
        {
            if (hostBorder.Clip is RectangleGeometry clipRect)
            {
                clipRect.Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
            }
        };

        bool hasPointerGestures =
            spec.OnPointerDown.Count > 0 ||
            spec.OnPointerMove.Count > 0 ||
            spec.OnPointerUp.Count > 0;

        if ((spec.OnClick.Count > 0 || hasPointerGestures || spec.OnItemClick.Count > 0) && !_isNodeSelectionEnabled)
        {
            MarkInteractiveElement(hostBorder);
        }

        CustomWidgetEvaluationContext CreateCanvasPointerContext(Point pt, bool isPressed, string? targetId = null)
        {
            double effW = drawingCanvas.Width > 0 && !double.IsNaN(drawingCanvas.Width)
                ? drawingCanvas.Width
                : Math.Max(1.0, hostBorder.ActualWidth);
            double effH = drawingCanvas.Height > 0 && !double.IsNaN(drawingCanvas.Height)
                ? drawingCanvas.Height
                : Math.Max(1.0, hostBorder.ActualHeight);
            double normX = Math.Clamp(pt.X / effW, 0.0, 1.0);
            double normY = Math.Clamp(pt.Y / effH, 0.0, 1.0);
            return _viewModel.CreateEvaluationContext()
                .WithPointerEvent(
                    targetId ?? spec.Id,
                    Math.Round(pt.X, 1),
                    Math.Round(pt.Y, 1),
                    Math.Round(normX, 4),
                    Math.Round(normY, 4),
                    isPressed);
        }

        if (hasPointerGestures && !_isNodeSelectionEnabled)
        {
            bool pointerDown = false;
            hostBorder.PointerPressed += (_, e) => FireAndForgetSafeUi(async () =>
            {
                pointerDown = true;
                hostBorder.CapturePointer(e.Pointer);
                BeginInteractiveSession(hostBorder);
                if (spec.OnPointerDown.Count > 0)
                {
                    e.Handled = true;
                    Point pt = e.GetCurrentPoint(drawingCanvas).Position;
                    await _viewModel.ExecuteActionsAsync(spec.OnPointerDown, CreateCanvasPointerContext(pt, isPressed: true));
                }
            });

            long lastMoveDispatchTick = 0;
            hostBorder.PointerMoved += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (spec.OnPointerMove.Count == 0)
                {
                    return;
                }

                // A 1000 Hz mouse would otherwise flood QuickJS with one script turn per event.
                long nowTick = Environment.TickCount64;
                if (nowTick - lastMoveDispatchTick < 16)
                {
                    return;
                }
                lastMoveDispatchTick = nowTick;

                Point pt = e.GetCurrentPoint(drawingCanvas).Position;
                await _viewModel.ExecuteActionsAsync(spec.OnPointerMove, CreateCanvasPointerContext(pt, isPressed: pointerDown));
            });

            hostBorder.PointerReleased += (_, e) => FireAndForgetSafeUi(async () =>
            {
                bool wasDown = pointerDown;
                pointerDown = false;
                hostBorder.ReleasePointerCapture(e.Pointer);
                EndInteractiveSession(hostBorder);
                if (spec.OnPointerUp.Count > 0 && wasDown)
                {
                    e.Handled = true;
                    Point pt = e.GetCurrentPoint(drawingCanvas).Position;
                    await _viewModel.ExecuteActionsAsync(spec.OnPointerUp, CreateCanvasPointerContext(pt, isPressed: false));
                }
            });

            hostBorder.PointerCanceled += (_, e) =>
            {
                pointerDown = false;
                hostBorder.ReleasePointerCapture(e.Pointer);
                EndInteractiveSession(hostBorder);
            };
        }

        if (spec.OnClick.Count > 0 && !_isNodeSelectionEnabled)
        {
            hostBorder.Tapped += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (e.Handled)
                {
                    return;
                }

                Point pt = e.GetPosition(drawingCanvas);
                e.Handled = true;
                await _viewModel.ExecuteActionsAsync(spec.OnClick, CreateCanvasPointerContext(pt, isPressed: false));
            });
        }

        return ApplyContainerVisualOverrides(hostBorder, spec, ctx);
    }

    private static (double MinX, double MinY, double Width, double Height) ParseCanvasViewBox(
        string? rawViewBox,
        double? fallbackW,
        double? fallbackH)
    {
        if (!string.IsNullOrWhiteSpace(rawViewBox))
        {
            string[] parts = rawViewBox.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double mx) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double my) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double vw) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double vh) &&
                vw > 0 && vh > 0)
            {
                return (mx, my, vw, vh);
            }
        }

        return (0, 0, Math.Max(16, fallbackW ?? 260), Math.Max(16, fallbackH ?? 160));
    }

    private static ChartFrameSignature BuildCanvasRenderSignature(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        ChartFrameSignature sig = ChartFrameSignature.Create();
        if (!string.IsNullOrWhiteSpace(spec.ElementsSource))
        {
            string expr = spec.ElementsSource.Trim();
            if (expr.StartsWith("${", StringComparison.Ordinal) && expr.EndsWith('}'))
            {
                expr = expr[2..^1].Trim();
            }

            if (expr.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr["$computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    sig.AddString(compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText());
                }
            }
            else if (expr.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr["computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    sig.AddString(compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText());
                }
            }
            else
            {
                sig.AddString(CustomWidgetExpressionEvaluator.EvaluateExpression(expr, ctx));
            }
        }

        if (spec.Elements.Count > 0)
        {
            sig.AddInt32(spec.Elements.Count);
            foreach (CustomWidgetCanvasElementSpec el in spec.Elements)
            {
                sig.AddString(el.Type);
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.X, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Y, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.X2, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Y2, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Width, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Height, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Radius, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.StartAngle, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.SweepAngle, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.RotationExpression, ctx));
                sig.AddDouble(el.Rotation);
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Data, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Points, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Text, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Fill, ctx));
                sig.AddString(CustomWidgetExpressionEvaluator.Interpolate(el.Stroke, ctx));
            }
        }

        return sig;
    }

    private static IReadOnlyList<CustomWidgetCanvasElementSpec> ResolveCanvasElements(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(spec.ElementsSource))
        {
            string expr = spec.ElementsSource.Trim();
            if (expr.StartsWith("${", StringComparison.Ordinal) && expr.EndsWith('}'))
            {
                expr = expr[2..^1].Trim();
            }

            string? rawJson = null;
            if (expr.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase) ||
                expr.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr.StartsWith('$') ? expr["$computed.".Length..] : expr["computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    if (compEl.ValueKind == JsonValueKind.Array)
                    {
                        List<CustomWidgetCanvasElementSpec> directShapes = ParseDynamicCanvasElementsFromArray(compEl);
                        if (spec.Elements.Count == 0)
                        {
                            return directShapes;
                        }

                        var merged = new List<CustomWidgetCanvasElementSpec>(directShapes.Count + spec.Elements.Count);
                        merged.AddRange(directShapes);
                        merged.AddRange(spec.Elements);
                        return merged;
                    }

                    if (compEl.ValueKind == JsonValueKind.String)
                    {
                        rawJson = compEl.GetString();
                    }
                }
            }
            else
            {
                rawJson = CustomWidgetExpressionEvaluator.EvaluateExpression(expr, ctx);
            }

            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.AsSpan().TrimStart().StartsWith('['))
            {
                List<CustomWidgetCanvasElementSpec> dynamicShapes = ParseDynamicCanvasElementsJson(rawJson);
                if (spec.Elements.Count == 0)
                {
                    return dynamicShapes;
                }

                var combined = new List<CustomWidgetCanvasElementSpec>(dynamicShapes.Count + spec.Elements.Count);
                combined.AddRange(dynamicShapes);
                combined.AddRange(spec.Elements);
                return combined;
            }
        }

        return spec.Elements;
    }

    private static List<CustomWidgetCanvasElementSpec> ParseDynamicCanvasElementsJson(string rawJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return ParseDynamicCanvasElementsFromArray(doc.RootElement);
            }
        }
        catch
        {
        }

        return [];
    }

    private static List<CustomWidgetCanvasElementSpec> ParseDynamicCanvasElementsFromArray(JsonElement arrayElement)
    {
        var list = new List<CustomWidgetCanvasElementSpec>();
        try
        {
            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            static string? ReadScalar(JsonElement obj, string prop)
            {
                if (!obj.TryGetProperty(prop, out JsonElement p)) return null;
                return p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString(),
                    JsonValueKind.Number => p.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
            }

            static double ReadDouble(JsonElement obj, string prop, double fallback)
            {
                if (!obj.TryGetProperty(prop, out JsonElement p)) return fallback;
                if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d)) return d;
                if (p.ValueKind == JsonValueKind.String &&
                    double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd)) return sd;
                return fallback;
            }

            int idx = 0;
            foreach (JsonElement item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string type = (ReadScalar(item, "type") ?? ReadScalar(item, "kind") ?? "rect").Trim();
                if (string.IsNullOrWhiteSpace(type)) continue;
                bool isLine = string.Equals(type, "line", StringComparison.OrdinalIgnoreCase);
                string? cxVal = ReadScalar(item, "cx");
                string? cyVal = ReadScalar(item, "cy");
                string? rotExpr = ReadScalar(item, "rotation") ?? ReadScalar(item, "rotate") ?? ReadScalar(item, "angle");

                list.Add(new CustomWidgetCanvasElementSpec(
                    Type: type,
                    Id: ReadScalar(item, "id"),
                    X: ReadScalar(item, "x") ?? (isLine ? ReadScalar(item, "x1") : (cxVal ?? ReadScalar(item, "x1"))),
                    Y: ReadScalar(item, "y") ?? (isLine ? ReadScalar(item, "y1") : (cyVal ?? ReadScalar(item, "y1"))),
                    X2: ReadScalar(item, "x2"),
                    Y2: ReadScalar(item, "y2"),
                    Width: ReadScalar(item, "width") ?? ReadScalar(item, "w"),
                    Height: ReadScalar(item, "height") ?? ReadScalar(item, "h"),
                    Radius: ReadScalar(item, "r") ?? ReadScalar(item, "radius"),
                    Rx: ReadScalar(item, "rx") ?? ReadScalar(item, "cornerRadius"),
                    Ry: ReadScalar(item, "ry"),
                    Data: ReadScalar(item, "d") ?? ReadScalar(item, "data"),
                    Points: ReadScalar(item, "points"),
                    Text: ReadScalar(item, "text") ?? ReadScalar(item, "value"),
                    Fill: ReadScalar(item, "fill") ?? ReadScalar(item, "color"),
                    Stroke: ReadScalar(item, "stroke"),
                    StrokeWidth: Math.Clamp(ReadDouble(item, "strokeWidth", 1.5), 0, 64),
                    Opacity: Math.Clamp(ReadDouble(item, "opacity", 1.0), 0, 1.0),
                    FontSize: Math.Clamp(ReadDouble(item, "fontSize", 12), 6, 120),
                    Rotation: ReadDouble(item, "rotation", ReadDouble(item, "rotate", ReadDouble(item, "angle", 0))),
                    Align: ReadScalar(item, "align") ?? ReadScalar(item, "textAnchor"),
                    Cx: cxVal,
                    Cy: cyVal,
                    FontWeight: ReadScalar(item, "fontWeight") ?? ReadScalar(item, "weight"),
                    RotationExpression: rotExpr,
                    StartAngle: ReadScalar(item, "startAngle") ?? ReadScalar(item, "startDeg"),
                    EndAngle: ReadScalar(item, "endAngle") ?? ReadScalar(item, "endDeg"),
                    SweepAngle: ReadScalar(item, "sweepAngle") ?? ReadScalar(item, "sweepDeg") ?? ReadScalar(item, "sweep"),
                    StrokeCap: ReadScalar(item, "strokeCap") ?? ReadScalar(item, "lineCap")));

                idx++;
                if (idx >= 512) break;
            }
        }
        catch
        {
        }

        return list;
    }

    private static PenLineCap ResolveCanvasStrokeCap(string? rawCap)
    {
        ReadOnlySpan<char> cap = (rawCap ?? "round").AsSpan().Trim();
        if (cap.Equals("flat", StringComparison.OrdinalIgnoreCase) ||
            cap.Equals("butt", StringComparison.OrdinalIgnoreCase))
        {
            return PenLineCap.Flat;
        }

        if (cap.Equals("square", StringComparison.OrdinalIgnoreCase))
        {
            return PenLineCap.Square;
        }

        return PenLineCap.Round;
    }

    private sealed class CanvasShapeState
    {
        public required string CanonicalKind { get; init; }
        public string? LastPathData { get; set; }
        public string? LastPointsData { get; set; }
        public double LastArcCx { get; set; } = double.NaN;
        public double LastArcCy { get; set; } = double.NaN;
        public double LastArcR { get; set; } = double.NaN;
        public double LastArcStart { get; set; } = double.NaN;
        public double LastArcSweep { get; set; } = double.NaN;
        public bool LastArcFilled { get; set; }
        public string? LastTextAlign { get; set; }
        public string? LastFontWeight { get; set; }
        public bool HasItemClickHandler { get; set; }
        public string? BoundItemId { get; set; }
    }

    private static string NormalizeCanvasShapeKind(string rawType)
    {
        ReadOnlySpan<char> t = rawType.AsSpan().Trim();
        if (t.Equals("rect", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("rectangle", StringComparison.OrdinalIgnoreCase))
        {
            return "rect";
        }
        if (t.Equals("circle", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("ellipse", StringComparison.OrdinalIgnoreCase))
        {
            return "ellipse";
        }
        if (t.Equals("arc", StringComparison.OrdinalIgnoreCase))
        {
            return "arc";
        }
        if (t.Equals("line", StringComparison.OrdinalIgnoreCase))
        {
            return "line";
        }
        if (t.Equals("polygon", StringComparison.OrdinalIgnoreCase))
        {
            return "polygon";
        }
        if (t.Equals("polyline", StringComparison.OrdinalIgnoreCase))
        {
            return "polyline";
        }
        if (t.Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            return "path";
        }
        if (t.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            return "text";
        }

        return string.Empty;
    }

    private static bool MatchesExpectedCanvasElementType(UIElement element, string canonicalKind) =>
        canonicalKind switch
        {
            "rect" => element is Rectangle,
            "ellipse" => element is Ellipse,
            "arc" or "path" => element is WinUIPath,
            "line" => element is Line,
            "polygon" => element is Polygon,
            "polyline" => element is Polyline,
            "text" => element is TextBlock,
            _ => false
        };

    private bool TryReconcileCanvasShapesInPlace(
        Canvas drawingCanvas,
        List<CanvasShapeState> states,
        IReadOnlyList<CustomWidgetCanvasElementSpec> shapes,
        CustomWidgetNodeSpec canvasSpec,
        CustomWidgetEvaluationContext ctx)
    {
        for (int i = 0; i < shapes.Count; i++)
        {
            string expectedKind = NormalizeCanvasShapeKind(shapes[i].Type);
            if (string.IsNullOrEmpty(expectedKind) ||
                !string.Equals(states[i].CanonicalKind, expectedKind, StringComparison.Ordinal) ||
                !MatchesExpectedCanvasElementType(drawingCanvas.Children[i], expectedKind))
            {
                return false;
            }
        }

        for (int i = 0; i < shapes.Count; i++)
        {
            if (drawingCanvas.Children[i] is not FrameworkElement fe ||
                !TryApplyCanvasShapePropertiesInPlace(fe, states[i], shapes[i], canvasSpec, ctx))
            {
                return false;
            }
        }

        return true;
    }

    private UIElement? BuildSingleCanvasShape(
        CustomWidgetCanvasElementSpec el,
        CustomWidgetNodeSpec canvasSpec,
        CustomWidgetEvaluationContext ctx,
        out CanvasShapeState? createdState)
    {
        string canonicalKind = NormalizeCanvasShapeKind(el.Type);
        FrameworkElement? element = canonicalKind switch
        {
            "rect" => new Rectangle(),
            "ellipse" => new Ellipse(),
            "arc" => new WinUIPath(),
            "line" => new Line(),
            "polygon" => new Polygon { StrokeLineJoin = PenLineJoin.Round },
            "polyline" => new Polyline { StrokeLineJoin = PenLineJoin.Round },
            "path" => new WinUIPath { StrokeLineJoin = PenLineJoin.Round },
            "text" => CreateCanvasTextElement(),
            _ => null
        };

        if (element is null)
        {
            createdState = null;
            return null;
        }

        var state = new CanvasShapeState { CanonicalKind = canonicalKind };
        if (!TryApplyCanvasShapePropertiesInPlace(element, state, el, canvasSpec, ctx))
        {
            createdState = null;
            return null;
        }

        createdState = state;
        return element;
    }

    private static TextBlock CreateCanvasTextElement()
    {
        var tb = new TextBlock();
        WidgetTextShadow.SetCast(tb, false);
        return tb;
    }

    private bool TryApplyCanvasShapePropertiesInPlace(
        FrameworkElement element,
        CanvasShapeState state,
        CustomWidgetCanvasElementSpec el,
        CustomWidgetNodeSpec canvasSpec,
        CustomWidgetEvaluationContext ctx)
    {
        static double EvalCoord(string? expr, CustomWidgetEvaluationContext evalCtx, double fallback = 0) =>
            string.IsNullOrWhiteSpace(expr)
                ? fallback
                : (CustomWidgetExpressionEvaluator.EvaluateDouble(expr, evalCtx) ?? fallback);

        static bool NearlyEqual(double a, double b) =>
            (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) <= 0.0005;

        static bool SameBrush(Brush? a, Brush? b) =>
            ReferenceEquals(a, b) ||
            (a is SolidColorBrush sa && b is SolidColorBrush sb && sa.Color == sb.Color);

        static void SetCanvasPosIfChanged(UIElement target, double x, double y)
        {
            double curX = Canvas.GetLeft(target);
            if (double.IsNaN(curX) || Math.Abs(curX - x) > 0.0005)
            {
                Canvas.SetLeft(target, x);
            }

            double curY = Canvas.GetTop(target);
            if (double.IsNaN(curY) || Math.Abs(curY - y) > 0.0005)
            {
                Canvas.SetTop(target, y);
            }
        }

        Brush? fillBrush = el.LinearGradient is { Stops.Count: > 0 }
            ? BuildLinearGradientBrush(el.LinearGradient, ctx)
            : el.RadialGradient is { Stops.Count: > 0 }
                ? BuildRadialGradientBrush(el.RadialGradient, ctx)
                : (!string.IsNullOrWhiteSpace(el.Fill)
                    ? ResolveAnyColorBrush(el.Fill, ctx, "accent")
                    : null);
        Brush? strokeBrush = !string.IsNullOrWhiteSpace(el.Stroke)
            ? ResolveAnyColorBrush(el.Stroke, ctx, "accent")
            : null;
        PenLineCap lineCap = ResolveCanvasStrokeCap(el.StrokeCap);

        switch (state.CanonicalKind)
        {
            case "rect" when element is Rectangle rect:
            {
                double x = EvalCoord(el.X, ctx);
                double y = EvalCoord(el.Y, ctx);
                double w = Math.Max(0, EvalCoord(el.Width, ctx, 20));
                double h = Math.Max(0, EvalCoord(el.Height, ctx, 20));
                double rx = Math.Max(0, EvalCoord(el.Rx, ctx, 0));
                double ry = Math.Max(0, EvalCoord(el.Ry, ctx, rx));
                double st = strokeBrush is not null ? el.StrokeWidth : 0;

                if (!NearlyEqual(rect.Width, w)) rect.Width = w;
                if (!NearlyEqual(rect.Height, h)) rect.Height = h;
                if (!NearlyEqual(rect.RadiusX, rx)) rect.RadiusX = rx;
                if (!NearlyEqual(rect.RadiusY, ry)) rect.RadiusY = ry;
                if (!SameBrush(rect.Fill, fillBrush)) rect.Fill = fillBrush;
                if (!SameBrush(rect.Stroke, strokeBrush)) rect.Stroke = strokeBrush;
                if (!NearlyEqual(rect.StrokeThickness, st)) rect.StrokeThickness = st;
                SetCanvasPosIfChanged(rect, x, y);
                break;
            }

            case "ellipse" when element is Ellipse ellipse:
            {
                bool isExplicitEllipse = el.Type.AsSpan().Trim().Equals("ellipse", StringComparison.OrdinalIgnoreCase);
                double cx = EvalCoord(el.X ?? el.Cx, ctx);
                double cy = EvalCoord(el.Y ?? el.Cy, ctx);
                double r = Math.Max(0, EvalCoord(el.Radius, ctx, 8));
                double rx = isExplicitEllipse ? Math.Max(0, EvalCoord(el.Rx ?? el.Width, ctx, r)) : r;
                double ry = isExplicitEllipse ? Math.Max(0, EvalCoord(el.Ry ?? el.Height, ctx, r)) : r;
                double w = rx * 2;
                double h = ry * 2;
                double st = strokeBrush is not null ? el.StrokeWidth : 0;

                if (!NearlyEqual(ellipse.Width, w)) ellipse.Width = w;
                if (!NearlyEqual(ellipse.Height, h)) ellipse.Height = h;
                if (!SameBrush(ellipse.Fill, fillBrush)) ellipse.Fill = fillBrush;
                if (!SameBrush(ellipse.Stroke, strokeBrush)) ellipse.Stroke = strokeBrush;
                if (!NearlyEqual(ellipse.StrokeThickness, st)) ellipse.StrokeThickness = st;
                SetCanvasPosIfChanged(ellipse, cx - rx, cy - ry);
                break;
            }

            case "arc" when element is WinUIPath arcPath:
            {
                double cx = EvalCoord(el.X ?? el.Cx, ctx);
                double cy = EvalCoord(el.Y ?? el.Cy, ctx);
                double r = Math.Max(0.5, EvalCoord(el.Radius, ctx, 24));
                double startDeg = EvalCoord(el.StartAngle, ctx, -90);
                double sweepDeg = !string.IsNullOrWhiteSpace(el.SweepAngle)
                    ? EvalCoord(el.SweepAngle, ctx, 0)
                    : (!string.IsNullOrWhiteSpace(el.EndAngle)
                        ? EvalCoord(el.EndAngle, ctx, startDeg) - startDeg
                        : 270);
                double absSweep = Math.Clamp(Math.Abs(sweepDeg), 0, 359.95);
                if (absSweep <= 0.1)
                {
                    if (arcPath.Visibility != Visibility.Collapsed)
                    {
                        arcPath.Visibility = Visibility.Collapsed;
                    }
                    state.LastArcSweep = 0;
                    break;
                }

                if (arcPath.Visibility != Visibility.Visible)
                {
                    arcPath.Visibility = Visibility.Visible;
                }

                bool isFilled = fillBrush is not null;
                if (arcPath.Data is null ||
                    !NearlyEqual(state.LastArcCx, cx) ||
                    !NearlyEqual(state.LastArcCy, cy) ||
                    !NearlyEqual(state.LastArcR, r) ||
                    !NearlyEqual(state.LastArcStart, startDeg) ||
                    !NearlyEqual(state.LastArcSweep, sweepDeg) ||
                    state.LastArcFilled != isFilled)
                {
                    bool clockwise = sweepDeg >= 0;
                    double effectiveEndDeg = clockwise ? startDeg + absSweep : startDeg - absSweep;
                    double startRad = startDeg * (Math.PI / 180.0);
                    double endRad = effectiveEndDeg * (Math.PI / 180.0);
                    var startPt = new Point(cx + (r * Math.Cos(startRad)), cy + (r * Math.Sin(startRad)));
                    var endPt = new Point(cx + (r * Math.Cos(endRad)), cy + (r * Math.Sin(endRad)));
                    var arcSize = new Size(r, r);
                    bool isLargeArc = absSweep > 180.0;
                    SweepDirection dir = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

                    if (arcPath.Data is PathGeometry existingPg &&
                        existingPg.Figures.Count == 1 &&
                        existingPg.Figures[0].Segments.Count == 1 &&
                        existingPg.Figures[0].Segments[0] is ArcSegment existingArc)
                    {
                        PathFigure existingFig = existingPg.Figures[0];
                        existingFig.StartPoint = startPt;
                        existingFig.IsFilled = isFilled;
                        existingArc.Point = endPt;
                        existingArc.Size = arcSize;
                        existingArc.IsLargeArc = isLargeArc;
                        existingArc.SweepDirection = dir;
                    }
                    else
                    {
                        var fig = new PathFigure
                        {
                            StartPoint = startPt,
                            IsClosed = false,
                            IsFilled = isFilled
                        };
                        fig.Segments.Add(new ArcSegment
                        {
                            Point = endPt,
                            Size = arcSize,
                            RotationAngle = 0,
                            IsLargeArc = isLargeArc,
                            SweepDirection = dir
                        });
                        var arcGeom = new PathGeometry();
                        arcGeom.Figures.Add(fig);
                        arcPath.Data = arcGeom;
                    }

                    state.LastArcCx = cx;
                    state.LastArcCy = cy;
                    state.LastArcR = r;
                    state.LastArcStart = startDeg;
                    state.LastArcSweep = sweepDeg;
                    state.LastArcFilled = isFilled;
                }

                Brush effectiveStroke = strokeBrush ?? fillBrush ?? ResolveSemanticBrush("accent");
                double st = Math.Max(0.5, el.StrokeWidth);
                if (!SameBrush(arcPath.Fill, fillBrush)) arcPath.Fill = fillBrush;
                if (!SameBrush(arcPath.Stroke, effectiveStroke)) arcPath.Stroke = effectiveStroke;
                if (!NearlyEqual(arcPath.StrokeThickness, st)) arcPath.StrokeThickness = st;
                if (arcPath.StrokeStartLineCap != lineCap) arcPath.StrokeStartLineCap = lineCap;
                if (arcPath.StrokeEndLineCap != lineCap) arcPath.StrokeEndLineCap = lineCap;
                break;
            }

            case "line" when element is Line line:
            {
                double x1 = EvalCoord(el.X, ctx);
                double y1 = EvalCoord(el.Y, ctx);
                double x2 = EvalCoord(el.X2, ctx);
                double y2 = EvalCoord(el.Y2, ctx);
                Brush effectiveStroke = strokeBrush ?? fillBrush ?? ResolveForegroundBrush("default");
                double st = Math.Max(0.5, el.StrokeWidth);

                if (!NearlyEqual(line.X1, x1)) line.X1 = x1;
                if (!NearlyEqual(line.Y1, y1)) line.Y1 = y1;
                if (!NearlyEqual(line.X2, x2)) line.X2 = x2;
                if (!NearlyEqual(line.Y2, y2)) line.Y2 = y2;
                if (!SameBrush(line.Stroke, effectiveStroke)) line.Stroke = effectiveStroke;
                if (!NearlyEqual(line.StrokeThickness, st)) line.StrokeThickness = st;
                if (line.StrokeStartLineCap != lineCap) line.StrokeStartLineCap = lineCap;
                if (line.StrokeEndLineCap != lineCap) line.StrokeEndLineCap = lineCap;
                break;
            }

            case "polygon" when element is Polygon polygon:
            {
                string rawPts = CustomWidgetExpressionEvaluator.Interpolate(el.Points, ctx);
                PointCollection targetPts = polygon.Points ??= [];
                if (!string.Equals(state.LastPointsData, rawPts, StringComparison.Ordinal))
                {
                    state.LastPointsData = rawPts;
                    UpdatePointCollectionInPlace(targetPts, rawPts);
                }

                Visibility nextVis = targetPts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (polygon.Visibility != nextVis) polygon.Visibility = nextVis;
                if (nextVis == Visibility.Collapsed) break;

                double st = strokeBrush is not null ? el.StrokeWidth : 0;
                if (!SameBrush(polygon.Fill, fillBrush)) polygon.Fill = fillBrush;
                if (!SameBrush(polygon.Stroke, strokeBrush)) polygon.Stroke = strokeBrush;
                if (!NearlyEqual(polygon.StrokeThickness, st)) polygon.StrokeThickness = st;
                break;
            }

            case "polyline" when element is Polyline polyline:
            {
                string rawPts = CustomWidgetExpressionEvaluator.Interpolate(el.Points, ctx);
                PointCollection targetPts = polyline.Points ??= [];
                if (!string.Equals(state.LastPointsData, rawPts, StringComparison.Ordinal))
                {
                    state.LastPointsData = rawPts;
                    UpdatePointCollectionInPlace(targetPts, rawPts);
                }

                Visibility nextVis = targetPts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (polyline.Visibility != nextVis) polyline.Visibility = nextVis;
                if (nextVis == Visibility.Collapsed) break;

                Brush effectiveStroke = strokeBrush ?? fillBrush ?? ResolveSemanticBrush("accent");
                double st = Math.Max(0.5, el.StrokeWidth);
                if (!SameBrush(polyline.Fill, fillBrush)) polyline.Fill = fillBrush;
                if (!SameBrush(polyline.Stroke, effectiveStroke)) polyline.Stroke = effectiveStroke;
                if (!NearlyEqual(polyline.StrokeThickness, st)) polyline.StrokeThickness = st;
                if (polyline.StrokeStartLineCap != lineCap) polyline.StrokeStartLineCap = lineCap;
                if (polyline.StrokeEndLineCap != lineCap) polyline.StrokeEndLineCap = lineCap;
                break;
            }

            case "path" when element is WinUIPath path:
            {
                string rawD = CustomWidgetExpressionEvaluator.Interpolate(el.Data, ctx);
                if (!string.Equals(state.LastPathData, rawD, StringComparison.Ordinal))
                {
                    state.LastPathData = rawD;
                    path.Data = ParseSvgPathGeometry(rawD);
                }

                Visibility nextVis = path.Data is null ? Visibility.Collapsed : Visibility.Visible;
                if (path.Visibility != nextVis) path.Visibility = nextVis;
                if (nextVis == Visibility.Collapsed) break;

                double st = strokeBrush is not null ? el.StrokeWidth : 0;
                if (!SameBrush(path.Fill, fillBrush)) path.Fill = fillBrush;
                if (!SameBrush(path.Stroke, strokeBrush)) path.Stroke = strokeBrush;
                if (!NearlyEqual(path.StrokeThickness, st)) path.StrokeThickness = st;
                if (path.StrokeStartLineCap != lineCap) path.StrokeStartLineCap = lineCap;
                if (path.StrokeEndLineCap != lineCap) path.StrokeEndLineCap = lineCap;

                double ox = EvalCoord(el.X, ctx, 0);
                double oy = EvalCoord(el.Y, ctx, 0);
                SetCanvasPosIfChanged(path, Math.Abs(ox) > 0.001 ? ox : 0.0, Math.Abs(oy) > 0.001 ? oy : 0.0);
                break;
            }

            case "text" when element is TextBlock tb:
            {
                double x = EvalCoord(el.X, ctx);
                double y = EvalCoord(el.Y, ctx);
                string txt = CustomWidgetExpressionEvaluator.Interpolate(el.Text, ctx);
                Brush fg = fillBrush ?? strokeBrush ?? ResolveForegroundBrush("default");
                bool textMetricsChanged = false;

                if (!string.Equals(tb.Text, txt, StringComparison.Ordinal))
                {
                    tb.Text = txt;
                    textMetricsChanged = true;
                }
                if (!NearlyEqual(tb.FontSize, el.FontSize))
                {
                    tb.FontSize = el.FontSize;
                    textMetricsChanged = true;
                }
                if (!SameBrush(tb.Foreground, fg))
                {
                    tb.Foreground = fg;
                }
                if (!string.Equals(state.LastFontWeight, el.FontWeight, StringComparison.Ordinal))
                {
                    state.LastFontWeight = el.FontWeight;
                    tb.FontWeight = TryResolveFontWeight(el.FontWeight) ?? FontWeights.Normal;
                    textMetricsChanged = true;
                }

                ReadOnlySpan<char> align = (el.Align ?? "left").AsSpan().Trim();
                bool isCenter = align.Equals("center", StringComparison.OrdinalIgnoreCase) ||
                                align.Equals("middle", StringComparison.OrdinalIgnoreCase);
                bool isRight = !isCenter &&
                               (align.Equals("right", StringComparison.OrdinalIgnoreCase) ||
                                align.Equals("end", StringComparison.OrdinalIgnoreCase));
                if (isCenter || isRight)
                {
                    if (textMetricsChanged || !string.Equals(state.LastTextAlign, el.Align, StringComparison.Ordinal))
                    {
                        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    }
                    state.LastTextAlign = el.Align;
                    double dx = isCenter
                        ? -tb.DesiredSize.Width * 0.5
                        : -tb.DesiredSize.Width;
                    SetCanvasPosIfChanged(tb, x + dx, y - (tb.DesiredSize.Height * 0.5));
                }
                else
                {
                    state.LastTextAlign = el.Align;
                    SetCanvasPosIfChanged(tb, x, y);
                }
                break;
            }

            default:
                return false;
        }

        double clampedOpacity = Math.Clamp(el.Opacity, 0.0, 1.0);
        if (!NearlyEqual(element.Opacity, clampedOpacity))
        {
            element.Opacity = clampedOpacity;
        }

        double rotAngle = !string.IsNullOrWhiteSpace(el.RotationExpression)
            ? EvalCoord(el.RotationExpression, ctx, el.Rotation)
            : el.Rotation;
        if (Math.Abs(rotAngle) > 0.01)
        {
            Point targetOrigin;
            double targetCenterX;
            double targetCenterY;
            if (!string.IsNullOrWhiteSpace(el.Cx) || !string.IsNullOrWhiteSpace(el.Cy))
            {
                double pivotX = EvalCoord(el.Cx, ctx);
                double pivotY = EvalCoord(el.Cy, ctx);
                double leftOffset = element is (Line or Polygon or Polyline) ? 0.0 : Canvas.GetLeft(element);
                double topOffset = element is (Line or Polygon or Polyline) ? 0.0 : Canvas.GetTop(element);
                if (double.IsNaN(leftOffset)) leftOffset = 0.0;
                if (double.IsNaN(topOffset)) topOffset = 0.0;
                targetOrigin = new Point(0, 0);
                targetCenterX = pivotX - leftOffset;
                targetCenterY = pivotY - topOffset;
            }
            else if (element is Line ln)
            {
                targetOrigin = new Point(0, 0);
                targetCenterX = (ln.X1 + ln.X2) * 0.5;
                targetCenterY = (ln.Y1 + ln.Y2) * 0.5;
            }
            else
            {
                targetOrigin = new Point(0.5, 0.5);
                targetCenterX = 0.0;
                targetCenterY = 0.0;
            }

            if (element.RenderTransformOrigin != targetOrigin)
            {
                element.RenderTransformOrigin = targetOrigin;
            }

            if (element.RenderTransform is RotateTransform existingRt)
            {
                if (!NearlyEqual(existingRt.Angle, rotAngle)) existingRt.Angle = rotAngle;
                if (!NearlyEqual(existingRt.CenterX, targetCenterX)) existingRt.CenterX = targetCenterX;
                if (!NearlyEqual(existingRt.CenterY, targetCenterY)) existingRt.CenterY = targetCenterY;
            }
            else
            {
                element.RenderTransform = new RotateTransform
                {
                    Angle = rotAngle,
                    CenterX = targetCenterX,
                    CenterY = targetCenterY
                };
            }
        }
        else if (element.RenderTransform is RotateTransform existingZeroRt &&
                 !NearlyEqual(existingZeroRt.Angle, 0.0))
        {
            existingZeroRt.Angle = 0.0;
        }

        if (canvasSpec.OnItemClick.Count > 0 && !_isNodeSelectionEnabled && !string.IsNullOrWhiteSpace(el.Id))
        {
            state.BoundItemId = CustomWidgetExpressionEvaluator.Interpolate(el.Id, ctx);
            if (!state.HasItemClickHandler)
            {
                state.HasItemClickHandler = true;
                element.Tapped += (sender, e) => FireAndForgetSafeUi(async () =>
                {
                    if (string.IsNullOrWhiteSpace(state.BoundItemId))
                    {
                        return;
                    }

                    e.Handled = true;
                    Point pt = sender is UIElement ui && VisualTreeHelper.GetParent(ui) is UIElement parentCanvas
                        ? e.GetPosition(parentCanvas)
                        : new Point(0, 0);
                    CustomWidgetEvaluationContext evCtx = _viewModel.CreateEvaluationContext()
                        .WithPointerEvent(state.BoundItemId, Math.Round(pt.X, 1), Math.Round(pt.Y, 1));
                    await _viewModel.ExecuteActionsAsync(canvasSpec.OnItemClick, evCtx);
                });
            }
        }
        else
        {
            state.BoundItemId = null;
        }

        string? resolvedElementId = string.IsNullOrWhiteSpace(el.Id)
            ? null
            : CustomWidgetExpressionEvaluator.Interpolate(el.Id, ctx);
        double motionPivotX = EvalCoord(el.Cx, ctx, 78.0);
        double motionPivotY = EvalCoord(el.Cy, ctx, 63.0);
        FluentMotionHelper.SyncCanvasAmbientMotion(element, resolvedElementId, motionPivotX, motionPivotY);

        return true;
    }

    private static void UpdatePointCollectionInPlace(PointCollection pts, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            pts.Clear();
            return;
        }

        string[] tokens = raw.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int writeIdx = 0;
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
                double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
            {
                SetOrAppendPoint(pts, writeIdx++, new Point(px, py));
            }
        }

        TrimTrailingPoints(pts, writeIdx);
    }

    internal static PathGeometry? ParseSvgPathGeometry(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData))
        {
            return null;
        }

        var geometry = new PathGeometry();
        PathFigure? currentFigure = null;
        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        double lastCtrlX = 0, lastCtrlY = 0;
        char lastCmd = '\0';

        int idx = 0;
        int len = pathData.Length;

        void SkipSeparators()
        {
            while (idx < len && (char.IsWhiteSpace(pathData[idx]) || pathData[idx] == ','))
            {
                idx++;
            }
        }

        bool TryReadNumber(out double val)
        {
            SkipSeparators();
            if (idx >= len)
            {
                val = 0;
                return false;
            }

            int start = idx;
            if (pathData[idx] is '+' or '-')
            {
                idx++;
            }

            bool hasDigits = false;
            while (idx < len && char.IsDigit(pathData[idx]))
            {
                idx++;
                hasDigits = true;
            }

            if (idx < len && pathData[idx] == '.')
            {
                idx++;
                while (idx < len && char.IsDigit(pathData[idx]))
                {
                    idx++;
                    hasDigits = true;
                }
            }

            if (!hasDigits)
            {
                idx = start;
                val = 0;
                return false;
            }

            if (idx < len && pathData[idx] is 'e' or 'E')
            {
                int expStart = idx++;
                if (idx < len && pathData[idx] is '+' or '-') idx++;
                bool expDigits = false;
                while (idx < len && char.IsDigit(pathData[idx]))
                {
                    idx++;
                    expDigits = true;
                }
                if (!expDigits) idx = expStart;
            }

            return double.TryParse(
                pathData.AsSpan(start, idx - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out val);
        }

        bool TryReadFlag(out bool flag)
        {
            SkipSeparators();
            if (idx < len && pathData[idx] is '0' or '1')
            {
                flag = pathData[idx] == '1';
                idx++;
                return true;
            }
            flag = false;
            return false;
        }

        PathFigure EnsureFigure()
        {
            if (currentFigure is null)
            {
                currentFigure = new PathFigure
                {
                    StartPoint = new Point(curX, curY),
                    IsFilled = true,
                    IsClosed = false
                };
                geometry.Figures.Add(currentFigure);
            }
            return currentFigure;
        }

        char cmd = '\0';
        while (true)
        {
            SkipSeparators();
            if (idx >= len)
            {
                break;
            }

            char ch = pathData[idx];
            if (char.IsLetter(ch))
            {
                cmd = ch;
                idx++;
            }
            else if (cmd == '\0')
            {
                break;
            }

            bool isRel = char.IsLower(cmd);
            char upper = char.ToUpperInvariant(cmd);

            switch (upper)
            {
                case 'M':
                {
                    if (!TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    curX = isRel ? curX + x : x;
                    curY = isRel ? curY + y : y;
                    startX = curX;
                    startY = curY;
                    currentFigure = new PathFigure
                    {
                        StartPoint = new Point(curX, curY),
                        IsFilled = true,
                        IsClosed = false
                    };
                    geometry.Figures.Add(currentFigure);
                    cmd = isRel ? 'l' : 'L';
                    break;
                }

                case 'L':
                {
                    if (!TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    curX = isRel ? curX + x : x;
                    curY = isRel ? curY + y : y;
                    EnsureFigure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    break;
                }

                case 'H':
                {
                    if (!TryReadNumber(out double x)) return geometry;
                    curX = isRel ? curX + x : x;
                    EnsureFigure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    break;
                }

                case 'V':
                {
                    if (!TryReadNumber(out double y)) return geometry;
                    curY = isRel ? curY + y : y;
                    EnsureFigure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    break;
                }

                case 'C':
                {
                    if (!TryReadNumber(out double x1) || !TryReadNumber(out double y1) ||
                        !TryReadNumber(out double x2) || !TryReadNumber(out double y2) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    lastCtrlX = ox + x2;
                    lastCtrlY = oy + y2;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new BezierSegment
                    {
                        Point1 = new Point(ox + x1, oy + y1),
                        Point2 = new Point(lastCtrlX, lastCtrlY),
                        Point3 = new Point(curX, curY)
                    });
                    break;
                }

                case 'S':
                {
                    if (!TryReadNumber(out double x2) || !TryReadNumber(out double y2) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    double rx1 = lastCmd is 'C' or 'c' or 'S' or 's' ? (2 * curX) - lastCtrlX : curX;
                    double ry1 = lastCmd is 'C' or 'c' or 'S' or 's' ? (2 * curY) - lastCtrlY : curY;
                    lastCtrlX = ox + x2;
                    lastCtrlY = oy + y2;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new BezierSegment
                    {
                        Point1 = new Point(rx1, ry1),
                        Point2 = new Point(lastCtrlX, lastCtrlY),
                        Point3 = new Point(curX, curY)
                    });
                    break;
                }

                case 'Q':
                {
                    if (!TryReadNumber(out double x1) || !TryReadNumber(out double y1) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    lastCtrlX = ox + x1;
                    lastCtrlY = oy + y1;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Point(lastCtrlX, lastCtrlY),
                        Point2 = new Point(curX, curY)
                    });
                    break;
                }

                case 'T':
                {
                    if (!TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    lastCtrlX = lastCmd is 'Q' or 'q' or 'T' or 't' ? (2 * curX) - lastCtrlX : curX;
                    lastCtrlY = lastCmd is 'Q' or 'q' or 'T' or 't' ? (2 * curY) - lastCtrlY : curY;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Point(lastCtrlX, lastCtrlY),
                        Point2 = new Point(curX, curY)
                    });
                    break;
                }

                case 'A':
                {
                    if (!TryReadNumber(out double rx) || !TryReadNumber(out double ry) ||
                        !TryReadNumber(out double angle) ||
                        !TryReadFlag(out bool largeArc) || !TryReadFlag(out bool sweep) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    curX = isRel ? curX + x : x;
                    curY = isRel ? curY + y : y;
                    EnsureFigure().Segments.Add(new ArcSegment
                    {
                        Size = new Size(Math.Abs(rx), Math.Abs(ry)),
                        RotationAngle = angle,
                        IsLargeArc = largeArc,
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        Point = new Point(curX, curY)
                    });
                    break;
                }

                case 'Z':
                {
                    if (currentFigure is not null)
                    {
                        currentFigure.IsClosed = true;
                    }
                    curX = startX;
                    curY = startY;
                    cmd = '\0';
                    break;
                }

                default:
                    return geometry.Figures.Count > 0 ? geometry : null;
            }

            lastCmd = ch;
        }

        return geometry.Figures.Count > 0 ? geometry : null;
    }

    // =========================================================================
    // Adaptive Sparkline & Telemetry Backdrop Modes
    // =========================================================================

    internal static string NormalizeSparklineBackdropMode(string? rawVariant, bool showGrid)
    {
        if (!showGrid)
        {
            return "none";
        }

        string normalized = (rawVariant ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "none" or "off" or "plain" or "false" => "none",
            "dots" or "dot" or "matrix" => "dots",
            "lines" or "rules" or "horizontal" or "scanline" => "lines",
            _ => "grid"
        };
    }

    internal static Geometry BuildSparklineBackdropGeometry(string mode, double width, double height)
    {
        double w = Math.Max(12.0, width);
        double h = Math.Max(10.0, height);

        if (string.Equals(mode, "dots", StringComparison.Ordinal))
        {
            const double pitchX = 8.0;
            const double pitchY = 6.0;
            const double dotRadius = 0.75;
            var group = new GeometryGroup();
            int cols = Math.Max(2, (int)Math.Floor((w - 6.0) / pitchX) + 1);
            int rows = Math.Max(2, (int)Math.Floor((h - 4.0) / pitchY) + 1);
            double offsetX = Math.Max(3.0, (w - ((cols - 1) * pitchX)) * 0.5);
            double offsetY = Math.Max(2.5, (h - ((rows - 1) * pitchY)) * 0.5);

            for (int r = 0; r < rows; r++)
            {
                double cy = offsetY + (r * pitchY);
                for (int c = 0; c < cols; c++)
                {
                    double cx = offsetX + (c * pitchX);
                    group.Children.Add(new EllipseGeometry
                    {
                        Center = new Point(cx, cy),
                        RadiusX = dotRadius,
                        RadiusY = dotRadius
                    });
                }
            }
            return group;
        }

        var pathGeom = new PathGeometry();
        if (string.Equals(mode, "none", StringComparison.Ordinal))
        {
            return pathGeom;
        }

        int hDivs = h >= 24.0 ? 4 : 3;
        for (int i = 1; i < hDivs; i++)
        {
            double y = Math.Round((h * i / hDivs) * 2.0) * 0.5;
            var hFig = new PathFigure { StartPoint = new Point(0, y), IsClosed = false, IsFilled = false };
            hFig.Segments.Add(new LineSegment { Point = new Point(w, y) });
            pathGeom.Figures.Add(hFig);
        }

        if (string.Equals(mode, "grid", StringComparison.Ordinal))
        {
            const double vStep = 16.0;
            int vCols = Math.Max(2, (int)Math.Round(w / vStep));
            double actualVStep = w / vCols;
            for (int c = 1; c < vCols; c++)
            {
                double x = Math.Round((c * actualVStep) * 2.0) * 0.5;
                var vFig = new PathFigure { StartPoint = new Point(x, 0), IsClosed = false, IsFilled = false };
                vFig.Segments.Add(new LineSegment { Point = new Point(x, h) });
                pathGeom.Figures.Add(vFig);
            }
        }

        return pathGeom;
    }

    private FrameworkElement BuildSparkline(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double chartHeight = Math.Clamp(spec.Height ?? 36, 18, 140);
        string ResolveTone(CustomWidgetEvaluationContext evalCtx) =>
            string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
                ? "accent"
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Color, evalCtx);

        string ResolveBackdropMode(CustomWidgetEvaluationContext evalCtx)
        {
            string? rawVariant = !string.IsNullOrWhiteSpace(spec.Backdrop)
                ? spec.Backdrop
                : spec.Variant;
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(rawVariant, evalCtx);
            return NormalizeSparklineBackdropMode(resolved, spec.ShowGrid);
        }

        IReadOnlyList<double> series = ResolveSparklineSeries(spec, ctx);
        double latest = series.Count > 0 ? series[^1] : 0;

        Brush ResolveAreaBrush(Brush activeStroke)
        {
            Color strokeColor = activeStroke is SolidColorBrush scb
                ? scb.Color
                : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
            Color topColor = Color.FromArgb((byte)(IsDark ? 52 : 34), strokeColor.R, strokeColor.G, strokeColor.B);
            if (!_sparklineBrushCache.TryGetValue(topColor, out LinearGradientBrush? cachedArea))
            {
                cachedArea = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1)
                };
                cachedArea.GradientStops.Add(new GradientStop { Color = topColor, Offset = 0 });
                cachedArea.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, topColor.R, topColor.G, topColor.B), Offset = 1 });
                _sparklineBrushCache[topColor] = cachedArea;
            }
            return cachedArea;
        }

        Brush initialStrokeBrush = ResolveSemanticBrush(ResolveTone(ctx));
        Brush initialAreaBrush = ResolveAreaBrush(initialStrokeBrush);

        var backdropPatternPath = new WinUIPath
        {
            Stretch = Stretch.None,
            UseLayoutRounding = false,
            IsHitTestVisible = false
        };
        var backdropPlate = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0.75),
            IsHitTestVisible = false,
            Child = backdropPatternPath
        };

        var areaPoly = new Polygon
        {
            Fill = spec.ShowArea ? initialAreaBrush : GetOrCreateSolidBrush(Colors.Transparent),
            Stretch = Stretch.None,
            IsHitTestVisible = false
        };
        var linePoly = new Polyline
        {
            Stroke = initialStrokeBrush,
            StrokeThickness = 1.65,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            IsHitTestVisible = false
        };
        var cursorDot = new Ellipse
        {
            Width = 4.5,
            Height = 4.5,
            Fill = initialStrokeBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };

        var canvasHost = new Grid
        {
            Height = chartHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        canvasHost.Children.Add(backdropPlate);
        canvasHost.Children.Add(areaPoly);
        canvasHost.Children.Add(linePoly);
        canvasHost.Children.Add(cursorDot);

        string lastBackdropMode = string.Empty;
        double lastBackdropWidth = -1;
        double lastBackdropHeight = -1;

        Brush wellBgBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(9, 0, 0, 0));
        Brush wellBorderBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0));
        Brush gridLineBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
        Brush dotMatrixBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0));
        Brush transparentBrush = GetOrCreateSolidBrush(Colors.Transparent);

        void UpdateBackdrop(string mode, double w, double h)
        {
            if (string.Equals(mode, lastBackdropMode, StringComparison.Ordinal) &&
                Math.Abs(w - lastBackdropWidth) < 0.5 &&
                Math.Abs(h - lastBackdropHeight) < 0.5)
            {
                return;
            }

            lastBackdropMode = mode;
            lastBackdropWidth = w;
            lastBackdropHeight = h;

            if (string.Equals(mode, "none", StringComparison.Ordinal))
            {
                backdropPlate.Background = transparentBrush;
                backdropPlate.BorderBrush = transparentBrush;
                backdropPatternPath.Data = null;
                backdropPatternPath.Visibility = Visibility.Collapsed;
                return;
            }

            backdropPlate.Background = wellBgBrush;
            backdropPlate.BorderBrush = wellBorderBrush;
            backdropPatternPath.Visibility = Visibility.Visible;
            backdropPatternPath.Data = BuildSparklineBackdropGeometry(mode, w, h);
            if (string.Equals(mode, "dots", StringComparison.Ordinal))
            {
                backdropPatternPath.Fill = dotMatrixBrush;
                backdropPatternPath.Stroke = null;
                backdropPatternPath.StrokeThickness = 0;
            }
            else
            {
                backdropPatternPath.Fill = null;
                backdropPatternPath.Stroke = gridLineBrush;
                backdropPatternPath.StrokeThickness = 0.75;
            }
        }

        double minBound = spec.Min;
        double maxBound = spec.Max > spec.Min ? spec.Max : spec.Min + 100.0;

        void RenderPoints(IReadOnlyList<double> pts, CustomWidgetEvaluationContext evalCtx)
        {
            double w = canvasHost.ActualWidth > 12 ? canvasHost.ActualWidth : 180;
            double h = Math.Max(14, chartHeight);

            UpdateBackdrop(ResolveBackdropMode(evalCtx), w, h);

            if (ContainsDynamicToken(spec.Color))
            {
                Brush nextStroke = ResolveSemanticBrush(ResolveTone(evalCtx));
                linePoly.Stroke = nextStroke;
                cursorDot.Fill = nextStroke;
                if (spec.ShowArea)
                {
                    areaPoly.Fill = ResolveAreaBrush(nextStroke);
                }
            }

            Span<Point> lineScratch = stackalloc Point[160];
            Span<Point> areaScratch = stackalloc Point[162];
            ComputeAdaptiveSparklinePointsIntoSpan(
                pts,
                w,
                h,
                minBound,
                maxBound,
                lineScratch,
                areaScratch,
                out int lineCount,
                out int areaCount,
                out Point cursorPt);

            PointCollection linePts = linePoly.Points ??= [];
            for (int i = 0; i < lineCount; i++)
            {
                SetOrAppendPoint(linePts, i, lineScratch[i]);
            }
            TrimTrailingPoints(linePts, lineCount);

            PointCollection areaPts = areaPoly.Points ??= [];
            if (spec.ShowArea && areaCount > 0)
            {
                for (int i = 0; i < areaCount; i++)
                {
                    SetOrAppendPoint(areaPts, i, areaScratch[i]);
                }
                TrimTrailingPoints(areaPts, areaCount);
            }
            else
            {
                areaPts.Clear();
            }

            double dotLeft = Math.Clamp(cursorPt.X - 2.25, 0, Math.Max(0, w - 4.5));
            double dotTop = Math.Clamp(cursorPt.Y - 2.25, 0, Math.Max(0, h - 4.5));
            cursorDot.Margin = new Thickness(dotLeft, dotTop, 0, 0);
        }

        RenderPoints(series, ctx);
        canvasHost.SizeChanged += (_, _) =>
        {
            CustomWidgetEvaluationContext latestCtx = _viewModel.CreateEvaluationContext();
            RenderPoints(ResolveSparklineSeries(spec, latestCtx), latestCtx);
        };

        string rawTitle = spec.Title ?? spec.Text ?? string.Empty;
        string title = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx);
        string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, ctx);
        TextBlock? titleTb = null;
        TextBlock? latestTb = null;

        RegisterRetainedPatcher(liveCtx =>
        {
            IReadOnlyList<double> liveSeries = ResolveSparklineSeries(spec, liveCtx);
            RenderPoints(liveSeries, liveCtx);
            if (titleTb is not null && ContainsDynamicToken(rawTitle))
            {
                titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
            }
            if (latestTb is not null && liveSeries.Count > 0)
            {
                string liveUnit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, liveCtx);
                latestTb.Text = $"{liveSeries[^1].ToString("0.##", CultureInfo.InvariantCulture)}{liveUnit}";
            }
        });

        if (string.IsNullOrWhiteSpace(title) && !spec.ShowLatest)
        {
            return canvasHost;
        }

        var wrapper = new StackPanel { Spacing = 4 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(title))
        {
            titleTb = new TextBlock
            {
                Text = title,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            header.Children.Add(titleTb);
        }
        if (spec.ShowLatest)
        {
            latestTb = new TextBlock
            {
                Text = series.Count > 0 ? $"{latest.ToString("0.##", CultureInfo.InvariantCulture)}{unit}" : $"0{unit}",
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("default")
            };
            SetTabularNumerals(latestTb);
            Grid.SetColumn(latestTb, 1);
            header.Children.Add(latestTb);
        }
        wrapper.Children.Add(header);
        wrapper.Children.Add(canvasHost);
        return wrapper;
    }

    private IReadOnlyList<double> ResolveSparklineSeries(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string? colName = spec.Collection ?? spec.Items;
        if (!string.IsNullOrWhiteSpace(colName))
        {
            var colResult = new List<double>();
            string valField = string.IsNullOrWhiteSpace(spec.ValueField) ? "value" : spec.ValueField!;
            if (TryResolveComputedRecords(colName, ctx, out IReadOnlyList<CustomWidgetRecord> computedRecords) &&
                computedRecords.Count > 0)
            {
                foreach (CustomWidgetRecord rec in computedRecords.TakeLast(160))
                {
                    if (double.TryParse(rec.GetField(valField), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        colResult.Add(d);
                    }
                }

                if (colResult.Count > 0)
                {
                    return colResult;
                }
            }

            string resolvedCol = CustomWidgetExpressionEvaluator.Interpolate(colName, ctx).Trim();
            string cleanCol = resolvedCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase)
                ? resolvedCol["collections.".Length..]
                : resolvedCol;
            if (!string.IsNullOrWhiteSpace(cleanCol) &&
                ctx.Collections.TryGetValue(cleanCol, out List<CustomWidgetRecord>? records) &&
                records is { Count: > 0 })
            {
                string colValField = string.IsNullOrWhiteSpace(spec.ValueField) ? "value" : spec.ValueField!;
                foreach (CustomWidgetRecord rec in Enumerable.Reverse(records).TakeLast(160))
                {
                    string raw = rec.GetField(colValField);
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        colResult.Add(d);
                    }
                }

                if (colResult.Count > 0)
                {
                    return colResult;
                }
            }
        }

        string? rawExpr = spec.Sparkline ?? spec.Value;
        if (!string.IsNullOrWhiteSpace(rawExpr))
        {
            IReadOnlyList<double> sysHist = CustomWidgetExpressionEvaluator.ResolveHistory(rawExpr, ctx);
            if (sysHist.Count > 0)
            {
                return sysHist;
            }

            var parsed = new List<double>();
            string interpolated = CustomWidgetExpressionEvaluator.Interpolate(rawExpr, ctx);
            foreach (string part in interpolated.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    parsed.Add(v);
                }
            }

            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        return [];
    }

    private static void SetOrAppendPoint(PointCollection points, int index, Point point)
    {
        if (index < points.Count)
        {
            points[index] = point;
        }
        else
        {
            points.Add(point);
        }
    }

    private static void TrimTrailingPoints(PointCollection points, int desiredCount)
    {
        while (points.Count > desiredCount)
        {
            points.RemoveAt(points.Count - 1);
        }
    }

    public const double SparklineColumnPitchDip = 5.0;

    internal static (Point[] Centers, double DotRadius, Size Bounds) ComputeSixDotGripSpec()
    {
        const double dotRadius = 1.15;
        const double pitch = 4.5;
        const double x0 = 1.5;
        const double y0 = 1.5;

        var centers = new Point[6];
        int idx = 0;
        for (int row = 0; row < 3; row++)
        {
            double cy = y0 + (row * pitch);
            for (int col = 0; col < 2; col++)
            {
                centers[idx++] = new Point(x0 + (col * pitch), cy);
            }
        }

        return (centers, dotRadius, new Size((x0 * 2.0) + pitch, (y0 * 2.0) + (pitch * 2.0)));
    }

    internal static Microsoft.UI.Xaml.Shapes.Path CreateSixDotGripIcon(Brush fill)
    {
        (Point[] centers, double dotRadius, Size bounds) = ComputeSixDotGripSpec();
        var group = new GeometryGroup();
        foreach (Point center in centers)
        {
            group.Children.Add(new EllipseGeometry
            {
                Center = center,
                RadiusX = dotRadius,
                RadiusY = dotRadius
            });
        }

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Data = group,
            Fill = fill,
            Stretch = Stretch.None,
            UseLayoutRounding = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

    internal static void TryApplyDragGripCursor(UIElement element)
    {
        try
        {
            UiElementCursorHelper.SetCursor(element, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        }
        catch
        {
        }
    }

    internal static int ComputeSparklineSlotCount(double width) =>
        Math.Clamp((int)Math.Round(Math.Max(24.0, width) / SparklineColumnPitchDip) + 1, 12, 160);

    private static void ComputeAdaptiveSparklinePointsIntoSpan(
        IReadOnlyList<double>? pts,
        double width,
        double height,
        double minBound,
        double maxBound,
        Span<Point> lineOut,
        Span<Point> areaOut,
        out int lineCount,
        out int areaCount,
        out Point cursorPoint)
    {
        double w = Math.Max(12.0, width);
        double h = Math.Max(10.0, height);
        double bottomY = h - 1.0;
        const double topPad = 2.0;
        double usableH = Math.Max(4.0, bottomY - topPad);

        int slotCount = Math.Min(ComputeSparklineSlotCount(w), lineOut.Length);
        double stepX = w / (slotCount - 1);

        int rawCount = pts?.Count ?? 0;
        int visibleDataCount = Math.Min(rawCount, slotCount);
        int dataStartOffset = rawCount - visibleDataCount;
        int emptySlotCount = slotCount - visibleDataCount;

        if (visibleDataCount <= 0)
        {
            lineOut[0] = new Point(0, bottomY);
            lineOut[1] = new Point(w, bottomY);
            lineCount = 2;
            areaCount = 0;
            cursorPoint = new Point(w, bottomY);
            return;
        }

        double minV = minBound;
        double maxV = maxBound > minBound ? maxBound : minBound + 100.0;
        for (int k = 0; k < visibleDataCount; k++)
        {
            double v = pts![dataStartOffset + k];
            if (v < minV)
            {
                minV = v;
            }
            if (v > maxV)
            {
                maxV = v * 1.08;
            }
        }

        double span = Math.Max(1.0, maxV - minV);
        for (int s = 0; s < emptySlotCount; s++)
        {
            lineOut[s] = new Point(s * stepX, bottomY);
        }

        for (int k = 0; k < visibleDataCount; k++)
        {
            int slotIdx = emptySlotCount + k;
            double x = slotIdx == slotCount - 1 ? w : slotIdx * stepX;
            double v = pts![dataStartOffset + k];
            double norm = Math.Clamp((v - minV) / span, 0.0, 1.0);
            double y = bottomY - (norm * usableH);
            lineOut[slotIdx] = new Point(x, y);
        }

        lineCount = slotCount;
        int areaStartSlot = Math.Max(0, emptySlotCount - 1);
        int areaCurveCount = Math.Min(slotCount - areaStartSlot, Math.Max(0, areaOut.Length - 2));
        areaCount = areaCurveCount + 2;
        areaOut[0] = new Point(lineOut[areaStartSlot].X, bottomY);
        for (int i = 0; i < areaCurveCount; i++)
        {
            areaOut[i + 1] = lineOut[areaStartSlot + i];
        }
        areaOut[areaCount - 1] = new Point(w, bottomY);
        cursorPoint = lineOut[slotCount - 1];
    }

    internal static (Point[] LinePoints, Point[] AreaPoints, Point CursorPoint) ComputeAdaptiveSparklinePoints(
        IReadOnlyList<double> pts,
        double width,
        double height,
        double minBound = 0.0,
        double maxBound = 100.0)
    {
        Span<Point> lineScratch = stackalloc Point[160];
        Span<Point> areaScratch = stackalloc Point[162];
        ComputeAdaptiveSparklinePointsIntoSpan(
            pts,
            width,
            height,
            minBound,
            maxBound,
            lineScratch,
            areaScratch,
            out int lineCount,
            out int areaCount,
            out Point cursorPoint);

        return (
            lineScratch[..lineCount].ToArray(),
            areaCount > 0 ? areaScratch[..areaCount].ToArray() : [],
            cursorPoint);
    }

    private static void PopulateSparklineGeometry(
        IReadOnlyList<double> history,
        double currentPct,
        double width,
        double height,
        Polyline linePolyline,
        Polygon areaPolygon)
    {
        PointCollection linePts = linePolyline.Points ??= [];
        PointCollection areaPts = areaPolygon.Points ??= [];

        IReadOnlyList<double> effectiveSeries = history is { Count: > 0 }
            ? history
            : (currentPct > 0 ? [currentPct] : []);
        Span<Point> lineScratch = stackalloc Point[160];
        Span<Point> areaScratch = stackalloc Point[162];
        ComputeAdaptiveSparklinePointsIntoSpan(
            effectiveSeries,
            width,
            height,
            minBound: 0.0,
            maxBound: 100.0,
            lineScratch,
            areaScratch,
            out int lineCount,
            out int areaCount,
            out _);

        for (int i = 0; i < lineCount; i++)
        {
            SetOrAppendPoint(linePts, i, lineScratch[i]);
        }
        TrimTrailingPoints(linePts, lineCount);

        for (int i = 0; i < areaCount; i++)
        {
            SetOrAppendPoint(areaPts, i, areaScratch[i]);
        }
        TrimTrailingPoints(areaPts, areaCount);
    }
}
