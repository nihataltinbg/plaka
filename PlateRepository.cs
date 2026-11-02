using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PlakaTakip;

public sealed class PlateRepository
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public PlateRepository(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Plates (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Plate       TEXT NOT NULL,
                DetectedAt  TEXT NOT NULL,
                CameraName  TEXT NOT NULL,
                Confidence  REAL NOT NULL,
                ImagePath   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Plates_Plate ON Plates(Plate);
            CREATE INDEX IF NOT EXISTS IX_Plates_DetectedAt ON Plates(DetectedAt);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Add(string plate, DateTime detectedAt, string cameraName, double confidence, string imagePath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Plates (Plate, DetectedAt, CameraName, Confidence, ImagePath)
            VALUES ($plate, $at, $camera, $conf, $img)
            """;
        cmd.Parameters.AddWithValue("$plate", plate);
        cmd.Parameters.AddWithValue("$at", detectedAt.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$camera", cameraName);
        cmd.Parameters.AddWithValue("$conf", confidence);
        cmd.Parameters.AddWithValue("$img", imagePath);
        cmd.ExecuteNonQuery();
    }

    public List<PlateRecord> Search(string? plateFilter, DateTime? from, DateTime? to, int limit = 1000)
    {
        var sql = new StringBuilder(
            "SELECT Id, Plate, DetectedAt, CameraName, Confidence, ImagePath FROM Plates WHERE 1=1");

        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var filter = new string((plateFilter ?? string.Empty).Where(char.IsLetterOrDigit).ToArray())
            .ToUpperInvariant();

        if (filter.Length > 0)
        {
            sql.Append(" AND REPLACE(Plate, ' ', '') LIKE $plate");
            cmd.Parameters.AddWithValue("$plate", $"%{filter}%");
        }

        if (from.HasValue)
        {
            sql.Append(" AND DetectedAt >= $from");
            cmd.Parameters.AddWithValue("$from", from.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
        }

        if (to.HasValue)
        {
            sql.Append(" AND DetectedAt <= $to");
            cmd.Parameters.AddWithValue("$to", to.Value.ToString(DateFormat, CultureInfo.InvariantCulture));
        }

        sql.Append(" ORDER BY DetectedAt DESC, Id DESC LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();

        var list = new List<PlateRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PlateRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTime.ParseExact(reader.GetString(2), DateFormat, CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetString(5)));
        }

        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
