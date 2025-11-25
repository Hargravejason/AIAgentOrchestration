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

using HtmlAgilityPack;
using IronOcr;
using IronPdf;
using IronSoftware.Drawing;
using IronSoftware.Drawing; // for Color
using IronWord;
using IronWord.Models;
using IronWord.Models.Enums;
using System.Text;
using System.Text.RegularExpressions;
using static HtmlToDocumentModelConverter;
using static IronOcr.OcrResult;

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



public class HtmlToDocumentModelConverter
{
  public DocumentModel Convert(string html)
  {
    var hap = new HtmlDocument();
    hap.LoadHtml(html);

    var model = new DocumentModel();
    var root = hap.DocumentNode.SelectSingleNode("//body") ?? hap.DocumentNode;

    foreach (var child in root.ChildNodes)
    {
      ProcessBlockNode(child, model.Blocks);
    }

    return model;
  }

  private void ProcessBlockNode(HtmlNode node, List<Block> blocks)
  {
    if (node.NodeType == HtmlNodeType.Text)
    {
      var text = node.InnerText.Trim();
      if (!string.IsNullOrEmpty(text))
      {
        var p = new ParagraphBlock();
        p.Inlines.Add(new TextRunInline { Text = text });
        blocks.Add(p);
      }
      return;
    }

    if (node.NodeType != HtmlNodeType.Element)
      return;

    switch (node.Name.ToLowerInvariant())
    {
      case "h1":
      case "h2":
      case "h3":
      case "h4":
      case "h5":
      case "h6":
        blocks.Add(CreateHeadingBlock(node));
        break;

      case "p":
      case "div":
        blocks.Add(CreateParagraphBlock(node));
        break;

      case "ul":
      case "ol":
        blocks.Add(CreateListBlock(node));
        break;

      default:
        // recurse into children for unknown containers
        foreach (var child in node.ChildNodes)
          ProcessBlockNode(child, blocks);
        break;
    }
  }

  private HeadingBlock CreateHeadingBlock(HtmlNode node)
  {
    var h = new HeadingBlock
    {
      Level = int.Parse(node.Name.Substring(1, 1))
    };
    ProcessInlineChildren(node, h.Inlines, bold: true, italic: false, underline: false);
    return h;
  }

  private ParagraphBlock CreateParagraphBlock(HtmlNode node)
  {
    var p = new ParagraphBlock();
    ProcessInlineChildren(node, p.Inlines, bold: false, italic: false, underline: false);
    return p;
  }

  private ListBlock CreateListBlock(HtmlNode node)
  {
    var ordered = node.Name.Equals("ol", StringComparison.OrdinalIgnoreCase);
    var list = new ListBlock { Ordered = ordered };

    foreach (var li in node.Elements("li"))
    {
      var item = new ListItem();
      ProcessInlineChildren(li, item.Inlines, bold: false, italic: false, underline: false);
      list.Items.Add(item);
    }

    return list;
  }

  private void ProcessInlineChildren(
      HtmlNode node,
      List<Inline> inlines,
      bool bold,
      bool italic,
      bool underline)
  {
    foreach (var child in node.ChildNodes)
    {
      if (child.NodeType == HtmlNodeType.Text)
      {
        var text = HtmlEntity.DeEntitize(child.InnerText);
        if (!string.IsNullOrWhiteSpace(text))
        {
          inlines.Add(new TextRunInline
          {
            Text = text,
            Bold = bold,
            Italic = italic,
            Underline = underline
          });
        }
      }
      else if (child.NodeType == HtmlNodeType.Element)
      {
        var tag = child.Name.ToLowerInvariant();
        var b = bold;
        var i = italic;
        var u = underline;

        switch (tag)
        {
          case "b":
          case "strong": b = true; break;
          case "i":
          case "em": i = true; break;
          case "u": u = true; break;
        }

        ProcessInlineChildren(child, inlines, b, i, u);
      }
    }
  }


  public class DocumentModel
  {
    public List<Block> Blocks { get; set; } = new();
  }

  public abstract class Block { }

  public class ParagraphBlock : Block
  {
    public List<Inline> Inlines { get; set; } = new();
  }

  public class HeadingBlock : ParagraphBlock
  {
    public int Level { get; set; } // 1–6
  }

  public class ListBlock : Block
  {
    public bool Ordered { get; set; }
    public List<ListItem> Items { get; set; } = new();
  }

