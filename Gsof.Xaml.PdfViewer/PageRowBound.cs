using System.Windows;

namespace Gsof.Xaml.PdfViewer
{
	internal class PageRowBound
	{
		public Size Size { get; private set; }
		public double VerticalOffset { get; private set; }
		public double HorizontalOffset { get; private set; }

		public PageRowBound(Size size, double verticalOffset, double horizontalOffset)
		{
			this.Size = size;
			this.VerticalOffset = verticalOffset;
			this.HorizontalOffset = horizontalOffset; 
		}

		public Size SizeIncludingOffset
		{
			get { return new Size(this.Size.Width + this.HorizontalOffset, this.Size.Height + this.VerticalOffset); }
		}
	}
}
