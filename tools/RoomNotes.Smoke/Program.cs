using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoomNotes.Core;
using RoomNotes.Speech;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: RoomNotes.Smoke <ggml-base.en.bin> <synthetic-WAV-directory|--text-only> [legal|patient|board]");
    return 2;
}
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(12));
using var speech = new LocalSpeechEngine();
using var foundry = new FoundryLocalClient();
var timer = Stopwatch.StartNew();
var textOnly = args[1] == "--text-only";
if (!textOnly)
{
    await speech.InitializeAsync(args[0], cancel.Token);
    Console.WriteLine($"Speech model loaded, CPU ARM64, {timer.Elapsed.TotalSeconds:0.0}s; microphones available: {MicrophoneSession.GetDevices().Count}");
    var silence = await speech.TranscribeAsync(new float[16000 * 2], cancel.Token);
    if (silence.Length > 0) throw new InvalidOperationException("Silence produced a hallucinated transcript.");
}
var runtime = await foundry.ConnectAsync(cancel.Token);
Console.WriteLine($"Real generation ready: {runtime.ModelId}, {runtime.ExecutionProvider}, loopback {runtime.Endpoint}");
var selected = MeetingTemplate.All.Where(t => args.Length == 2 || t.Id == args[2]).ToArray();
if (selected.Length == 0) throw new ArgumentException("Unknown showcase template.");
foreach (var template in selected)
{
    var input = template.SampleTranscript;
    if (!textOnly)
    {
        var speechWatch = Stopwatch.StartNew();
        input = await speech.TranscribeWaveAsync(Path.Combine(args[1], template.Id + ".wav"),
            new Progress<string>(Console.WriteLine), cancel.Token);
        Console.WriteLine($"{template.Id} speech: {speechWatch.Elapsed.TotalSeconds:0.0}s. Fictional transcript: {input}");
    }
    var result = await foundry.AnalyzeAsync(input, template, new Progress<string>(Console.WriteLine), cancel.Token);
    var fields = template.Fields.Select(f => new FormField(f)).ToList();
    foreach (var field in fields) field.Apply(result.Fields[field.Definition.Id]);
    var count = fields.Count(f => f.Value.Length > 0);
    Console.WriteLine($"{template.Name}: {count}/{fields.Count} evidence-matched fields, " +
        $"{result.Actions.Count} actions, {result.Risks.Count} concerns, {result.Elapsed.TotalSeconds:0.0}s, " +
        $"tokens={result.Tokens?.ToString() ?? "not reported"}");
    foreach (var field in fields) Console.WriteLine($"  {field.Definition.Id}: {field.Value} [{field.Status}]");
    Console.WriteLine($"  Summary: {result.Summary}\n  Actions: {string.Join("; ", result.Actions)}\n  Concerns: {string.Join("; ", result.Risks)}");
    if (count != 6) throw new InvalidOperationException($"{template.Name}: all six sample fields must populate.");
    var expected = template.Id switch
    {
        "legal" => new Dictionary<string, string[]>
        {
            ["client_name"] = ["Cedar Works"], ["participants"] = ["Morgan Lee", "Alex Chen"],
            ["meeting_date"] = ["September 27", "2026"], ["issue"] = ["disputed", "software renewal invoice"],
            ["agreed_action"] = ["Alex Chen", "collect", "signed agreement"], ["follow_up"] = ["October 2", "2026"]
        },
        "patient" => new Dictionary<string, string[]>
        {
            ["patient"] = ["Taylor Reed"], ["clinician"] = ["Casey Stone"],
            ["concern"] = ["trouble sleeping", "two weeks"], ["reported_habits"] = ["coffee", "afternoon"],
            ["agreed_next_step"] = ["sleep diary", "seven days"], ["follow_up"] = ["October 5", "2026"]
        },
        _ => new Dictionary<string, string[]>
        {
            ["meeting_title"] = ["Harbor Cooperative", "service desk review"], ["participants"] = ["Jordan Patel", "Sam Rivera"],
            ["meeting_date"] = ["September 27", "2026"], ["decision"] = ["approved", "service desk pilot"],
            ["assigned_action"] = ["Sam Rivera", "prepare", "pilot budget"], ["deadline"] = ["October 9", "2026"]
        }
    };
    foreach (var (key, phrases) in expected)
    {
        static string NormalizeSpokenNumber(string value) => Regex.Replace(value,
            @"\b(two|seven)\b", match => match.Value.Equals("two", StringComparison.OrdinalIgnoreCase) ? "2" : "7",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var value = NormalizeSpokenNumber(fields.Single(f => f.Definition.Id == key).Value);
        if (phrases.Any(phrase => !AnalysisParser.HasEvidence(value, NormalizeSpokenNumber(phrase))))
            throw new InvalidOperationException($"{template.Name}: field {key} lost or altered a scripted fact.");
    }
    var expectedFact = template.Id switch { "legal" => "Cedar", "patient" => "Taylor", _ => "pilot" };
    var expectedField = template.Id switch { "legal" => "client_name", "patient" => "patient", _ => "decision" };
    if (!fields.Single(f => f.Definition.Id == expectedField).Value.Contains(expectedFact, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{template.Name}: the essential fact did not reach its target field.");
    if (result.Actions.Count == 0 || result.Risks.Count == 0 || result.Summary.Length == 0)
        throw new InvalidOperationException($"{template.Name}: the notes pass lost a required sample output.");
    var flagTopic = template.Id switch { "legal" => "signed agreement", "patient" => "medication list", _ => "security review" };
    if (!result.Risks.Any(flag => flag.Contains(flagTopic, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"{template.Name}: the unresolved review was not flagged.");
    var json = ReviewedExport.Create(template, input, result, fields, true, false);
    using var exported = JsonDocument.Parse(json);
    if (exported.RootElement.GetProperty("transcript").ValueKind != JsonValueKind.Null)
        throw new InvalidOperationException("Transcript unexpectedly included in export.");
}
Console.WriteLine($"PASS: {(textOnly ? "sample text" : "real local speech")} -> QNN analysis -> 6/6 evidence-backed fields -> reviewed JSON, " +
    $"{timer.Elapsed.TotalSeconds:0.0}s");
return 0;
