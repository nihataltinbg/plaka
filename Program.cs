namespace PlakaTakip;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // RTSP'yi TCP üzerinden aç: UDP'ye göre çok daha az bozuk kare / paket kaybı
        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
