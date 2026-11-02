using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Fotoarhiv.App;

/// <summary>Local vector icons transcribed from the supplied prototype; no fonts or network.</summary>
public sealed class ReferenceIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(ReferenceIcon), new FrameworkPropertyMetadata("images", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(ReferenceIcon), new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public ReferenceIcon() { Width = 19; Height = 19; IsHitTestVisible = false; }
    private static readonly Dictionary<string, Geometry> Shapes = new()
    {
        ["images"] = Geometry.Parse("M6 3 H18 Q21 3 21 6 V18 Q21 21 18 21 H6 Q3 21 3 18 V6 Q3 3 6 3 M10.5 9 A1.5 1.5 0 1 1 7.5 9 A1.5 1.5 0 1 1 10.5 9 M21 16 L16 11 7 21"),
        ["grid"] = Geometry.Parse("M4.3 3 H8.7 Q10 3 10 4.3 V8.7 Q10 10 8.7 10 H4.3 Q3 10 3 8.7 V4.3 Q3 3 4.3 3 M15.3 3 H19.7 Q21 3 21 4.3 V8.7 Q21 10 19.7 10 H15.3 Q14 10 14 8.7 V4.3 Q14 3 15.3 3 M4.3 14 H8.7 Q10 14 10 15.3 V19.7 Q10 21 8.7 21 H4.3 Q3 21 3 19.7 V15.3 Q3 14 4.3 14 M15.3 14 H19.7 Q21 14 21 15.3 V19.7 Q21 21 19.7 21 H15.3 Q14 21 14 19.7 V15.3 Q14 14 15.3 14"),
        ["search"] = Geometry.Parse("M18 11 A7 7 0 1 1 4 11 A7 7 0 1 1 18 11 M16.5 16.5 L21.5 21.5"),
        ["map"] = Geometry.Parse("M3 6 L9 3 15 6 21 3 V18 L15 21 9 18 3 21 Z M9 3 V18 M15 6 V21"),
        ["layers"] = Geometry.Parse("M12 2 L22 7 12 12 2 7 Z M2 12 L12 17 22 12 M2 17 L12 22 22 17"),
        ["folder"] = Geometry.Parse("M5 4 H10 L12 7 H19 Q21 7 21 9 V19 Q21 21 19 21 H5 Q3 21 3 19 V6 Q3 4 5 4 Z"),
        ["plus"] = Geometry.Parse("M12 5 V19 M5 12 H19"),
        ["download"] = Geometry.Parse("M12 3 V16 M7 11 L12 16 17 11 M5 18 V21 H19 V18"),
        ["copy"] = Geometry.Parse("M10 8 H19 Q21 8 21 10 V19 Q21 21 19 21 H10 Q8 21 8 19 V10 Q8 8 10 8 Z M4 16 H3 Q2 16 2 15 V4 Q2 2 4 2 H15 Q16 2 16 3 V4"),
        ["move"] = Geometry.Parse("M12 3 L16 7 12 11 M16 7 H6 Q3 7 3 10 V18 Q3 21 6 21 H19 Q22 21 22 18 V12"),
        ["harddrive"] = Geometry.Parse("M5 5 H19 Q22 5 22 8 V16 Q22 19 19 19 H5 Q2 19 2 16 V8 Q2 5 5 5 Z M2 13 H22 M6 16 H6.1 M10 16 H10.1"),
        ["shield"] = Geometry.Parse("M12 2 L21 6 V12 C21 17 17 21 12 22 C7 21 3 17 3 12 V6 Z M8 12 L11 15 16 9"),
        ["pin"] = Geometry.Parse("M20 10 C20 16 12 22 12 22 C12 22 4 16 4 10 A8 8 0 1 1 20 10 Z M14.5 10 A2.5 2.5 0 1 1 9.5 10 A2.5 2.5 0 1 1 14.5 10"),
        ["close"] = Geometry.Parse("M5 5 L19 19 M19 5 L5 19"),
        ["info"] = Geometry.Parse("M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M12 11 V17 M12 7 H12.01"),
        ["arrow"] = Geometry.Parse("M7 17 L17 7 M8 7 H17 V16"),
        ["filter"] = Geometry.Parse("M3 5 H21 M7 12 H17 M10 19 H14"),
        ["chevron"] = Geometry.Parse("M9 18 L15 12 9 6")
    };
    protected override void OnRender(DrawingContext dc)
    {
        if (!Shapes.TryGetValue(Kind, out var shape)) shape = Shapes["images"];
        var pen = new Pen(Foreground, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, pen, shape); dc.Pop();
    }
}

/// <summary>Measures only the bounded page. Photo aspect ratio and gaps follow the HTML.</summary>
public sealed class ReferenceTilePanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(ReferenceTilePanel), new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(ReferenceTilePanel), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(nameof(AspectRatio), typeof(double), typeof(ReferenceTilePanel), new FrameworkPropertyMetadata(1.12d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double AspectRatio { get => (double)GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }
    private double _width;
    private double[] _heights = [];
    protected override Size MeasureOverride(Size available)
    {
        var columns = Math.Max(1, Columns);
        var width = double.IsFinite(available.Width) ? available.Width : Math.Max(240, ActualWidth);
        _width = Math.Max(1, (width - Gap * (columns - 1)) / columns);
        _heights = new double[(InternalChildren.Count + columns - 1) / columns];
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(_width, AspectRatio > 0 ? _width / AspectRatio : double.PositiveInfinity));
            _heights[i / columns] = Math.Max(_heights[i / columns], AspectRatio > 0 ? _width / AspectRatio : child.DesiredSize.Height);
        }
        return new Size(width, _heights.Sum() + Math.Max(0, _heights.Length - 1) * Gap);
    }
    protected override Size ArrangeOverride(Size size)
    {
        int columns = Math.Max(1, Columns); double y = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (i > 0 && i % columns == 0) y += _heights[i / columns - 1] + Gap;
            InternalChildren[i].Arrange(new Rect(i % columns * (_width + Gap), y, _width, _heights[i / columns]));
        }
        return size;
    }
}
