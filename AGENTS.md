# Nitrous

## Keep the notes current

Findings belong in `C:\Users\Jeremy\Documents\Nitrous-TGP-Investigation\TGP-NOTES.md`, not in the
conversation. When a session produces something worth keeping — a protocol detail, a measurement,
a firmware quirk, a rejected hypothesis, or a UI constraint found by testing — append it before
the session ends. Do not wait to be asked: a reboot loses the chat, and re-deriving a result costs
another round trip with a tester.

Write down what was measured, on which machine, and how. Cite the file and line when a finding
explains a specific check.

## Build and publish

Test builds go to `bin\publishN` and are reported with their SHA-256:

```powershell
dotnet publish app/Nitrous.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:Version=1.0.0 -p:DebugType=none -p:DebugSymbols=false -o bin\publishN
```

Never tag or publish a release unless asked.

## Editing XAML

The `edit` tool rewrites whole XAML files. For `app/Ui/NitrousDashboard.xaml`, splice by line index
in a PowerShell script and re-read the region afterwards to confirm the indentation survived. The
file mixes tabs and spaces, so match on line numbers rather than on whitespace.

## Ground rules

- No speculative hardware writes. Every opcode and value must come from observed Acer behaviour or
  from static evidence in Acer's own software.
- Never wake a sleeping dGPU.
- GPU offsets are positive-only: core `0..300`, memory `0..1000`.
- When a capability cannot be proven, disable or hide the control and say why, rather than letting
  it look like it worked.
