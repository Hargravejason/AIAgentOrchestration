using MuPDFCore;
using MuPDFCore.StructuredText;
using System.Linq;
using System.Text.RegularExpressions;


namespace PdfSkeleton
{
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

  public enum BlockType { Paragraph, BulletListItem, NumberedListItem, Table, Image, PageBreak }

  public sealed class ContentBlock
  {
    public required BlockType Type { get; init; }
    public string? Text { get; set; }               // paragraph/list/caption
    public string? ImageId { get; set; }
    public byte[]? ImageBytes { get; set; }         // (fill later if you add crop-render)
    public TableBlock? Table { get; set; }
    public int Page { get; set; }                   // 1-based
    public string? BlockId { get; set; }            // stable id
  }

  public sealed class TableBlock
  {
    public string? Caption { get; set; }
    public List<List<string>> Rows { get; } = new();
  }

  // ----- Options (tiny, keep simple) -----
  public sealed class MuPdfCoreSkeletonOptions
  {
    public bool EmitPageBreaks { get; set; } = false;
    public double HeadingSizeRatio { get; set; } = 1.25; // line font size vs doc median
    public bool DetectTables { get; set; } = true;
    public double TableColTolerance { get; set; } = 6;   // px tolerance for column bands
    public double TableRowTolerance { get; set; } = 3.5; // px tolerance for row baseline
    public int MaxWordsPerParagraph { get; set; } = 800; // soft split
  }

  public sealed class MuPdfCoreSkeletonParser
  {
    private readonly MuPdfCoreSkeletonOptions _opt;
    public MuPdfCoreSkeletonParser(MuPdfCoreSkeletonOptions? opt = null) => _opt = opt ?? new();

    public DocumentSkeleton Parse(byte[] pdfBytes, string sourceId = "pdf")
    {
      using var ms = new MemoryStream(pdfBytes, writable: false);
      return Parse(ms, sourceId);
    }

