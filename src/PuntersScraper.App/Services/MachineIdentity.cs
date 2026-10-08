using Microsoft.Win32;
using PuntersScraper.Shared.Messaging;

namespace PuntersScraper.App.Services;

/// <summary>
/// Resolves the <see cref="ProducerIdentity"/> stamped onto every RabbitMQ event this app
/// publishes: the Windows <c>MachineGuid</c>, computer name, Windows user name, and app version.
/// None of these change while the app runs, so they're read once and cached in
/// <see cref="Current"/>. Never throws — a value that can't be read (locked-down registry, missing
/// key) is simply null, since identity is informational and must never block a publish.
/// </summary>
public static class MachineIdentity
{
    private const string CryptographyKeyPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValueName = "MachineGuid";

    private static readonly Lazy<ProducerIdentity> LazyCurrent = new(() => Resolve(ReadMachineGuidFromRegistry));

    /// <summary>This process's identity, resolved on first use.</summary>
    public static ProducerIdentity Current => LazyCurrent.Value;

    /// <summary>Builds an identity using <paramref name="readMachineGuid"/> for the registry part,
    /// so tests can substitute a fake or failing reader. A reader that throws or returns a blank
    /// value yields a null <see cref="ProducerIdentity.MachineGuid"/>.</summary>
    public static ProducerIdentity Resolve(Func<string?> readMachineGuid) => new(
        MachineGuid: SafeRead(readMachineGuid),
        MachineName: SafeRead(() => Environment.MachineName),
        UserName: SafeRead(ReadUserName),
        ApplicationVersion: SafeRead(() => UpdateChecker.CurrentVersionText));

    /// <summary>Reads <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>. Forces the 64-bit
    /// registry view: a 32-bit process would otherwise be redirected to <c>Wow6432Node</c>, which
    /// doesn't carry this value.</summary>
    public static string? ReadMachineGuidFromRegistry()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(CryptographyKeyPath);
        return key?.GetValue(MachineGuidValueName) as string;
    }

    private static string ReadUserName()
    {
        return Environment.UserName;
    }

    private static string? SafeRead(Func<string?> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }
}
