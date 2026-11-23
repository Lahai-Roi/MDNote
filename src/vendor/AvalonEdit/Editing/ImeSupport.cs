// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ICSharpCode.AvalonEdit.Editing
{
	class ImeSupport
	{
		readonly TextArea textArea;
		IntPtr currentContext;
		IntPtr previousContext;
		IntPtr defaultImeWnd;
		HwndSource hwndSource;
		EventHandler requerySuggestedHandler; // we need to keep the event handler instance alive because CommandManager.RequerySuggested uses weak references
		bool isReadOnly;

		public ImeSupport(TextArea textArea)
		{
			if (textArea == null)
				throw new ArgumentNullException("textArea");
			this.textArea = textArea;
			InputMethod.SetIsInputMethodSuspended(this.textArea, textArea.Options.EnableImeSupport);
			// We listen to CommandManager.RequerySuggested for both caret offset changes and changes to the set of read-only sections.
			// This is because there's no dedicated event for read-only section changes; but RequerySuggested needs to be raised anyways
			// to invalidate the Paste command.
			requerySuggestedHandler = OnRequerySuggested;
			CommandManager.RequerySuggested += requerySuggestedHandler;
			textArea.OptionChanged += TextAreaOptionChanged;
		}

		void OnRequerySuggested(object sender, EventArgs e)
		{
			UpdateImeEnabled();
		}

		void TextAreaOptionChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "EnableImeSupport") {
				InputMethod.SetIsInputMethodSuspended(this.textArea, textArea.Options.EnableImeSupport);
				UpdateImeEnabled();
			}
		}

		public void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
		{
			UpdateImeEnabled();
		}

		public void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
		{
			if (e.OldFocus == textArea && currentContext != IntPtr.Zero)
				ImeNativeWrapper.NotifyIme(currentContext);
			ClearContext();
		}

		// MDNote: the host's IME trace file (MDNOTE_IME_TRACE), read once; nothing is written when it is unset.
		static readonly string mdnTracePath = Environment.GetEnvironmentVariable("MDNOTE_IME_TRACE");
		static void MdnTrace(string message)
		{
			if (string.IsNullOrEmpty(mdnTracePath)) return;
			try { System.IO.File.AppendAllText(mdnTracePath, DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " avalon " + message + Environment.NewLine); } catch (Exception) { }
		}

		[System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
		static bool MdnForeground(IntPtr hwnd) { return GetForegroundWindow() == hwnd; }
		// MDNote: the current context was bound while the window was not the foreground window (see WM_NCACTIVATE in WndProc).
		bool boundInBackground;
		void RebindInForeground()
		{
			if (hwndSource == null || !textArea.IsKeyboardFocused) return;
			MdnTrace("rebind in foreground");
			ClearContext();
			CreateContext();
		}

		void UpdateImeEnabled()
		{
			if (textArea.Options.EnableImeSupport && textArea.IsKeyboardFocused) {
				bool newReadOnly = !textArea.ReadOnlySectionProvider.CanInsert(textArea.Caret.Offset);
				if (hwndSource == null || isReadOnly != newReadOnly) {
					MdnTrace("update recreate bound=" + (hwndSource != null) + " readOnly=" + isReadOnly + "->" + newReadOnly);
					ClearContext(); // clear existing context (on read-only change)
					isReadOnly = newReadOnly;
					CreateContext();
				}
			} else {
				ClearContext();
			}
		}

		void ClearContext()
		{
			if (hwndSource != null) {
				MdnTrace("clear restore=0x" + previousContext.ToInt64().ToString("X") + " current=0x" + currentContext.ToInt64().ToString("X"));
				ImeNativeWrapper.ImmAssociateContext(hwndSource.Handle, previousContext);
				ImeNativeWrapper.ImmReleaseContext(defaultImeWnd, currentContext);
				currentContext = IntPtr.Zero;
				defaultImeWnd = IntPtr.Zero;
				hwndSource.RemoveHook(WndProc);
				hwndSource = null;
			}
		}

		void CreateContext()
		{
			hwndSource = (HwndSource)PresentationSource.FromVisual(this.textArea);
			if (hwndSource != null) {
				if (isReadOnly) {
					defaultImeWnd = IntPtr.Zero;
					currentContext = IntPtr.Zero;
				} else {
					defaultImeWnd = ImeNativeWrapper.ImmGetDefaultIMEWnd(IntPtr.Zero);
					currentContext = ImeNativeWrapper.ImmGetContext(defaultImeWnd);
				}
				previousContext = ImeNativeWrapper.ImmAssociateContext(hwndSource.Handle, currentContext);
				boundInBackground = !MdnForeground(hwndSource.Handle);
				MdnTrace("create foreground=" + !boundInBackground + " readOnly=" + isReadOnly + " imeWnd=0x" + defaultImeWnd.ToInt64().ToString("X") + " context=0x" + currentContext.ToInt64().ToString("X") + " previous=0x" + previousContext.ToInt64().ToString("X") + " hwnd=0x" + hwndSource.Handle.ToInt64().ToString("X"));
				hwndSource.AddHook(WndProc);
				// UpdateCompositionWindow() will be called by the caret becoming visible

				var threadMgr = ImeNativeWrapper.GetTextFrameworkThreadManager();
				if (threadMgr != null) {
					// Even though the docs says passing null is invalid, this seems to help
					// activating the IME on the default input context that is shared with WPF
					threadMgr.SetFocus(IntPtr.Zero);
				}
			}
		}

		IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			switch (msg) {
				case ImeNativeWrapper.WM_INPUTLANGCHANGE:
					// Don't mark the message as handled; other windows
					// might want to handle it as well.

					// If we have a context, recreate it
					MdnTrace("inputlangchange bound=" + (hwndSource != null));
					if (hwndSource != null) {
						ClearContext();
						CreateContext();
					}
					break;
				case ImeNativeWrapper.WM_IME_COMPOSITION:
					UpdateCompositionWindow();
					break;
				case 0x0086: // WM_NCACTIVATE
					// MDNote 2026-11-22: a context bound while the window was not yet the foreground window (a launch: the
					// TextArea takes focus before the window comes to the front) leaves the TSF Korean IME off, Han/Eng dead,
					// until the context is rebuilt (VM trace: every failing launch bound only before this message, every
					// passing one after it). Rebuild it once the window is in front, the way WM_INPUTLANGCHANGE does.
					if (wParam != IntPtr.Zero && boundInBackground && hwndSource != null) {
						boundInBackground = false;
						textArea.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(RebindInForeground));
					}
					break;
			}
			return IntPtr.Zero;
		}

		// MDNote: the owning editor keeps the composition window where it placed it (true = skip the caret write), see SourceEditor.KeepCjkCompositionWindow.
		internal Func<bool> KeepCompositionWindow;

		public void UpdateCompositionWindow()
		{
			if (currentContext != IntPtr.Zero) {
				ImeNativeWrapper.SetCompositionFont(hwndSource, currentContext, textArea);
				var keep = KeepCompositionWindow;
				if (keep != null && keep()) return;
				ImeNativeWrapper.SetCompositionWindow(hwndSource, currentContext, textArea);
			}
		}
	}
}
