using System.Collections.Concurrent;
using AfuZip.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AfuZip.App.Services;

/// <summary>
/// Parolayı ContentDialog ile sorar. Motor callback'leri arka plan thread'inde bekler; diyalog UI thread'inde
/// açılır. Aynı anda yalnızca bir ContentDialog açılabildiği için istekler sıraya alınır.
/// Doğru parola oturum boyunca arşiv yolu başına hatırlanır; motor reddederse bir sonraki istekte tekrar sorulur.
/// </summary>
public sealed class UiPasswordProvider : IPasswordProvider
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private DispatcherQueue? _dispatcher;
    private Func<XamlRoot?>? _xamlRoot;

    public void Attach(DispatcherQueue dispatcher, Func<XamlRoot?> xamlRoot)
    {
        _dispatcher = dispatcher;
        _xamlRoot = xamlRoot;
    }

    public async ValueTask<string?> GetPasswordAsync(string archivePath, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(archivePath, out var cached))
            return cached;
        if (_dispatcher is null)
            return null;

        await _dialogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Beklerken başka bir görev aynı arşiv için parolayı almış olabilir.
            if (_cache.TryGetValue(archivePath, out cached))
                return cached;

            var wasRejected = _rejected.TryRemove(archivePath, out _);
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    answer.TrySetResult(await ShowDialogAsync(archivePath, wasRejected));
                }
                catch (Exception ex)
                {
                    answer.TrySetException(ex);
                }
            });
            if (!queued)
                return null;

            var password = await answer.Task.WaitAsync(ct).ConfigureAwait(false);
            if (password is not null)
                _cache[archivePath] = password;
            return password;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public void OnPasswordRejected(string archivePath)
    {
        _cache.TryRemove(archivePath, out _);
        _rejected[archivePath] = true;
    }

    private async Task<string?> ShowDialogAsync(string archivePath, bool wasRejected)
    {
        var root = _xamlRoot?.Invoke();
        if (root is null)
            return null;

        var box = new PasswordBox { PlaceholderText = "Parola", PasswordRevealMode = PasswordRevealMode.Peek };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        if (wasRejected)
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "Parola yanlış",
                Message = "Tekrar deneyin.",
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = $"\"{Path.GetFileName(archivePath)}\" şifreli. Açmak için parolayı girin.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Parola gerekli",
            Content = panel,
            PrimaryButtonText = "Aç",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }
}
