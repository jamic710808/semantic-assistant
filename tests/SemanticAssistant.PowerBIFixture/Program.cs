using SemanticAssistant.PowerBIFixture;

if (args.Length == 2 && args[0] == "--verify-native-writer-owned")
    return await NativeWriterReadbackVerification.RunAsync(args[1]);
if (args.Length == 2 && args[0] == "--prepare-ui-owned")
    return await OwnedDesktopVerification.RunAsync(args[1], prepareUiOnly: true);
if (args.Length == 3 && args[0] == "--verify-persisted")
    return await PersistenceDesktopVerification.RunAsync(args[1], args[2]);
if (args.Length == 2 && args[0] == "--verify-owned")
    return await OwnedDesktopVerification.RunAsync(args[1]);
if (args.Length == 2 && args[0] == "--inspect-owned")
    return await OwnedDesktopVerification.InspectAsync(args[1]);

if (args.Length != 2 || args[0] != "--create")
{
    Console.Error.WriteLine("Usage: SemanticAssistant.PowerBIFixture --create <existing-output-parent>");
    return 2;
}
try
{
    var project = SyntheticDesktopFixture.CreateProject(args[1]);
    Console.WriteLine(project);
    Console.WriteLine("synthetic-pbip-created;tables=4;measures=5;relationships=2;source-rows=69;live-verified=false");
    return 0;
}
catch
{
    Console.Error.WriteLine("合成測試專案產生失敗；請指定一般目錄，每次產生新專案而不覆蓋原檔。");
    return 1;
}