  public class ListItem
  {
    public List<Inline> Inlines { get; set; } = new();
  }

  public abstract class Inline { }

  public class TextRunInline : Inline
  {
    public string Text { get; set; } = string.Empty;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
  }
}



public interface IDocumentWriter<TOutput>
{
  TOutput Write(HtmlToDocumentModelConverter.DocumentModel model);
}

public class IronPdfDocumentWriter : IDocumentWriter<PdfDocument>
{
  private readonly double _pageWidth;
  private readonly double _pageHeight;
  private readonly double _marginLeft;
  private readonly double _marginRight;
  private readonly double _marginTop;
  private readonly double _marginBottom;
  private readonly string _fontName;

  // basic layout config
  public IronPdfDocumentWriter(
        double pageWidth = 210,   // e.g. A4 width (mm or whatever unit IronPdf is using)
        double pageHeight = 297,  // e.g. A4 height
        double marginLeft = 20,
        double marginRight = 20,
        double marginTop = 20,
        double marginBottom = 20,
        string fontName = "Arial")
  {
    _pageWidth = pageWidth;
    _pageHeight = pageHeight;
    _marginLeft = marginLeft;
    _marginRight = marginRight;
    _marginTop = marginTop;
    _marginBottom = marginBottom;
    _fontName = fontName;
  }

  public PdfDocument Write(DocumentModel model)
  {
    if (model == null) throw new ArgumentNullException(nameof(model));

    var pdf = new PdfDocument(_pageWidth, _pageHeight);

    // Remove the auto-created blank page so we control everything
    pdf.Pages.Clear();

    // Start with first page
    int pageIndex = pdf.AddPage(_pageWidth, _pageHeight);

    // cursorYTop = distance from the *top* margin going downward
    double cursorYTop = _marginTop;

    foreach (var block in model.Blocks)
    {
      switch (block)
      {
        case HeadingBlock heading:
          WriteHeading(pdf, ref pageIndex, ref cursorYTop, heading);
          break;

        case ParagraphBlock paragraph when block is not HeadingBlock:
          WriteParagraph(pdf, ref pageIndex, ref cursorYTop, paragraph);
          break;

        case ListBlock list:
          WriteList(pdf, ref pageIndex, ref cursorYTop, list);
          break;
      }
    }

    return pdf;
  }

  // ------------------ helpers ------------------

  private void EnsureSpaceOrNewPage(
      PdfDocument pdf,
      ref int pageIndex,
      ref double cursorYTop,
      double neededHeight)
  {
    // If writing this block would go past the bottom margin, start a new page
    if (cursorYTop + neededHeight > _pageHeight - _marginBottom)
    {
      pageIndex = pdf.AddPage(_pageWidth, _pageHeight);
      cursorYTop = _marginTop;
    }
  }

  private static IEnumerable<string> WrapText(string text, int maxCharsPerLine)
  {
    if (string.IsNullOrWhiteSpace(text))
      yield break;

    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var line = new StringBuilder();

    foreach (var word in words)
    {
      if (line.Length + word.Length + (line.Length > 0 ? 1 : 0) > maxCharsPerLine)
      {
        if (line.Length > 0)
        {
          yield return line.ToString();
          line.Clear();
        }
      }

      if (line.Length > 0)
        line.Append(' ');

      line.Append(word);
    }

    if (line.Length > 0)
      yield return line.ToString();
  }

  private double ToPdfY(double cursorYTop, double lineHeight)
  {
    // Convert "distance from top" to "distance from bottom" (baseline)
    // pdfY = pageHeight - (topOffset + lineHeight)
    return _pageHeight - cursorYTop - lineHeight;
  }

  // ------------------ block writers ------------------

  private void WriteHeading(
      PdfDocument pdf,
      ref int pageIndex,
      ref double cursorYTop,
      HeadingBlock heading)
  {
    double fontSize = heading.Level switch
    {
      1 => 22,
      2 => 18,
      3 => 16,
      _ => 14
    };
    double lineHeight = fontSize + 4;

    var text = string.Concat(heading.Inlines.OfType<TextRunInline>().Select(r => r.Text));
    var lines = WrapText(text, maxCharsPerLine: 60).ToList();

    double totalHeight = lines.Count * lineHeight;
    EnsureSpaceOrNewPage(pdf, ref pageIndex, ref cursorYTop, totalHeight);

    foreach (var line in lines)
    {
      var pdfY = ToPdfY(cursorYTop, lineHeight);

      pdf.DrawText(
          line,
          _fontName,
          fontSize,
          pageIndex,
          _marginLeft,
          pdfY,
          IronSoftware.Drawing.Color.Black,
          0);

      cursorYTop += lineHeight;
    }

    // extra space after heading
    cursorYTop += 4;
  }

