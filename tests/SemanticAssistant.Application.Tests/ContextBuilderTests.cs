using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ContextBuilderTests
{
    [Fact]
    public void SummaryContainsNamesButNoDefinitions()
    {
        var snapshot = DemoModel.Snapshot with
        {
            Tables = [new("synthetic-table", [new("secret-column", "String", "secret-expression")],
                [new("secret-measure", "secret-dax")])]
        };
        var context = new ModelContextBuilder().Build(snapshot);
        Assert.Contains("synthetic-table", context);
        Assert.DoesNotContain("secret-", context);
        Assert.True(context.Length <= 2_000);
    }

    [Fact]
    public void PreferredNamesAreOrderedDeduplicatedAndBounded()
    {
        var snapshot = Tables("a", "b", "c");
        var context = new ModelContextBuilder(new PreferredContextTables(["c", "missing", "c"]),
            new(MaxTables: 2)).Build(snapshot);
        Assert.Contains("\"c\", \"a\"", context);
        Assert.DoesNotContain("\"b\"", context);
        Assert.DoesNotContain("missing", context);
    }

    [Fact]
    public void UnknownAndEndlessDuplicateSelectionIsBounded()
    {
        var selector = new DuplicateSelector();
        var context = new ModelContextBuilder(selector).Build(Tables("known"));
        Assert.Equal(257, selector.Iterations);
        Assert.Equal(1, context.Split("\"known\"").Length - 1);
        Assert.DoesNotContain("invented", context);
    }

    [Fact]
    public void OversizedNameDoesNotHideLaterSmallNames()
    {
        var context = new ModelContextBuilder(budget: new(MaxCharacters: 128))
            .Build(Tables(new string('x', 300), "fit"));
        Assert.True(context.Length <= 128);
        Assert.DoesNotContain("xxx", context);
        Assert.Contains("\"fit\"", context);
    }

    [Fact]
    public void DefaultNeverIncludesMoreThanTwentyTables()
    {
        var context = new ModelContextBuilder().Build(Tables(Enumerable.Range(0, 25).Select(i => $"t{i}").ToArray()));
        Assert.Contains("\"t19\"", context);
        Assert.DoesNotContain("\"t20\"", context);
    }

    [Fact]
    public void ControlCharactersAreEncodedAndLongSourceIsOmitted()
    {
        var snapshot = Tables("line\n\"quoted\"") with
        {
            Identity = DemoModel.Snapshot.Identity with { Name = new string('中', 1_000) + "🌱" }
        };
        var context = new ModelContextBuilder(budget: new(MaxCharacters: 128)).Build(snapshot);
        Assert.True(context.Length <= 128);
        Assert.DoesNotContain('\n', context);
        Assert.DoesNotContain("\\u4E2D", context);
        Assert.Contains(JsonSerializer.Serialize("line\n\"quoted\""), context);
    }

    [Theory]
    [InlineData(0, 2_000)]
    [InlineData(21, 2_000)]
    [InlineData(20, 127)]
    [InlineData(20, 2_001)]
    public void HostCannotExpandOrDisableBudget(int tables, int characters) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelContextBuilder(budget: new(tables, characters)).Build(DemoModel.Snapshot));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingReadOrMetadataEgressNeverReadsOrSelects(bool metadataRead, bool external)
    {
        var reader = new ProbeReader();
        var selector = new ProbeSelector();
        var provider = new CaptureProvider(external);
        var consent = external ? new EgressConsent(provider.Identity!, [EgressCategory.UserMessage],
            DateTimeOffset.UtcNow.AddMinutes(1)) : null;
        var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
            metadataRead ? [Capability.MetadataRead] : [], consent);
        var outcome = await Run(reader, provider, selector, session);
        Assert.Equal("ok", outcome.Code);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, selector.Calls);
        Assert.DoesNotContain("synthetic-table", provider.Context);
    }

    [Fact]
    public async Task AuthorizedTurnUsesHostSelectionAtActualProviderBoundary()
    {
        var reader = new ProbeReader();
        var provider = new CaptureProvider(false);
        var selector = new ProbeSelector();
        var session = new PermissionSession(reader.Snapshot.Identity, ConversationMode.Analyze, [Capability.MetadataRead]);
        var outcome = await Run(reader, provider, selector, session);
        Assert.Equal("ok", outcome.Code);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(1, selector.Calls);
        Assert.Contains("synthetic-table", provider.Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrSourceChangeBeforeBuildRejectsContext(bool changeSource)
    {
        var reader = new ProbeReader();
        var selector = new ProbeSelector();
        var provider = new CaptureProvider(false);
        var session = new PermissionSession(reader.Snapshot.Identity, ConversationMode.Analyze, [Capability.MetadataRead]);
        reader.AfterRead = () =>
        {
            if (changeSource) reader.Snapshot = reader.Snapshot with
            { Identity = reader.Snapshot.Identity with { Revision = "changed" } };
            else session.Update(session.Snapshot.Target, session.Snapshot.Mode, []);
        };
        var outcome = await Run(reader, provider, selector, session);
        Assert.Equal(changeSource ? "target_changed" : "stale_permission", outcome.Code);
        Assert.Equal(0, selector.Calls);
        Assert.Null(provider.Context);
    }

    private static ModelSnapshot Tables(params string[] names) => DemoModel.Snapshot with
    { Tables = names.Select(n => new TableDefinition(n, [], [])).ToImmutableArray() };

    private static Task<ConversationOutcome> Run(ProbeReader reader, CaptureProvider provider,
        IContextTableSelector selector, PermissionSession session) =>
        new ConversationCoordinator(reader, provider, new(reader, new DemoModel(), session), session,
            contextBuilder: new(selector)).RunAsync(new(Guid.NewGuid(), "合成摘要", session.Snapshot.Mode,
                session.Snapshot), _ => Task.CompletedTask);

    private sealed class ProbeReader : ISemanticModelReader
    {
        public ModelSnapshot Snapshot { get; set; } = Tables("synthetic-table");
        public int Reads { get; private set; }
        public Action? AfterRead { get; set; }
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; AfterRead?.Invoke();
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class ProbeSelector : IContextTableSelector
    {
        public int Calls { get; private set; }
        public IEnumerable<string> Select(ImmutableArray<string> tableNames) { Calls++; return tableNames; }
    }

    private sealed class DuplicateSelector : IContextTableSelector
    {
        public int Iterations { get; private set; }
        public IEnumerable<string> Select(ImmutableArray<string> tableNames)
        {
            while (true) { Iterations++; yield return Iterations == 1 ? "invented" : "known"; }
        }
    }

    private sealed class CaptureProvider(bool external) : IAiProvider
    {
        public bool UsesExternalNetwork => external;
        public ProviderIdentity? Identity => external ? new("fixture", "https://example.invalid/v1/chat/completions", "synthetic") : null;
        public string? Context { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Context = request.Context;
            await Task.Yield(); yield return new ProviderEvent.Text("合成摘要完成。");
        }
    }
}
