using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PdfSkeleton;

namespace AIAgentOrchestration.WebApplicationTest.Pages
{
  public class IndexModel : PageModel
  {
    private readonly ILogger<IndexModel> _logger;
    public string documentSkeleton { get; set; }

    public IndexModel(ILogger<IndexModel> logger)
    {
      _logger = logger;
    }

    public async Task OnGetAsync()
    {
      string pdfFilePath = "C:\\Users\\hargr\\Downloads\\253 N 8th Ave, Upland, CA 91786, US.pdf";

      byte[] pdfBytes = await System.IO.File.ReadAllBytesAsync(pdfFilePath);

      //var pdfParser = new MuPdfCorePlainTextParser();
      //documentSkeleton = pdfParser.Parse(pdfBytes);
    }
  }
}
