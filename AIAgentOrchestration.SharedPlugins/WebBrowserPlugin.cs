using AngleSharp;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace AIAgentOrchestration.SharedPlugins;

public class WebBrowserPlugin
{
  private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
  private readonly ILogger<WebBrowserPlugin> _logger;
  //private readonly HttpClient _httpClient;

  public WebBrowserPlugin(Microsoft.Extensions.Configuration.IConfiguration config, ILogger<WebBrowserPlugin> logger)
  {
    _config = config;
    _logger = logger;
    //_httpClient = httpClient;
  }

  [KernelFunction, Description("Provides a browser to perform website lookups")]
  public async Task<string> BrowseInternet(string Url)
  {
    Console.WriteLine($"Browse: {Url}");
    try
    {

      return await PerformWebpageLookup(Url);

      //skip playwright for now, it is not working in the current environment
      using var playwright = await Playwright.CreateAsync();

      // initialize a Chromium instance
      await using var browser = await playwright.Chromium.LaunchAsync(new()
      {
#if DEBUG
        Headless = false, // set to "false" while developing
#else
        Headless = true, 
#endif
      });
      // open a new page within the current browser context
      var page = await browser.NewPageAsync();

      await page.GotoAsync(Url);

      var html = await page.ContentAsync();

      return html;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, $"Error in {nameof(WebBrowserPlugin)}.{nameof(BrowseInternet)}");
      throw;
    }
  }



  private async Task<string> PerformWebpageLookup(string Url, int minContent = 150)
  {
    var config = Configuration.Default.WithDefaultLoader().WithJs();
    var context = BrowsingContext.New(config);
    var document = await context.OpenAsync(Url);

    // Remove boilerplate noise
    foreach (var tag in document.QuerySelectorAll("script, style, nav, header, footer, aside, noscript, iframe, form, svg"))
    {
      tag.Remove();
    }

    // Try to find main content by heuristics
    var candidates = document
        .QuerySelectorAll("main, article, section, div")
        .Where(e => e.TextContent.Length > minContent)
        .Select(e => new
        {
          Element = e,
          Score = e.TextContent.Split(' ').Length + e.QuerySelectorAll("p").Length * 10
        })
        .OrderByDescending(x => x.Score)
        .Take(1)
        .ToList();

    var best = candidates.FirstOrDefault()?.Element;

    if (best is null)
      return $"Unable to Look up website";

    // Optional: Sanitize content further
    var content = TextCleaner.CleanWhitespace(best.TextContent.Trim());
    var title = document.Title ?? best.TextContent.Split('.').FirstOrDefault()?.Trim();

    return $"{title}\r\n{content}";
  }
  
  internal static class TextCleaner
  {
    private static readonly string[] JunkPhrases =
    {
        "Breaking News",
        "More () »",
        "Read More",
        "Advertisement",
        "Click here to read",
        "Top Stories",
        "Watch Now",
        "Live Updates"
    };

    public static string CleanWhitespace(string input)
    {
      if (string.IsNullOrWhiteSpace(input))
        return "";

      // Normalize line endings
      input = input.Replace("\r\n", "\n").Replace("\r", "\n");

      // Remove non-printing characters (zero-width, etc.)
      input = Regex.Replace(input, @"[\u200B-\u200D\uFEFF]", "");

      // Remove tabs
      input = input.Replace("\t", " ");

      // Remove junk phrases (case-insensitive)
      foreach (var phrase in JunkPhrases)
      {
        input = Regex.Replace(input, Regex.Escape(phrase), "", RegexOptions.IgnoreCase);
      }

      // Collapse 3+ newlines to 2
      input = Regex.Replace(input, @"\n{3,}", "\n\n");

      // Collapse multiple spaces
      input = Regex.Replace(input, @"[ ]{2,}", " ");

      // Trim each line and remove lines with only symbols or very few characters
      var lines = input
          .Split('\n')
          .Select(line => line.Trim())
          .Where(line => line.Length > 2 && !Regex.IsMatch(line, @"^[^\w\d]+$")) // filter symbol-only lines
          .ToList();

      return string.Join("\n", lines).Trim();
    }
  }

}
