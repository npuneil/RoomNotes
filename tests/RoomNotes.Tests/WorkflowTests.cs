using System.Text.Json;
using RoomNotes.Core;
using Xunit;

namespace RoomNotes.Tests;

public sealed class WorkflowTests
{
    private static readonly MeetingTemplate Template = MeetingTemplate.All[0];
    private const string Transcript = "The client is Cedar Works. Alex will collect the signed agreement. The deadline is October 2, 2026.";
    private static string Output(object? matter = null) => JsonSerializer.Serialize(new
    {
        summary = "The parties discussed the agreement.",
        actions = new[] { "Alex will collect the signed agreement." },
        risks = Array.Empty<string>(),
        fields = Template.Fields.ToDictionary(f => f.Id, f => f.Id == "client_name" ? matter : null)
    });
    private static AnalysisResult Parse(object? matter = null) => AnalysisParser.Parse(Output(matter), Transcript, Template);

    [Fact]
    public void MatchingQuoteFillsFieldWithEvidence()
    {
        var result = Parse("Cedar Works");
        Assert.Equal("Cedar Works", result.Fields["client_name"].Value);
        Assert.Equal("Cedar Works", result.Fields["client_name"].Evidence);
        Assert.Empty(result.Warnings);
        Assert.Null(result.Fields["participants"].Value);
    }

    [Fact]
    public void InventedValueIsWithheldAndWarned()
    {
        var result = Parse("A company never mentioned");
        Assert.Null(result.Fields["client_name"].Value);
        Assert.Single(result.Warnings);
        Assert.Contains("Withheld", result.Fields["client_name"].Status);
    }

