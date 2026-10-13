using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class OwnedCancellationVerificationTests
{
    [Fact]
    public void FixedWorkloadOnlyAcceptsItsOwnGuidMarker()
    {
        var dax=OwnedCancellationVerification.Workload("SA_Cancel_"+Guid.NewGuid().ToString("N"));
        Assert.Contains("GENERATESERIES(1,5000000,1)",dax);Assert.DoesNotContain("Sales",dax);
    }
    [Theory]
    [InlineData("SA_Cancel_'")]
    [InlineData("Other_00000000000000000000000000000000")]
    [InlineData("SA_Cancel_0000000000000000000000000000000x")]
    public void OtherMarkerCannotBecomeDax(string marker)=>Assert.Throws<InvalidDataException>(()=>OwnedCancellationVerification.Workload(marker));
    [Theory]
    [InlineData(1,2026,0,true)]
    [InlineData(1,2026,1900,true)]
    [InlineData(1,2026,2026,false)]
    [InlineData(0,2026,0,false)]
    [InlineData(1,1900,0,false)]
    public void FinishedOrUnidentifiedCommandIsNotInFlight(int session,int start,int end,bool active)
    {
        var command=new OwnedCancellationVerification.ActiveCommand(session,new(start,1,1),end==0?null:new DateTime(end,1,2),0);
        Assert.Equal(active,command.IsActive);
    }
    [Fact]
    public void LateSnapshotMustStillIdentifyACommandStartedInsideThisQueryWindow()
    {
        var issued=new DateTimeOffset(2026,10,13,0,0,0,TimeSpan.Zero);
        var active=new OwnedCancellationVerification.ActiveCommand(1,issued.UtcDateTime.AddSeconds(1),null,1500);
        Assert.True(OwnedCancellationVerification.WasStartedForQuery(active,issued,TimeSpan.FromSeconds(2)));
        Assert.False(OwnedCancellationVerification.WasStartedForQuery(active with { StartedUtc=issued.UtcDateTime.AddSeconds(-1) },issued,TimeSpan.FromSeconds(2)));
        Assert.False(OwnedCancellationVerification.WasStartedForQuery(active with { StartedUtc=issued.UtcDateTime.AddSeconds(3) },issued,TimeSpan.FromSeconds(2)));
    }
}
