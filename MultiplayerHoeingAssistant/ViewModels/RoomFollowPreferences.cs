using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

// Deliberately outside assistant-config.json and its import/export contract. A read invitation
// is a capability, not UI text, and is protected for the current Windows user on disk.
internal sealed record RoomFollowPreferences(bool ShareEnabled = false, bool FollowEnabled = false, string Invitation = "")
{
    public override string ToString() => $"Follow preferences: share={ShareEnabled}, follow={FollowEnabled}, invitation saved={Invitation.Length > 0}";
}

internal sealed class RoomFollowPreferencesStore(string? path = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NexusBGI.RoomFollowPreferences.v1");
    private const int MaximumBytes = 128 * 1024;
    private readonly string _path = Path.GetFullPath(path ?? Path.Combine(AssistantDataDirectory.Root, "room-follow-preferences.protected"));

    internal RoomFollowPreferences Load()
    {
        byte[]? plaintext = null;
        try
        {
            ConfigTransferTransaction.CheckPath(_path);
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumBytes) throw InvalidPreferences();
            var ciphertext = new byte[(int)stream.Length];
            stream.ReadExactly(ciphertext);
            plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            var preferences = JsonSerializer.Deserialize<RoomFollowPreferences>(plaintext)
                ?? throw InvalidPreferences();
            Validate(preferences);
            return preferences;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException or ArgumentException)
        { throw InvalidPreferences(); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal void Save(RoomFollowPreferences preferences)
    {
        Validate(preferences);
        using var writing = ConfigFileWriteGate.Acquire(_path);
        _ = Load(); // A corrupt or another user's file is not replaced with guessed settings.
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(preferences);
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            if (ciphertext.Length > MaximumBytes) throw InvalidPreferences();
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(ciphertext);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void Validate(RoomFollowPreferences preferences)
    {
        if (preferences.Invitation is null || preferences.FollowEnabled && preferences.Invitation.Length == 0
            || preferences.FollowEnabled && preferences.ShareEnabled) throw InvalidPreferences();
        if (preferences.Invitation.Length > 0) _ = RoomFollowInvitation.Parse(preferences.Invitation);
    }

    private static InvalidDataException InvalidPreferences() => new("房间跟随设置无效或不属于当前 Windows 用户。");
}
