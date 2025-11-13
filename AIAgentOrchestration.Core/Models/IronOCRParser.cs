// PdfSkeletonIronOcr.cs — Minimal, Linux‑friendly PDF → RAG skeleton using IronOCR
// - .NET 9 compatible
// - Works on Azure App Service (Linux) via IronOcr + IronOcr.LinuxNativeBinaries
// - No system.drawing usage by you directly; IronOCR handles its own native deps
// - Extracts: Paragraphs, Bullet/Numbered list items, Tables (geometry from words)
// - Images: optional hook (delegate) so you can plug an image extractor later (e.g., PdfPig/IronPDF)
//
// NuGet (suggested):
//   <PackageReference Include="IronOcr" Version="2025.*" />
//   <PackageReference Include="IronOcr.LinuxNativeBinaries" Version="2025.*" Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
//
// Usage:
//   var parser = new PdfSkeletonIronOcr.Parser();
//   var skel = parser.Parse(pdfBytes, sourceId: "doc1");
//
// Optional image hook:
//   var parser = new PdfSkeletonIronOcr.Parser(pageImageProvider: (pageIndex) => MyGetImages(pageIndex));
//   // pageImageProvider returns a list of (Bounds, Bytes) for that page.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IronOcr;
using IronSoftware.Drawing;

namespace PdfSkeletonIronOcr
{
  // ===== Model =====
  public sealed class DocumentSkeleton
  {
    public required string SourceId { get; init; }
    public List<Section> Sections { get; } = new();
  }

  public sealed class Section
  {
    public required string Heading { get; set; }
    public int HeadingLevel { get; set; } = 1;
    public List<ContentBlock> ContentBlocks { get; } = new();
  }

  public enum BlockType
  {
    Paragraph,
    BulletListItem,
    NumberedListItem,
    Table,
    Image,
    PageBreak
  }

  public sealed class TableBlock
  {
    public string? Caption { get; set; }
    public List<List<string>> Rows { get; } = new();
  }

  public sealed class ContentBlock
  {
    public required BlockType Type { get; init; }
    public string? Text { get; set; }
    public string? ImageId { get; set; }
    public byte[]? ImageBytes { get; set; }
    public TableBlock? Table { get; set; }
    public int Page { get; set; }
    public string? BlockId { get; set; }
  }

  // ===== Parser =====
  public sealed class Parser
  {
    // Optional image extractor you can plug in later (e.g., from PdfPig/IronPdf)
    // Input: pageIndex (0‑based). Output: list of (bounds, bytes).
    public delegate List<(System.Drawing.Rectangle Bounds, byte[] Bytes)> PageImageProvider(int pageIndex);
    private readonly PageImageProvider? _pageImageProvider;

    public Parser(PageImageProvider? pageImageProvider = null)
    {
      _pageImageProvider = pageImageProvider;
    }

