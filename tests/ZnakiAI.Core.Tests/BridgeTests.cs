using System.Text.Json.Nodes;
using Xunit;
using ZnakiAI.Core.Bridge;
using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Geometry;
using ZnakiAI.Core.Planning;

namespace ZnakiAI.Core.Tests;

public class BridgeTests
{
    private static SignCatalog LoadCatalog()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "catalog"))) dir = Path.GetDirectoryName(dir)!;
        return SignCatalog.Load(Path.Combine(dir, "catalog", "znaki_pionowe.json"),
            Path.Combine(dir, "catalog", "znaki_poziome.json"));
    }

    [Fact]
    public async Task Request_and_error_travel_through_the_pipe()
    {
        string pipe = "ZnakiAI.Test." + Guid.NewGuid().ToString("N");
        using var server = new BridgeServer((req, _) => req.Metoda switch
        {
            "echo" => Task.FromResult<JsonNode?>(new JsonObject { ["x"] = req.Parametry!["x"]!.GetValue<double>() * 2 }),
            _ => throw new BridgeException("nieznana"),
        }, pipe);
        server.Start();
        var client = new BridgeClient(pipe);

        var r = await client.CallAsync("echo", new { x = 21.5 }, TimeSpan.FromSeconds(10));
        Assert.Equal(43.0, r!["x"]!.GetValue<double>());

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.CallAsync("inna", null, TimeSpan.FromSeconds(10)));
        Assert.Equal("nieznana", ex.Message);
    }

    [Fact]
    public async Task Missing_plugin_gives_readable_message()
    {
        var client = new BridgeClient("ZnakiAI.Brak." + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.CallAsync("stan", null, TimeSpan.FromSeconds(10)));
        Assert.Contains("BricsCAD", ex.Message);
    }

    [Fact]
    public void Check_reports_errors_and_positions_without_drawing()
    {
        var axis = Alignment.FromPolyline([new Vec2(0, 0), new Vec2(100, 0)], [0.0]);
        var plan = new PlacementPlan
        {
            Znaki =
            [
                new SignPlacement { Kod = "A-14", Km = 20, Strona = Side.Prawa, Odsuniecie = 1 },
                new SignPlacement { Kod = "X-99", Km = 30 },
            ],
        };
        var r = PlanTools.Check(plan, axis, LoadCatalog(), new DrawingSettings());
        Assert.Single(r["znaki"]!.AsArray());
        Assert.Equal(20.0, r["znaki"]![0]!["slupek_x"]!.GetValue<double>());
        Assert.Equal(-1.0, r["znaki"]![0]!["slupek_y"]!.GetValue<double>());
        Assert.Contains("X-99", r["bledy"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Point_tools_round_trip_on_arc()
    {
        var axis = Alignment.FromPolyline([new Vec2(0, 0), new Vec2(60, 0), new Vec2(100, 20)], [0.0, 0.2]);
        var p = PlanTools.PointAt(axis, 75, -2.5);
        var back = PlanTools.PointInfo(axis, p["x"]!.GetValue<double>(), p["y"]!.GetValue<double>());
        Assert.Equal(75, back["km"]!.GetValue<double>(), 2);
        Assert.Equal(-2.5, back["odsuniecie"]!.GetValue<double>(), 2);
        Assert.Equal("Prawa", back["strona"]!.GetValue<string>());
        Assert.Throws<BridgeException>(() => PlanTools.PointAt(axis, axis.Length + 1, 0));
    }
}
