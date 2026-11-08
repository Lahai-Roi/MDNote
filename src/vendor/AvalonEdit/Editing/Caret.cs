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
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Editing
{
	/// <summary>
	/// Helper class with caret-related methods.
	/// </summary>
	public sealed class Caret
	{
		readonly TextArea textArea;
		readonly TextView textView;
		readonly CaretLayer caretAdorner;
		bool visible;

		internal Caret(TextArea textArea)
		{
			this.textArea = textArea;
			this.textView = textArea.TextView;
			position = new TextViewPosition(1, 1, 0);

			caretAdorner = new CaretLayer(textArea);
			textView.InsertLayer(caretAdorner, KnownLayer.Caret, LayerInsertionPosition.Replace);
			textView.VisualLinesChanged += TextView_VisualLinesChanged;
			textView.ScrollOffsetChanged += TextView_ScrollOffsetChanged;
		}

		internal void UpdateIfVisible()
		{
			if (visible) {
				Show();
			}
		}

		/// <summary>
		/// MDNote 2026-11-08: the font size the next typed text will get (a size picked before typing), or 0.
		/// The caret takes that size's height until text is typed. Call <see cref="Refresh"/> after it changes.
		/// </summary>
		public Func<double> PendingEmSize { get; set; }

		/// <summary>MDNote 2026-11-08: redraws the caret after <see cref="PendingEmSize"/> changed.</summary>
		public void Refresh()
		{
			UpdateIfVisible();
			EventHandler handler = Refreshed;
			if (handler != null) handler(this, EventArgs.Empty);
		}

		/// <summary>MDNote 2026-11-08: raised by <see cref="Refresh"/>, for a host that paints its own caret (a table cell).</summary>
		public event EventHandler Refreshed;

		double PendingEmSizeNow()
		{
			Func<double> ask = PendingEmSize;
			if (ask == null) return 0;
			try { return ask(); } catch (Exception) { return 0; }
		}

		void TextView_VisualLinesChanged(object sender, EventArgs e)
		{
			if (visible) {
				Show();
			}
			// required because the visual columns might have changed if the
			// element generators did something differently than on the last run
			// (e.g. a FoldingSection was collapsed)
			InvalidateVisualColumn();
		}

		void TextView_ScrollOffsetChanged(object sender, EventArgs e)
		{
			if (caretAdorner != null) {
				caretAdorner.InvalidateVisual();
			}
		}

		double desiredXPos = double.NaN;
		TextViewPosition position;

		/// <summary>
		/// Gets/Sets the position of the caret.
		/// Retrieving this property will validate the visual column (which can be expensive).
		/// Use the <see cref="Location"/> property instead if you don't need the visual column.
		/// </summary>
		public TextViewPosition Position {
			get {
				ValidateVisualColumn();
				return position;
			}
			set {
				if (position != value) {
					position = value;

					storedCaretOffset = -1;

					//Debug.WriteLine("Caret position changing to " + value);

					ValidatePosition();
					InvalidateVisualColumn();
					RaisePositionChanged();
					Log("Caret position changed to " + value);
					if (visible)
						Show();
				}
			}
		}

		/// <summary>
		/// Gets the caret position without validating it.
		/// </summary>
		internal TextViewPosition NonValidatedPosition {
			get {
				return position;
			}
		}

		/// <summary>
		/// Gets/Sets the location of the caret.
		/// The getter of this property is faster than <see cref="Position"/> because it doesn't have
		/// to validate the visual column.
		/// </summary>
		public TextLocation Location {
			get {
				return position.Location;
			}
			set {
				this.Position = new TextViewPosition(value);
			}
		}

		/// <summary>
		/// Gets/Sets the caret line.
		/// </summary>
		public int Line {
			get { return position.Line; }
			set {
				this.Position = new TextViewPosition(value, position.Column);
			}
		}

		/// <summary>
		/// Gets/Sets the caret column.
		/// </summary>
		public int Column {
			get { return position.Column; }
			set {
				this.Position = new TextViewPosition(position.Line, value);
			}
		}

		/// <summary>
		/// Gets/Sets the caret visual column.
		/// </summary>
		public int VisualColumn {
			get {
				ValidateVisualColumn();
				return position.VisualColumn;
			}
			set {
				this.Position = new TextViewPosition(position.Line, position.Column, value);
			}
		}

		bool isInVirtualSpace;

		/// <summary>
		/// Gets whether the caret is in virtual space.
		/// </summary>
		public bool IsInVirtualSpace {
			get {
				ValidateVisualColumn();
				return isInVirtualSpace;
			}
		}

		int storedCaretOffset;

		internal void OnDocumentChanging()
		{
			storedCaretOffset = this.Offset;
			InvalidateVisualColumn();
		}

		internal void OnDocumentChanged(DocumentChangeEventArgs e)
		{
			InvalidateVisualColumn();
			if (storedCaretOffset >= 0) {
				// If the caret is at the end of a selection, we don't expand the selection if something
				// is inserted at the end. Thus we also need to keep the caret in front of the insertion.
				AnchorMovementType caretMovementType;
				if (!textArea.Selection.IsEmpty && storedCaretOffset == textArea.Selection.SurroundingSegment.EndOffset)
					caretMovementType = AnchorMovementType.BeforeInsertion;
				else
					caretMovementType = AnchorMovementType.Default;
				int newCaretOffset = e.GetNewOffset(storedCaretOffset, caretMovementType);
				TextDocument document = textArea.Document;
				if (document != null) {
					// keep visual column
					this.Position = new TextViewPosition(document.GetLocation(newCaretOffset), position.VisualColumn);
				}
			}
			storedCaretOffset = -1;
		}

		/// <summary>
		/// Gets/Sets the caret offset.
		/// Setting the caret offset has the side effect of setting the <see cref="DesiredXPos"/> to NaN.
		/// </summary>
		public int Offset {
			get {
				TextDocument document = textArea.Document;
				if (document == null) {
					return 0;
				} else {
					return document.GetOffset(position.Location);
				}
			}
			set {
				TextDocument document = textArea.Document;
				if (document != null) {
					this.Position = new TextViewPosition(document.GetLocation(value));
					this.DesiredXPos = double.NaN;
				}
			}
		}

		/// <summary>
		/// Gets/Sets the desired x-position of the caret, in device-independent pixels.
		/// This property is NaN if the caret has no desired position.
		/// </summary>
		public double DesiredXPos {
			get { return desiredXPos; }
			set { desiredXPos = value; }
		}

		void ValidatePosition()
		{
			if (position.Line < 1)
				position.Line = 1;
			if (position.Column < 1)
				position.Column = 1;
			if (position.VisualColumn < -1)
				position.VisualColumn = -1;
			TextDocument document = textArea.Document;
			if (document != null) {
				if (position.Line > document.LineCount) {
					position.Line = document.LineCount;
					position.Column = document.GetLineByNumber(position.Line).Length + 1;
					position.VisualColumn = -1;
				} else {
					DocumentLine line = document.GetLineByNumber(position.Line);
					if (position.Column > line.Length + 1) {
						position.Column = line.Length + 1;
						position.VisualColumn = -1;
					}
				}
			}
		}

		/// <summary>
		/// Event raised when the caret position has changed.
		/// If the caret position is changed inside a document update (between BeginUpdate/EndUpdate calls),
		/// the PositionChanged event is raised only once at the end of the document update.
		/// </summary>
		public event EventHandler PositionChanged;

		bool raisePositionChangedOnUpdateFinished;

		void RaisePositionChanged()
		{
			if (textArea.Document != null && textArea.Document.IsInUpdate) {
				raisePositionChangedOnUpdateFinished = true;
			} else {
				if (PositionChanged != null) {
					PositionChanged(this, EventArgs.Empty);
				}
			}
		}

		internal void OnDocumentUpdateFinished()
		{
			if (raisePositionChangedOnUpdateFinished) {
				if (PositionChanged != null) {
					PositionChanged(this, EventArgs.Empty);
				}
			}
		}

		bool visualColumnValid;

		void ValidateVisualColumn()
		{
			if (!visualColumnValid) {
				TextDocument document = textArea.Document;
				if (document != null) {
					Debug.WriteLine("Explicit validation of caret column");
					var documentLine = document.GetLineByNumber(position.Line);
					RevalidateVisualColumn(textView.GetOrConstructVisualLine(documentLine));
				}
			}
		}

		void InvalidateVisualColumn()
		{
			visualColumnValid = false;
		}

		/// <summary>
		/// Validates the visual column of the caret using the specified visual line.
		/// The visual line must contain the caret offset.
		/// </summary>
		void RevalidateVisualColumn(VisualLine visualLine)
		{
			if (visualLine == null)
				throw new ArgumentNullException("visualLine");

			// mark column as validated
			visualColumnValid = true;

			int caretOffset = textView.Document.GetOffset(position.Location);
			int firstDocumentLineOffset = visualLine.FirstDocumentLine.Offset;
			position.VisualColumn = visualLine.ValidateVisualColumn(position, textArea.Selection.EnableVirtualSpace);

			// search possible caret positions
			int newVisualColumnForwards = visualLine.GetNextCaretPosition(position.VisualColumn - 1, LogicalDirection.Forward, CaretPositioningMode.Normal, textArea.Selection.EnableVirtualSpace);
			// If position.VisualColumn was valid, we're done with validation.
			if (newVisualColumnForwards != position.VisualColumn) {
				// also search backwards so that we can pick the better match
				int newVisualColumnBackwards = visualLine.GetNextCaretPosition(position.VisualColumn + 1, LogicalDirection.Backward, CaretPositioningMode.Normal, textArea.Selection.EnableVirtualSpace);

				if (newVisualColumnForwards < 0 && newVisualColumnBackwards < 0)
					throw ThrowUtil.NoValidCaretPosition();

				// determine offsets for new visual column positions
				int newOffsetForwards;
				if (newVisualColumnForwards >= 0)
					newOffsetForwards = visualLine.GetRelativeOffset(newVisualColumnForwards) + firstDocumentLineOffset;
				else
					newOffsetForwards = -1;
				int newOffsetBackwards;
				if (newVisualColumnBackwards >= 0)
					newOffsetBackwards = visualLine.GetRelativeOffset(newVisualColumnBackwards) + firstDocumentLineOffset;
				else
					newOffsetBackwards = -1;

				int newVisualColumn, newOffset;
				// if there's only one valid position, use it
				if (newVisualColumnForwards < 0) {
					newVisualColumn = newVisualColumnBackwards;
					newOffset = newOffsetBackwards;
				} else if (newVisualColumnBackwards < 0) {
					newVisualColumn = newVisualColumnForwards;
					newOffset = newOffsetForwards;
				} else {
					// two valid positions: find the better match
					if (Math.Abs(newOffsetBackwards - caretOffset) < Math.Abs(newOffsetForwards - caretOffset)) {
						// backwards is better
						newVisualColumn = newVisualColumnBackwards;
						newOffset = newOffsetBackwards;
					} else {
						// forwards is better
						newVisualColumn = newVisualColumnForwards;
						newOffset = newOffsetForwards;
					}
				}
				this.Position = new TextViewPosition(textView.Document.GetLocation(newOffset), newVisualColumn);
			}
			isInVirtualSpace = (position.VisualColumn > visualLine.VisualLength);
		}

		Rect CalcCaretRectangle(VisualLine visualLine)
		{
			if (!visualColumnValid) {
				RevalidateVisualColumn(visualLine);
			}

			TextLine textLine = visualLine.GetTextLine(position.VisualColumn, position.IsAtEndOfLine);
			double xPos = visualLine.GetTextLineVisualXPosition(textLine, position.VisualColumn);
			double lineTop = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop);
			double lineBottom = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextBottom);
			// MDNote 2026-11-08: the caret takes the height of the text it stands on (a heading or a sized run), not the
			// default line height; the default is kept when no run with a usable font is found.
			double runTop, runHeight;
			if (CaretRunMetrics(visualLine, textLine, position.VisualColumn, out runTop, out runHeight)) {
				lineTop = runTop;
				lineBottom = runTop + runHeight;
			}

			return new Rect(xPos,
							lineTop,
							SystemParameters.CaretWidth,
							lineBottom - lineTop);
		}

		// The run the caret belongs to: the text element ending at or containing the caret column, else the one after it, else any
		// element of the line with a usable font (an empty "# " heading has only its hidden prefix). Top = the line's baseline minus
		// that font's ascent, height = its line spacing, so the caret spans that font's glyph box on the shared baseline.
		bool CaretRunMetrics(VisualLine visualLine, TextLine textLine, int column, out double top, out double height)
		{
			top = 0; height = 0;
			VisualLineElement chosen = null;
			foreach (VisualLineElement element in visualLine.Elements) {
				if (!UsableRun(element)) continue;
				// The nearest document run starting before the caret (a composition string drawn between them has no document text of its own).
				if (element.VisualColumn < column && element.DocumentLength > 0) chosen = element;
			}
			if (chosen == null) {
				foreach (VisualLineElement element in visualLine.Elements)
					if (UsableRun(element) && element.DocumentLength > 0 && element.VisualColumn >= column) { chosen = element; break; }
			}
			if (chosen == null) {
				foreach (VisualLineElement element in visualLine.Elements)
					if (UsableRun(element)) { chosen = element; break; }
			}
			if (chosen == null) return false;
			// A run on another wrapped row of the same visual line would give that row's metrics.
			TextLine chosenLine = visualLine.GetTextLine(Math.Min(chosen.VisualColumn, Math.Max(0, visualLine.VisualLength)));
			if (chosenLine != textLine && chosen.DocumentLength > 0) return false;
			var p = chosen.TextRunProperties;
			FontFamily family = p.Typeface.FontFamily;
			double em = p.FontRenderingEmSize;
			double pending = PendingEmSizeNow();
			if (pending >= 6) em = pending;
			double baseline = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) + textLine.Baseline;
			top = baseline - em * family.Baseline;
			height = em * family.LineSpacing;
			return height > 0;
		}

		static bool UsableRun(VisualLineElement element)
		{
			var p = element.TextRunProperties;
			return p != null && p.Typeface != null && p.Typeface.FontFamily != null && p.FontRenderingEmSize >= 6;
		}

		Rect CalcCaretOverstrikeRectangle(VisualLine visualLine)
		{
			if (!visualColumnValid) {
				RevalidateVisualColumn(visualLine);
			}

			int currentPos = position.VisualColumn;
			// The text being overwritten in overstrike mode is everything up to the next normal caret stop
			int nextPos = visualLine.GetNextCaretPosition(currentPos, LogicalDirection.Forward, CaretPositioningMode.Normal, true);
			TextLine textLine = visualLine.GetTextLine(currentPos);

			Rect r;
			if (currentPos < visualLine.VisualLength) {
				// If the caret is within the text, use GetTextBounds() for the text being overwritten.
				// This is necessary to ensure the rectangle is calculated correctly in bidirectional text.
				var textBounds = textLine.GetTextBounds(currentPos, nextPos - currentPos)[0];
				r = textBounds.Rectangle;
				r.Y += visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop);
			} else {
				// If the caret is at the end of the line (or in virtual space),
				// use the visual X position of currentPos and nextPos (one or more of which will be in virtual space)
				double xPos = visualLine.GetTextLineVisualXPosition(textLine, currentPos);
				double xPos2 = visualLine.GetTextLineVisualXPosition(textLine, nextPos);
				double lineTop = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop);
				double lineBottom = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextBottom);
				r = new Rect(xPos, lineTop, xPos2 - xPos, lineBottom - lineTop);
			}
			// If the caret is too small (e.g. in front of zero-width character), ensure it's still visible
			if (r.Width < SystemParameters.CaretWidth)
				r.Width = SystemParameters.CaretWidth;
			return r;
		}

		/// <summary>
		/// Returns the caret rectangle. The coordinate system is in device-independent pixels from the top of the document.
		/// </summary>
		public Rect CalculateCaretRectangle()
		{
			if (textView != null && textView.Document != null) {
				VisualLine visualLine = textView.GetOrConstructVisualLine(textView.Document.GetLineByNumber(position.Line));
				return textArea.OverstrikeMode ? CalcCaretOverstrikeRectangle(visualLine) : CalcCaretRectangle(visualLine);
			} else {
				return Rect.Empty;
			}
		}

		/// <summary>
		/// Minimum distance of the caret to the view border.
		/// </summary>
		internal const double MinimumDistanceToViewBorder = 30;

		/// <summary>
		/// Scrolls the text view so that the caret is visible.
		/// </summary>
		public void BringCaretToView()
		{
			BringCaretToView(MinimumDistanceToViewBorder);
		}

		internal void BringCaretToView(double border)
		{
			Rect caretRectangle = CalculateCaretRectangle();
			if (!caretRectangle.IsEmpty) {
				caretRectangle.Inflate(border, border);
				textView.MakeVisible(caretRectangle);
			}
		}

		/// <summary>
		/// Makes the caret visible and updates its on-screen position.
		/// </summary>
		public void Show()
		{
			Log("Caret.Show()");
			visible = true;
			if (!showScheduled) {
				showScheduled = true;
				textArea.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowInternal));
			}
		}

		bool showScheduled;
		bool hasWin32Caret;

		void ShowInternal()
		{
			showScheduled = false;

			// if show was scheduled but caret hidden in the meantime
			if (!visible)
				return;

			if (caretAdorner != null && textView != null) {
				VisualLine visualLine = textView.GetVisualLine(position.Line);
				if (visualLine != null) {
					Rect caretRect = this.textArea.OverstrikeMode ? CalcCaretOverstrikeRectangle(visualLine) : CalcCaretRectangle(visualLine);
					// Create Win32 caret so that Windows knows where our managed caret is. This is necessary for
					// features like 'Follow text editing' in the Windows Magnifier.
					if (!hasWin32Caret) {
						hasWin32Caret = Win32.CreateCaret(textView, caretRect.Size);
					}
					if (hasWin32Caret) {
						Win32.SetCaretPosition(textView, caretRect.Location - textView.ScrollOffset);
					}
					caretAdorner.Show(caretRect);
					textArea.ime.UpdateCompositionWindow();
				} else {
					caretAdorner.Hide();
				}
			}
		}

		/// <summary>
		/// Makes the caret invisible.
		/// </summary>
		public void Hide()
		{
			Log("Caret.Hide()");
			visible = false;
			if (hasWin32Caret) {
				Win32.DestroyCaret();
				hasWin32Caret = false;
			}
			if (caretAdorner != null) {
				caretAdorner.Hide();
			}
		}

		[Conditional("DEBUG")]
		static void Log(string text)
		{
			// commented out to make debug output less noisy - add back if there are any problems with the caret
			//Debug.WriteLine(text);
		}

		/// <summary>
		/// Gets/Sets the color of the caret.
		/// </summary>
		public Brush CaretBrush {
			get { return caretAdorner.CaretBrush; }
			set { caretAdorner.CaretBrush = value; }
		}
	}
}
