using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaDebrid_cli.Serialization;
using Tmds.DBus;

namespace MediaDebrid_cli.SecretsManager;

[UnsupportedOSPlatform("windows")]
public sealed class SecureStorageLinux : ISecureStorage, IDisposable
{
    private const string ServiceName = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string CollectionPath = "/org/freedesktop/secrets/collection/login";

    private readonly string _fallbackFilePath;
    private readonly byte[] _fallbackKey;
    private readonly string _lockFilePath;
    private ObjectPath? _cachedSessionPath;

    private Connection? _dbusConn;
    private bool _disposed;

    public SecureStorageLinux()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "MediaDebrid");

        // Ensure directory exists with restricted access
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
#if NET7_0_OR_GREATER
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch
            {
            }
#endif
        }

        _fallbackFilePath = Path.Combine(dir, "linux_secrets.json");
        _lockFilePath = Path.Combine(dir, "linux_secrets.lock");
        _fallbackKey = DeriveMachineKey();
    }

    public async Task SaveAsync(string key, string value)
    {
        ThrowIfDisposed();

        // 1. Durability: Save to encrypted local fallback first (with cross-process lock)
        using (await AcquireFileLockAsync())
        {
            await SaveToFallbackInternalAsync(key, value);
        }

        // 2. Integration: Mirror to system keyring
        try
        {
            var session = await GetKeyringSessionAsync();
            if (session != null) await SaveToKeyringInternalAsync(session.Value, key, value);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SecureStorage] Keyring mirror failed: {ex.Message}");
            ResetDbusConnection();
        }
    }

    public async Task<string?> LoadAsync(string key)
    {
        ThrowIfDisposed();

        // 1. Primary: Try native system keyring
        try
        {
            var session = await GetKeyringSessionAsync();
            if (session != null)
            {
                var result = await LoadFromKeyringInternalAsync(session.Value, key);
                if (result != null) return result;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SecureStorage] Keyring load failed: {ex.Message}");
            ResetDbusConnection();
        }

        // 2. Fallback: Load from local encrypted store
        using (await AcquireFileLockAsync())
        {
            return await LoadFromFallbackInternalAsync(key);
        }
    }

    public async Task DeleteAsync(string key)
    {
        ThrowIfDisposed();

        // Wipe from Keyring
        try
        {
            var session = await GetKeyringSessionAsync();
            if (session != null) await DeleteFromKeyringInternalAsync(key);
        }
        catch
        {
            ResetDbusConnection();
        }

        // Wipe from Fallback
        using (await AcquireFileLockAsync())
        {
            await DeleteFromFallbackInternalAsync(key);
        }
    }


    #region Keyring Logic (DBus)

    private void ResetDbusConnection()
    {
        _dbusConn?.Dispose();
        _dbusConn = null;
        _cachedSessionPath = null;
    }

    private async Task<ObjectPath?> GetKeyringSessionAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            return null;

        try
        {
            if (_dbusConn == null)
            {
                _dbusConn = new Connection(Address.Session);
                // ConnectAsync is safe to call; if already connected, it returns immediately.
                await _dbusConn.ConnectAsync();
            }

            if (_cachedSessionPath == null)
            {
                var service = _dbusConn.CreateProxy<ISecretService>(ServiceName, ServicePath);
                // Note: Values are sent over a local Unix socket.
                var (_, sessionPath) = await service.OpenSessionAsync("plain", string.Empty);
                _cachedSessionPath = sessionPath;
            }

            return _cachedSessionPath;
        }
        catch
        {
            // If the connection was lost or the session invalidated, 
            // we reset everything so the next attempt starts fresh.
            ResetDbusConnection();
            return null;
        }
    }

    private async Task SaveToKeyringInternalAsync(ObjectPath session, string key, string value)
    {
        var accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var secret = new Secret(session, Encoding.UTF8.GetBytes(value));

        var props = new Dictionary<string, object>
        {
            { "org.freedesktop.Secret.Item.Label", "MediaDebrid Secret" },
            {
                "org.freedesktop.Secret.Item.Attributes", new Dictionary<string, string>
                    { { "service", "mediadebrid" }, { "account_hash", accountHash } }
            }
        };

        var collection = _dbusConn!.CreateProxy<ISecretCollection>(ServiceName, CollectionPath);
        await collection.CreateItemAsync(props, secret, true);
    }

    private async Task<string?> LoadFromKeyringInternalAsync(ObjectPath session, string key)
    {
        var accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var service = _dbusConn!.CreateProxy<ISecretService>(ServiceName, ServicePath);
        var items = await service.SearchItemsAsync(new Dictionary<string, string>
        {
            { "service", "mediadebrid" }, { "account_hash", accountHash }
        });

        if (items == null || items.Length == 0) return null;

        var item = _dbusConn.CreateProxy<ISecretItem>(ServiceName, items[0]);
        var secret = await item.GetSecretAsync(session);
        return Encoding.UTF8.GetString(secret.Value);
    }

    private async Task DeleteFromKeyringInternalAsync(string key)
    {
        var accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var service = _dbusConn!.CreateProxy<ISecretService>(ServiceName, ServicePath);
        var items = await service.SearchItemsAsync(new Dictionary<string, string>
        {
            { "service", "mediadebrid" }, { "account_hash", accountHash }
        });

        if (items == null) return;
        foreach (var path in items)
            await _dbusConn.CreateProxy<ISecretItem>(ServiceName, path).DeleteAsync();
    }

    #endregion

    #region Fallback Logic (Encrypted File)

    private async Task<IDisposable> AcquireFileLockAsync()
    {
        var retries = 60; // 3 seconds total wait time
        while (retries > 0)
            try
            {
                return new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(50);
                retries--;
            }

        throw new TimeoutException("Timeout waiting for cross-process secret store lock.");
    }

    private async Task SaveToFallbackInternalAsync(string key, string value)
    {
        var dict = await LoadFallbackDictAsync();
        dict[key] = Encrypt(value);

        var tempFile = _fallbackFilePath + ".tmp";
        await File.WriteAllTextAsync(tempFile, JsonSerializer.Serialize(dict, MediaDebridJsonContext.Default.DictionaryStringString));
#if NET7_0_OR_GREATER
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
        }
