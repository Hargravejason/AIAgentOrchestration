using System.Text.RegularExpressions;
using MuPDFCore;
using MuPDFCore.StructuredText;


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

        // Emit image blocks (IDs + cropped PNG bytes)
        int imgIdx = 0;
        foreach (var block in st.StructuredTextBlocks)
        {
          if (block is MuPDFImageStructuredTextBlock ib)
          {
            imgIdx++;
            var imgbytes = RenderRegionAsPng(doc, p, ib.BoundingBox, dpi: 220); // or 200–220 if you prefer

            current.ContentBlocks.Add(new ContentBlock
            {
              Type = BlockType.Image,
              ImageId = $"p{pageNum}_img{imgIdx}",
              ImageBytes = imgbytes,
              Page = pageNum,
              BlockId = Id("img", pageNum, ib.GetHashCode())
            });
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

    // ---- Tiny table detector (lines → rows, character X0 bands → columns) ----
    private sealed record TablePlacement(TableBlock Table, Rectangle Area);

    private static List<TablePlacement> DetectTables(MuPDFStructuredTextPage st, MuPdfCoreSkeletonOptions opt)
    {
      // collect line baselines (use Y1 ~ “bottom” in MuPDF coords)
      var lines = st.StructuredTextBlocks
                    .OfType<MuPDFTextStructuredTextBlock>()
                    .SelectMany(b => b.Lines)
                    .OrderBy(l => l.BoundingBox.Y0)
                    .ToList();
      if (lines.Count == 0) return new();

      // group into row candidates by similar baseline (Y1)
      var rows = new List<List<MuPDFStructuredTextLine>>();
      foreach (var ln in lines)
      {
        var hit = rows.FirstOrDefault(r => Math.Abs(r[0].BoundingBox.Y1 - ln.BoundingBox.Y1) <= opt.TableRowTolerance);
        if (hit == null) rows.Add(new() { ln }); else hit.Add(ln);
      }

      // build column bands from character X0 positions
      var colEdges = new List<double>();
      foreach (var ln in lines)
      {
        foreach (var ch in ln.Characters)
        {
          var x = CharLeftX(ch); // left-ish quad vertex
          if (!colEdges.Any(e => Math.Abs(e - x) <= opt.TableColTolerance))
            colEdges.Add(x);
        }
      }
      colEdges.Sort();
      if (colEdges.Count < 2 || rows.Count < 2) return new();

      // fill rows → cells
      var tRows = new List<List<string>>();
      foreach (var r in rows)
      {
        // merge the row's lines left-to-right
        var textPieces = r.Select(x => (x.BoundingBox.X0, x.Text ?? "")).OrderBy(t => t.X0).Select(t => t.Item2).ToArray();
        var rowText = string.Join(" ", textPieces).Trim();
        // split into cells by nearest band using characters (more robust than whitespace)
        var cells = new string[colEdges.Count];
        Array.Fill(cells, "");
        foreach (var ln in r)
        {
          foreach (var ch in ln.Characters.OrderBy(c => CharLeftX(c)))
          {
            int idx = NearestIndex(colEdges, CharLeftX(ch));
            if (idx < 0 || idx >= cells.Length) continue;
            cells[idx] += ch.Character;
          }
        }
        // normalize cells
        for (int i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim();
        if (cells.Count(c => !string.IsNullOrWhiteSpace(c)) >= 2)
          tRows.Add(cells.ToList());
      }
      if (tRows.Count < 2) return new();

      var tb = new TableBlock();
      foreach (var r in tRows) tb.Rows.Add(r);

      // union area over participating lines
      var rects = rows.SelectMany(r => r.Select(l => l.BoundingBox));
      var area = Union(rects);

      return new() { new TablePlacement(tb, area) };
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

    private static byte[] RenderRegionAsPng(
    MuPDFDocument doc,
    int pageIndex,          // 0-based
    Rectangle region,       // in page coordinates
    double dpi = 288        // tweak as desired (72 = 1x)
)
    {
      // MuPDFCore uses a "zoom" factor: 72 dpi == 1.0
      double zoom = dpi / 72.0;

      using var ms = new MemoryStream();
      // Write (part of) a page to a stream (PNG):
      // doc.WriteImage(page, region, zoom, pixelFormat, stream, fileType, includeAnnotations=true)
      doc.WriteImage(pageIndex, region, zoom, PixelFormats.RGBA, ms, RasterOutputFileTypes.PNG, includeAnnotations: true);
      return ms.ToArray();
    }
  }

}
