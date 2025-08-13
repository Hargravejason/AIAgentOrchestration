namespace SPS.SharedPlugins.SearchModels;

public record SearchResult(bool Success, IList<Search_Result>? Results, string? Error);

public record Search_Result
(
  int position,
  string? title ,
  string? link,
  string? redirect_link,
  string? displayed_link,
  string? snippet ,
  string[]? snippet_highlighted_words ,
  string? source,
  string? htmlSnippit 
);

internal class WebSearchResult
{
	public Search_metadata Metadata { get; set; } = new Search_metadata();
	public Search_parameters Parameters { get; set; } = new Search_parameters();
	public Search_information Information { get; set; } = new Search_information();
	public IList<Organic_results> Results { get; set; } = new List<Organic_results>();

	public bool Success { get; set; }
	public string Error { get; set; } = string.Empty;
}

internal class Search_metadata
{
	public string? id { get; set; }
	public string status { get; set; } = "ERROR";
	public string? json_endpoint { get; set; }
	public DateTime created_at { get; set; } = DateTime.UtcNow;
	public DateTime processed_at { get; set; } = DateTime.UtcNow;
	public string? google_url { get; set; }
	public string? raw_html_file { get; set; }
	public float? total_time_taken { get; set; }
}

internal class Search_parameters
{
	public string? engine { get; set; }
	public string? q { get; set; }
	public string? location_requested { get; set; }
	public string? location_used { get; set; }
	public string? google_domain { get; set; }
	public string? hl { get; set; }
	public string? gl { get; set; }
	public string? device { get; set; }
}
internal class Search_information
{
	public string? query_displayed { get; set; }
	public long? total_results { get; set; }
	public float? time_taken_displayed { get; set; }
	public string? organic_results_state { get; set; }
}

internal class Organic_results
{
	public int position { get; set; }
	public string? title { get; set; }
	public string? link { get; set; }
	public string? redirect_link { get; set; }
	public string? displayed_link { get; set; }
	public string? thumbnail { get; set; }
	public string? favicon { get; set; }
	public string? snippet { get; set; }
	public string[]? snippet_highlighted_words { get; set; }
	public string? source { get; set; }
	public string? total_time_taken { get; set; }
	public Sitelinks? sitelinks { get; set; }

	public string? htmlSnippit { get; set; }
}

internal class Sitelinks
{
	public List<Link> inline { get; set; } = new List<Link>();

}

internal class Link
{
	public string? title { get; set; }
	public string? link { get; set; } 
}