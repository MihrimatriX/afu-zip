using System.Text;
using AfuZip.Core;

namespace AfuZip.Cli;

/// <summary>
/// --password verilmişse onu kullanır; verilmemişse ve konsol etkileşimliyse gizli girişle sorar.
/// Yanlış parola bildirildiğinde bir sonraki istekte tekrar sorar (en fazla 3 deneme).
/// </summary>
internal sealed class ConsolePasswordProvider(string? fixedPassword) : IPasswordProvider
{
    private const int MaxAttempts = 3;
    private string? _cached = fixedPassword;
    private int _attempts;

    public ValueTask<string?> GetPasswordAsync(string archivePath, CancellationToken ct = default)
    {
        if (_cached is not null)
            return ValueTask.FromResult<string?>(_cached);
        if (Console.IsInputRedirected || _attempts >= MaxAttempts)
            return ValueTask.FromResult<string?>(null);

        _attempts++;
        lock (Console.Out)
        {
            Console.Error.Write($"'{Path.GetFileName(archivePath)}' parolası: ");
            _cached = ReadHidden();
            Console.Error.WriteLine();
        }
        return ValueTask.FromResult<string?>(_cached.Length == 0 ? null : _cached);
    }

    public void OnPasswordRejected(string archivePath)
    {
        _cached = null;
        if (!Console.IsInputRedirected)
            Console.Error.WriteLine("Parola yanlış.");
    }

    private static string ReadHidden()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                return buffer.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
