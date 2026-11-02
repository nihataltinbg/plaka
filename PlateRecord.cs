namespace PlakaTakip;

public sealed record PlateRecord(
    long Id,
    string Plate,
    DateTime DetectedAt,
    string CameraName,
    double Confidence,
    string ImagePath);
