using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Aerochat.Connectivity;

public sealed class DpapiTokenCache : ITokenCache
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Aerochat.Session.v1");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    private readonly string _path;

    public DpapiTokenCache(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aerochat", "session.bin")
            : path;
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            byte[] protectedToken = await File.ReadAllBytesAsync(_path, cancellationToken);
            byte[] tokenBytes = ProtectedData.Unprotect(protectedToken, Entropy, DataProtectionScope.CurrentUser);
            string token = StrictUtf8.GetString(tokenBytes);
            if (string.IsNullOrWhiteSpace(token))
                throw new DecoderFallbackException();

            return token;
        }
        catch (CryptographicException)
        {
            RemoveCorruptFile();
            return null;
        }
        catch (DecoderFallbackException)
        {
            RemoveCorruptFile();
            return null;
        }
    }

    public async Task SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        byte[] protectedToken = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token),
            Entropy,
            DataProtectionScope.CurrentUser);
        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        await SaveGate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedToken, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            finally
            {
                SaveGate.Release();
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }

    private void RemoveCorruptFile()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
