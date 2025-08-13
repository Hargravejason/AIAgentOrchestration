using Newtonsoft.Json.Linq;
using SPS.SharedPlugins.SearchModels;

namespace AIAgentOrchestration.SharedPlugins.Utilities;

internal static class SearchExtentions
{
  public static WebSearchResult toResult(this JObject? results, bool success)
  {
    WebSearchResult result = new();
    if (results != null)
    {
      if (results.ContainsKey("search_metadata") && results["search_metadata"] is JObject)
      {
        JObject metaData = (JObject)results["search_metadata"]!;
        if (metaData.ContainsKey("id")) result.Metadata.id = metaData["id"]!.Value<string>();
        if (metaData.ContainsKey("status")) result.Metadata.status = metaData["status"]!.Value<string>() ?? "ERROR";
        if (metaData.ContainsKey("json_endpoint")) result.Metadata.json_endpoint = metaData["json_endpoint"]!.Value<string>();
        if (metaData.ContainsKey("created_at")) result.Metadata.created_at = DateTime.TryParse(metaData["created_at"]!.Value<string>(), out DateTime created) ? created : DateTime.UtcNow;
        if (metaData.ContainsKey("processed_at")) result.Metadata.processed_at = DateTime.TryParse(metaData["processed_at"]!.Value<string>(), out DateTime processed) ? processed : DateTime.UtcNow;
        if (metaData.ContainsKey("google_url")) result.Metadata.google_url = metaData["google_url"]!.Value<string>();
        if (metaData.ContainsKey("raw_html_file")) result.Metadata.raw_html_file = metaData["raw_html_file"]!.Value<string>();
        if (metaData.ContainsKey("total_time_taken")) result.Metadata.total_time_taken = metaData["total_time_taken"]!.Value<float>();
      }
      if (results.ContainsKey("search_parameters") && results["search_parameters"] is JObject)
      {
        JObject paramData = (JObject)results["search_parameters"]!;
        if (paramData.ContainsKey("engine")) result.Parameters.engine = paramData["engine"]!.Value<string>();
        if (paramData.ContainsKey("q")) result.Parameters.q = paramData["q"]!.Value<string>();
        if (paramData.ContainsKey("location_requested")) result.Parameters.location_requested = paramData["location_requested"]!.Value<string>();
        if (paramData.ContainsKey("location_used")) result.Parameters.location_used = paramData["location_used"]!.Value<string>();
        if (paramData.ContainsKey("google_domain")) result.Parameters.google_domain = paramData["google_domain"]!.Value<string>();
        if (paramData.ContainsKey("hl")) result.Parameters.hl = paramData["hl"]!.Value<string>();
        if (paramData.ContainsKey("gl")) result.Parameters.gl = paramData["gl"]!.Value<string>();
        if (paramData.ContainsKey("device")) result.Parameters.device = paramData["device"]!.Value<string>();
      }
      if (results.ContainsKey("search_information") && results["search_information"] is JObject)
      {
        JObject infoData = (JObject)results["search_information"]!;
        if (infoData.ContainsKey("query_displayed")) result.Information.query_displayed = infoData["query_displayed"]!.Value<string>();
        if (infoData.ContainsKey("total_results")) result.Information.total_results = infoData["total_results"]!.Value<long>();
        if (infoData.ContainsKey("time_taken_displayed")) result.Information.time_taken_displayed = infoData["time_taken_displayed"]!.Value<float>();
        if (infoData.ContainsKey("organic_results_state")) result.Information.organic_results_state = infoData["organic_results_state"]!.Value<string>();
      }
      if (results.ContainsKey("organic_results") && results["organic_results"] is JArray)
      {
        JArray OrganicData = (JArray)results["organic_results"]!;
        foreach (JObject item in OrganicData)
        {
          Organic_results organic_Results = new();
          if (item.ContainsKey("position")) organic_Results.position = item["position"]!.Value<int>();
          if (item.ContainsKey("title")) organic_Results.title = item["title"]!.Value<string>();
          if (item.ContainsKey("link")) organic_Results.link = item["link"]!.Value<string>();
          if (item.ContainsKey("redirect_link")) organic_Results.redirect_link = item["redirect_link"]!.Value<string>();
          if (item.ContainsKey("displayed_link")) organic_Results.displayed_link = item["displayed_link"]!.Value<string>();
          if (item.ContainsKey("thumbnail")) organic_Results.thumbnail = item["thumbnail"]!.Value<string>();
          if (item.ContainsKey("favicon")) organic_Results.favicon = item["favicon"]!.Value<string>();
          if (item.ContainsKey("snippet")) organic_Results.snippet = item["snippet"]!.Value<string>();
          if (item.ContainsKey("snippet_highlighted_words")) organic_Results.snippet_highlighted_words = ((JArray)item["snippet_highlighted_words"]!).Select(x => x.Value<string>()).ToArray()!;
          if (item.ContainsKey("sitelinks") && item["sitelinks"] is JObject)
          {
            if (((JObject)item["sitelinks"]!).ContainsKey("inline") && ((JObject)item["sitelinks"]!)["inline"] is JArray)
            {
              organic_Results.sitelinks = new() { inline = new List<Link>() };
              foreach (var inlineitem in ((JObject)item["sitelinks"]!)["inline"]!)
              {
                Link link = new();
                if (item.ContainsKey("title")) link.title = item["title"]!.Value<string>();
                if (item.ContainsKey("link")) organic_Results.link = item["link"]!.Value<string>();
                organic_Results.sitelinks.inline.Add(link);
              }
            }
          }

          //html snippit
          if (!string.IsNullOrEmpty(organic_Results.snippet) && organic_Results.snippet_highlighted_words != null && organic_Results.snippet_highlighted_words.Count() > 0)
          {
            organic_Results.htmlSnippit = HighlightWords(organic_Results.snippet, organic_Results.snippet_highlighted_words);
          }

          result.Results.Add(organic_Results);
        }
      }
      result.Success = success;
    }

    return result;
  }

  public static SearchResult toResult(this WebSearchResult result)
  {
    return new SearchResult(result.Success, result.Results == null ? null :
                            result.Results.Select(x => 
                                                  new Search_Result(x.position, x.title, x.link,x.redirect_link,x.displayed_link, x.snippet, x.snippet_highlighted_words, x.source,x.htmlSnippit)).ToList()
                                                  , result.Error);
  }
  public static string HighlightWords(string input, string[] wordsToHighlight)
  {
    string result = string.Empty;
    int lastIndex = 0;

    foreach (var word in wordsToHighlight)
    {
      // Find the first occurrence of the word after the last match
      int index = input.IndexOf(word, lastIndex);
      if (index != -1)
      {
        // Append the text before the match and the bolded word
        result += input.Substring(lastIndex, index - lastIndex) + "<b>" + word + "</b>";
        // Update the last index to be after the matched word
        lastIndex = index + word.Length;
      }
    }

    // Append any remaining text after the last match
    result += input.Substring(lastIndex);
    return result;
  }
}