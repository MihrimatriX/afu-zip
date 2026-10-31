namespace AfuZip.Core;

/// <summary>
/// Uygulamanın arşiv açma giriş noktası. Motor yönlendirmesine ek olarak tek dosyalık sarmalayıcıları
/// (tar.xz, tar.zst, .7z.001 parçaları) otomatik açar: kullanıcı "x.tar" değil tar'ın içeriğini görür.
/// </summary>
public sealed class ArchiveOpener(EngineRouter router)
{
    public EngineRouter Router => router;

    public async Task<IArchiveReader> OpenAsync(string archivePath, OpenOptions? options = null, CancellationToken ct = default)
    {
        var reader = await router.OpenAsync(archivePath, options, ct).ConfigureAwait(false);
        if (!ShouldUnwrap(reader))
            return reader;

        try
        {
            return await NestedArchive.OpenAsync(router, reader, 0, options, ownsOuter: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or AggregateException or IOException
                                   && ex is not ArchivePasswordException)
        {
            // İçteki dosya aslında arşiv değilse dış arşivi olduğu gibi göster.
            return reader;
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Arşiv içindeki bir arşivi açar; dış arşiv açık kalır (geri gezinmek için).</summary>
    public Task<IArchiveReader> OpenNestedAsync(IArchiveReader outer, int index, OpenOptions? options = null, CancellationToken ct = default) =>
        NestedArchive.OpenAsync(router, outer, index, options, ownsOuter: false, ct);

    internal static bool ShouldUnwrap(IArchiveReader reader) =>
        reader.Entries.Count == 1 &&
        (reader.Format.IsSingleStream() || reader.FormatName.Equals("Split", StringComparison.OrdinalIgnoreCase)) &&
        NestedArchive.LooksLikeArchive(reader.Entries[0]);
}
