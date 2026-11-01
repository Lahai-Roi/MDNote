# MDNote vendored AvalonEdit
Upstream: https://github.com/icsharpcode/AvalonEdit
Commit: 862415d51eddc9eac93f462dbc522ffbf929cd52 (NuGet 6.3.1.120)
License: MIT, see LICENSE. Original notices retained.

Local modifications:
- TextView.LineHeight dependency property and matching paragraph/default metrics.
- U+2028/U+2029 recognition in the line tree and newline utilities.
- Classic Framework MSBuild project and explicit assembly versions for this build environment.

Build with Framework64 MSBuild and Microsoft.Net.Compilers 3.11.0 tools:
MSBuild.exe MDNote.AvalonEdit.csproj /p:CscToolPath=<compiler tools folder>
The WPF markup compiler requires the normal desktop build environment.
Do not replace lib/ICSharpCode.AvalonEdit.dll with upstream NuGet DLL without
removing the local LineHeight API calls from SourceEditor/ReadingLayout.
- DefaultLineHeight changes recalculate augmented height metrics in one postorder traversal (O(n)).
- 2026-10-23: IME SetCompositionFont uses the caret run's em size/typeface and negative DPI-scaled LOGFONT height, not the positive line/cell box height. This fixes native caret updates synchronously, including heading and sized-span composition.
- 2026-10-24: IME GetCompositionFont exposes the native setter's size/typeface calculation for probes and uses GetOrConstructVisualLine for the caret line, preserving heading em size after edits before visual lines render. Existing element selection is unchanged (heading-start/empty-heading boundary failure not reproduced).
