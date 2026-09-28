using System.Text.Json;

namespace RoomNotes.Core;

public static class ReviewedExport
{
    public static string Create(MeetingTemplate template, string transcript, AnalysisResult analysis,
        IReadOnlyList<FormField> fields, bool reviewed, bool includeTranscript)
    {
        if (!reviewed) throw new InvalidOperationException("Review the draft and confirm before exporting.");
        if (analysis.Fingerprint != AnalysisParser.Fingerprint(template.Id, transcript))
            throw new InvalidOperationException("The transcript changed. Analyze the current transcript before exporting.");
        if (fields.Count != template.Fields.Count ||
            !fields.Select(f => f.Definition.Id).ToHashSet().SetEquals(template.Fields.Select(f => f.Id)))
            throw new InvalidOperationException("The form does not match the selected template.");
        if (fields.Any(f => f.Definition.Required && string.IsNullOrWhiteSpace(f.Value)))
            throw new InvalidOperationException("Complete the required form fields before exporting.");
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, application = "RoomNotes", template = template.Id,
            exportedAt = DateTimeOffset.UtcNow, reviewedByUser = true,
            analysis = new { analysis.Summary, analysis.Actions, analysis.Risks, analysis.Warnings,
                summarySource = "source-backed extracted fields",
                reviewFlagsSource = "deterministic incomplete-review phrase scan, not a risk determination" },
            fields = fields.ToDictionary(f => f.Definition.Id, f => new
            {
                value = f.Value, evidence = f.Evidence, userEdited = f.UserEdited, status = f.Status
            }),
            inference = new { model = analysis.ModelId, device = "NPU", provider = "QNNExecutionProvider",
                totalTokens = analysis.Tokens, elapsedSeconds = analysis.Elapsed.TotalSeconds },
            transcript = includeTranscript ? transcript : null,
            notice = "Human-reviewed draft. No clinical or legal determination. Exported files may be synced by the selected folder."
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