    public DocumentSkeleton Parse(Stream pdfStream, string sourceId = "pdf")
    {
      using var ctx = new MuPDFContext();
      var bytes = ReadAll(pdfStream);
      using var doc = new MuPDFDocument(ctx, bytes, InputFileTypes.PDF);

      var skel = new DocumentSkeleton { SourceId = sourceId };
      var current = new Section { Heading = "Document", HeadingLevel = 1 };
      skel.Sections.Add(current);

      // Pass 1: gather font sizes to estimate "body" median for heading detection
      var sizes = new List<double>();
      for (int i = 0; i < doc.Pages.Count; i++)
      {
        using var st = doc.GetStructuredTextPage(i);                       // page → structured text blocks
        foreach (var block in st.StructuredTextBlocks)                     // blocks include text or image types
        {
          if (block is MuPDFTextStructuredTextBlock tb)
          {
            foreach (var line in tb.Lines)
            {
              if (line.Characters.Length > 0)
                sizes.Add(line.Characters.Average(c => (double)c.Size));
            }
          }
        }
      }
      var bodyMedian = Percentile(sizes, 0.5);

      // Pass 2: build sections + blocks
      for (int p = 0; p < doc.Pages.Count; p++)
      {
        int pageNum = p + 1;
        if (_opt.EmitPageBreaks && p > 0)
          current.ContentBlocks.Add(new ContentBlock { Type = BlockType.PageBreak, Page = pageNum, BlockId = Id("pb", pageNum, p) });

        const StructuredTextFlags ST_FLAGS =
                                            StructuredTextFlags.PreserveImages       // collect image blocks
                                          | StructuredTextFlags.Segment              // segment page into regions
                                          | StructuredTextFlags.AccurateBoundingBoxes// better glyph/line boxes
                                          | StructuredTextFlags.Dehyphenate          // merge end-of-line hyphens
                                          | StructuredTextFlags.CollectStructure;    // get structured text with desired flags

        using var st = doc.GetStructuredTextPage(p,true, ST_FLAGS);

        // Estimate full page rectangle
        Rectangle pageRect;
        var textBlocks = st.StructuredTextBlocks.OfType<MuPDFTextStructuredTextBlock>().ToList();
        if (textBlocks.Count > 0)
          pageRect = textBlocks.Select(b => b.BoundingBox).Aggregate((a, b) => Union(new[] { a, b }));
        else
          pageRect = new Rectangle(0, 0, 612, 792); // fallback @72dpi (letter size)

        // Prefer: union of all text block rectangles (most accurate)
        //var textBlocks = st.StructuredTextBlocks
        //                   .OfType<MuPDFTextStructuredTextBlock>()
        //                   //.Select(b => b.BoundingBox)
        //                   .ToList();


        //if (textBlocks.Count > 0)
        //  pageRect = textBlocks.Aggregate((a, b) => Union(new Rectangle[]{ a, b }));
        //else
        //  // Fallback if no text blocks — use a standard letter page @72dpi
        //  pageRect = new Rectangle(0, 0, 612, 792);

        // Emit image blocks (IDs + cropped PNG bytes)
        int imgIdx = 0;
        foreach (var block in st.StructuredTextBlocks)
        {
          if (block is MuPDFImageStructuredTextBlock ib)
          {
            imgIdx++;
            var imgbytes = RenderRegionAsjpeg(doc, p, ib.BoundingBox, dpi: 300);

            foreach (var b in ProcessImageBlock(
                         imgbytes,
                         imageId: $"p{pageNum}_img{imgIdx}",
                         pageNum: pageNum,
                         region: ib.BoundingBox,
                         pageRect: pageRect))
            {
              current.ContentBlocks.Add(b);
            }
          }
        }

        // Optional table detection (very small heuristic)
        var tables = _opt.DetectTables ? DetectTables(st, _opt) : new();

        // Text blocks → lines (reading-ish order: sort lines by Y0, then X0)
        var allLines = st.StructuredTextBlocks
                         .OfType<MuPDFTextStructuredTextBlock>()
                         .SelectMany(b => b.Lines)
                         .OrderBy(ln => ln.BoundingBox.Y0)
                         .ThenBy(ln => ln.BoundingBox.X0)
                         .ToList();

        foreach (var ln in allLines)
        {
          var text = ln.Text ?? string.Empty;
          text = text.Trim();
          if (text.Length == 0) continue;

          // heading?
          var size = ln.Characters.Length > 0 ? ln.Characters.Average(c => (double)c.Size) : bodyMedian;
          if (size >= bodyMedian * _opt.HeadingSizeRatio)
          {
            current = new Section { Heading = text, HeadingLevel = InferHeadingLevel(size, bodyMedian) };
            skel.Sections.Add(current);
            continue;
          }

          // lists?
          if (IsNumbered(text))
          {
            current.ContentBlocks.Add(new ContentBlock
            {
              Type = BlockType.NumberedListItem,
              Text = StripListMarker(text),
              Page = pageNum,
              BlockId = Id("nl", pageNum, ln.GetHashCode())
            });
            continue;
          }
          if (IsBulleted(text))
          {
            current.ContentBlocks.Add(new ContentBlock
            {
              Type = BlockType.BulletListItem,
              Text = StripListMarker(text),
              Page = pageNum,
              BlockId = Id("bl", pageNum, ln.GetHashCode())
            });
            continue;
          }

          // caption → nearest table (simple: lines starting with "Table/Fig")
          if (tables.Count > 0 && Regex.IsMatch(text, @"^\s*(Table|Fig(?:ure)?\.?)\s*\d+[:\.\-\s]", RegexOptions.IgnoreCase))
          {
            var nearest = NearestTable(tables, ln.BoundingBox);
            if (nearest != null && nearest.Table.Caption == null)
            {
              nearest.Table.Caption = text;
              continue;
            }
          }

          // paragraph (soft-split by words to avoid huge chunks)
          foreach (var chunk in SoftSplit(text, _opt.MaxWordsPerParagraph))
          {
            current.ContentBlocks.Add(new ContentBlock
            {
              Type = BlockType.Paragraph,
              Text = chunk,
              Page = pageNum,
              BlockId = Id("p", pageNum, chunk.GetHashCode())
            });
          }
        }

        // emit tables (after scanning captions)
        foreach (var t in tables)
        {
          current.ContentBlocks.Add(new ContentBlock
          {
            Type = BlockType.Table,
            Text = t.Table.Caption,
            Table = t.Table,
            Page = pageNum,
            BlockId = Id("t", pageNum, t.GetHashCode())
          });
        }
      }

      return skel;
    }

