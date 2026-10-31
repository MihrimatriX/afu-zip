namespace AfuZip.App.Services;

/// <summary>
/// Hata günlüğü: %LOCALAPPDATA%\AfuZip\logs\afuzip.log. Durum çubuğundaki kısa mesajın arkasındaki ayrıntı
/// (istisna türü, HRESULT, yığın) burada kalır; hata bildirirken bu dosya eklenebilir. 1 MB'ı geçince yenilenir.
/// </summary>
internal static class AppLog
{
    private static readonly Lock Gate = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfuZip", "logs");

    public static string FilePath => Path.Combine(Folder, "afuzip.log");

    public static void Error(string context, Exception ex) => Write($"HATA {context}\n{ex}");

    public static void Info(string message) => Write(message);

    /// <summary>Kullanıcıya gösterilecek kısa metin: boş ya da genel COM mesajlarında HRESULT eklenir.</summary>
    public static string Describe(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException
            ? $"{ex.Message.Trim()} (0x{ex.HResult:X8}, ayrıntı: günlük dosyası)"
            : ex.Message;

    private static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1 << 20)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}\n\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
