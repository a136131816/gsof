using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gsof.Xaml.PdfViewer.Helper;
using Gsof.Xaml.PdfViewer.Native;

namespace Gsof.Xaml.PdfViewer.MuPdf
{
    public static class MuPdfWrapper
    {
        /// <summary>
        /// Extracts a PDF page as a Bitmap for a given pdf filename and a page number.
        /// </summary>
        /// <param name="pageNumber">Page number, starting at 1</param>
        /// <param name="zoomFactor">Used to get a smaller or bigger Bitmap, depending on the specified value</param>
        /// <param name="password">The password for the pdf file (if required)</param>
        public static ImageSource ExtractPage(IPdfSource source, int pageNumber, float zoomFactor = 1.0f, string password = null)
        {
            var pageNumberIndex = Math.Max(0, pageNumber - 1); // pages start at index 0

            using (var stream = new PdfFileStream(source))
            {
                ValidatePassword(stream.Document, password);

                IntPtr p = NativeMethods.LoadPage(stream.Document, pageNumberIndex); // loads the page
                var bmp = RenderPage(stream.Context, stream.Document, p, zoomFactor);
                NativeMethods.FreePage(stream.Document, p); // releases the resources consumed by the page

                return bmp;
            }
        }

        /// <summary>
        /// Gets the page bounds for all pages of the given PDF. If a relevant rotation is supplied, the bounds will
        /// be rotated accordingly before returning.
        /// </summary>
        /// <param name="rotation">The rotation that should be applied</param>
        /// <param name="password">The password for the pdf file (if required)</param>
        /// <returns></returns>
        public static Size[] GetPageBounds(IPdfSource source, ImageRotation rotation = ImageRotation.None, string password = null)
        {
            Func<double, double, System.Windows.Size> sizeCallback = (width, height) => new System.Windows.Size(width, height);

            if (rotation == ImageRotation.Rotate90 || rotation == ImageRotation.Rotate270)
                sizeCallback = (width, height) => new System.Windows.Size(height, width); // switch width and height

            using (var stream = new PdfFileStream(source))
            {
                ValidatePassword(stream.Document, password);

                var pageCount = NativeMethods.CountPages(stream.Document); // gets the number of pages in the document
                var resultBounds = new System.Windows.Size[pageCount];

                for (int i = 0; i < pageCount; i++)
                {
                    IntPtr p = NativeMethods.LoadPage(stream.Document, i); // loads the page
                    Rectangle pageBound = NativeMethods.BoundPage(stream.Document, p);

                    resultBounds[i] = sizeCallback(pageBound.Width, pageBound.Height);

                    NativeMethods.FreePage(stream.Document, p); // releases the resources consumed by the page
                }

                return resultBounds;
            }
        }

        /// <summary>
        /// Return the total number of pages for a give PDF.
        /// </summary>
        public static int CountPages(IPdfSource source, string password = null)
        {
            using (var stream = new PdfFileStream(source))
            {
                ValidatePassword(stream.Document, password);

                return NativeMethods.CountPages(stream.Document); // gets the number of pages in the document
            }
        }

        public static bool NeedsPassword(IPdfSource source)
        {
            using (var stream = new PdfFileStream(source))
            {
                return NeedsPassword(stream.Document);
            }
        }

        private static void ValidatePassword(IntPtr doc, string password)
        {
            if (NeedsPassword(doc) && NativeMethods.AuthenticatePassword(doc, password) == 0)
                throw new MissingOrInvalidPdfPasswordException();
        }

        private static bool NeedsPassword(IntPtr doc)
        {
            return NativeMethods.NeedsPassword(doc) != 0;
        }

        static ImageSource RenderPage(IntPtr context, IntPtr document, IntPtr page, float zoomFactor)
        {
            Rectangle pageBound = NativeMethods.BoundPage(document, page);
            System.Windows.Media.Matrix ctm = new System.Windows.Media.Matrix();
            IntPtr pix = IntPtr.Zero;
            IntPtr dev = IntPtr.Zero;

            var currentDpi = DpiHelper.GetCurrentDpi();
            var zoomX = zoomFactor * (currentDpi.HorizontalDpi / DpiHelper.DEFAULT_DPI);
            var zoomY = zoomFactor * (currentDpi.VerticalDpi / DpiHelper.DEFAULT_DPI);

            // gets the size of the page and multiplies it with zoom factors
            int width = (int)(pageBound.Width * zoomX);
            int height = (int)(pageBound.Height * zoomY);

            // sets the matrix as a scaling matrix (zoomX,0,0,zoomY,0,0)
            ctm.M11 = zoomX;
            ctm.M12 = zoomY;

            // creates a pixmap the same size as the width and height of the page
            pix = NativeMethods.NewPixmap(context, NativeMethods.FindDeviceColorSpace(context, "DeviceRGB"), width, height);
            // sets white color as the background color of the pixmap
            NativeMethods.ClearPixmap(context, pix, 0xFF);

            // creates a drawing device
            dev = NativeMethods.NewDrawDevice(context, pix);
            // draws the page on the device created from the pixmap
            NativeMethods.RunPage(document, page, dev, ctm, IntPtr.Zero);

            NativeMethods.FreeDevice(dev); // frees the resources consumed by the device

            WriteableBitmap wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);

