using GdPicture14;

namespace PartialRedactionDemo;

/// <summary>Accepts PDFs as-is and converts images / Office files / emails to PDF with GdPicture.</summary>
public static class InputConverter
{
    public static byte[] ToPdf(byte[] bytes, string fileName)
    {
        var format = GdPictureDocumentUtilities.GetDocumentFormat(new MemoryStream(bytes), fileName);
        if (format == GdPicture14.DocumentFormat.DocumentFormatPDF) return bytes;
        if (format == GdPicture14.DocumentFormat.DocumentFormatUNKNOWN)
            throw new InvalidOperationException("Unsupported file type. Use a PDF, an image or an Office document.");

        using var converter = new GdPictureDocumentConverter();
        var status = converter.LoadFromStream(new MemoryStream(bytes), format);
        if (status != GdPictureStatus.OK) throw new InvalidOperationException("Could not read the file: " + status);
        using var output = new MemoryStream();
        status = converter.SaveAsPDF(output, PdfConformance.PDF1_7);
        if (status != GdPictureStatus.OK) throw new InvalidOperationException("Conversion to PDF failed: " + status);
        return output.ToArray();
    }
}
