using OpenCvSharp;
using Tesseract;

namespace PlakaTakip;

/// <summary>Kırpılmış plaka görüntüsünü Tesseract ile okur. Tek thread'den kullanın.</summary>
public sealed class PlateReader : IDisposable
{
    private readonly TesseractEngine _engine;

    public PlateReader(string tessDataPath)
    {
        _engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
        _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        _engine.DefaultPageSegMode = PageSegMode.SingleLine;
    }

    /// <returns>Ham metin ve ortalama güven (0..1)</returns>
    public (string Text, float Confidence) Read(Mat plate)
    {
        if (plate.Empty()) return (string.Empty, 0f);

        using var prepared = Preprocess(plate);
        Cv2.ImEncode(".png", prepared, out var bytes);

        using var pix = Pix.LoadFromMemory(bytes);
        using var page = _engine.Process(pix);
        return (page.GetText().Trim(), page.GetMeanConfidence());
    }

    private static Mat Preprocess(Mat plate)
    {
        using var gray = new Mat();
        if (plate.Channels() == 1) plate.CopyTo(gray);
        else Cv2.CvtColor(plate, gray, ColorConversionCodes.BGR2GRAY);

        // OCR için sabit yükseklik
        const int targetHeight = 100;
        var scale = targetHeight / (double)gray.Height;
        using var resized = new Mat();
        Cv2.Resize(gray, resized,
            new OpenCvSharp.Size(Math.Max(1, (int)(gray.Width * scale)), targetHeight),
            0, 0, InterpolationFlags.Cubic);

        using var binary = new Mat();
        Cv2.Threshold(resized, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        // Beyaz zemin üzerinde siyah yazı olacak şekilde ayarla
        if (Cv2.Mean(binary).Val0 < 127)
            Cv2.BitwiseNot(binary, binary);

        var padded = new Mat();
        Cv2.CopyMakeBorder(binary, padded, 12, 12, 12, 12, BorderTypes.Constant, Scalar.White);
        return padded;
    }

    public void Dispose() => _engine.Dispose();
}
