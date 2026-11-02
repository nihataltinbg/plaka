using OpenCvSharp;

namespace PlakaTakip;

public static class SnapshotWriter
{
    /// <summary>Tüm kareyi, plakanın etrafına yeşil çerçeve çizerek JPEG olarak kaydeder.</summary>
    public static void Save(Mat frame, Rect plateBounds, string path)
    {
        using var annotated = frame.Clone();
        Cv2.Rectangle(annotated, plateBounds, new Scalar(0, 255, 0), 3);

        // ImWrite yerine ImEncode + File.WriteAllBytes: yolda Türkçe karakter (Çağrı, Şule...) olsa da çalışır
        Cv2.ImEncode(".jpg", annotated, out var bytes, new[] { (int)ImwriteFlags.JpegQuality, 85 });
        File.WriteAllBytes(path, bytes);
    }
}