    // ---------- helpers ----------

    private static byte[] ReadAll(Stream s)
    {
      if (s is MemoryStream ms && ms.TryGetBuffer(out var seg))
        return seg.ToArray();
      using var tmp = new MemoryStream();
      s.CopyTo(tmp);
      return tmp.ToArray();
    }

    private static string Id(string prefix, int page, int salt) => $"{prefix}_{page}_{Math.Abs(salt):X}";

    private static IEnumerable<string> SoftSplit(string text, int maxWords)
    {
      if (maxWords <= 0) { yield return text; yield break; }
      var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < words.Length; i += maxWords)
      {
        var take = Math.Min(maxWords, words.Length - i);
        yield return string.Join(' ', words.AsSpan(i, take).ToArray());
      }
    }

    private static bool IsNumbered(string s) => Regex.IsMatch(s, @"^\s*(\d+[\.\)]|[A-Za-z][\.\)])\s+");
    private static bool IsBulleted(string s) => Regex.IsMatch(s, @"^\s*([\-–•▪●□■➤\*])\s+");
    private static string StripListMarker(string s) => Regex.Replace(s, @"^\s*(\d+[\.\)]|[A-Za-z][\.\)]|[\-–•▪●□■➤\*])\s+", "").Trim();

    private static int InferHeadingLevel(double size, double body)
    {
      var r = size / Math.Max(1e-6, body);
      if (r >= 1.8) return 1;
      if (r >= 1.4) return 2;
      return 3;
    }

    private static double Percentile(List<double> v, double p)
    {
      if (v == null || v.Count == 0) return 12;
      v.Sort();
      var idx = (v.Count - 1) * p;
      int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
      if (lo == hi) return v[lo];
      var h = idx - lo;
      return v[lo] * (1 - h) + v[hi] * h;
    }

    private sealed record TablePlacement(TableBlock Table, Rectangle Area);

