using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Files;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class TmdlReaderTests
{
    [Fact]
    public async Task BundledExampleMatchesTheDocumentedStructure()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Examples", "definition");
        var snapshot = await new TmdlModelReader(folder).ReadSnapshotAsync(default);
        Assert.Equal("SyntheticFileModel", snapshot.Identity.Name);
        Assert.Equal(2, snapshot.Tables.Length);
        Assert.Equal(3, snapshot.Tables.Sum(t => t.Columns.Length));
        Assert.Equal("SUM('SyntheticSales'[Amount])", snapshot.Tables.Single(t => t.Name == "SyntheticSales").Measures.Single().Expression);
        Assert.Equal("SyntheticDates", snapshot.Relationships.Single().ToTable);
    }

    [Fact]
    public async Task MissingDirectoryFailsWithoutChangingWorkspace()
    {
        var workspace = new SemanticAssistant.Application.ModelWorkspace(SemanticAssistant.Adapters.Demo.DemoModel.Snapshot);
        var missing = Path.Combine(NewTestFolder(), "missing");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => workspace.LoadOfflineAsync(new TmdlModelReader(missing), default));
        Assert.False(workspace.Current.IsOffline);
    }

    [Fact]
    public async Task MalformedDefinitionFailsBeforeWorkspaceCommit()
    {
        var folder = NewTestFolder();
        await File.WriteAllTextAsync(Path.Combine(folder, "model.tmdl"), "invalid TMDL ??");
        var workspace = new SemanticAssistant.Application.ModelWorkspace(SemanticAssistant.Adapters.Demo.DemoModel.Snapshot);
        await Assert.ThrowsAnyAsync<Exception>(() => workspace.LoadOfflineAsync(new TmdlModelReader(folder), default));
        Assert.False(workspace.Current.IsOffline);
    }

    [Fact]
    public async Task OfficialTomDeserializerReadsOnlyGeneratedSyntheticFiles()
    {
        var folder = NewTestFolder();
        using var database = new Database { Name = "SyntheticFileModel", ID = "synthetic-file", CompatibilityLevel = 1600, Model = new Model { Name = "Model" } };
        var sales = new Table { Name = "SyntheticSales" };
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal, SourceColumn = "Amount" });
        sales.Columns.Add(new DataColumn { Name = "Date", DataType = DataType.DateTime, SourceColumn = "Date" });
        sales.Measures.Add(new Measure { Name = "Total", Expression = "SUM('SyntheticSales'[Amount])" });
        var dates = new Table { Name = "SyntheticDates" };
        dates.Columns.Add(new DataColumn { Name = "Date", DataType = DataType.DateTime, SourceColumn = "Date" });
        database.Model.Tables.Add(sales);
        database.Model.Tables.Add(dates);
        database.Model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "SyntheticLink",
            FromColumn = sales.Columns["Date"],
            ToColumn = dates.Columns["Date"],
            IsActive = true
        });
        TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        var reader = new TmdlModelReader(folder);
        var snapshot = await reader.ReadSnapshotAsync(default);
        Assert.Equal("SyntheticFileModel", snapshot.Identity.Name);
        Assert.Equal(2, snapshot.Tables.Length);
        Assert.Equal("Total", snapshot.Tables.Single(t => t.Name == "SyntheticSales").Measures.Single().Name);
        Assert.Equal("SyntheticLink", snapshot.Relationships.Single().Name);
        Assert.Equal(snapshot.Identity.Revision, (await reader.ReadSnapshotAsync(default)).Identity.Revision);
        Assert.DoesNotContain(folder, snapshot.Identity.Id);
    }

    [Fact]
    public async Task EmptyDirectoryIsRejected()
        => await Assert.ThrowsAsync<InvalidDataException>(() => new TmdlModelReader(NewTestFolder()).ReadSnapshotAsync(default));

    [Fact]
    public async Task OversizeFileIsRejectedBeforeTomParsing()
    {
        var folder = NewTestFolder();
        await File.WriteAllTextAsync(Path.Combine(folder, "model.tmdl"), new string('x', 1_048_577));
        await Assert.ThrowsAsync<InvalidDataException>(() => new TmdlModelReader(folder).ReadSnapshotAsync(default));
    }

    [Fact]
    public async Task CancellationPreventsFileParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TmdlModelReader(NewTestFolder()).ReadSnapshotAsync(cancellation.Token));
    }

    private static string NewTestFolder()
    {
        // Only generated test files; retained for inspection instead of deleting arbitrary paths.
        var folder = Path.Combine(Path.GetTempPath(), "SemanticAssistant-synthetic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
