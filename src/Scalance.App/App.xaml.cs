using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scalance.App.Services;
using Scalance.App.ViewModels;
using Scalance.App.Views;
using Scalance.Core.Abstractions;
using Scalance.Data;
using Scalance.Drivers;
using Scalance.Protocols.Dcp;

namespace Scalance.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();

        services.AddDbContextFactory<ScalanceDbContext>(opts =>
            opts.UseSqlite($"Data Source={ScalanceDbContext.DefaultDbPath()}"));

        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<ICredentialStore, DpapiCredentialStore>();
        services.AddSingleton<IDeviceDriverFactory, DeviceDriverFactory>();
        services.AddSingleton<DeviceOperationsService>();
        services.AddSingleton<DeviceSelection>();
        services.AddSingleton<OperationLog>();
        services.AddSingleton<DcpDiscoveryService>();

        services.AddSingleton<DeviceListViewModel>();
        services.AddSingleton<NtpEditorViewModel>();
        services.AddSingleton<VlanEditorViewModel>();
        services.AddSingleton<VpnEditorViewModel>();
        services.AddSingleton<SubnetEditorViewModel>();
        services.AddSingleton<FirewallEditorViewModel>();
        services.AddSingleton<ConnectionStatusViewModel>();
        services.AddSingleton<TopologyViewModel>();
        services.AddSingleton<DiscoveryViewModel>();
        services.AddSingleton<BasicWizardViewModel>();
        services.AddSingleton<BulkOpsViewModel>();
        services.AddSingleton<SyslogEditorViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddTransient<DeviceEditorViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();

        using (var scope = Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ScalanceDbContext>>();
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
        }

        var main = Services.GetRequiredService<MainWindow>();
        main.DataContext = Services.GetRequiredService<MainViewModel>();
        main.Show();
    }
}
