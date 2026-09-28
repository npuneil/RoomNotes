namespace RoomNotes.Core;

public sealed record FieldDefinition(string Id, string Label, string Hint, bool Required = false);

public sealed record MeetingTemplate(string Id, string Name, string Description,
    IReadOnlyList<FieldDefinition> Fields, string SampleTranscript)
{
    public static IReadOnlyList<MeetingTemplate> All { get; } =
    [
        new("legal", "Legal consultation", "Draft matter intake. No legal advice or privilege determination.",
        [
            new("client_name", "Client / matter", "The stated client name", true),
            new("participants", "Participants", "People explicitly named"),
            new("meeting_date", "Meeting date", "Use the date exactly as spoken"),
            new("issue", "Issue discussed", "The concern stated in the conversation"),
            new("agreed_action", "Agreed next action", "An action explicitly agreed, not a recommendation"),
            new("follow_up", "Follow-up / deadline", "Only a stated follow-up date or deadline")
        ],
        ReadSample("legal")),
        new("patient", "Patient consultation", "Draft visit notes. No diagnosis, prescribing, or clinical decision support.",
        [
            new("patient", "Patient", "The patient name stated in the conversation", true),
            new("clinician", "Clinician", "The clinician explicitly identified"),
            new("concern", "Reported concern", "The patient's own words describing why they came"),
            new("reported_habits", "Reported routine / history", "The patient's reported habits or routine changes, not the clinician's review"),
            new("agreed_next_step", "Discussed next step", "What the clinician asked the patient to do or bring, not the appointment date"),
            new("follow_up", "Follow-up", "The stated follow-up arrangement")
        ],
        ReadSample("patient")),
        new("board", "Board session", "Draft minutes and action register. Decisions must be explicitly stated.",
        [
            new("meeting_title", "Meeting / organization", "The named meeting or organization", true),
            new("participants", "Participants", "People explicitly named"),
            new("meeting_date", "Meeting date", "Use the date exactly as spoken"),
            new("decision", "Decision", "A decision explicitly made, not a proposal"),
            new("assigned_action", "Action / owner", "The full agreed assignment, including who will do what"),
            new("deadline", "Deadline", "A stated due date")
        ],
        ReadSample("board"))
    ];

    private static string ReadSample(string id)
    {
        using var stream = typeof(MeetingTemplate).Assembly.GetManifestResourceStream($"RoomNotes.Samples.{id}.txt")
            ?? throw new InvalidOperationException($"The packaged {id} sample script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
