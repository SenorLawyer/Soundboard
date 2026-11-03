using System;
using System.Windows;
using System.Windows.Media;

namespace Soundboard;

public sealed class SoundGlyph : FrameworkElement
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(nameof(Variant), typeof(int), typeof(SoundGlyph),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int Variant { get => (int)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    private static readonly string[] Top = ["#FF9D74", "#ED9DD9", "#FFDC75", "#7BDCFA", "#B7A5FF", "#79E1BC"];
    private static readonly string[] Bottom = ["#DF5550", "#A058B5", "#C48C32", "#4286D3", "#7759CC", "#3E9C89"];
    private static readonly string[] Shapes = [
        "M4,9 L7,9 14,5 14,15 7,12 4,12 Z M6.5,12.3 L8,16.2 9.6,16.2 8.4,12.8 M16,8 Q18,10 16,12",
        "M4,10 L4,11 M7,7.2 L7,14 M10,4.6 L10,16.5 M13,6 L13,15 M16,8.4 L16,12.8 M18.4,9.5 L18.4,11.5",
        "M5,9 C5,5 14,3 16,6 C18,9 11,14 7,12.5 C5.5,12 5,11 5,9 Z M5,10.5 L5.5,13 Q7,17 13,14 Q17,12 17,9 L16.7,7 M8.8,9.7 L12.5,7.7",
        "M4,11 L7.6,10 9.7,4 11.5,9.3 15,10.8 11.5,12.6 9.5,17.5 7.9,12.6 Z M15,4 L15,7 M13.5,5.5 L16.5,5.5",
        "M10.8,3.8 L10.8,10 M6.6,6.2 C1.7,10.7 5,17 10.8,17 C16.8,17 19.3,10.2 15,6.2",
        "M4,11 L7,11 8.5,6 11.2,16 13.4,8.3 14.7,11 18,11"
    ];
    private static readonly DrawingGroup[] Drawings = Make();
    private static DrawingGroup[] Make()
    {
        var result = new DrawingGroup[Shapes.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                var gradient = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(Top[i]), (Color)ColorConverter.ConvertFromString(Bottom[i]), 78);
                dc.DrawRoundedRectangle(gradient, new Pen(new SolidColorBrush(Color.FromArgb(43, 255, 255, 255)), 0.7), new Rect(1, 1, 20, 20), 5, 5);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 250, 245)), 1.45) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, Geometry.Parse(Shapes[i]));
            }
            drawing.Freeze();
            result[i] = drawing;
        }
        return result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 22, ActualHeight / 22));
        dc.DrawDrawing(Drawings[Math.Max(0, Variant) % Drawings.Length]);
        dc.Pop();
    }
}
