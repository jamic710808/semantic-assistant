using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Contracts;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class OwnedQueryVerificationTests
{
    [Fact]
    public void OriginalInlineFixtureIsAllowedWithoutEngineConnection()
    {
        using var database=SyntheticDesktopFixture.CreateDatabase();
        OwnedQueryVerification.ValidateFixture(database);
        database.Model.Tables["Sales"].Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name="Unknown", Expression="1" });
        Assert.Throws<InvalidDataException>(()=>OwnedQueryVerification.ValidateFixture(database));
    }

    [Fact]
    public async Task SuccessfulQueryCannotBeMistakenForExpectedRejection()
    {
        Assert.False(await OwnedQueryVerification.WasRejectedAsync<InvalidDataException>(()=>Task.FromResult(new QueryResult(
            "synthetic",DemoModel.Snapshot.Identity,[],[],false,"EVALUATE x"))));
        Assert.True(await OwnedQueryVerification.WasRejectedAsync<InvalidDataException>(()=>Task.FromException<QueryResult>(new InvalidDataException())));
        await Assert.ThrowsAsync<IOException>(()=>OwnedQueryVerification.WasRejectedAsync<InvalidDataException>(
            ()=>Task.FromException<QueryResult>(new IOException())));
    }
}
