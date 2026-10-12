using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PogSplit.TwitchBridge;

/// <summary>
/// Keeps the bridge token out of layout files (people share those). It is encrypted for the current Windows user
/// with DPAPI and stored under %AppData%\PogSplit.
/// </summary>
public static class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PogSplit.TwitchBridge.v1");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PogSplit",
        "twitch-bridge.dat");

    public static Pairing Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            Pairing pairing = JsonSerializer.Deserialize<Pairing>(plain);
            return string.IsNullOrEmpty(pairing?.Token) ? null : pairing;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(Pairing pairing)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(pairing);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
