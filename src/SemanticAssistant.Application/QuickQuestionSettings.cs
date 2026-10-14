using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SemanticAssistant.Application;

public sealed record QuickQuestionSet([property: JsonRequired] string Question1,
    [property: JsonRequired] string Question2, [property: JsonRequired] string Question3)
{
    [JsonIgnore] public ImmutableArray<string> Questions => [Question1, Question2, Question3];
    public void Validate()
    {
        foreach (var question in Questions)
            if (question is null || question.Length > 1000 || question.Any(char.IsControl))
                throw new InvalidDataException("快捷問題需為單行文字，最多1000字。");
    }
}

public sealed record QuickQuestionSettings([property: JsonRequired] QuickQuestionSet Local,
    [property: JsonRequired] QuickQuestionSet Cloud)
{
    // Local text follows the supplied reference. Cloud seed is independently chosen;
    // both sets are editable, and no submitted text is silently rewritten.
    public static QuickQuestionSet LocalDefaults => new("介紹這個PowerBI資料模型的基本結構", "分析所有的視覺化頁面，並給出優化建議", "請幫我創建度量值：");
    public static QuickQuestionSet CloudDefaults => new("介紹這個PowerBI語意模型的基本結構", "請對該模型的資料進行全面探索性分析", "請幫我創建度量值：");
    public static QuickQuestionSettings Default => new(LocalDefaults, CloudDefaults);
    private static QuickQuestionSet NormalizeSet(QuickQuestionSet selected, QuickQuestionSet defaults)
    {
        var questions = selected.Questions.Select((question, i) => string.IsNullOrWhiteSpace(question) ? defaults.Questions[i] : question.Trim()).ToArray();
        return new(questions[0], questions[1], questions[2]);
    }
    public QuickQuestionSettings Normalize() => new(NormalizeSet(Local, LocalDefaults), NormalizeSet(Cloud, CloudDefaults));
    public ImmutableArray<string> For(bool cloud)
    {
        var normalized = Normalize();
        return (cloud ? normalized.Cloud : normalized.Local).Questions;
    }
    public void Validate()
    {
        if (Local is null || Cloud is null) throw new InvalidDataException("缺少本地或雲端快捷問題設定。");
        Local.Validate(); Cloud.Validate();
    }
}
