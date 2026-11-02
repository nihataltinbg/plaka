using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mat = OpenCvSharp.Mat;
using Rect = OpenCvSharp.Rect;

namespace PlakaTakip;

public sealed class MainForm : Form
{
    private sealed record Overlay(Rectangle Bounds, string Plate, DateTime At);

    // ---- Servisler ----
    private readonly CameraService _camera = new();
    private readonly PlateDetector _detector = new();
    private PlateReader? _reader;
    private readonly PlateRepository _repo;
    private readonly string _imageDir;

    private CancellationTokenSource? _processCts;
    private Task? _processTask;

    // ---- Ayarlar (UI thread'inde güncellenir, işleme thread'inde okunur) ----
    private volatile int _minConfidencePct = 60;
    private volatile int _cooldownSeconds = 30;
    private volatile string _cameraName = "Kamera 1";
    private volatile List<Overlay> _overlays = new();

    // ---- Tekrar kaydı engelleme / doğrulama (yalnızca işleme thread'i kullanır) ----
    private readonly Dictionary<string, (int Count, DateTime First)> _votes = new();
    private readonly Dictionary<string, DateTime> _lastSaved = new();

    private int _uiBusy;
    private List<PlateRecord> _currentRecords = new();

    // ---- Kontroller ----
    private readonly TextBox _txtCameraName = new() { Text = "Kamera 1", Width = 100 };
    private readonly TextBox _txtUrl = new() { Text = "rtsp://admin:sifre@192.168.1.64:554/Streaming/Channels/101", Width = 430 };
    private readonly Button _btnStart = new() { Text = "Başlat", AutoSize = true };
    private readonly Button _btnStop = new() { Text = "Durdur", AutoSize = true, Enabled = false };
    private readonly NumericUpDown _numConfidence = new() { Minimum = 0, Maximum = 100, Value = 60, Width = 55 };
    private readonly NumericUpDown _numCooldown = new() { Minimum = 1, Maximum = 3600, Value = 30, Width = 60 };
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly TextBox _txtSearch = new() { Width = 150, PlaceholderText = "Plaka ara (örn. 34ABC)" };
    private readonly DateTimePicker _dtFrom = new() { Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private readonly DateTimePicker _dtTo = new() { Width = 110, Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private readonly Button _btnSearch = new() { Text = "Ara", AutoSize = true };
    private readonly Button _btnExport = new() { Text = "CSV'ye aktar", AutoSize = true };
    private readonly Label _lblCount = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private readonly ToolStripStatusLabel _lblStatus = new() { Text = "Hazır" };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window
    };

    public MainForm()
    {
        Text = "Plaka Takip Sistemi";
        Width = 1300;
        Height = 760;
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PlakaTakip");
        _imageDir = Path.Combine(dataDir, "Goruntuler");
        Directory.CreateDirectory(_imageDir);
        _repo = new PlateRepository(Path.Combine(dataDir, "plaka.db"));

        BuildUi();

        _camera.FrameReady += OnFrame;
        _camera.StatusChanged += SetStatus;

        RefreshGrid();
    }

    // =====================================================================
    //  Arayüz
    // =====================================================================
    private void BuildUi()
    {
        var topBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        topBar.Controls.AddRange(new Control[]
        {
            MakeLabel("Kamera adı:"), _txtCameraName,
            MakeLabel("IP kamera adresi:"), _txtUrl,
            _btnStart, _btnStop,
            MakeLabel("Güven eşiği %:"), _numConfidence,
            MakeLabel("Aynı plakayı tekrar kaydetme (sn):"), _numCooldown
        });

        var searchBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        searchBar.Controls.AddRange(new Control[]
        {
            _txtSearch,
            MakeLabel("Başlangıç:"), _dtFrom,
            MakeLabel("Bitiş:"), _dtTo,
            _btnSearch, _btnExport, _lblCount
        });

        _grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Plaka", DataPropertyName = nameof(PlateRecord.Plate), FillWeight = 22 },
            new DataGridViewTextBoxColumn { HeaderText = "Tarih / Saat", DataPropertyName = nameof(PlateRecord.DetectedAt), FillWeight = 32, DefaultCellStyle = { Format = "dd.MM.yyyy HH:mm:ss" } },
            new DataGridViewTextBoxColumn { HeaderText = "Kamera", DataPropertyName = nameof(PlateRecord.CameraName), FillWeight = 26 },
            new DataGridViewTextBoxColumn { HeaderText = "Güven", DataPropertyName = nameof(PlateRecord.Confidence), FillWeight = 14, DefaultCellStyle = { Format = "P0" } });

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_grid);      // Fill önce eklenir
        right.Controls.Add(searchBar);  // Top sonra

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_picture, 0, 0);
        layout.Controls.Add(right, 1, 0);

        var status = new StatusStrip();
        status.Items.Add(_lblStatus);

        Controls.Add(layout);
        Controls.Add(topBar);
        Controls.Add(status);

        _btnStart.Click += (_, _) => StartCamera();
        _btnStop.Click += (_, _) => StopCamera();
        _btnSearch.Click += (_, _) => RefreshGrid();
        _btnExport.Click += (_, _) => ExportCsv();
        _txtSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            RefreshGrid();
        };
        _numConfidence.ValueChanged += (_, _) => _minConfidencePct = (int)_numConfidence.Value;
        _numCooldown.ValueChanged += (_, _) => _cooldownSeconds = (int)_numCooldown.Value;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _currentRecords.Count) return;
            var path = _currentRecords[e.RowIndex].ImagePath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(8, 8, 2, 0) };

    // =====================================================================
    //  Başlat / Durdur
    // =====================================================================
    private void StartCamera()
    {
        try
        {
            _reader ??= new PlateReader(Path.Combine(AppContext.BaseDirectory, "tessdata"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "OCR motoru başlatılamadı. 'tessdata' klasöründe eng.traineddata dosyası olduğundan emin olun.\n\n" + ex.Message,
                "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var source = _txtUrl.Text.Trim();
        if (source.Length == 0)
        {
            MessageBox.Show("Kamera adresini girin.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cameraName = string.IsNullOrWhiteSpace(_txtCameraName.Text) ? "Kamera" : _txtCameraName.Text.Trim();
        _minConfidencePct = (int)_numConfidence.Value;
        _cooldownSeconds = (int)_numCooldown.Value;

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;

        _camera.Start(source);

        _processCts = new CancellationTokenSource();
        var token = _processCts.Token;
        _processTask = Task.Run(() => ProcessLoop(token));
    }

    private void StopCamera()
    {
        _processCts?.Cancel();
        try { _processTask?.Wait(5000); } catch (AggregateException) { }
        _processCts = null;
        _processTask = null;

        _camera.Stop();

        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "Durduruldu";
    }

    // =====================================================================
    //  Plaka işleme (arka plan thread'i)
    // =====================================================================
    private void ProcessLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var frame = _camera.TakeLatestFrame();
            if (frame is null)
            {
                if (ct.WaitHandle.WaitOne(100)) break;
                continue;
            }

            try
            {
                ProcessFrame(frame);
            }
            catch (Exception ex)
            {
                SetStatus("İşleme hatası: " + ex.Message);
            }

            if (ct.WaitHandle.WaitOne(120)) break;
        }
    }

    private void ProcessFrame(Mat frame)
    {
        var reader = _reader;
        if (reader is null) return;

        var found = new List<Overlay>();
        var now = DateTime.Now;

        foreach (var candidate in _detector.Detect(frame))
        {
            using (candidate)
            {
                var (text, confidence) = reader.Read(candidate.Image);
                if (confidence * 100 < _minConfidencePct) continue;
                if (!TurkishPlate.TryNormalize(text, out var plate)) continue;

                var b = candidate.Bounds;
                found.Add(new Overlay(new Rectangle(b.X, b.Y, b.Width, b.Height), TurkishPlate.Format(plate), now));

                if (ShouldSave(plate, now))
                    SaveDetection(frame, candidate.Bounds, plate, confidence, now);
            }
        }

        if (found.Count > 0) _overlays = found;
    }

    /// <summary>Aynı plaka 3 sn içinde en az 2 kez okunmalı (yanlış okumaları eler) ve bekleme süresi dolmuş olmalı.</summary>
    private bool ShouldSave(string plate, DateTime now)
    {
        if (_lastSaved.TryGetValue(plate, out var savedAt) && (now - savedAt).TotalSeconds < _cooldownSeconds)
            return false;

        if (!_votes.TryGetValue(plate, out var vote) || (now - vote.First).TotalSeconds > 3)
            vote = (0, now);

        vote = (vote.Count + 1, vote.First);
        if (vote.Count < 2)
        {
            _votes[plate] = vote;
            return false;
        }

        _votes.Remove(plate);
        _lastSaved[plate] = now;

        if (_lastSaved.Count > 500)
        {
            foreach (var key in _lastSaved.Where(kv => (now - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList())
                _lastSaved.Remove(key);
        }

        return true;
    }

    private void SaveDetection(Mat frame, Rect box, string plate, float confidence, DateTime at)
    {
        var dayDir = Path.Combine(_imageDir, at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dayDir);
        var path = Path.Combine(dayDir, $"{at:HHmmss}_{plate}.jpg");

        SnapshotWriter.Save(frame, box, path);
        _repo.Add(TurkishPlate.Format(plate), at, _cameraName, confidence, path);

        SetStatus($"Kaydedildi: {TurkishPlate.Format(plate)}");
        BeginInvokeSafe(RefreshGrid);
    }

    // =====================================================================
    //  Canlı görüntü
    // =====================================================================
    private void OnFrame(Bitmap bmp)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            bmp.Dispose();
            return;
        }

        DrawOverlays(bmp);

        // UI yetişemiyorsa kareyi at (kuyruk birikmesin)
        if (Interlocked.Exchange(ref _uiBusy, 1) == 1)
        {
            bmp.Dispose();
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();
                Volatile.Write(ref _uiBusy, 0);
            });
        }
        catch (InvalidOperationException)
        {
            bmp.Dispose();
            Volatile.Write(ref _uiBusy, 0);
        }
    }

    private void DrawOverlays(Bitmap bmp)
    {
        var overlays = _overlays;
        if (overlays.Count == 0) return;

        var now = DateTime.Now;
        using var g = Graphics.FromImage(bmp);
        using var pen = new Pen(Color.Lime, Math.Max(3, bmp.Width / 400));
        using var font = new Font("Segoe UI", Math.Max(14f, bmp.Width / 60f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));

        foreach (var o in overlays)
        {
            if ((now - o.At).TotalSeconds > 2) continue; // eski tespitleri gösterme

            g.DrawRectangle(pen, o.Bounds);
            var size = g.MeasureString(o.Plate, font);
            var y = Math.Max(0f, o.Bounds.Y - size.Height);
            g.FillRectangle(bg, o.Bounds.X, y, size.Width, size.Height);
            g.DrawString(o.Plate, font, Brushes.Lime, o.Bounds.X, y);
        }
    }

    // =====================================================================
    //  Kayıtlar: arama + CSV
    // =====================================================================
    private void RefreshGrid()
    {
        try
        {
            DateTime? from = _dtFrom.Checked ? _dtFrom.Value.Date : null;
            DateTime? to = _dtTo.Checked ? _dtTo.Value.Date.AddDays(1).AddSeconds(-1) : null;

            _currentRecords = _repo.Search(_txtSearch.Text, from, to);
            _grid.DataSource = _currentRecords;
            _lblCount.Text = $"{_currentRecords.Count} kayıt";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Kayıtlar okunamadı: " + ex.Message;
        }
    }

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"plaka_kayitlari_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("Id;Plaka;Tarih;Kamera;Güven (%);Görüntü");
        foreach (var r in _currentRecords)
        {
            sb.AppendLine(string.Join(';',
                r.Id,
                Csv(r.Plate),
                r.DetectedAt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(r.CameraName),
                (r.Confidence * 100).ToString("0", CultureInfo.InvariantCulture),
                Csv(r.ImagePath)));
        }

        // UTF-8 BOM: Excel Türkçe karakterleri doğru açsın
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        _lblStatus.Text = "CSV kaydedildi: " + dlg.FileName;
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // =====================================================================
    //  Yardımcılar
    // =====================================================================
    private void SetStatus(string text) => BeginInvokeSafe(() => _lblStatus.Text = text);

    private void BeginInvokeSafe(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopCamera();
        _reader?.Dispose();
        _camera.Dispose();
        base.OnFormClosing(e);
    }
}