#endif
        File.Move(tempFile, _fallbackFilePath, true);
    }

    private async Task<string?> LoadFromFallbackInternalAsync(string key)
    {
        var dict = await LoadFallbackDictAsync();
        if (!dict.TryGetValue(key, out var encrypted)) return null;

        try
        {
            return Decrypt(encrypted);
        }
        catch (CryptographicException)
        {
            // Self-healing: Purge entry if corrupted/tampered
            dict.Remove(key);
            await File.WriteAllTextAsync(_fallbackFilePath, JsonSerializer.Serialize(dict, MediaDebridJsonContext.Default.DictionaryStringString));
            return null;
        }
    }

    private async Task DeleteFromFallbackInternalAsync(string key)
    {
        var dict = await LoadFallbackDictAsync();
        if (dict.Remove(key))
            await File.WriteAllTextAsync(_fallbackFilePath, JsonSerializer.Serialize(dict, MediaDebridJsonContext.Default.DictionaryStringString));
    }

    private async Task<Dictionary<string, string>> LoadFallbackDictAsync()
    {
        if (!File.Exists(_fallbackFilePath)) return new Dictionary<string, string>();
        try
        {
            var json = await File.ReadAllTextAsync(_fallbackFilePath);
            return JsonSerializer.Deserialize(json, MediaDebridJsonContext.Default.DictionaryStringString) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // Move corrupt file out of the way for debugging
            var backup = _fallbackFilePath + ".corrupt.bak";
            if (File.Exists(_fallbackFilePath)) File.Move(_fallbackFilePath, backup, true);
            return new Dictionary<string, string>();
        }
    }

    private byte[] DeriveMachineKey()
    {
        var id = File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : Environment.MachineName;
        var masterSeed = $"{id}:{Environment.UserName}:mediadebrid-v1";
        var salt = Encoding.UTF8.GetBytes("mediadebrid-linux-2025-salt");
        return Rfc2898DeriveBytes.Pbkdf2(masterSeed, salt, 100000, HashAlgorithmName.SHA512, 32);
    }

    private string Encrypt(string plainText)
    {
        using var aes = new AesGcm(_fallbackKey, 16);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        var res = new byte[40 + plainBytes.Length];
        nonce.CopyTo(res.AsSpan(0, 12));
        tag.CopyTo(res.AsSpan(12, 16));
        cipherBytes.CopyTo(res.AsSpan(28));
        return Convert.ToBase64String(res);
    }

    private string Decrypt(string cipherText)
    {
        var data = Convert.FromBase64String(cipherText);
        using var aes = new AesGcm(_fallbackKey, 16);
        var plainBytes = new byte[data.Length - 28];
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    #endregion

    #region Helpers

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SecureStorageLinux));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dbusConn?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion
}

#region SUPPORTING DBUS TYPES

[DBusInterface("org.freedesktop.Secret.Service")]
public interface ISecretService : IDBusObject
{
    Task<(object output, ObjectPath session)> OpenSessionAsync(string algorithm, object input);
    Task<ObjectPath[]> SearchItemsAsync(Dictionary<string, string> attributes);
}

[DBusInterface("org.freedesktop.Secret.Collection")]
public interface ISecretCollection : IDBusObject
{
    Task<ObjectPath> CreateItemAsync(Dictionary<string, object> properties, Secret secret, bool replace);
}

[DBusInterface("org.freedesktop.Secret.Item")]
public interface ISecretItem : IDBusObject
{
    Task<Secret> GetSecretAsync(ObjectPath session);
    Task DeleteAsync();
}

public struct Secret(ObjectPath session, byte[] value)
{
    public ObjectPath Session = session;
    public byte[] Parameters = Array.Empty<byte>();
    public byte[] Value = value;
    public string ContentType = "text/plain";
}

#endregion