    [Fact]
    public void WhitespaceAndCaseAreNormalizedForEvidence()
    {
        var result = Parse("cedar   works");
        Assert.Equal("cedar   works", result.Fields["client_name"].Value);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"summary\": \"a\", \"summary\": \"b\"}")]
    public void InvalidOutputCannotLookSuccessful(string output) =>
        Assert.Throws<AnalysisFormatException>(() => AnalysisParser.Parse(output, Transcript, Template));

    [Fact]
    public void MarkdownFenceIsAccepted() =>
        Assert.NotNull(AnalysisParser.Parse("```json\n" + Output("Cedar Works") + "\n```", Transcript, Template).Fields["client_name"].Value);

    [Fact]
    public void ExtraFieldsAreNeverAppliedAndProduceAWarning()
    {
        var output = Output().Replace("\"fields\":{", "\"fields\":{\"unexpected\":null,");
        var result = AnalysisParser.Parse(output, Transcript, Template);
        Assert.Equal(6, result.Fields.Count);
        Assert.False(result.Fields.ContainsKey("unexpected"));
        Assert.Contains(result.Warnings, warning => warning.Contains("extra model field"));
    }

    [Fact]
    public void MissingFieldIsRejected()
    {
        var output = Output().Replace("\"client_name\":null,", "");
        Assert.Throws<AnalysisFormatException>(() => AnalysisParser.Parse(output, Transcript, Template));
    }

    [Fact]
    public void InvalidFieldTypeIsRejected() => Assert.Throws<AnalysisFormatException>(() => Parse(42));

    [Fact]
    public void DuplicateFieldIsRejected()
    {
        var output = Output().Replace("\"client_name\":null", "\"client_name\":null,\"client_name\":null");
        Assert.Throws<AnalysisFormatException>(() => AnalysisParser.Parse(output, Transcript, Template));
    }

    [Fact]
    public void SplitPreservesEveryWordWithoutExceedingLimit()
    {
        var input = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"Word{i}."));
        var chunks = AnalysisParser.SplitTranscript(input, 200);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 200));
        Assert.Equal(AnalysisParser.Normalize(input), AnalysisParser.Normalize(string.Join(" ", chunks)));
    }

    [Fact]
    public void LongUnbrokenInputStillHasBoundedChunks()
    {
        var input = new string('a', 7201);
        var chunks = AnalysisParser.SplitTranscript(input);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(input, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 3600));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    public void EmptyTranscriptIsRejected(string input) =>
        Assert.Throws<ArgumentException>(() => AnalysisParser.SplitTranscript(input));

    [Fact]
    public void OversizedTranscriptIsRejectedNotTruncated() =>
        Assert.Throws<ArgumentException>(() => AnalysisParser.SplitTranscript(new string('x', 60001)));

    [Fact]
    public void ConflictingChunkValuesAreWithheld()
    {
        var first = Parse("Cedar Works");
        var second = Parse("Alex");
        var merged = AnalysisParser.Merge([first, second], Template, Transcript, "actual-model", TimeSpan.FromSeconds(1));
        Assert.Null(merged.Fields["client_name"].Value);
        Assert.Contains("Conflicting", merged.Fields["client_name"].Status);
        Assert.Single(merged.Warnings);
        Assert.Null(merged.Tokens);
    }

    [Fact]
    public void DuplicateChunkEvidenceIsMergedWithoutConflict()
    {
        var first = Parse("Cedar Works") with { Tokens = 100 };
        var second = Parse("cedar works") with { Tokens = 90 };
        var merged = AnalysisParser.Merge([first, second], Template, Transcript, "actual-model", TimeSpan.FromSeconds(1));
        Assert.Equal("Cedar Works", merged.Fields["client_name"].Value);
        Assert.Equal(190, merged.Tokens);
        Assert.Empty(merged.Warnings);
    }

    [Fact]
    public void ManualEditsSurviveReanalysisAndLoseAiProvenance()
    {
        var field = new FormField(Template.Fields[0]);
        field.Apply(new("Cedar Works", "Cedar Works", "AI draft"));
        field.Value = "Reviewed matter title";
        field.Apply(new("Alex", "Alex", "AI draft"));
        Assert.Equal("Reviewed matter title", field.Value);
        Assert.True(field.UserEdited);
        Assert.Null(field.Evidence);
        Assert.Contains("Edited by you", field.Status);
    }

    [Fact]
    public void ClearingAnAutoFilledFieldIsAlsoPreserved()
    {
        var field = new FormField(Template.Fields[0]);
        field.Apply(new("Cedar Works", "Cedar Works", "AI draft"));
        field.Value = "";
        field.Apply(new("Cedar Works", "Cedar Works", "AI draft"));
        Assert.Equal("", field.Value);
        Assert.True(field.UserEdited);
    }

    [Theory]
    [InlineData("http://127.0.0.1:64393", "http://127.0.0.1:64393/v1/")]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080/v1/")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/v1/")]
    public void OnlyLoopbackEndpointsAreAccepted(string input, string expected) =>
        Assert.Equal(expected, FoundryLocalClient.ValidateEndpoint(input).AbsoluteUri);

    [Theory]
    [InlineData("https://example.com/v1")]
    [InlineData("http://192.168.1.50:8080/v1")]
    [InlineData("http://127.0.0.1.example.com:80")]
    [InlineData("http://user:password@127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/v1?secret=value")]
    [InlineData("http://127.0.0.1:8080/v1#fragment")]
    [InlineData("http://127.0.0.1:8080/somewhere")]
    [InlineData("https://127.0.0.1:8080")]
    public void NonlocalOrAmbiguousEndpointsAreRejected(string input) =>
        Assert.Throws<InvalidOperationException>(() => FoundryLocalClient.ValidateEndpoint(input));

    private static List<FormField> Form()
    {
        var fields = Template.Fields.Select(f => new FormField(f)).ToList();
        fields[0].Value = "Cedar Works";
        return fields;
    }

    [Fact]
    public void ExportRequiresReview() =>
        Assert.Throws<InvalidOperationException>(() => ReviewedExport.Create(Template, Transcript, Parse(), Form(), false, false));

    [Fact]
    public void ExportRequiresCurrentTranscript() =>
        Assert.Throws<InvalidOperationException>(() => ReviewedExport.Create(Template, Transcript + " changed", Parse(), Form(), true, false));

    [Fact]
    public void ExportRequiresMatchingTemplate() =>
        Assert.Throws<InvalidOperationException>(() => ReviewedExport.Create(MeetingTemplate.All[1], Transcript, Parse(), Form(), true, false));

    [Fact]
    public void RequiredFieldsCannotBeEmpty()
    {
        var fields = Form();
        fields[0].Value = "";
        Assert.Throws<InvalidOperationException>(() => ReviewedExport.Create(Template, Transcript, Parse(), fields, true, false));
    }

    [Fact]
    public void FullTranscriptIsOptIn()
    {
        using var document = JsonDocument.Parse(ReviewedExport.Create(Template, Transcript, Parse(), Form(), true, false));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("transcript").ValueKind);
        Assert.True(document.RootElement.GetProperty("reviewedByUser").GetBoolean());
        Assert.Equal(6, document.RootElement.GetProperty("fields").EnumerateObject().Count());
        using var included = JsonDocument.Parse(ReviewedExport.Create(Template, Transcript, Parse(), Form(), true, true));
        Assert.Equal(Transcript, included.RootElement.GetProperty("transcript").GetString());
    }

    [Fact]
    public void EveryTemplateHasUniqueRequiredFieldsAndConversationalSample()
    {
        Assert.Equal(3, MeetingTemplate.All.Count);
        foreach (var template in MeetingTemplate.All)
        {
            Assert.Equal(template.Fields.Count, template.Fields.Select(f => f.Id).Distinct().Count());
            Assert.Contains(template.Fields, f => f.Required);
            Assert.True(template.SampleTranscript.Length > 500);
            Assert.Contains("\n", template.SampleTranscript);
            Assert.DoesNotContain("Client name:", template.SampleTranscript);
            Assert.DoesNotContain("Meeting title:", template.SampleTranscript);
        }
    }

    [Fact]
    public void TwoFocusedPassesProduceAValidatedCombinedResult()
    {
        using var original = JsonDocument.Parse(Output("Cedar Works"));
        var fields = original.RootElement.GetProperty("fields").GetRawText();
        var notes = """{"actions":"Alex will collect the signed agreement."}""";
        var result = AnalysisParser.ParsePasses(fields, notes, Transcript, Template);
        Assert.Equal("Cedar Works", result.Fields["client_name"].Value);
        Assert.Single(result.Actions);
        Assert.Empty(result.Risks);
    }

    [Fact]
    public void FirstRunSummaryOnlyRegressionCannotProduceEmptySuccess()
    {
        var incomplete = """{"summary":"A meeting occurred."}""";
        Assert.Throws<AnalysisFormatException>(() => AnalysisParser.ParsePasses(incomplete, incomplete, Transcript, Template));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    public void EmptyActionPassCannotApplyFields(string notes)
    {
        using var original = JsonDocument.Parse(Output("Cedar Works"));
        var fields = original.RootElement.GetProperty("fields").GetRawText();
        Assert.Throws<AnalysisFormatException>(() => AnalysisParser.ParsePasses(fields, notes, Transcript, Template));
    }

    [Theory]
    [InlineData("Doctor Casey Stone", "Doctor Case")]
    [InlineData("The invoice is undisputed", "disputed")]
    [InlineData("The total is 12000", "2000")]
    public void PartialWordsOrNumbersAreNotEvidence(string transcript, string value) =>
        Assert.False(AnalysisParser.HasEvidence(transcript, value));

    [Fact]
    public void SpeechDoesNotSplitAtEightSecondsInTheMiddleOfAnUtterance()
    {
        var segmenter = new SpeechSegmenter();
        Assert.Empty(segmenter.Append(Enumerable.Repeat(0.2f, 16000 * 10).ToArray()));
        Assert.Empty(segmenter.Append(new float[8000]));
        var chunks = segmenter.Append(new float[4800]);
        Assert.Single(chunks);
        Assert.True(chunks[0].Length >= 16000 * 10);
        Assert.True(segmenter.Flush().Length < 4800);
    }

    [Fact]
    public void SpeechChunksAreBoundedWithoutDroppingSamples()
    {
        var input = Enumerable.Repeat(0.2f, 16000 * 26 + 7).ToArray();
        var segmenter = new SpeechSegmenter();
        var chunks = segmenter.Append(input).Append(segmenter.Flush()).ToArray();
        Assert.Equal(input.Length, chunks.Sum(c => c.Length));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 16000 * SpeechSegmenter.MaximumSeconds));
    }

    [Fact]
    public void ParticipantsCanBeGroundedInDifferentTurns()
    {
        var json = Output().Replace("\"participants\":null", "\"participants\":\"Morgan Lee, Alex Chen\"");
        var result = AnalysisParser.Parse(json, "Morgan Lee: Thanks for joining.\nAlex Chen: Happy to help.", Template);
        Assert.Equal("Morgan Lee, Alex Chen", result.Fields["participants"].Value);
        Assert.Contains("matched participant excerpts", result.Fields["participants"].Status);
        Assert.Contains("\"Alex Chen\"", result.Fields["participants"].Evidence);
    }

    [Fact]
    public void EveryNameInAParticipantListMustHaveEvidence()
    {
        var json = Output().Replace("\"participants\":null", "\"participants\":\"Morgan Lee, Invented Person\"");
        var result = AnalysisParser.Parse(json, "Morgan Lee: Thanks for joining.\nAlex Chen: Happy to help.", Template);
        Assert.Null(result.Fields["participants"].Value);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void VerificationCanRemoveOnlyTheKnownFieldLabel()
    {
        var definition = Template.Fields.Single(f => f.Id == "agreed_action");
        var result = AnalysisParser.GroundField(Transcript, definition,
            "Agreed next action: Alex will collect the signed agreement.");
        Assert.Equal("Alex will collect the signed agreement.", result?.Value);
        Assert.Null(AnalysisParser.GroundField(Transcript, definition,
            "Agreed next action: Someone will invent unsupported commitments."));
    }

    [Fact]
    public void ParaphrasesCanSelectSourceButAreNeverInsertedAsFacts()
    {
        var definition = Template.Fields.Single(f => f.Id == "agreed_action");
        var result = AnalysisParser.GroundField(Transcript, definition, "Alex collects signed agreement");
        Assert.Equal("Alex will collect the signed agreement.", result?.Value);
        Assert.Equal(result?.Value, result?.Evidence);
        Assert.Contains("review field fit", result?.Status);
        Assert.NotEqual("Alex collects signed agreement", result?.Value);
    }

    [Fact]
    public void SourcePassageRecoveryNeverChangesNegationOrAmounts()
    {
        var definition = Template.Fields.Single(f => f.Id == "agreed_action");
        var result = AnalysisParser.GroundField("Alex will not approve the invoice for 12000 dollars.",
            definition, "Alex will approve invoice for 2000 dollars");
        Assert.Equal("Alex will not approve the invoice for 12000 dollars.", result?.Value);
    }

    [Fact]
    public void IdentityFieldsCannotUsePassageRecovery()
    {
        Assert.Null(AnalysisParser.GroundField("Morgan Lee is here from Cedar Works.",
            Template.Fields[0], "Morgan Lee Consulting"));
    }

    [Fact]
    public void ParticipantNamesAreDeduplicated()
    {
        var field = Template.Fields.Single(f => f.Id == "participants");
        var result = AnalysisParser.GroundField("Morgan Lee: Hello.\nAlex Chen: Hello.",
            field, "Alex Chen, Morgan Lee, Alex Chen");
        Assert.Equal("Alex Chen, Morgan Lee", result?.Value);
    }

    [Fact]
    public void ActionQuoteIncludesItsOwnerFromTheSameSentence()
    {
        var field = Template.Fields.Single(f => f.Id == "agreed_action");
        var result = AnalysisParser.GroundField(Transcript, field, "collect the signed agreement");
        Assert.Equal("Alex will collect the signed agreement.", result?.Value);
    }

    [Fact]
    public void IncompleteReviewFlagsQuoteOnlyTheOriginalTranscript()
    {
        const string text = "We haven't reviewed your medication list yet. The security review is incomplete. The budget is approved.";
        var flags = AnalysisParser.FindOpenReviews(text);
        Assert.Equal(2, flags.Count);
        Assert.All(flags, flag => Assert.Contains(flag, text));
        Assert.DoesNotContain(flags, flag => flag.Contains("budget"));
    }

    [Fact]
    public void ModelPreparationRemovesChunkFormattingNotSpokenFacts()
    {
        const string input = "Doctor Casey Stone: I'm Doctor Casey Stone.\n\nTaylor Reed: I'm Taylor Reed.\nI've had trouble sleeping.";
        Assert.Equal("I'm Doctor Casey Stone. I'm Taylor Reed. I've had trouble sleeping.",
            AnalysisParser.PrepareModelTranscript(input));
    }

    [Fact]
    public void DisplayedSummaryUsesOnlyGroundedFields()
    {
        var result = Parse("Cedar Works");
        var summary = AnalysisParser.ComposeSummary(Template, result.Fields);
        Assert.Equal("Client / matter: Cedar Works", summary);
        Assert.DoesNotContain("The parties discussed", summary);
    }

    [Fact]
    public void ADecisionIncludesItsObjectAndLimitation()
    {
        var field = MeetingTemplate.All[2].Fields.Single(f => f.Id == "decision");
        var result = AnalysisParser.GroundField("We've approved the service desk pilot, not a full rollout.", field, "Approved");
        Assert.Equal("We've approved the service desk pilot, not a full rollout.", result?.Value);
    }

    [Fact]
    public void CommitmentsKeepActorsAndExcludeQuestionsAndNegations()
    {
        const string text = "Alex Chen will collect the signed agreement. Shall we review it Friday? Sam Rivera will not approve the budget.";
        Assert.Equal(["Alex Chen will collect the signed agreement."], AnalysisParser.FindCommittedActions(text));
    }

    [Fact]
    public void DirectNextStepIsKeptAsOriginalInstruction()
    {
        const string text = "For now, keep a sleep diary for seven days and bring it with your medication list.";
        Assert.Equal([text], AnalysisParser.FindCommittedActions(text));
    }
}
