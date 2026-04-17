using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Scalance.App.Services;
using Scalance.Core.Capabilities;

namespace Scalance.App.ViewModels;

/// <summary>
/// Operator view for on-device configuration backups
/// (PH_SCALANCE-S615-CLI_76 sec 5.4 pp. 135-142) plus a "export running-config
/// to file" convenience built on top of <c>BackupConfigAsync</c>.
///
/// The device holds named backups internally; this VM lists them, lets the
/// operator create / restore / delete entries, and provides a local
/// `show running-config` dump for offline archival.
/// </summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string newBackupName = "";
    [ObservableProperty] private string? selectedBackup;

    public ObservableCollection<string> Backups { get; } = new();

    public BackupViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
    {
        _ops = ops; _selection = selection; _log = log;
        _selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceSelection.Current)) RefreshForSelection();
        };
        RefreshForSelection();
    }

    private void RefreshForSelection()
    {
        var d = _selection.Current;
        SelectedDeviceName = d?.Name;
        FeatureSupported = d is not null
            && CapabilityMatrix.Supports(d.Model, DeviceCapability.ConfigBackup)
            && CapabilityMatrix.Supports(d.Model, DeviceCapability.SshCli);
        Backups.Clear();
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。請按「載入」列出設備上的備份。"
            : "此設備不支援 configbackup（需要 SSH-CLI 與 Config backup 能力）。";
        NotifyAll();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;
    private bool CanCreate() => CanUse() && !string.IsNullOrWhiteSpace(NewBackupName);
    private bool CanActOnSelected() => CanUse() && !string.IsNullOrWhiteSpace(SelectedBackup);

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task LoadAsync()
    {
        var d = _selection.Current; if (d is null) return;
        IsBusy = true; StatusMessage = "正在列出設備備份…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.ListConfigBackupsAsync();
            Backups.Clear();
            if (r.Success && r.Value is not null)
            {
                foreach (var n in r.Value) Backups.Add(n);
                StatusMessage = $"找到 {Backups.Count} 筆備份。";
            }
            else StatusMessage = $"載入失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var d = _selection.Current; if (d is null) return;
        var name = NewBackupName.Trim();
        IsBusy = true; StatusMessage = $"建立備份 '{name}'…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.CreateConfigBackupAsync(name);
            DryRunPreview.LogIfDryRun(driver, _log, "Backup create");
            if (r.Success)
            {
                if (!Backups.Contains(name)) Backups.Add(name);
                NewBackupName = "";
                StatusMessage = $"已建立 '{name}'。";
            }
            else StatusMessage = $"建立失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task RestoreAsync()
    {
        var d = _selection.Current; if (d is null || SelectedBackup is null) return;
        var name = SelectedBackup;
        IsBusy = true; StatusMessage = $"還原備份 '{name}'…（裝置會重啟對應設定）";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.RestoreConfigBackupAsync(name);
            DryRunPreview.LogIfDryRun(driver, _log, "Backup restore");
            StatusMessage = r.Success ? $"已套用備份 '{name}'。" : $"還原失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task DeleteAsync()
    {
        var d = _selection.Current; if (d is null || SelectedBackup is null) return;
        var name = SelectedBackup;
        IsBusy = true; StatusMessage = $"刪除備份 '{name}'…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.DeleteConfigBackupAsync(name);
            DryRunPreview.LogIfDryRun(driver, _log, "Backup delete");
            if (r.Success)
            {
                Backups.Remove(name);
                SelectedBackup = null;
                StatusMessage = $"已刪除 '{name}'。";
            }
            else StatusMessage = $"刪除失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ExportRunningConfigAsync()
    {
        var d = _selection.Current; if (d is null) return;
        IsBusy = true; StatusMessage = "正在抓取 show running-config …";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.BackupConfigAsync();
            if (!r.Success || r.Value is null)
            {
                StatusMessage = $"抓取失敗：{r.Message}";
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "儲存 running-config",
                FileName = $"{d.Name}-running-config-{DateTime.Now:yyyyMMdd-HHmmss}.cfg",
                Filter = "CLI config (*.cfg;*.txt)|*.cfg;*.txt|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) { StatusMessage = "已取消匯出。"; return; }
            await File.WriteAllTextAsync(dlg.FileName, r.Value);
            StatusMessage = $"已匯出至 {dlg.FileName}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void NotifyAll()
    {
        LoadCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ExportRunningConfigCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyAll();
    partial void OnNewBackupNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
    partial void OnSelectedBackupChanged(string? value)
    {
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"Backup: {value}");
    }
}