    private static List<TablePlacement> DetectTables(
        MuPDFStructuredTextPage st,
        MuPdfCoreSkeletonOptions opt,
        int maxColumns = 10,
        int minRows = 2,
        int maxCellChars = 40,           // used for "short-line" density
        double alignScoreMin = 0.75,     // % of lines that snap to some col band
        double shortLineFracMin = 0.60   // % of lines <= maxCellChars
    )
    {
      var lines = st.StructuredTextBlocks
          .OfType<MuPDFTextStructuredTextBlock>()
          .SelectMany(b => b.Lines)
          .Select(l => new { Text = (l.Text ?? string.Empty).Trim(), Box = l.BoundingBox })
          .Where(x => x.Text.Length > 0)
          .OrderBy(x => x.Box.Y0).ThenBy(x => x.Box.X0)
          .ToList();

      if (lines.Count == 0) return new();

      // --- derive dynamic tolerances from page stats ---
      var lineHeights = lines.Select(x => (double)x.Box.Y1 - x.Box.Y0).Where(h => h > 0.1).ToList();
      double medH = Median(lineHeights, fallback: 12.0);
      double rowTol = Math.Max(1.5, 0.5 * medH);         // baseline clustering tol (Y)
      double colTol = Math.Max(2.0, 0.6 * (0.35 * medH)); // X-band tol; 0.35*H ≈ char width guess

      // --- build row bands (cluster by Y1) ---
      var rowBands = new List<List<(string Text, Rectangle Box)>>();
      foreach (var ln in lines)
      {
        var hit = rowBands.FirstOrDefault(r => Math.Abs(r[0].Box.Y1 - ln.Box.Y1) <= rowTol);
        if (hit == null) rowBands.Add(new() { (ln.Text, ln.Box) });
        else hit.Add((ln.Text, ln.Box));
      }

      // find contiguous sequences of row bands to evaluate as candidate tables
      var placements = new List<TablePlacement>();
      int start = 0;
      while (start < rowBands.Count)
      {
        int end = start;
        // grow while next band is close vertically (avoid big gaps)
        while (end + 1 < rowBands.Count &&
               Math.Abs(rowBands[end + 1][0].Box.Y0 - rowBands[end][0].Box.Y1) <= 3 * medH)
          end++;

        int bandCount = end - start + 1;
        if (bandCount >= minRows)
        {
          var blockRows = rowBands.GetRange(start, bandCount);
          var placement = TryMakeTable(blockRows, maxColumns, colTol, maxCellChars, alignScoreMin, shortLineFracMin);
          if (placement != null) placements.Add(placement);
        }

        start = end + 1;
      }

      return placements;

      // ---- local helpers ----

      static TablePlacement? TryMakeTable(
          List<List<(string Text, Rectangle Box)>> rowsGeom,
          int maxCols,
          double colTol,
          int maxCellChars,
          double alignScoreMin,
          double shortLineFracMin)
      {
        // Build global X bands from all line left edges
        var xAll = rowsGeom.SelectMany(r => r.Select(c => (double)c.Box.X0))
                           .OrderBy(x => x).ToList();
        if (xAll.Count < 4) return null;

        var colCenters = new List<double>();
        foreach (var x in xAll)
        {
          if (colCenters.Count == 0 || Math.Abs(colCenters[^1] - x) > colTol)
            colCenters.Add(x);
        }
        if (colCenters.Count < 2 || colCenters.Count > maxCols) return null;

        // Assign each (Text,Box) to nearest col band for each row
        var tableRows = new List<List<string>>();
        var perRowNonEmpty = new List<int>();
        int alignedLines = 0, totalLines = 0;
        int shortLines = 0, totalCells = 0;

        foreach (var r in rowsGeom)
        {
          var sorted = r.OrderBy(c => c.Box.X0).ToList();
          var cells = Enumerable.Repeat("", colCenters.Count).ToArray();

          foreach (var c in sorted)
          {
            totalLines++;
            int idx = NearestIndex(colCenters, c.Box.X0);
            if (idx < 0 || idx >= cells.Length) continue;

            // snap check for alignment score
            if (Math.Abs(colCenters[idx] - c.Box.X0) <= colTol) alignedLines++;

            if (cells[idx].Length > 0) cells[idx] += " ";
            cells[idx] += c.Text;

            totalCells++;
            if (c.Text.Length <= maxCellChars) shortLines++;
          }

          int nn = cells.Count(s => !string.IsNullOrWhiteSpace(s));
          perRowNonEmpty.Add(nn);
          tableRows.Add(cells.ToList());
        }

        if (totalLines == 0) return null;
        double alignScore = (double)alignedLines / totalLines;
        double shortFrac = (double)shortLines / Math.Max(1, totalCells);

        // Column count consistency
        int modeCols = perRowNonEmpty.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        bool consistentCols = perRowNonEmpty.All(c => c == modeCols);
        bool colsOk = modeCols >= 2 && modeCols <= maxCols;

        // Reject if it doesn't look like a table
        if (!(alignScore >= alignScoreMin && shortFrac >= shortLineFracMin && consistentCols && colsOk))
          return null;

        // Rectangularize to modeCols
        var finalRows = new List<List<string>>();
        foreach (var r in tableRows)
        {
          var v = r;
          if (v.Count > modeCols) v = v.Take(modeCols).ToList();
          if (v.Count < modeCols) v.AddRange(Enumerable.Repeat("", modeCols - v.Count));
          finalRows.Add(v);
        }

        var tb = new TableBlock();
        foreach (var row in finalRows) tb.Rows.Add(row);

        // Area = union of all row rectangles
        var area = rowsGeom.SelectMany(r => r.Select(c => c.Box)).Aggregate((a, b) => Union(new[] { a, b }));
        return new TablePlacement(tb, area);
      }

      static int NearestIndex(List<double> sorted, double x)
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

      static double Median(List<double> v, double fallback)
      {
        if (v == null || v.Count == 0) return fallback;
        v.Sort();
        int n = v.Count;
        return (n % 2 == 1) ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
      }
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
      if (lo > 0 && Math.Abs(sorted[lo - 1] - x) < Math.Abs(sorted[lo] - x)) return lo - 1;
      return lo;
    }

    private static TablePlacement? NearestTable(List<TablePlacement> tables, Rectangle bb)
    {
      if (tables.Count == 0) return null;
      double best = double.MaxValue;
      TablePlacement? bestT = null;
      foreach (var t in tables)
      {
        // distance from line to top edge of table area
        var d = Math.Abs(t.Area.Y0 - bb.Y1);
        if (d < best) { best = d; bestT = t; }
      }
      return bestT;
    }