            wb.Lock();
            unsafe
            { // converts the pixmap data to Bitmap data
                byte* ptrSrc = (byte*)NativeMethods.GetSamples(context, pix); // gets the rendered data from the pixmap
                byte* ptrDest = (byte*)wb.BackBuffer;
                for (int y = 0; y < height; y++)
                {
                    byte* pl = ptrDest;
                    byte* sl = ptrSrc;
                    for (int x = 0; x < width; x++)
                    {
                        //Swap these here instead of in MuPDF because most pdf images will be rgb or cmyk.
                        //Since we are going through the pixels one by one anyway swap here to save a conversion from rgb to bgr.
                        pl[2] = sl[0]; //b-r
                        pl[1] = sl[1]; //g-g
                        pl[0] = sl[2]; //r-b
                        pl[3] = 255;
                        //sl[3] is the alpha channel, we will skip it here
                        pl += 4;
                        sl += 4;
                    }
                    ptrDest += wb.BackBufferStride;
                    ptrSrc += width * 4;
                }
            }

            wb.AddDirtyRect(new Int32Rect(0, 0, width, height));
            wb.Unlock();

            NativeMethods.DropPixmap(context, pix);
            //bmp.SetResolution(currentDpi.HorizontalDpi, currentDpi.VerticalDpi);

            return wb;
        }

        /// <summary>
        /// Helper class for an easier disposing of unmanaged resources
        /// </summary>
        private sealed class PdfFileStream : IDisposable
        {
            const uint FZ_STORE_DEFAULT = 256 << 20;

            public IntPtr Context { get; private set; }
            public IntPtr Stream { get; private set; }
            public IntPtr Document { get; private set; }

            public PdfFileStream(IPdfSource source)
            {
                if (source is FileSource)
                {
                    var fs = (FileSource)source;
                    Context = NativeMethods.NewContext(IntPtr.Zero, IntPtr.Zero, FZ_STORE_DEFAULT); // Creates the context
                    Stream = NativeMethods.OpenFile(Context, fs.Filename); // opens file as a stream
                    Document = NativeMethods.OpenDocumentStream(Context, ".pdf", Stream); // opens the document
                }
                else if (source is MemorySource)
                {
                    var ms = (MemorySource)source;
                    Context = NativeMethods.NewContext(IntPtr.Zero, IntPtr.Zero, FZ_STORE_DEFAULT); // Creates the context
                    GCHandle pinnedArray = GCHandle.Alloc(ms.Bytes, GCHandleType.Pinned);
                    IntPtr pointer = pinnedArray.AddrOfPinnedObject();
                    Stream = NativeMethods.OpenStream(Context, pointer, ms.Bytes.Length); // opens file as a stream
                    Document = NativeMethods.OpenDocumentStream(Context, ".pdf", Stream); // opens the document
                    pinnedArray.Free();
                }
            }

            public void Dispose()
            {
                NativeMethods.CloseDocument(Document); // releases the resources
                NativeMethods.CloseStream(Stream);
                NativeMethods.FreeContext(Context);
            }
        }
    }

    public class MissingOrInvalidPdfPasswordException : Exception
    {
        public MissingOrInvalidPdfPasswordException()
            : base("A password for the pdf document was either not provided or is invalid.")
        { }
    }

    public interface IPdfSource
    { }

    public class FileSource : IPdfSource
    {
        public string Filename { get; private set; }

        public FileSource(string filename)
        {
            this.Filename = filename;
        }
    }

    public class MemorySource : IPdfSource
    {
        public byte[] Bytes { get; private set; }

        public MemorySource(byte[] bytes)
        {
            this.Bytes = bytes;
        }
    }
}