    public DocumentSkeleton Parse(byte[] pdfBytes, string sourceId = "pdf")
    {
      using var input = new OcrInput();
      input.Load(pdfBytes);

      var ocr = CreateEngine();
      var result = ocr.Read(input);

      var skel = new DocumentSkeleton { SourceId = sourceId };
      var section = new Section { Heading = "Document", HeadingLevel = 1 };
      skel.Sections.Add(section);

      int paraIdx = 0, imgIdx = 0, tableIdx = 0, listIdx = 0;

      for (int p = 0; p < result.Pages.Count; p++)
      {
        var page = result.Pages[p];

        // Inline images first (if provider present), but we emit them IN ORDER later by merging into flow
        var pageImages = _pageImageProvider?.Invoke(p) ?? new List<(System.Drawing.Rectangle Bounds, byte[] Bytes)>();

        // Build a single, merged content stream from words (for tables & lists) and images.
        // We’ll iterate over text lines in top‑to‑bottom order and interleave images that fall before each line.

        // Collect text lines and their bounds
        var lines = page.Lines
            .Select(ln => ((string Text, RectangleF Bounds))((ln.Text ?? string.Empty).Trim(),ln.BoundingBox))
            .Where(x => x.Text.Length > 0)
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .ToList();

        // Paragraph/List buffer per page
        var paraBuf = new List<string>();
        bool inList = false;
        BlockType currentListType = BlockType.Paragraph;
        var listItems = new List<string>();

        void FlushParagraph()
        {
          if (paraBuf.Count == 0) return;
          var text = string.Join(" ", paraBuf).Trim();
          paraBuf.Clear();
          if (text.Length == 0) return;
          section.ContentBlocks.Add(new ContentBlock
          {
            Type = BlockType.Paragraph,
            Text = text,
            Page = p + 1,
            BlockId = $"p_{p + 1}_{++paraIdx}"
          });
        }
        void FlushList()
        {
          if (!inList || listItems.Count == 0) return;
          foreach (var li in listItems)
          {
            section.ContentBlocks.Add(new ContentBlock
            {
              Type = currentListType,
              Text = li.Trim(),
              Page = p + 1,
              BlockId = $"list_{p + 1}_{++listIdx}"
            });
          }
          listItems.Clear();
          inList = false;
          currentListType = BlockType.Paragraph;
        }

        bool IsBulletListItem(string text)
        {
          var t = text.TrimStart();
          return t.StartsWith("•") || t.StartsWith("- ") || t.StartsWith("* ") ||
                 t.StartsWith("– ") || t.StartsWith("— ");
        }
        bool IsNumberedListItem(string text)
        {
          var t = text.TrimStart();
          return Regex.IsMatch(t, @"^([0-9]+[.)])\s") || Regex.IsMatch(t, @"^([A-Za-z]+[.)])\s");
        }

        // Prepare table detection using WORDS (more stable than lines)
        // Map each text line to its words for table geometry
        var wordsByLine = page.Words
            .GroupBy(w => NearestLineIndex(lines, w.BoundingBox))
            .Where(g => g.Key >= 0)
            .ToDictionary(g => g.Key, g => g.Select(w => (w.Text?.Trim() ?? string.Empty, w.BoundingBox)).Where(x => x.Item1.Length > 0).ToList());

        var tableSpans = DetectTablesFromWords(lines, wordsByLine);

        int k = 0; int spanIdx = 0;
        while (k < lines.Count)
        {
          // Interleave any images that occur ABOVE the current line
          if (pageImages.Count > 0)
          {
            var currentTop = lines[k].Bounds.Top;
            var emit = pageImages.Where(img => img.Bounds.Top < currentTop).OrderBy(img => img.Bounds.Top).ToList();
            foreach (var im in emit)
            {
              FlushList();
              FlushParagraph();
              imgIdx++;
              section.ContentBlocks.Add(new ContentBlock
              {
                Type = BlockType.Image,
                ImageId = $"p{p + 1}_img{imgIdx}",
                ImageBytes = im.Bytes,
                Page = p + 1,
                BlockId = $"img_{p + 1}_{imgIdx}"
              });
            }
            pageImages = pageImages.Except(emit).ToList();
          }

          // Tables inline
          if (spanIdx < tableSpans.Count && k == tableSpans[spanIdx].RowStart)
          {
            FlushList();
            FlushParagraph();
            var tspan = tableSpans[spanIdx];
            section.ContentBlocks.Add(new ContentBlock
            {
              Type = BlockType.Table,
              Table = tspan.Table,
              Page = p + 1,
              BlockId = $"t_{p + 1}_{++tableIdx}"
            });
            k = tspan.RowEnd + 1;
            spanIdx++;
            continue;
          }

          // Lists vs paragraphs
          var text = lines[k].Text;
          if (IsBulletListItem(text) || IsNumberedListItem(text))
          {
            var listType = IsBulletListItem(text) ? BlockType.BulletListItem : BlockType.NumberedListItem;
            if (!inList)
            {
              FlushParagraph();
              inList = true;
              currentListType = listType;
            }
            else if (currentListType != listType)
            {
              FlushList();
              inList = true;
              currentListType = listType;
            }
            listItems.Add(text);
          }
          else
          {
            if (inList) FlushList();
            paraBuf.Add(text);
          }

          k++;
        }

        // Emit any images that come after the last line on the page
        if (pageImages.Count > 0)
        {
          FlushList();
          FlushParagraph();
          foreach (var im in pageImages.OrderBy(i => i.Bounds.Top))
          {
            imgIdx++;
            section.ContentBlocks.Add(new ContentBlock
            {
              Type = BlockType.Image,
              ImageId = $"p{p + 1}_img{imgIdx}",
              ImageBytes = im.Bytes,
              Page = p + 1,
              BlockId = $"img_{p + 1}_{imgIdx}"
            });
          }
        }

        // End of page
        FlushList();
        FlushParagraph();
        // section.ContentBlocks.Add(new ContentBlock { Type = BlockType.PageBreak, Page = p+1, BlockId = $"pb_{p+1}" });
      }

      return skel;
    }

    private static IronTesseract CreateEngine()
    {
      var ocr = new IronTesseract();
      // Basic, robust defaults; IronOCR handles scanned & digital PDFs internally
      ocr.Language = OcrLanguage.English;
      ocr.Configuration.ReadBarCodes = false;
      ocr.Configuration.PageSegmentationMode = TesseractPageSegmentationMode.Auto;
      // You can tweak: ocr.Configuration.EngineMode, Brightness/Contrast, etc., if needed
      return ocr;
    }

    // ===== Table detection from WORD geometry =====
    private sealed record TableSpan(int RowStart, int RowEnd, TableBlock Table);

