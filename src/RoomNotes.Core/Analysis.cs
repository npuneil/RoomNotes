using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomNotes.Core;

public sealed record ExtractedField(string? Value, string? Evidence, string Status);
public sealed record AnalysisResult(string Summary, IReadOnlyList<string> Actions,
    IReadOnlyList<string> Risks, IReadOnlyDictionary<string, ExtractedField> Fields,
    IReadOnlyList<string> Warnings, string ModelId, int? Tokens, TimeSpan Elapsed, string Fingerprint);

public sealed class AnalysisFormatException(string message) : Exception(message);

public static class AnalysisParser
{
    private static string StripFence(string output)
    {
        var text = output.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            text = text[7..].Trim();
        else if (text.StartsWith("```")) text = text[3..].Trim();
        if (text.EndsWith("```")) text = text[..^3].Trim();
        return text;
    }

    public static AnalysisResult ParsePasses(string fieldsOutput, string notesOutput,
        string transcript, MeetingTemplate template)
    {
        try
        {
            using var fields = JsonDocument.Parse(StripFence(fieldsOutput), new JsonDocumentOptions { MaxDepth = 12 });
            RequireObject(fields.RootElement);
            using var actionDocument = JsonDocument.Parse(StripFence(notesOutput), new JsonDocumentOptions { MaxDepth = 12 });
            RequireObject(actionDocument.RootElement);
            var actionValue = actionDocument.RootElement.GetProperty("actions");
            if (actionValue.ValueKind != JsonValueKind.String || actionValue.GetString()!.Length > 1200)
                throw new AnalysisFormatException("The action-analysis pass returned an invalid response.");
            var actionText = actionValue.GetString()!.Trim();
            var result = Parse(JsonSerializer.Serialize(new
            {
                summary = template.Name + " draft",
                actions = Array.Empty<string>(),
                risks = Array.Empty<string>(),
                fields = fields.RootElement
            }), transcript, template);
            var action = actionText.Length == 0 ? null : GroundField(transcript,
                new FieldDefinition("agreed_action", "Agreed action", ""), actionText);
            string[] actions = action?.Value is { } value ? [value] : [];
            var commitments = FindCommittedActions(transcript);
            if (commitments.Count > 0) actions = commitments.ToArray();
            var flagged = FindOpenReviews(transcript);
            var warnings = result.Warnings.ToList();
            if (action is null && actionText.Length > 0 && commitments.Count == 0)
                warnings.Add("An action without enough supporting source text was withheld.");
            return result with { Summary = ComposeSummary(template, result.Fields),
                Actions = actions, Risks = flagged, Warnings = warnings };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AnalysisFormatException("The model did not complete the extraction or notes pass. " +
                "Nothing from this attempt was applied. Use short, explicit statements and retry.");
        }
    }

