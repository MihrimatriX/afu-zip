using System.Collections.ObjectModel;
using AfuZip.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfuZip.App.ViewModels;

/// <summary>Preset kartı: kullanıcı codec yerine amaç seçer.</summary>
/// <param name="MethodOverride">Yöntem bu 7z.dll'de yoksa kullanılacak olan (ana 7-Zip'te ZSTD → LZMA2).</param>
public sealed record PresetOption(CompressionPreset Preset, bool IsAvailable, string? MethodOverride = null)
{
    public string Name => Preset.DisplayName;
    public string Description => IsAvailable ? Preset.Description : $"{Preset.Description} (7-Zip ZS gerekir)";
    public string Detail => $"{(Preset.TarWrapped ? "tar." : "")}{Preset.Format.DisplayName().ToLowerInvariant()} · {MethodOverride ?? Preset.Method} · {Preset.Level}";
    public double Opacity => IsAvailable ? 1 : 0.45;
}

/// <summary>Biçim seçeneği ve o biçimde kullanılabilen yöntemler.</summary>
public sealed record FormatOption(string Label, string Extension, ArchiveFormat Format, bool TarWrapped, string[] Methods, int MaxLevel = 9)
{
    public bool CanEncrypt => Format is ArchiveFormat.SevenZip or ArchiveFormat.Zip && !TarWrapped;
    public bool CanEncryptHeaders => Format == ArchiveFormat.SevenZip;
    public bool CanSolid => Format == ArchiveFormat.SevenZip;
    public bool HasLevels => Format != ArchiveFormat.Tar;
    public override string ToString() => Label;
}

/// <summary>
/// "Arşiv oluştur" diyaloğu. Preset seçilince biçim, yöntem, seviye ve solid ayarlanır; bunlardan biri elle
/// değişirse seçim "özel" olur. Boyut ve süre tahmini kaynaklar bir kez taranarak (arka planda) hesaplanır.
/// </summary>
public sealed class CreateArchiveViewModel : ObservableObject
{
    private static readonly (string Label, long? Bytes)[] VolumeChoiceList =
    [
        ("Bölme", null),
        ("100 MB", 100L << 20),
        ("700 MB (CD)", 700L << 20),
        ("4 GB (FAT32)", (4L << 30) - (1L << 20)),
        ("Özel…", -1),
    ];

    private readonly bool _hasSevenZip;
    private IReadOnlyList<SourceItem> _collected = [];
    private CancellationTokenSource? _scanCts;
    private bool _applyingPreset;

    private int _selectedPresetIndex = 1;
    private int _selectedFormatIndex;
    private int _methodIndex;
    private double _level = 5;
    private bool _solid = true;
    private double _threads = double.NaN;
    private int _volumeIndex;
    private double _customVolumeMb = 1024;
    private string _targetPath = "";
    private string _password = "";
    private string _confirmPassword = "";
    private bool _encryptHeaders = true;
    private bool _openWhenDone = true;
    private string _estimateText = "";
    private string _validationMessage = "";
    private string _notice = "";

    public CreateArchiveViewModel(IEnumerable<string> sources, bool hasSevenZip, bool hasZstd)
    {
        _hasSevenZip = hasSevenZip;

        // 7z.dll yoksa yalnızca yönetilen motorun yazabildikleri: zip, tar, tar.gz.
        Formats = hasSevenZip
            ?
            [
                new("7z", ".7z", ArchiveFormat.SevenZip, false, ["LZMA2", "LZMA", "PPMd", "BZip2", .. hasZstd ? ["ZSTD"] : Array.Empty<string>(), "Copy"]),
                new("zip", ".zip", ArchiveFormat.Zip, false, ["Deflate", "Deflate64", "BZip2", "LZMA", "Copy"]),
                new("tar", ".tar", ArchiveFormat.Tar, false, ["Copy"], 0),
                new("tar.gz", ".tar.gz", ArchiveFormat.GZip, true, ["Deflate"]),
                new("tar.xz", ".tar.xz", ArchiveFormat.Xz, true, ["LZMA2"]),
                new("tar.bz2", ".tar.bz2", ArchiveFormat.BZip2, true, ["BZip2"]),
                .. hasZstd ? [new FormatOption("tar.zst", ".tar.zst", ArchiveFormat.Zstd, true, ["ZSTD"], 22)] : Array.Empty<FormatOption>(),
            ]
            :
            [
                new("zip", ".zip", ArchiveFormat.Zip, false, ["Deflate", "Copy"]),
                new("tar", ".tar", ArchiveFormat.Tar, false, ["Copy"], 0),
                new("tar.gz", ".tar.gz", ArchiveFormat.GZip, true, ["Deflate"]),
            ];

        Presets = [.. CompressionPreset.All.Select(p => new PresetOption(p, IsPresetAvailable(p, hasSevenZip, hasZstd),
            p.Method == "ZSTD" && !hasZstd ? "LZMA2" : null))];
        foreach (var method in Formats[0].Methods)
            Methods.Add(method);
        foreach (var source in sources)
            Sources.Add(source);
        Sources.CollectionChanged += (_, _) => OnSourcesChanged();

        var initial = Presets.FirstOrDefault(p => p.IsAvailable && p.Preset == CompressionPreset.Balanced) ?? Presets.First(p => p.IsAvailable);
        _targetPath = DefaultTarget(Sources, Formats[0].Extension);
        SelectedPresetIndex = Presets.ToList().IndexOf(initial);
        OnSourcesChanged();
    }

