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
- 2026-11-08: Caret.CalcCaretRectangle takes the height of the text run the caret stands on (nearest document run before it, else after it, else any usable run: an empty heading's hidden prefix), on the line's baseline (ascent = em*FontFamily.Baseline, height = em*LineSpacing). Before, every caret was DefaultLineHeight tall, so a heading's caret was body-sized (11-08 user).
- 2026-11-08: Caret.PendingEmSize (Func<double>) and Caret.Refresh(): a font size picked before typing sets the caret's em size until the text is typed (11-08 user: the caret kept the old height until the first syllable composed).
- 2026-11-08: Caret.Refreshed event, raised by Refresh(), so a host that paints its own caret (MDNote's table cell caret) follows a pending size.
- 2026-11-09: TextArea's hide-cursor-while-typing calls user32 ShowCursor(true/false) instead of System.Windows.Forms.Cursor.Show()/Hide(), so typing and mouse moves no longer load the WinForms assemblies. The isMouseCursorHidden balance is unchanged (one Show per Hide).
- 2026-11-22: ImeSupport rebuilds its IME context on WM_NCACTIVATE (window coming to the front) when the context was bound while the window was not the foreground window (boundInBackground), the same Clear/Create WM_INPUTLANGCHANGE does. At launch the TextArea takes focus before the window is in front; a context bound only then left the TSF Korean IME off with Han/Eng dead until focus left the window (user report, 2.5.2 too; VM: failing launches bound only before WM_NCACTIVATE, passing ones after it; with the rebuild 10/10 launches typed Hangul). ImeSupport also writes Update/Create/Clear lines to the host's MDNOTE_IME_TRACE file when that variable is set (nothing otherwise).
- 2026-11-22: ImeSupport.KeepCompositionWindow (Func<bool>, set through the public TextArea.KeepImeCompositionWindow): when it returns true, UpdateCompositionWindow still sets the composition font but skips the caret composition-window write. MDNote's SourceEditor keeps the form at the composition start while a ja/zh string is drawn inline (GitHub #5: Rime/Weasel showed its candidate window at the string end for ~30 ms). Rebuilt with Framework64 MSBuild /t:Rebuild /p:Configuration=Release and tools\build\roslyn-3.11\tools; the HEAD source rebuilt the same way had the same type/member list and resources as the shipped 2.5.2 DLL (size differs by build environment only).
