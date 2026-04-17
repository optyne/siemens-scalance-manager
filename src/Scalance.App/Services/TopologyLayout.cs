using System.IO;
using System.Text.Json;

namespace Scalance.App.Services;

public sealed class TopologyLayout
{
    public Dictionary<Guid, NodePosition> Positions { get; set; } = new();

    public sealed class NodePosition
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private static string FilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SiemensScalanceManager", "topology.json");

    public static TopologyLayout Load()
    {
        var path = FilePath();
        if (!File.Exists(path)) return new TopologyLayout();
        try { return JsonSerializer.Deserialize<TopologyLayout>(File.ReadAllText(path)) ?? new(); }
        catch { return new TopologyLayout(); }
    }

    public void Save()
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
