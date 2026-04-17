using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.Core.Abstractions;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

public sealed partial class DeviceEditorViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;
    private Device _original = new();

    [ObservableProperty] private string name = "";
    [ObservableProperty] private string host = "";
    [ObservableProperty] private string? description;
    [ObservableProperty] private DeviceModelKind model = DeviceModelKind.Xc200;
    [ObservableProperty] private int snmpPort = 161;
    [ObservableProperty] private int sshPort = 22;
    [ObservableProperty] private SnmpVersion snmpVersion = SnmpVersion.V2c;
    [ObservableProperty] private ProtocolKind preferredProtocol = ProtocolKind.Snmp;
    [ObservableProperty] private string snmpCommunityRead = "public";
    [ObservableProperty] private string snmpCommunityWrite = "private";
    [ObservableProperty] private string? sshUsername;
    [ObservableProperty] private string? sshPassword;

    public IReadOnlyList<DeviceModelKind> ModelOptions { get; } =
        Enum.GetValues<DeviceModelKind>().Where(v => v != DeviceModelKind.Unknown).ToArray();
    public IReadOnlyList<SnmpVersion> SnmpVersions { get; } = Enum.GetValues<SnmpVersion>();
    public IReadOnlyList<ProtocolKind> Protocols { get; } = Enum.GetValues<ProtocolKind>();

    public DeviceEditorViewModel(ICredentialStore credentials)
    {
        _credentials = credentials;
    }

    public void Load(Device device)
    {
        _original = device;
        Name = device.Name;
        Host = device.Host;
        Description = device.Description;
        Model = device.Model == DeviceModelKind.Unknown ? DeviceModelKind.Xc200 : device.Model;
        SnmpPort = device.SnmpPort;
        SshPort = device.SshPort;
        SnmpVersion = device.SnmpVersion;
        PreferredProtocol = device.PreferredProtocol;
    }

    public async Task LoadWithCredentialAsync(Device device, CancellationToken ct = default)
    {
        Load(device);
        if (device.CredentialId.HasValue)
        {
            var cred = await _credentials.GetAsync(device.CredentialId.Value, ct);
            if (cred is not null)
            {
                SshUsername = cred.Username;
                SshPassword = cred.Password;
                SnmpCommunityRead = cred.SnmpCommunityRead ?? "public";
                SnmpCommunityWrite = cred.SnmpCommunityWrite ?? "private";
            }
        }
    }

    public Device ToDevice()
    {
        _original.Name = Name;
        _original.Host = Host;
        _original.Description = Description;
        _original.Model = Model;
        _original.SnmpPort = SnmpPort;
        _original.SshPort = SshPort;
        _original.SnmpVersion = SnmpVersion;
        _original.PreferredProtocol = PreferredProtocol;
        return _original;
    }

    public async Task<Guid?> SaveCredentialIfProvidedAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SnmpCommunityRead) && string.IsNullOrWhiteSpace(SshUsername))
            return _original.CredentialId;

        if (_original.CredentialId.HasValue)
            await _credentials.DeleteAsync(_original.CredentialId.Value, ct);

        var cred = new Credential(
            Username: SshUsername,
            Password: SshPassword,
            PrivateKeyPath: null,
            SnmpCommunityRead: string.IsNullOrWhiteSpace(SnmpCommunityRead) ? null : SnmpCommunityRead,
            SnmpCommunityWrite: string.IsNullOrWhiteSpace(SnmpCommunityWrite) ? null : SnmpCommunityWrite,
            SnmpV3: null);
        var id = await _credentials.SaveAsync(cred, Name, ct);
        _original.CredentialId = id;
        return id;
    }
}
