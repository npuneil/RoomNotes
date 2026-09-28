# RoomNotes

A standalone **native WinUI 3 / ARM64** meeting workspace for Windows 11 on
Snapdragon. Combines the Live Assist and Meeting Notes workflow from the Contoso
demo without its Flask server, browser, Python environment, cloud integrations,
or other tabs.

**Microphone or WAV -> local speech-to-text -> local analysis -> editable form ->
human-reviewed JSON export.**

## Get started

```powershell
git clone https://github.com/npuneil/RoomNotes.git
Set-Location .\RoomNotes
```

Follow [Build and first-time setup](#build-and-first-time-setup) to install the
prerequisites, download the speech model, and publish the app. After that, launch
it from the repository directory:

```powershell
.\run.ps1
```

This repository contains source code, setup scripts, tests, and **fictional**
demo conversations. It does not include model weights, executable builds,
credentials, recordings, or exported sessions. Setup downloads the speech model
from its upstream host; Foundry Local manages its own language-model cache.

The published executable is `dist\RoomNotes-win-arm64\RoomNotes.App.exe`.
Distribute the **entire folder**, not just the executable. It includes .NET,
WinUI, Whisper's ARM64 native libraries, and the speech model. Foundry Local and
its cached QNN language model remain separate prerequisites. No MSIX registration
or developer-mode change is required.

## Workflow

1. Wait for the separate speech and analysis readiness indicators. Readiness
   for analysis includes a real generated response, not merely a listening port.
2. Choose **Legal consultation**, **Patient consultation**, or **Board session**.
   These templates produce draft documentation, not advice, diagnoses, privilege
   determinations, or automatic decisions.
3. Select a microphone, confirm capture permission, and press **Start microphone**.
   The input meter shows capture level. After eight seconds, capture waits for
   a phrase pause (at least 0.7 seconds) before transcribing, with a 25-second
   maximum chunk size. Press **Stop** to flush the final chunk.
   **Analyze and fill when capture stops** is enabled by default.
4. Alternatively, paste a transcript, load a clearly labeled **fictional text
   sample**, or import a local mono/stereo PCM or float **WAV** (up to 30 minutes).
   Text samples still use the real local model for analysis; they do not simulate
   speech recognition. WAV files are transcribed without changing the source.
5. **Analyze + auto-fill form** produces a source-backed summary from extracted
   facts and extracts agreed actions. The model's freeform summary is not shown:
   the rendered summary uses grounded field values to avoid introducing causes,
   diagnoses, gender, or commitments that were never stated.
   A separate deterministic phrase scan flags incomplete, outstanding, or
   explicitly unreviewed items using only original transcript sentences. These
   are labeled **Open reviews / transcript flags**, not a model risk judgment.
   Form values must match a contiguous transcript excerpt (ignoring
   whitespace/case, but not allowing partial words or numbers). Participant lists
   can combine separately matched names from different turns; each source is shown.
   Unsupported or conflicting extractions are withheld with
   a visible explanation. This checks textual evidence, not semantic correctness.
6. Edit the fields. Manual edits survive re-analysis, including deliberate
   clearing. New transcript edits invalidate review; re-analyze before exporting.
   Start a **New session** to change templates or discard a draft.
7. Review all results and complete the required field (`*`). Check the review box
   and **Save reviewed form (.json)**. The full transcript is excluded unless
   explicitly selected. Excerpts used as field evidence are included. No CRM,
   EHR, browser form, or external system is submitted to automatically.

## What actually runs

| Stage | Engine | Hardware |
|---|---|---|
| Speech | Whisper Base English, Whisper.net / whisper.cpp | Local ARM64 **CPU**, not NPU |
| Analysis and field extraction | Foundry Local, `qwen2.5-1.5b-instruct-qnn-npu:3` on this device | Snapdragon **NPU**, QNN |
| UI and audio capture | WinUI 3 / NAudio WinMM | Native ARM64 process |

Foundry's dynamic endpoint is discovered through its JSON CLI before analysis.
Only loopback HTTP is accepted; HTTP redirects and proxies are disabled. Text
inference is serialized through the streamed response. Each chunk uses two
focused passes (structured form extraction, then an extractive agreed-action
check), with 512- and 192-token output budgets and a three-minute timeout.
Inputs are split at up to 3,600
characters without silently discarding earlier portions. Conflicting form
excerpts across chunks require manual review. There is no CPU/GPU fallback for
the language model and no cloud speech or language fallback.

The separate passes avoid asking the small on-device model to generate a deeply
nested schema in one response. The second pass returns only an action candidate
in a single-key JSON object. The grounded action is used to fill the form's next-action
field. Extra model keys are explicitly reported and
ignored, never inserted into the form. Missing required response keys, invalid
types, duplicate keys, and incomplete streams remain errors; the prior draft is
preserved rather than replaced by an empty success.
If an extracted value lacks a source match, a single focused NPU verification
pass asks for an exact quote for that field. The same evidence checks still
apply; an unsuccessful verification leaves the field blank and visibly flagged.
For narrative fields, a model paraphrase can instead locate an overlapping
source sentence. Only the **original source sentence** is inserted, never the
paraphrase, and it is labeled **Source passage - review field fit**. This is
lexical retrieval, not a confidence score or semantic verification. Identity and
date fields cannot use this recovery. Human review remains mandatory.
An explicit named-person commitment ("Alex Chen will collect ...") or direct
next-step instruction is retained as its full source sentence, ahead of a model
paraphrase. Questions and negated commitments are excluded by this conservative
phrase rule. The workflow is a hybrid of local model extraction and source-text
rules, not an autonomous decision maker.

This build intentionally targets Snapdragon ARM64 and English speech. It does
not provide speaker diarization, system/Teams audio capture, PDF/Word form
mapping, translation, clinical decision support, or automatic external submission.
Chunked speech recognition can split words and misrecognize names; edit the
transcript before analysis. A bounded audio queue stops capture explicitly if
speech processing cannot keep up. Sessions are limited to 60,000 transcript
characters; long analyses may take multiple sequential model calls.

## Privacy boundaries

- RoomNotes starts with the microphone **off**. Capture requires an explicit
  permission checkbox and Start action.
- RoomNotes keeps captured audio buffers, transcripts, and drafts in process
  memory. It does not write recordings, autosave sessions, log prompts, send
  telemetry, or call an internet AI API. Audio buffers are cleared after use.
- Transcript excerpts are sent to the **local Foundry process** for inference.
  Foundry has its own diagnostics and lifecycle. RoomNotes cannot guarantee
  another process, OS paging/crash dumps, endpoint software, or a shared device
  never retains information. It is not a legal privilege or compliance guarantee.
- Export is explicit. Files, including evidence excerpts, may be synced if the
  chosen folder is OneDrive or another sync location. Existing input recordings
  and exported files are not deleted by New session or app exit.
- Initial dependency/model setup requires internet. With models cached and the
  local runtime available, inference uses no cloud service. RoomNotes never
  downloads a model during a meeting.

## Build and first-time setup

Prerequisites: Windows 11 ARM64 on Snapdragon, .NET 9 SDK, official Microsoft
Foundry Local CLI 0.10+, the ARM64 Visual C++ runtime, and microphone access for
desktop apps. The app uses Windows App SDK 2.2.0 and is self-contained when
published.

```powershell
winget install Microsoft.FoundryLocal
foundry model download qwen2.5-1.5b
foundry model load qwen2.5-1.5b
.\setup-model.ps1
.\publish.ps1
.\run.ps1
```

`setup-model.ps1` downloads `ggml-base.en.bin` (147,964,211 bytes) from the pinned
whisper.cpp model revision `5359861c739e955e79d9a303bcbc70fb988958b1`. It verifies
SHA-256 `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002`
before activating it. The app also verifies this hash when loading the model.
The setup script is idempotent and deletes failed partial downloads.

## Validation

```powershell
dotnet test .\tests\RoomNotes.Tests\RoomNotes.Tests.csproj
dotnet build .\src\RoomNotes.App\RoomNotes.App.csproj -c Release -p:Platform=ARM64
```

The unit suite covers extraction validation, missing/invalid output, bounded
chunking, conflict handling, edit preservation, local endpoint constraints, and
review/staleness/export gates, including the first-run malformed-output
regression. To exercise **all three templates** using the exact sample text and
fictional synthesized speech (not the microphone):

```powershell
.\verify.ps1
```

The script runs unit tests, analyzes all text samples, synthesizes all three
scripts locally, transcribes actual WAV bytes with Whisper, and runs both NPU
passes on each transcript. Every run must populate **all six** transcript-matched
fields, preserve the names, dates and substantive facts in every field, produce
summary/actions, flag the actual unresolved review,
and pass reviewed-export checks. Generated audio is removed afterward. No
microphone or customer data is used. `.\verify.ps1 -SkipAudio` runs text-only
hardware checks in addition to the unit tests.

For native UI checks on a fresh demo instance, run
`.\tools\verify-ui.ps1 -AppProcessId <RoomNotes process ID>`. It replaces that
instance's current session with fictional samples, exercises each template,
checks review/export gating and preservation of a manual edit across re-analysis,
and leaves a board draft open. Do not run it against a session you want to retain.

## Showcase scripts

`demo-scripts\legal.txt`, `patient.txt`, and `board.txt` are the single source for
both the app's **Load fictional sample** action and synthesized-audio checks.
These are multi-turn fictional conversations, not dictated form labels. They
include background, questions, clarification, and an agreed follow-up. Facts are
distributed across the exchange rather than presented in output-field order.
For a live demonstration, two people can read the parts without speaking the
speaker labels. Pause briefly between sentences, not in the middle of names or dates.
Each conversation supports six target fields and a specific unresolved concern:

| Script | Documentation outcome | Review moment |
|---|---|---|
| Legal | Cedar Works intake, invoice issue, agreement-collection action, follow-up | The signed agreement has not been reviewed |
| Patient | Taylor Reed visit note, reported concern/history, discussed plan, follow-up | The medication list has not been reviewed |
| Board | Harbor Cooperative decision, action owner, deadline | Vendor security review is incomplete |

Synthesized-audio checks remove the speaker labels before speaking, so names
must actually occur in the conversation. No labels are used as an audio shortcut.

The scripts are fictional documentation exercises. They contain no real client
or patient information, and no model results are hardcoded. The legal and patient
workflows extract what was said rather than advise what should be done. Correct
transcription errors before analysis. Missing facts should remain blank; the
demo is not meant to reward filling every field when the conversation lacks it.

## Troubleshooting

- **Speech unavailable:** run `setup-model.ps1`, republish, and keep the complete
  output folder together. Check the ARM64 Visual C++ runtime if native DLLs
  cannot load. Text input and NPU analysis can operate independently.
- **Microphone unavailable:** check Windows Settings > Privacy & security >
  Microphone > Let desktop apps access your microphone. Reconnect to refresh
  the input-device list. Capture uses the selected device, not system audio.
- **NPU unavailable / timeout:** use `foundry server status -o json` and
  `foundry cache list -o json`, then reconnect in the app. RoomNotes does not
  unload another app's models or kill the shared Foundry server.
- **Malformed model output:** no fields from that attempt are applied. Shorten
  or clarify the transcript and retry. Previous edits are retained.
- **Unfilled fields:** null means not stated; withheld means unsupported or
  conflicting. Read the transcript and enter reviewed values manually.

## License and dependencies

RoomNotes uses the [MIT License](LICENSE), retaining the original 2026 MIT
copyright notice from the Contoso demo from which its Live Assist and Meeting
Notes workflow was reimplemented. It has no dependency on that original app.
RoomNotes uses the MIT-licensed
[Whisper.net](https://github.com/sandrohanea/whisper.net),
[whisper.cpp](https://github.com/ggml-org/whisper.cpp),
[OpenAI Whisper weights](https://github.com/openai/whisper),
[NAudio](https://github.com/naudio/NAudio), and
[Windows App SDK](https://github.com/microsoft/WindowsAppSDK).
Qwen2.5-1.5B is distributed through Foundry's model catalog under its listed
license. Preserve dependency/model licenses when redistributing.