  private void WriteParagraph(
      PdfDocument pdf,
      ref int pageIndex,
      ref double cursorYTop,
      ParagraphBlock paragraph)
  {
    double fontSize = 12;
    double lineHeight = fontSize + 4;

    var text = string.Concat(paragraph.Inlines.OfType<TextRunInline>().Select(r => r.Text));
    var lines = WrapText(text, maxCharsPerLine: 80).ToList();

    double totalHeight = lines.Count * lineHeight;
    EnsureSpaceOrNewPage(pdf, ref pageIndex, ref cursorYTop, totalHeight);

    foreach (var line in lines)
    {
      var pdfY = ToPdfY(cursorYTop, lineHeight);

      pdf.DrawText(
          line,
          _fontName,
          fontSize,
          pageIndex,
          _marginLeft,
          pdfY,
          IronSoftware.Drawing.Color.Black,
          0);

      cursorYTop += lineHeight;
    }

    cursorYTop += 4; // spacing after paragraph
  }

  private void WriteList(
      PdfDocument pdf,
      ref int pageIndex,
      ref double cursorYTop,
      ListBlock list)
  {
    double fontSize = 12;
    double lineHeight = fontSize + 4;
    double bulletIndent = 10.0;
    double textIndent = 20.0;

    int index = 1;

    foreach (var item in list.Items)
    {
      var text = string.Concat(item.Inlines.OfType<TextRunInline>().Select(r => r.Text));
      var lines = WrapText(text, maxCharsPerLine: 70).ToList();
      double totalHeight = lines.Count * lineHeight;

      EnsureSpaceOrNewPage(pdf, ref pageIndex, ref cursorYTop, totalHeight);

      string bullet = list.Ordered ? $"{index}." : "•";

      for (int i = 0; i < lines.Count; i++)
      {
        var line = lines[i];
        var pdfY = ToPdfY(cursorYTop, lineHeight);

        if (i == 0)
        {
          // draw bullet/number
          pdf.DrawText(
              bullet,
              _fontName,
              fontSize,
              pageIndex,
              _marginLeft + bulletIndent,
              pdfY,
              IronSoftware.Drawing.Color.Black,
              0);
        }

        // draw text
        pdf.DrawText(
            line,
            _fontName,
            fontSize,
            pageIndex,
            _marginLeft + textIndent,
            pdfY,
            IronSoftware.Drawing.Color.Black,
            0);

        cursorYTop += lineHeight;
      }

      index++;
    }

    cursorYTop += 4; // spacing after list
  }
}

public static class PdfExtensions
{
  /// <summary>
  /// Adds a new blank page to the document and returns its page index.
  /// </summary>
  public static int AddPage(this PdfDocument doc, double widthMm, double heightMm)
  {
    var newPage = new IronPdf.Pages.PdfPage(widthMm, heightMm);
    doc.Pages.Add(newPage);
    return doc.PageCount - 1; // index of the page we just added
  }
}



public class IronWordWriter : IDocumentWriter<WordDocument>
{
  // You can pass defaults in via constructor later if you want
  private readonly string _defaultFontFamily;
  private readonly float _defaultFontSize;

  public IronWordWriter(
      string defaultFontFamily = "Calibri",
      float defaultFontSize = 11)
  {
    _defaultFontFamily = defaultFontFamily;
    _defaultFontSize = defaultFontSize;
  }

  public WordDocument Write(HtmlToDocumentModelConverter.DocumentModel model)
  {
    if (model == null) throw new ArgumentNullException(nameof(model));

    var doc = new WordDocument(); // new blank document 

    foreach (var block in model.Blocks)
    {
      switch (block)
      {
        case HtmlToDocumentModelConverter.HeadingBlock heading:
          AddHeading(doc, heading);
          break;

        case HtmlToDocumentModelConverter.ParagraphBlock paragraph when block is not HtmlToDocumentModelConverter.HeadingBlock:
          AddParagraph(doc, paragraph);
          break;

        case HtmlToDocumentModelConverter.ListBlock list:
          AddList(doc, list);
          break;

          // TableBlock etc. can be added later
      }
    }

    return doc;
  }

