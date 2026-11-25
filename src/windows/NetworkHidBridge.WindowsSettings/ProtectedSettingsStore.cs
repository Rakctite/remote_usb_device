using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetworkHidBridge.Core.Settings;

[SupportedOSPlatform("windows")]
public sealed class ProtectedSettingsStore
{
    private static readonly byte[] Entropy = "IoTSys.NetworkHidBridge.Settings.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ProtectedSettingsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IoTSys",
            "NetworkHidBridge",
            "settings.json");
    }

    public string Path => _path;

    public SettingsLoadResult LoadOrCreate()
    {
        if (!File.Exists(_path))
        {
            AppSettings created = AppSettings.CreateDefault();
            Save(created);
            return new(created, null);
        }

        try
        {
            PersistedSettings? persisted = JsonSerializer.Deserialize<PersistedSettings>(
                File.ReadAllText(_path),
                JsonOptions);
            if (persisted is null || !IPAddress.TryParse(persisted.Address, out IPAddress? address))
            {
                throw new InvalidDataException("Settings address is invalid.");
            }

            byte[] encrypted = Convert.FromBase64String(persisted.ProtectedPsk);
            byte[] psk = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return new(new AppSettings(address, persisted.Port, psk, persisted.StartWithWindows), null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(psk);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException
            or CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(AppSettings.CreateDefault(), "설정 파일을 읽지 못해 새 기본값을 사용합니다.");
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Settings path must have a parent directory.");
        }
        Directory.CreateDirectory(directory);

        byte[] psk = settings.CopyPsk();
        byte[] encrypted = ProtectedData.Protect(psk, Entropy, DataProtectionScope.CurrentUser);
        string temporary = _path + ".tmp";
        try
        {
            var persisted = new PersistedSettings(
                settings.Address.ToString(),
                settings.Port,
                Convert.ToBase64String(encrypted),
                settings.StartWithWindows);
            string json = JsonSerializer.Serialize(persisted, JsonOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record PersistedSettings(
        string Address,
        int Port,
        string ProtectedPsk,
        bool StartWithWindows);
}
