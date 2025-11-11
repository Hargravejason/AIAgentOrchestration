// PdfSkeleton.cs — Linear PDF parser for RAG using MuPDFCore
// Features:
//  - Safe MuPDFCore usage (no character/font access; strict dispose order)
//  - Linear emission in reading order: Paragraphs, Images (with optional OCR), Tables, Lists
//  - Geometry-only table detection within text blocks (no pipes, no glyph access)
//  - Bullet/Numbered list detection
//  - Minimal, Linux-friendly (no System.Drawing). Optional OCR via injected delegate.
//
// NuGet you’ll need:
//  - MuPDFCore
//  - (optional) Tesseract + ImageSharp for OCR/preproc (not hard-referenced here — we use a delegate)
//
// Usage:
//  var parser = new PdfSkeleton.Parser(ocrFunc: MyOcrFuncOrNull);
//  DocumentSkeleton skel = parser.Parse(pdfBytes, sourceId: "doc1");
//
//  Where `ocrFunc` is: string? MyOcrFunc(byte[] rgbImageBytesJpeg)
//  Return null/empty to skip OCR text; return recognized text to attach/emits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MuPDFCore;
using MuPDFCore.StructuredText;

namespace PdfSkeleton
{
  // ===== Skeleton model =====
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
    public string? Text { get; set; }          // paragraphs, lists, or OCR text on images
    public string? ImageId { get; set; }       // for images or paragraphs linked to images
    public byte[]? ImageBytes { get; set; }    // raw bytes (e.g., JPEG)
    public TableBlock? Table { get; set; }
    public int Page { get; set; }
    public string? BlockId { get; set; }
  }

  // ===== Parser =====
  public sealed class Parser
  {
    // Keep flags minimal and stable — DO NOT use Segment/CollectStructure here
    private const StructuredTextFlags ST_FLAGS =
          StructuredTextFlags.PreserveImages
        | StructuredTextFlags.Dehyphenate;
    // Add PreserveWhitespace/InhibitSpaces if needed for your docs

    private readonly Func<byte[], string?>? _ocrFunc; // optional OCR hook

    public Parser(Func<byte[], string?>? ocrFunc = null)
    {
      _ocrFunc = ocrFunc;
    }

    public DocumentSkeleton Parse(byte[] pdfBytes, string sourceId = "pdf")
    {
      using var ms = new MemoryStream(pdfBytes, writable: false);
      return Parse(ms, sourceId);
    }

    public DocumentSkeleton Parse(Stream pdfStream, string sourceId = "pdf")
    {
      DocumentSkeleton result;

      using (var ctx = new MuPDFContext())           // disposed last
      using (var doc = new MuPDFDocument(ctx, ReadAll(pdfStream), InputFileTypes.PDF))
      {
        var skel = new DocumentSkeleton { SourceId = sourceId };
        var section = new Section { Heading = "Document", HeadingLevel = 1 };
        skel.Sections.Add(section);

        int paraIdx = 0, imgIdx = 0, tableIdx = 0, listIdx = 0;

        for (int p = 0; p < doc.Pages.Count; p++)
        {
          int pageNum = p + 1;

          using (var st = doc.GetStructuredTextPage(p, includeAnnotations: true, flags: ST_FLAGS))
          {
            // Estimate pageRect from text blocks, fallback to letter @72dpi
            var textRects = st.StructuredTextBlocks
                .OfType<MuPDFTextStructuredTextBlock>()
                .Select(b => b.BoundingBox)
                .ToList();
            var pageRect = textRects.Count > 0 ? textRects.Aggregate(Union) : new Rectangle(0, 0, 612, 792);

            // Simple paragraph buffer and list state — per page
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
                Page = pageNum,
                BlockId = $"p_{pageNum}_{++paraIdx}"
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
                  Page = pageNum,
                  BlockId = $"list_{pageNum}_{++listIdx}"
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
              return Regex.IsMatch(t, @"^([0-9]+[.)])\s") ||
                     Regex.IsMatch(t, @"^([A-Za-z]+[.)])\s");
            }

            // Iterate blocks in the order MuPDF provides (approx reading order)
            foreach (var block in st.StructuredTextBlocks)
            {
              // Inline images
              if (block is MuPDFImageStructuredTextBlock ib)
              {
                // Lists end before non-text blocks
                FlushList();
                // Paragraphs flush before images to preserve order
                FlushParagraph();

                imgIdx++;
                var imgBytes = RenderRegionAsJpeg(doc, p, ib.BoundingBox, dpi: 220); // RGB JPEG (OCR-friendly)

                foreach (var b in ProcessImageBlock(
                    imgBytes,
                    imageId: $"p{pageNum}_img{imgIdx}",
                    pageNum: pageNum,
                    region: ib.BoundingBox,
                    pageRect: pageRect,
                    ocrFunc: _ocrFunc))
                {
                  section.ContentBlocks.Add(b);
                }
                continue;
              }

              // Text blocks
              if (block is MuPDFTextStructuredTextBlock tb)
              {
                var lines = tb.Lines
                    .OrderBy(ln => ln.BoundingBox.Y0)
                    .ThenBy(ln => ln.BoundingBox.X0)
                    .Select(ln => (txt: (ln.Text ?? string.Empty).Trim(), box: ln.BoundingBox))
                    .Where(x => x.txt.Length > 0)
                    .ToList();

                if (lines.Count == 0) continue;

                // Detect table spans within this text block (by line indices)
                var spans = DetectTablesInTextBlock(lines)
                    .OrderBy(s => s.rowStart)
                    .ToList();

                int k = 0; int spanIdx = 0;
                while (k < lines.Count)
                {
                  // If next table starts here → emit table and skip its lines
                  if (spanIdx < spans.Count && k == spans[spanIdx].rowStart)
                  {
                    FlushList();
                    FlushParagraph();
                    var tspan = spans[spanIdx];
                    section.ContentBlocks.Add(new ContentBlock
                    {
                      Type = BlockType.Table,
                      Table = tspan.table,
                      Page = pageNum,
                      BlockId = $"t_{pageNum}_{++tableIdx}"
                    });
                    k = tspan.rowEnd + 1;
                    spanIdx++;
                    continue;
                  }

                  // Otherwise, handle lists vs paragraphs
                  var text = lines[k].txt;
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
              }
            }

            // end of page — flush dangling states
            FlushList();
            FlushParagraph();
          }

          // (optional) you could emit a PageBreak block here if you want explicit page boundaries
          // section.ContentBlocks.Add(new ContentBlock { Type = BlockType.PageBreak, Page = pageNum, BlockId = $"pb_{pageNum}" });
        }

        // Give internal finalizers a chance while doc/ctx still alive (defensive for some builds)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        result = skel;
      }

      return result;
    }

    // ===== Helpers =====

    private static byte[] ReadAll(Stream s)
    {
      if (s is MemoryStream ms && ms.TryGetBuffer(out var seg)) return seg.ToArray();
      using var tmp = new MemoryStream();
      s.CopyTo(tmp);
      return tmp.ToArray();
    }

    // Render a rectangular region to JPEG (RGB) — safer for Tesseract.LoadFromMemory
    private static byte[] RenderRegionAsJpeg(MuPDFDocument doc, int pageIndex, Rectangle region, double dpi = 220)
    {
      double zoom = dpi / 72.0;
      using var ms = new MemoryStream();
      doc.WriteImage(pageIndex, region, zoom, PixelFormats.RGB, ms, RasterOutputFileTypes.JPEG, includeAnnotations: true);
      return ms.ToArray();
    }

    // Union of two MuPDF rectangles
    private static Rectangle Union(Rectangle a, Rectangle b)
    {
      float x0 = Math.Min(a.X0, b.X0);
      float y0 = Math.Min(a.Y0, b.Y0);
      float x1 = Math.Max(a.X1, b.X1);
      float y1 = Math.Max(a.Y1, b.Y1);
      return new Rectangle(x0, y0, x1, y1);
    }

    // Decide how to emit an image block; attach OCR or emit a linked paragraph when appropriate
    private static IEnumerable<ContentBlock> ProcessImageBlock(
        byte[] imgBytes,
        string imageId,
        int pageNum,
        Rectangle region,
        Rectangle pageRect,
        Func<byte[], string?>? ocrFunc)
    {
      const double TINY_AREA_RATIO = 0.05;  // <5% page → skip OCR
      const double LARGE_AREA_RATIO = 0.40;  // ≥40% page → attach OCR to the image block
      const int MIN_CHAR_TEXTY = 80;
      const int MIN_LINES_TEXTY = 3;
      const int MIN_WORDS_TEXTY = 12;
      const double MIN_ALNUM_RATIO = 0.60;

      double pageArea = RectArea(pageRect);
      double areaRatio = RectArea(region) / Math.Max(1.0, pageArea);

      var imgBlock = new ContentBlock
      {
        Type = BlockType.Image,
        ImageId = imageId,
        ImageBytes = imgBytes,
        Page = pageNum,
        BlockId = $"img_{pageNum}_{imageId}"
      };

      // Tiny images → just emit image
      if (areaRatio < TINY_AREA_RATIO || ocrFunc == null)
        return new[] { imgBlock };

      var ocr = ocrFunc(imgBytes);
      if (!IsTexty(ocr, MIN_CHAR_TEXTY, MIN_LINES_TEXTY, MIN_WORDS_TEXTY, MIN_ALNUM_RATIO))
        return new[] { imgBlock };

      if (areaRatio >= LARGE_AREA_RATIO)
      {
        // large region (scan/slide) → put OCR text on the image block
        imgBlock.Text = ocr;
        return new[] { imgBlock };
      }
      else
      {
        // smaller region with text → emit image + linked paragraph
        var para = new ContentBlock
        {
          Type = BlockType.Paragraph,
          Text = ocr,
          ImageId = imageId,
          Page = pageNum,
          BlockId = $"p_ocr_{pageNum}_{Math.Abs(ocr!.GetHashCode())}"
        };
        return new[] { imgBlock, para };
      }
    }

    private static bool IsTexty(string? text, int minChars, int minLines, int minWords, double minAlnumRatio)
    {
      if (string.IsNullOrWhiteSpace(text)) return false;
      var s = text.Trim();
      int charCount = s.Count(c => !char.IsWhiteSpace(c));
      if (charCount >= minChars) return true;
      int lineCount = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
      if (lineCount >= minLines) return true;
      var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length >= minWords)
      {
        int alnum = s.Count(c => char.IsLetterOrDigit(c));
        double alnumRatio = (double)alnum / Math.Max(1, s.Length);
        if (alnumRatio >= minAlnumRatio) return true;
      }
      return false;
    }

    // ===== Table detection (geometry-only, within a single text block) =====
    private sealed record TableSpan(int rowStart, int rowEnd, TableBlock table, Rectangle area);

    private static List<TableSpan> DetectTablesInTextBlock(List<(string txt, Rectangle box)> lines,
                                                           int maxColumns = 10,
                                                           int minRows = 2)
    {
      var result = new List<TableSpan>();
      if (lines.Count == 0) return result;

      // dynamic tolerances from this block’s geometry
      var hs = lines.Select(l => (double)(l.box.Y1 - l.box.Y0)).Where(h => h > 0.1).OrderBy(x => x).ToList();
      double medH = hs.Count == 0 ? 12.0 : (hs.Count % 2 == 1 ? hs[hs.Count / 2] : 0.5 * (hs[hs.Count / 2 - 1] + hs[hs.Count / 2]));
      double rowTol = Math.Max(1.5, 0.5 * medH);
      double colTol = Math.Max(2.0, 0.6 * (0.35 * medH)); // ~ char width guess

      // build row bands (cluster by Y1)
      var bands = new List<List<int>>();
      for (int i = 0; i < lines.Count; i++)
      {
        int hit = -1;
        for (int b = 0; b < bands.Count; b++)
        {
          int any = bands[b][0];
          if (Math.Abs(lines[any].box.Y1 - lines[i].box.Y1) <= rowTol) { hit = b; break; }
        }
        if (hit == -1) bands.Add(new List<int> { i }); else bands[hit].Add(i);
      }

      // walk contiguous band sequences and test as tables
      int start = 0;
      while (start < bands.Count)
      {
        int end = start;
        // allow modest vertical gaps; keep contiguous
        while (end + 1 < bands.Count &&
               Math.Abs(CenterY(lines, bands[end + 1][0]) - CenterY(lines, bands[end][0])) <= 3 * medH)
          end++;

        if (end - start + 1 >= minRows)
        {
          var span = TryMakeTable(lines, bands, start, end, maxColumns, colTol);
          if (span != null) result.Add(span);
        }
        start = end + 1;
      }

      return result;

      static double CenterY(List<(string txt, Rectangle box)> L, int idx)
          => (L[idx].box.Y0 + L[idx].box.Y1) * 0.5;
    }

    private static TableSpan? TryMakeTable(List<(string txt, Rectangle box)> lines,
                                           List<List<int>> bands,
                                           int bandStart, int bandEnd,
                                           int maxColumns,
                                           double colTol)
    {
      var idxs = bands.Skip(bandStart).Take(bandEnd - bandStart + 1).SelectMany(x => x).ToList();
      var xs = idxs.Select(i => (double)lines[i].box.X0).OrderBy(x => x).ToList();
      if (xs.Count < 4) return null;

      var centers = new List<double>();
      foreach (var x in xs)
      {
        if (centers.Count == 0 || Math.Abs(centers[^1] - x) > colTol) centers.Add(x);
      }
      if (centers.Count < 2 || centers.Count > maxColumns) return null;

      // build rows and compute alignment/consistency
      var tableRows = new List<List<string>>();
      var perRowNonEmpty = new List<int>();
      int aligned = 0, total = 0;

      for (int b = bandStart; b <= bandEnd; b++)
      {
        var rowIdxs = bands[b].OrderBy(i => lines[i].box.X0).ToList();
        var cells = Enumerable.Repeat(string.Empty, centers.Count).ToArray();

        foreach (var i in rowIdxs)
        {
          total++;
          double x0 = lines[i].box.X0;
          int j = NearestIndex(centers, x0);
          if (j < 0 || j >= cells.Length) continue;

          if (Math.Abs(centers[j] - x0) <= colTol) aligned++;
          if (cells[j].Length > 0) cells[j] += " ";
          cells[j] += lines[i].txt;
        }

        perRowNonEmpty.Add(cells.Count(s => !string.IsNullOrWhiteSpace(s)));
        tableRows.Add(cells.ToList());
      }

      if (total == 0) return null;
      double alignScore = (double)aligned / total;
      int modeCols = perRowNonEmpty.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
      bool colsOk = modeCols >= 2 && modeCols <= maxColumns;
      bool consistent = perRowNonEmpty.All(c => c == modeCols);

      if (!(alignScore >= 0.75 && consistent && colsOk)) return null;

      // rectangularize
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

      var rects = bands.Skip(bandStart).Take(bandEnd - bandStart + 1)
                       .SelectMany(g => g.Select(i => lines[i].box))
                       .ToList();
      var area = rects.Aggregate(Union);

      int rowStart = bands[bandStart].Min();
      int rowEnd = bands[bandEnd].Max();
      return new TableSpan(rowStart, rowEnd, tb, area);
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

    private static double RectArea(Rectangle r) => Math.Max(0, (r.X1 - r.X0)) * Math.Max(0, (r.Y1 - r.Y0));
  }
}
