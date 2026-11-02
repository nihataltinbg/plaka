using OpenCvSharp;

namespace PlakaTakip;

public sealed class PlateCandidate : IDisposable
{
    public required Rect Bounds { get; init; }   // Orijinal kare koordinatlarında
    public required Mat Image { get; init; }     // Kırpılmış plaka görüntüsü

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Klasik görüntü işleme ile plaka adayı bulur: gri → bilateral filtre → Canny → 4 köşeli dikdörtgen konturlar.
/// </summary>
public sealed class PlateDetector
{
    private const int MaxProcessingWidth = 1280;
    private const int MaxCandidates = 5;

    public List<PlateCandidate> Detect(Mat frame)
    {
        var result = new List<PlateCandidate>();
        if (frame.Empty()) return result;

        // Hız için büyük kareleri küçült
        var scale = frame.Width > MaxProcessingWidth ? MaxProcessingWidth / (double)frame.Width : 1.0;

        using var small = new Mat();
        if (scale < 1.0)
            Cv2.Resize(frame, small, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        else
            frame.CopyTo(small);

        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

        using var smooth = new Mat();
        Cv2.BilateralFilter(gray, smooth, 11, 17, 17);

        using var edges = new Mat();
        Cv2.Canny(smooth, edges, 30, 200);

        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var potential = new List<Rect>();
        foreach (var contour in contours)
        {
            if (contour.Length < 8) continue;

            // Ucuz filtreler önce
            var box = Cv2.BoundingRect(contour);
            if (box.Width < 60 || box.Height < 16) continue;
            if (box.Width > small.Width * 0.6) continue;

            var aspect = box.Width / (double)box.Height;
            if (aspect < 2.2 || aspect > 6.5) continue; // Türk plakası ≈ 4.7

            var perimeter = Cv2.ArcLength(contour, true);
            var approx = Cv2.ApproxPolyDP(contour, 0.02 * perimeter, true);
            if (approx.Length != 4 || !Cv2.IsContourConvex(approx)) continue;

            var fill = Cv2.ContourArea(approx) / (box.Width * (double)box.Height);
            if (fill < 0.75) continue;

            potential.Add(box);
        }

        var frameBounds = new Rect(0, 0, frame.Width, frame.Height);

        foreach (var box in potential.OrderByDescending(b => b.Width * b.Height))
        {
            var original = new Rect(
                (int)(box.X / scale),
                (int)(box.Y / scale),
                (int)(box.Width / scale),
                (int)(box.Height / scale));

            original = Rect.Intersect(original, frameBounds);
            if (original.Width < 40 || original.Height < 12) continue;
            if (result.Any(r => Overlaps(r.Bounds, original))) continue;

            result.Add(new PlateCandidate
            {
                Bounds = original,
                Image = new Mat(frame, original).Clone()
            });

            if (result.Count >= MaxCandidates) break;
        }

        return result;
    }

    private static bool Overlaps(Rect a, Rect b)
    {
        var i = Rect.Intersect(a, b);
        if (i.Width <= 0 || i.Height <= 0) return false;

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return (double)(i.Width * i.Height) / smaller > 0.5;
    }
}
