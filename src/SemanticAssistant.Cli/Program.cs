using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Files;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.Adapters.Mcp;

if (args.Length == 1 && args[0] == "--mcp-schema-worker-v1") return await McpSchemaWorker.RunAsync();

if (args.Length == 2 && args[0] == "--render-demo-report")
{
    var data = await new DemoModel().ExecuteAsync(DemoModel.Snapshot.Identity, DemoModel.SupportedQuery, new(), default);
    var output = Path.GetFullPath(args[1]);
    var permissions = new PermissionSession(data.Source, ConversationMode.Report, ImmutableHashSet.Create(Capability.ArtifactWrite));
    var path = await new HtmlReportExporter(output, permissions).ExportAsync(data, permissions.Snapshot, default);
    Console.WriteLine(path);
    return 0;
}

if (args.Length == 2 && args[0] == "--inspect-tmdl")
{
    try
    {
        var snapshot = await new TmdlModelReader(args[1]).ReadSnapshotAsync(default);
        // This explicitly requested local output is metadata, not an AI request or diagnostic log.
        Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch
    {
        Console.Error.WriteLine("TMDL 讀取失敗。請選擇 definition 目錄，確認格式、大小與檔案完整性。");
        return 1;
    }
}
if (args.Length != 0 && !(args.Length == 1 && args[0] == "--allow-query"))
{
    Console.Error.WriteLine("Usage: SemanticAssistant.Cli [--allow-query | --inspect-tmdl <definition-folder> | --render-demo-report <output-folder>]");
    return 2;
}
var grants = ImmutableHashSet.Create(Capability.MetadataRead);
if (args.Contains("--allow-query")) grants = grants.Add(Capability.DataQuery);
var model = new DemoModel();
var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Analyze, grants);
var coordinator = new ConversationCoordinator(model, new DemoAiProvider(), new ToolDispatcher(model, model, session), session);
var request = new ConversationRequest(Guid.NewGuid(), "分析合成銷售數值", ConversationMode.Analyze,
    session.Snapshot);
var outcome = await coordinator.RunAsync(request, e =>
{
    Console.WriteLine(JsonSerializer.Serialize(e));
    return Task.CompletedTask;
});
Console.WriteLine($"Outcome={outcome.Code}; tools={outcome.ToolResults.Length}; synthetic=true");
return outcome.Code == "ok" ? 0 : 1;
