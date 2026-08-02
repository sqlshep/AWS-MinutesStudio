using UglyToad.PdfPig;

namespace MinutesStudio.Core.Services;

/// <summary>Text of a single PDF page.</summary>
public sealed record PageText(int PageNumber, string Text);

public interface IPdfTextExtractor
{
    /// <summary>Extracts text page-by-page from a PDF file on disk.</summary>
    IReadOnlyList<PageText> ExtractPages(string filePath);

    /// <summary>Extracts text page-by-page from PDF bytes (e.g. a blob download).</summary>
    IReadOnlyList<PageText> ExtractPages(byte[] fileBytes);
}

/// <summary>Extracts raw text from PDFs using PdfPig. Keeps page boundaries so chunks can cite page ranges.</summary>
public sealed class PdfTextExtractor : IPdfTextExtractor
{
    public IReadOnlyList<PageText> ExtractPages(string filePath)
    {
        using var document = PdfDocument.Open(filePath);
        return ReadPages(document);
    }

    public IReadOnlyList<PageText> ExtractPages(byte[] fileBytes)
    {
        using var document = PdfDocument.Open(fileBytes);
        return ReadPages(document);
    }

    private static IReadOnlyList<PageText> ReadPages(PdfDocument document)
    {
        var pages = new List<PageText>();
        foreach (var page in document.GetPages())
        {
            var text = page.Text ?? string.Empty;
            pages.Add(new PageText(page.Number, text));
        }

        return pages;
    }
}