    public ObservableCollection<string> Sources { get; } = [];
    public IReadOnlyList<PresetOption> Presets { get; }
    public IReadOnlyList<FormatOption> Formats { get; }
    public ObservableCollection<string> Methods { get; } = [];
    /// <summary>Yönetilen motor ciltlere bölemez; 7z.dll yoksa yalnızca "Bölme".</summary>
    public IReadOnlyList<string> VolumeChoices => _hasSevenZip ? [.. VolumeChoiceList.Select(v => v.Label)] : [VolumeChoiceList[0].Label];

    /// <summary>-1: özel ayarlar.</summary>
    public int SelectedPresetIndex
    {
        get => _selectedPresetIndex;
        set
        {
            if (value >= 0 && !Presets[value].IsAvailable)
                value = _selectedPresetIndex; // kullanılamayan preset seçilemez
            if (!SetProperty(ref _selectedPresetIndex, value) && value < 0)
                return;
            if (value >= 0)
                ApplyPreset(Presets[value].Preset);
            OnPropertyChanged(nameof(PresetSummary));
        }
    }

    public string PresetSummary => _selectedPresetIndex >= 0 ? Presets[_selectedPresetIndex].Preset.Description : "Özel ayarlar";

    public int SelectedFormatIndex
    {
        get => _selectedFormatIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _selectedFormatIndex, value))
                return;
            var format = Format;
            Methods.Clear();
            foreach (var method in format.Methods)
                Methods.Add(method);
            SetMethodIndex(0);
            if (_level > format.MaxLevel)
                Level = format.MaxLevel;
            TargetPath = ReplaceExtension(_targetPath, format.Extension);
            OnPropertyChanged(nameof(Format));
            OnPropertyChanged(nameof(MaxLevel));
            OnPropertyChanged(nameof(CanEncrypt));
            OnPropertyChanged(nameof(CanEncryptHeaders));
            OnPropertyChanged(nameof(CanSolid));
            OnPropertyChanged(nameof(HasLevels));
            MarkCustom();
        }
    }

    public FormatOption Format => Formats[Math.Clamp(_selectedFormatIndex, 0, Formats.Count - 1)];
    public double MaxLevel => Format.MaxLevel;
    public bool CanEncrypt => Format.CanEncrypt;
    public bool CanEncryptHeaders => Format.CanEncryptHeaders;
    public bool CanSolid => Format.CanSolid;
    public bool HasLevels => Format.HasLevels;

    public int MethodIndex
    {
        get => _methodIndex;
        set
        {
            if (value >= 0 && SetProperty(ref _methodIndex, value))
                MarkCustom();
        }
    }

    public double Level
    {
        get => _level;
        set
        {
            if (SetProperty(ref _level, Math.Round(Math.Clamp(value, 0, MaxLevel))))
                MarkCustom();
        }
    }

    public bool Solid
    {
        get => _solid;
        set
        {
            if (SetProperty(ref _solid, value))
                MarkCustom();
        }
    }

    /// <summary>NaN: otomatik (tüm çekirdekler).</summary>
    public double Threads
    {
        get => _threads;
        set
        {
            if (SetProperty(ref _threads, value))
                UpdateEstimate();
        }
    }

    public int VolumeIndex
    {
        get => _volumeIndex;
        set
        {
            if (value >= 0 && SetProperty(ref _volumeIndex, value))
            {
                OnPropertyChanged(nameof(IsCustomVolume));
                Validate();
            }
        }
    }

    public bool IsCustomVolume => VolumeChoiceList[_volumeIndex].Bytes == -1;

    public double CustomVolumeMb
    {
        get => _customVolumeMb;
        set
        {
            if (SetProperty(ref _customVolumeMb, value))
                Validate();
        }
    }

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value))
                Validate();
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                OnPropertyChanged(nameof(HasPassword));
                Validate();
            }
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (SetProperty(ref _confirmPassword, value))
                Validate();
        }
    }

    public bool HasPassword => _password.Length > 0;
    public bool EncryptHeaders { get => _encryptHeaders; set => SetProperty(ref _encryptHeaders, value); }
    public bool OpenWhenDone { get => _openWhenDone; set => SetProperty(ref _openWhenDone, value); }
    public string EstimateText { get => _estimateText; private set => SetProperty(ref _estimateText, value); }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public bool HasValidationMessage => _validationMessage.Length > 0;

    /// <summary>Engellemeyen uyarı: hedef zaten var (üzerine yazılır), seçilen biçim parolayı kullanmaz.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;
    public bool IsValid => _validationMessage.Length == 0 && Sources.Count > 0;

    /// <summary>Seçili ayarlarla preset (özel ise elle girilenler).</summary>
    public CompressionPreset BuildPreset()
    {
        var format = Format;
        var method = Methods.Count > 0 ? Methods[Math.Clamp(_methodIndex, 0, Methods.Count - 1)] : "Copy";
        var basePreset = _selectedPresetIndex >= 0 ? Presets[_selectedPresetIndex].Preset : CompressionPreset.Balanced;
        return basePreset with
        {
            Id = _selectedPresetIndex >= 0 ? basePreset.Id : "custom",
            Format = format.Format,
            TarWrapped = format.TarWrapped,
            Method = method,
            Level = format.HasLevels ? (int)_level : 0,
            Solid = format.CanSolid && _solid,
        };
    }

    public CreateRequest BuildRequest()
    {
        var volume = VolumeChoiceList[_volumeIndex].Bytes switch
        {
            -1 => (long)(_customVolumeMb * (1 << 20)),
            var bytes => bytes,
        };
        var password = CanEncrypt && HasPassword ? _password : null;
        return new CreateRequest(
            Path.GetFullPath(_targetPath),
            [.. Sources],
            BuildPreset(),
            password,
            EncryptHeaders: password is not null && CanEncryptHeaders && _encryptHeaders,
            VolumeSize: volume,
            Threads: double.IsNaN(_threads) ? null : (int)_threads);
    }

    // --- İç işler -----------------------------------------------------------------------------------

    private void ApplyPreset(CompressionPreset preset)
    {
        _applyingPreset = true;
        try
        {
            var index = Formats.ToList().FindIndex(f => f.Format == preset.Format && f.TarWrapped == preset.TarWrapped);
            if (index >= 0)
                SelectedFormatIndex = index;
            SetMethodIndex(Math.Max(0, Methods.IndexOf(preset.Method)));
            Level = preset.Level;
            Solid = preset.Solid;
        }
        finally
        {
            _applyingPreset = false;
        }
        UpdateEstimate();
        Validate();
    }

    /// <summary>Liste yeniden doldurulunca ComboBox seçimi düşer; değer aynı olsa da bildirilir.</summary>
    private void SetMethodIndex(int index)
    {
        _methodIndex = index;
        OnPropertyChanged(nameof(MethodIndex));
    }

    private void MarkCustom()
    {
        if (!_applyingPreset && _selectedPresetIndex >= 0)
        {
            _selectedPresetIndex = -1;
            OnPropertyChanged(nameof(SelectedPresetIndex));
            OnPropertyChanged(nameof(PresetSummary));
        }
        if (!_applyingPreset)
        {
            UpdateEstimate();
            Validate();
        }
    }

    private async void OnSourcesChanged()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        Validate();
        if (Sources.Count == 0)
        {
            _collected = [];
            EstimateText = "";
            return;
        }

        EstimateText = "Dosyalar taranıyor…";
        var sources = Sources.ToList();
        try
        {
            var collected = await Task.Run(() => SourceCollector.Collect(sources), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _collected = collected;
            UpdateEstimate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!cts.IsCancellationRequested)
                EstimateText = $"Taranamadı: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateEstimate()
    {
        if (_collected.Count == 0)
            return;
        var estimate = CompressionEstimator.Estimate(_collected, BuildPreset(), double.IsNaN(_threads) ? null : (int)_threads);
        var time = estimate.EstimatedTime.TotalSeconds switch
        {
            < 2 => "birkaç saniye",
            < 90 => $"≈ {estimate.EstimatedTime.TotalSeconds:0} sn",
            < 5400 => $"≈ {estimate.EstimatedTime.TotalMinutes:0} dk",
            _ => $"≈ {estimate.EstimatedTime.TotalHours:0.#} saat",
        };
        var note = estimate.AlreadyCompressedBytes > estimate.InputBytes / 2
            ? " · içeriğin çoğu zaten sıkıştırılmış (video, resim, arşiv); hızlı preset yeterli"
            : "";
        EstimateText = $"{estimate.Files:N0} dosya · {ByteSize.Format(estimate.InputBytes)} → ≈ {ByteSize.Format(estimate.EstimatedBytes)} " +
                       $"({ByteSize.FormatRatio(estimate.Ratio)}) · {time}{note}";
    }

    private void Validate()
    {
        ValidationMessage = Sources.Count == 0 ? "Sıkıştırılacak dosya veya klasör ekleyin."
            : string.IsNullOrWhiteSpace(_targetPath) ? "Arşivin kaydedileceği yeri seçin."
            : !Path.IsPathFullyQualified(_targetPath) ? "Arşiv yolu tam bir yol olmalı (ör. C:\\Yedek\\proje.7z)."
            : Sources.Any(s => string.Equals(Path.GetFullPath(s), Path.GetFullPath(_targetPath), StringComparison.OrdinalIgnoreCase))
                ? "Arşiv, sıkıştırılan dosyalardan birinin üzerine yazılamaz."
            : HasPassword && CanEncrypt && _password != _confirmPassword ? "Parolalar aynı değil."
            : IsCustomVolume && _customVolumeMb is not (>= 1) ? "Cilt boyutu en az 1 MB olmalı."
            : "";
        OnPropertyChanged(nameof(IsValid));

        Notice = TargetExists() ? "Bu adda bir arşiv zaten var; Oluştur'a basınca üzerine yazılacak."
            : HasPassword && !CanEncrypt ? $"{Format.Label} biçimi şifrelemeyi desteklemiyor; parola kullanılmayacak (7z veya zip seçin)."
            : "";
    }

    private bool TargetExists()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_targetPath) || !Path.IsPathFullyQualified(_targetPath))
                return false;
            // Kaydetme seçicisinin bıraktığı boş dosya sayılmaz (kullanıcı orada zaten "değiştir" dedi).
            var file = new FileInfo(_targetPath);
            return (file.Exists && file.Length > 0) || File.Exists(_targetPath + ".001");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsPresetAvailable(CompressionPreset preset, bool hasSevenZip, bool hasZstd) =>
        hasSevenZip
            ? hasZstd || preset.Format != ArchiveFormat.Zstd // Hızlı preset (7z + ZSTD) ana 7-Zip'te LZMA2'ye düşer
            : preset.Format is ArchiveFormat.Zip or ArchiveFormat.Tar || (preset.Format == ArchiveFormat.GZip && preset.TarWrapped);

    private static string DefaultTarget(IEnumerable<string> sources, string extension)
    {
        var first = sources.FirstOrDefault();
        if (first is null)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Arşiv" + extension);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var folder = Path.GetDirectoryName(full) ?? full;
        // Tek kaynak: onun adı; birden fazla: bulundukları klasörün adı.
        var name = sources.Skip(1).Any()
            ? Path.GetFileName(folder)
            : Directory.Exists(full) ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
        return Path.Combine(folder, (string.IsNullOrEmpty(name) ? "Arşiv" : name) + extension);
    }

    internal static string ReplaceExtension(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        string[] known = [".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".tgz", ".txz", ".7z", ".zip", ".tar", ".wim"];
        var trimmed = known.FirstOrDefault(k => path.EndsWith(k, StringComparison.OrdinalIgnoreCase)) is { } match
            ? path[..^match.Length]
            : path;
        return trimmed + extension;
    }
}
