using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZnakiAI.Core.Bridge;

// Serwer MCP (stdio). stdout należy do protokołu, więc logi idą na stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new BridgeClient(Environment.GetEnvironmentVariable("ZNAKIAI_PIPE") is { Length: > 0 } p
    ? p
    : BridgeProtocol.PipeName));

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "znakiai-bricscad", Version = "1.0.0" };
        o.ServerInstructions = ZnakiAI.Mcp.BricscadTools.Instructions;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