    private static List<TableSpan> DetectTablesFromWords(List<(string Text, RectangleF Bounds)> lines, Dictionary<int, List<(string text, System.Drawing.RectangleF box)>> wordsByLine, int maxColumns = 10, int minRows = 2)
    {
      var spans = new List<TableSpan>();
      if (lines.Count == 0) return spans;

      int start = 0;
      while (start < lines.Count)
      {
        int end = start;
        while (end + 1 < lines.Count &&
               Math.Abs(lines[end + 1].Bounds.Top - lines[end].Bounds.Bottom)
                   <= Math.Max(8, lines[end].Bounds.Height * 2))
          end++;

        if (end - start + 1 >= minRows)
        {
          var span = TryMakeTableFromWords(lines, wordsByLine, start, end, maxColumns);
          if (span != null) spans.Add(span);
        }
        start = end + 1;
      }
      return spans;
    }

    private static TableSpan? TryMakeTableFromWords(List<(string Text, RectangleF Bounds)> lines, Dictionary<int, List<(string text, System.Drawing.RectangleF box)>> wordsByLine, int rowStart, int rowEnd, int maxColumns)
    {
      // Gather candidate words across the span
      var idxs = Enumerable.Range(rowStart, rowEnd - rowStart + 1).ToList();
      var words = idxs.SelectMany(i => wordsByLine.TryGetValue(i, out var w) ? w : new()).ToList();
      if (words.Count < 4) return null;

      // Build global column centers from word left edges
      var xs = words.Select(w => (double)w.box.Left).OrderBy(x => x).ToList();
      var centers = new List<double>();
      const double colTol = 8.0; // px tolerance; tweak if needed
      foreach (var x in xs)
        if (centers.Count == 0 || Math.Abs(centers[^1] - x) > colTol) centers.Add(x);

      if (centers.Count < 2 || centers.Count > maxColumns) return null;

      // Build rows by snapping words to nearest column center
      var tableRows = new List<List<string>>();
      var perRowNonEmpty = new List<int>();
      int aligned = 0, total = 0;

      foreach (var i in idxs)
      {
        var rowWords = (wordsByLine.TryGetValue(i, out var w) ? w : new())
                       .OrderBy(w => w.box.Left).ToList();
        var cells = Enumerable.Repeat(string.Empty, centers.Count).ToArray();
        foreach (var wo in rowWords)
        {
          total++;
          int j = NearestIndex(centers, wo.box.Left);
          if (j < 0 || j >= cells.Length) continue;
          if (Math.Abs(centers[j] - wo.box.Left) <= colTol) aligned++;
          if (cells[j].Length > 0) cells[j] += " ";
          cells[j] += wo.text;
        }
        perRowNonEmpty.Add(cells.Count(c => !string.IsNullOrWhiteSpace(c)));
        tableRows.Add(cells.ToList());
      }

      if (total == 0) return null;
      double alignScore = (double)aligned / total;
      int modeCols = perRowNonEmpty.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
      bool colsOk = modeCols >= 2 && modeCols <= maxColumns;
      bool consistent = perRowNonEmpty.All(c => c == modeCols);
      if (!(alignScore >= 0.75 && consistent && colsOk)) return null;

      // Rectangularize
      var finalRows = new List<List<string>>();
      foreach (var r in tableRows)
      {
        var v = r;
        if (v.Count > modeCols) v = v.Take(modeCols).ToList();
        if (v.Count < modeCols) v.AddRange(Enumerable.Repeat(string.Empty, modeCols - v.Count));
        finalRows.Add(v);
      }

      var tb = new TableBlock();
      foreach (var row in finalRows) tb.Rows.Add(row);
      return new TableSpan(rowStart, rowEnd, tb);
    }

    private static int NearestLineIndex(List<(string Text, RectangleF Bounds)> lines, System.Drawing.RectangleF bounds)
    {
      // choose the line whose vertical center is closest to the word’s vertical center
      int best = -1;
      double bestD = double.MaxValue;
      double y = bounds.Top + bounds.Height / 2.0;
      for (int i = 0; i < lines.Count; i++)
      {
        var L = lines[i].Bounds;
        double ly = L.Top + L.Height / 2.0;
        double d = Math.Abs(y - ly);
        if (d < bestD)
        {
          bestD = d;
          best = i;
        }
      }
      return best;
    }

    private static int NearestIndex(List<double> sorted, double x)
    {
      if (sorted.Count == 0) return -1;
      int lo = 0, hi = sorted.Count - 1;
      while (lo < hi)
      {
        int mid = (lo + hi) / 2;
        if (sorted[mid] < x) lo = mid + 1; else hi = mid;
      }
      if (lo > 0 && Math.Abs(sorted[lo - 1] - x) <= Math.Abs(sorted[lo] - x)) return lo - 1;
      return lo;
    }
  }
}