    private static Rectangle Union(IEnumerable<Rectangle> rects)
    {
      double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
      foreach (var r in rects)
      {
        x0 = Math.Min(x0, r.X0);
        y0 = Math.Min(y0, r.Y0);
        x1 = Math.Max(x1, r.X1);
        y1 = Math.Max(y1, r.Y1);
      }
      return new Rectangle((float)x0, (float)y0, (float)x1, (float)y1);
    }

    private static double CharLeftX(MuPDFStructuredTextCharacter ch)
    {
      // Use the minimum X of the left edge to be robust to skew/rotation.
      var q = ch.BoundingQuad;
      return Math.Min(q.UpperLeft.X, q.LowerLeft.X);
    }

    private static double CharRightX(MuPDFStructuredTextCharacter ch)
    {
      var q = ch.BoundingQuad;
      return Math.Max(q.UpperRight.X, q.LowerRight.X);
    }

    private static byte[] RenderRegionAsjpeg(MuPDFDocument doc,int pageIndex, Rectangle region, double dpi = 288)
    {
      // MuPDFCore uses a "zoom" factor: 72 dpi == 1.0
      double zoom = dpi / 72.0;

      using var ms = new MemoryStream();
      doc.WriteImage(pageIndex, region, zoom, PixelFormats.RGB, ms, RasterOutputFileTypes.JPEG, includeAnnotations: true);
      return ms.ToArray();
    }
    private static string OcrPngBytes(byte[] pngBytes, string lang = "eng")
    {
      using var engine = new Tesseract.TesseractEngine(@"Assets/tessdata", lang, Tesseract.EngineMode.LstmOnly);
      using var pix = Tesseract.Pix.LoadFromMemory(pngBytes);
      using var page = engine.Process(pix, Tesseract.PageSegMode.Auto);
      return page.GetText()?.Trim() ?? string.Empty;
    }

    private IEnumerable<ContentBlock> ProcessImageBlock(byte[] imgBytes, string imageId, int pageNum, Rectangle region, Rectangle pageRect, string ocrLang = "eng")
    {
      // thresholds (tweak as needed)
      const double TINY_AREA_RATIO = 0.05;   // <5% page -> skip OCR
      const double LARGE_AREA_RATIO = 0.40;   // >=40% page -> attach OCR to the image block
      const int MIN_CHAR_TEXTY = 80;
      const int MIN_LINES_TEXTY = 3;
      const int MIN_WORDS_TEXTY = 12;
      const double MIN_ALNUM_RATIO = 0.60;

      double areaRatio = RectArea(region) / Math.Max(1.0, RectArea(pageRect));

      // always create the image block
      var imgBlock = new ContentBlock
      {
        Type = BlockType.Image,
        ImageId = imageId,
        ImageBytes = imgBytes,
        Page = pageNum,
        BlockId = Id("img", pageNum, imageId.GetHashCode())
      };

      // tiny images → return image only (logos/icons)
      if (areaRatio < TINY_AREA_RATIO)
        return new[] { imgBlock };

      // OCR
      var ocrText = OcrPngBytes(imgBytes, ocrLang);
      if (!IsTexty(ocrText, MIN_CHAR_TEXTY, MIN_LINES_TEXTY, MIN_WORDS_TEXTY, MIN_ALNUM_RATIO))
        return new[] { imgBlock }; // not texty → keep image only

      // texty:
      if (areaRatio >= LARGE_AREA_RATIO)
      {
        // large region (full-page scan / big slide): attach text to the image block
        imgBlock.Text = ocrText;
        return new[] { imgBlock };
      }
      else
      {
        // smaller texty region: emit a paragraph linked to the image
        var para = new ContentBlock
        {
          Type = BlockType.Paragraph,
          Text = ocrText,
          ImageId = imageId,                    // provenance link
          Page = pageNum,
          BlockId = Id("p_ocr", pageNum, ocrText.GetHashCode())
        };
        return new[] { imgBlock, para };
      }

      // ---- local helpers ----
      static double RectArea(Rectangle r) => Math.Max(0, (r.X1 - r.X0)) * Math.Max(0, (r.Y1 - r.Y0));

      static bool IsTexty(string? text, int minChars, int minLines, int minWords, double minAlnumRatio)
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
    }


  }


}