    public static AnalysisResult Parse(string output, string transcript, MeetingTemplate template)
    {
        try
        {
            var text = StripFence(output);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            RequireObject(root);
            var summary = RequireText(root.GetProperty("summary"), 2400);
            var actions = ReadList(root.GetProperty("actions"));
            var risks = ReadList(root.GetProperty("risks"));
            var fields = root.GetProperty("fields");
            RequireObject(fields);
            var result = new Dictionary<string, ExtractedField>();
            var warnings = new List<string>();
            var unmapped = fields.EnumerateObject().Count(p => !template.Fields.Any(f => f.Id == p.Name));
            if (unmapped > 0)
                warnings.Add($"{unmapped} extra model field(s) were ignored; only this template's fields were applied.");
            foreach (var definition in template.Fields)
            {
                var value = fields.GetProperty(definition.Id);
                if (value.ValueKind == JsonValueKind.Null)
                {
                    result[definition.Id] = new(null, null, "Not stated - needs review");
                    continue;
                }
                var quote = RequireText(value, 1200);
                var grounded = GroundField(transcript, definition, quote);
                if (grounded is null)
                {
                    warnings.Add($"{definition.Label}: unsupported extraction withheld.");
                    result[definition.Id] = new(null, null, "Withheld - no matching transcript quote");
                }
                else
                    result[definition.Id] = grounded;
            }
            return new(summary, actions, risks, result, warnings, "", null, TimeSpan.Zero,
                Fingerprint(template.Id, transcript));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AnalysisFormatException("The local model returned incomplete or invalid structured output. " +
                "No form fields were applied. Try a shorter, clearer transcript.");
        }
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() !=
            element.EnumerateObject().Count())
            throw new AnalysisFormatException("The model returned an invalid or duplicate-key object.");
    }

    private static string RequireText(JsonElement element, int limit)
    {
        if (element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()) || element.GetString()!.Length > limit)
            throw new AnalysisFormatException("The model returned an invalid text field. No fields were applied.");
        return element.GetString()!.Trim();
    }

    private static string[] ReadList(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 12)
            throw new AnalysisFormatException("The model returned an invalid analysis list.");
        return element.EnumerateArray().Select(x => RequireText(x, 800)).ToArray();
    }

    public static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    public static string PrepareModelTranscript(string transcript) => Normalize(Regex.Replace(transcript,
        @"(?m)^(?:[A-Z][a-z]+\.?\s+){1,3}[A-Z][a-z]+:\s*", ""));
    public static bool HasEvidence(string transcript, string quote) =>
        Regex.IsMatch(Normalize(transcript), @"(?<![\p{L}\p{N}_])" + Regex.Escape(Normalize(quote)) +
            @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static ExtractedField? GroundField(string transcript, FieldDefinition definition, string quote)
    {
        if (string.IsNullOrWhiteSpace(quote) || quote.Length > 1200) return null;
        foreach (var label in new[] { definition.Label, definition.Id })
        {
            if (quote.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
            {
                quote = quote[(label.Length + 1)..].Trim().Trim('"');
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(quote)) return null;
        if (definition.Id == "participants")
        {
            var names = Regex.Split(quote, @"\s*(?:,|;|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            quote = string.Join(", ", names);
        }
        if (definition.Id is "agreed_action" or "assigned_action" or "decision" or "issue" && HasEvidence(transcript, quote))
        {
            var sentence = Regex.Split(transcript, @"(?:\r?\n){2,}|(?<=[.!?])\s+(?=[A-Z])")
                .Where(text => text.Length <= 1200 && HasEvidence(text, quote)).OrderBy(text => text.Length).FirstOrDefault();
            if (sentence is not null) quote = sentence.Trim();
        }
        var evidence = HasEvidence(transcript, quote) ? quote :
            definition.Id == "participants" ? MatchParticipantList(transcript, quote) : null;
        if (evidence is not null)
            return new(quote, evidence, evidence == quote ? "AI draft - transcript match" : "AI draft - matched participant excerpts");
        if (definition.Id is "issue" or "concern" or "reported_habits" or "agreed_next_step" or "agreed_action" or "decision" or "assigned_action")
        {
            var passage = FindSourcePassage(transcript, quote);
            if (passage is not null)
                return new(passage, passage, "Source passage - review field fit; model wording not used");
        }
        return null;
    }

    private static readonly HashSet<string> FunctionWords = new(
        ("a an the and or but so if to of for from in on at by with as i me my we our you your he she they it its " +
         "is am are was were be been being have has had do did does will would can could should that this those " +
         "there here about into then than just now also very own stated says said patient clinician").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FindOpenReviews(string transcript) =>
        Regex.Split(transcript, @"(?:\r?\n){2,}|(?<=[.!?])\s+(?=[A-Z])")
            .Select(sentence => sentence.Trim())
            .Where(sentence => Regex.IsMatch(sentence,
                @"\b(?:incomplete|unresolved|outstanding|pending)\b|" +
                @"\b(?:haven't|hasn't|not|never|no)\b[^.!?]{0,100}\b(?:reviewed|confirmed|verified|resolved|approved|received)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            .Where(sentence => sentence.Length is > 0 and <= 1200)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> FindCommittedActions(string transcript) =>
        Regex.Split(transcript, @"(?:\r?\n){2,}|(?<=[.!?])\s+(?=[A-Z])")
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length is > 0 and <= 1200 && !sentence.EndsWith('?'))
            .Where(sentence => !Regex.IsMatch(sentence, @"\b(?:not|never|might|if|haven't|hasn't|don't|doesn't)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Where(sentence => Regex.IsMatch(sentence, @"\b(?:[A-Z][a-z]+\s+){1,3}will\s+[a-z]+\b") ||
                Regex.IsMatch(sentence, @"^(?:For now,\s*)?(?:please\s+)?(?:keep|bring|collect|prepare|send|provide)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string ComposeSummary(MeetingTemplate template, IReadOnlyDictionary<string, ExtractedField> fields)
    {
        var keys = template.Id switch
        {
            "legal" => new[] { "client_name", "issue" },
            "patient" => ["patient", "concern"],
            "board" => ["meeting_title", "decision"],
            _ => throw new ArgumentException("Unknown meeting template.")
        };
        var facts = keys.Where(key => fields[key].Value is not null)
            .Select(key => template.Fields.Single(field => field.Id == key).Label + ": " + fields[key].Value).ToArray();
        return facts.Length == 0 ? "Insufficient source-backed details for a summary. Review the transcript." : string.Join("\n\n", facts);
    }

    private static string? FindSourcePassage(string transcript, string proposal)
    {
        static HashSet<string> Words(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value).Where(word => !FunctionWords.Contains(word)).ToHashSet();
        var query = Words(proposal);
        if (query.Count < 3) return null;
        // Recover source text, never a paraphrase. This ranks passages, not factual confidence.
        var passages = Regex.Split(transcript, @"(?:\r?\n){2,}|(?<=[.!?])\s+(?=[A-Z])")
            .Concat(Regex.Split(transcript, @"\r?\n+"))
            .Select(text => text.Trim()).Where(text => text.Length is > 0 and <= 1200)
            .Select(text => new { Text = text, Words = Words(text) })
            .Select(item => new { item.Text, Shared = item.Words.Count(query.Contains), Length = item.Words.Count })
            .Where(item => item.Shared >= 3 && item.Shared >= query.Count * 0.5)
            .OrderByDescending(item => item.Shared).ThenBy(item => item.Length).ToArray();
        return passages.FirstOrDefault()?.Text;
    }
    private static string? MatchParticipantList(string transcript, string value)
    {
        var names = Regex.Split(value, @"\s*(?:,|;|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return names.Length is >= 2 and <= 8 && names.All(name => name.Length >= 2 && HasEvidence(transcript, name))
            ? string.Join(" | ", names.Select(name => $"\"{name}\"")) : null;
    }
    public static string Fingerprint(string templateId, string transcript) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(templateId + "\n" + transcript)));

    public static IReadOnlyList<string> SplitTranscript(string transcript, int limit = 3600)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            throw new ArgumentException("Enter or capture a transcript before analyzing.");
        if (transcript.Length > 60000)
            throw new ArgumentException("This session exceeds 60,000 characters. Analyze shorter sessions.");
        if (limit < 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var chunks = new List<string>();
        var remaining = transcript.Trim();
        while (remaining.Length > limit)
        {
            var split = remaining.LastIndexOfAny(['\n', '.', '?', '!'], limit - 1, limit / 2);
            if (split < 0) split = remaining.LastIndexOf(' ', limit - 1, limit / 2);
            if (split < 0) split = limit - 1;
            chunks.Add(remaining[..(split + 1)].Trim());
            remaining = remaining[(split + 1)..].Trim();
        }
        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    public static AnalysisResult Merge(IReadOnlyList<AnalysisResult> chunks, MeetingTemplate template,
        string transcript, string modelId, TimeSpan elapsed)
    {
        if (chunks.Count == 0) throw new ArgumentException("No analysis chunks were returned.");
        var warnings = chunks.SelectMany(c => c.Warnings).Distinct().ToList();
        var fields = new Dictionary<string, ExtractedField>();
        foreach (var field in template.Fields)
        {
            var candidates = chunks.Select(c => c.Fields[field.Id]).Where(f => f.Value is not null)
                .DistinctBy(f => Normalize(f.Value!).ToUpperInvariant()).ToArray();
            fields[field.Id] = candidates.Length switch
            {
                0 => chunks.Select(c => c.Fields[field.Id]).FirstOrDefault(f => f.Status.StartsWith("Withheld"))
                    ?? new(null, null, "Not stated - needs review"),
                1 => candidates[0],
                _ => new(null, null, "Conflicting excerpts - enter after review")
            };
            if (candidates.Length > 1) warnings.Add($"{field.Label}: conflicting excerpts withheld.");
        }
        return new(
            string.Join("\n\n", chunks.Select((c, i) => chunks.Count == 1 ? c.Summary : $"Part {i + 1}: {c.Summary}")),
            chunks.SelectMany(c => c.Actions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            chunks.SelectMany(c => c.Risks).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            fields, warnings, modelId,
            chunks.All(c => c.Tokens.HasValue) ? chunks.Sum(c => c.Tokens!.Value) : null,
            elapsed, Fingerprint(template.Id, transcript));
    }
}

public sealed class FormField(FieldDefinition definition) : INotifyPropertyChanged
{
    private string _value = "";
    public FieldDefinition Definition { get; } = definition;
    public string Label => Definition.Label + (Definition.Required ? " *" : "");
    public string Hint => Definition.Hint;
    public bool UserEdited { get; private set; }
    public string? Evidence { get; private set; }
    public string Status { get; private set; } = "Awaiting transcript";
    public string Provenance => Evidence is null ? Status : Status.Contains("participant excerpts")
        ? $"{Status}\nSources: {Evidence}" : $"{Status}\nSource: \"{Evidence}\"";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            UserEdited = true;
            Evidence = null;
            Status = "Edited by you - review required";
            NotifyAll();
        }
    }
    public void Apply(ExtractedField extracted)
    {
        if (UserEdited) return;
        _value = extracted.Value ?? "";
        Evidence = extracted.Evidence;
        Status = extracted.Status;
        NotifyAll();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(Value), nameof(UserEdited), nameof(Evidence), nameof(Status), nameof(Provenance) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