  // ---------- Headings ----------

  private void AddHeading(WordDocument doc, HtmlToDocumentModelConverter.HeadingBlock heading)
  {
    // Map H1–H6 to font sizes
    float size = heading.Level switch
    {
      1 => 24,
      2 => 20,
      3 => 16,
      4 => 14,
      5 => 13,
      _ => 12
    };

    var paragraph = new IronWord.Models.Paragraph();

    foreach (var inline in heading.Inlines.OfType<HtmlToDocumentModelConverter.TextRunInline>())
    {
      var textContent = CreateTextContentFromInline(
          inline,
          fontSizeOverride: size,
          forceBold: true // headings are always at least bold
      );

      paragraph.AddText(textContent); // Add text to paragraph 
    }

    doc.AddParagraph(paragraph); // Add paragraph to document 
  }

  // ---------- Normal paragraphs ----------

  private void AddParagraph(WordDocument doc, HtmlToDocumentModelConverter.ParagraphBlock paragraphBlock)
  {
    var paragraph = new IronWord.Models.Paragraph();

    foreach (var inline in paragraphBlock.Inlines.OfType<HtmlToDocumentModelConverter.TextRunInline>())
    {
      var textContent = CreateTextContentFromInline(inline);
      paragraph.AddText(textContent);
    }

    doc.AddParagraph(paragraph);
  }

  // ---------- Lists (unordered / ordered) ----------

  private void AddList(WordDocument doc, HtmlToDocumentModelConverter.ListBlock listBlock)
  {
    // We’ll use MultiLevelTextList + ListItem as in IronWord’s Add List example 
    var textList = new MultiLevelTextList();

    int index = 1;
    foreach (var item in listBlock.Items)
    {
      var paragraph = new IronWord.Models.Paragraph();

      // If ordered, we can optionally prepend numbers into the text itself.
      // Word’s actual numbering formatting can be applied later if needed.
      string numberPrefix = listBlock.Ordered ? $"{index}. " : string.Empty;

      bool isFirstRun = true;
      foreach (var inline in item.Inlines.OfType<HtmlToDocumentModelConverter.TextRunInline>())
      {
        var textRun = inline;

        // Prepend numbering only once per item
        if (isFirstRun && !string.IsNullOrEmpty(numberPrefix))
        {
          textRun = new HtmlToDocumentModelConverter.TextRunInline
          {
            Text = numberPrefix + inline.Text,
            Bold = inline.Bold,
            Italic = inline.Italic,
            Underline = inline.Underline
          };
          isFirstRun = false;
        }

        var textContent = CreateTextContentFromInline(textRun);
        paragraph.AddText(textContent);
      }

      var listItem = new IronWord.Models.ListItem(paragraph);
      textList.AddItem(listItem);

      index++;
    }

    doc.AddMultiLevelTextList(textList);
  }

  // ---------- Helper: map TextRunInline -> TextContent + TextStyle ----------

  private TextContent CreateTextContentFromInline(
      HtmlToDocumentModelConverter.TextRunInline inline,
      float? fontSizeOverride = null,
      bool forceBold = false)
  {
    var text = new TextContent
    {
      Text = inline.Text ?? string.Empty
    };

    // Build a TextStyle only if we need it
    var style = new TextStyle
    {
      TextFont = new IronWord.Models.Font
      {
        FontFamily = _defaultFontFamily,
        FontSize = fontSizeOverride ?? _defaultFontSize
      },
      IsBold = inline.Bold || forceBold,
      IsItalic = inline.Italic,
    };

    if (inline.Underline)
    {
      style.Underline = new Underline(); // default underline style 
    }

    // If nothing special, you *could* leave Style null,
    // but it's fine to always set it for consistency.
    text.Style = style;

    return text;
  }

  public static class TextWrapper
  {
    public static IEnumerable<string> WrapText(string text, int maxCharsPerLine)
    {
      if (string.IsNullOrWhiteSpace(text))
        yield break;

      var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      var line = new StringBuilder();

      foreach (var word in words)
      {
        if (line.Length + word.Length + 1 > maxCharsPerLine)
        {
          if (line.Length > 0)
          {
            yield return line.ToString();
            line.Clear();
          }
        }

        if (line.Length > 0)
          line.Append(' ');

        line.Append(word);
      }

      if (line.Length > 0)
        yield return line.ToString();
    }
  }
}
