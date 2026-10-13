using System.Collections.Immutable;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public enum AssistantMode { Analyze, Report, HtmlFile, HtmlMeasure, Dashboard }
public enum ModeRoute { Conversation, HtmlFile, HtmlMeasure, Dashboard }
public sealed record ModeDefinition(AssistantMode Mode, string Label, ModeRoute Route,
    ConversationMode PermissionMode, ImmutableHashSet<Capability> Capabilities);
public sealed record ModeSnapshot(Guid ControllerId, long Version, AssistantMode Mode, ModelIdentity Target);
public sealed class ModePolicyException() : InvalidOperationException("操作模式、來源或授權已變更。");

// Describes permitted operations. Selection never creates capabilities or approvals.
public sealed class ModeController
{
    private readonly object sync = new();
    private ModeSnapshot current;
    public static ImmutableArray<ModeDefinition> Definitions { get; } =
    [
        new(AssistantMode.Analyze, "結構與分析", ModeRoute.Conversation, ConversationMode.Analyze, [Capability.MetadataRead, Capability.DataQuery, Capability.ExternalTool]),
        new(AssistantMode.Report, "報告模式", ModeRoute.Conversation, ConversationMode.Report, [Capability.MetadataRead, Capability.DataQuery, Capability.ExternalTool]),
        new(AssistantMode.HtmlFile, "HTML 檔案", ModeRoute.HtmlFile, ConversationMode.Report, [Capability.ArtifactWrite]),
        new(AssistantMode.HtmlMeasure, "HTML 度量值", ModeRoute.HtmlMeasure, ConversationMode.Analyze, [Capability.MetadataRead, Capability.DefinitionWrite]),
        new(AssistantMode.Dashboard, "Dashboard", ModeRoute.Dashboard, ConversationMode.Report,
            [Capability.MetadataRead, Capability.DefinitionWrite, Capability.DataQuery, Capability.ArtifactWrite])
    ];

    public ModeController(AssistantMode mode, ModelIdentity target)
    { Get(mode); current = new(Guid.NewGuid(), 1, mode, target); }
    public ModeSnapshot Snapshot { get { lock (sync) return current; } }
    public ModeSnapshot Change(AssistantMode mode, ModelIdentity target)
    {
        Get(mode);
        lock (sync) return current = current with { Version = current.Version + 1, Mode = mode, Target = target };
    }
    public static ModeDefinition Get(AssistantMode mode) => Definitions.FirstOrDefault(d => d.Mode == mode) ?? throw new ModePolicyException();
    public void EnsureRoute(ModeSnapshot snapshot, ModeRoute route)
    {
        lock (sync) if (snapshot != current || Get(snapshot.Mode).Route != route) throw new ModePolicyException();
    }
    public void Check(ModeSnapshot snapshot, PermissionSnapshot permissions, PermissionSession session, Capability capability)
    {
        lock (sync)
        {
            var definition = Get(snapshot.Mode);
            if (snapshot != current || permissions.Target != snapshot.Target || permissions.Mode != definition.PermissionMode ||
                !session.IsCurrent(permissions) || !permissions.Allows(capability) || !definition.Capabilities.Contains(capability))
                throw new ModePolicyException();
        }
    }
    public bool Allows(ModeSnapshot snapshot, Capability capability)
    { lock (sync) return snapshot == current && Get(snapshot.Mode).Capabilities.Contains(capability); }
}
