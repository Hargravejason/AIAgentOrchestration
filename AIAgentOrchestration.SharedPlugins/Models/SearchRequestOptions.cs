namespace SPS.SharedPlugins.SearchModels;

internal class SearchRequestOptions
{
	//Search Query
	/// <summary>
	/// Parameter defines the query you want to search. You can use anything that you would use in a regular Google search. e.g. inurl:, site:, intitle:. We also support advanced search query parameters such as as_dt and as_eq. See the full list of supported advanced search query parameters.
	/// </summary>
	public string Query { get; set; } = string.Empty;

	//Geographic Location
	/// <summary>
	/// Parameter defines from where you want the search to originate. If several locations match the location requested, we'll pick the most popular one. Head to the /locations.json API if you need more precise control. The location and uule parameters can't be used together. It is recommended to specify location at the city level in order to simulate a real user’s search. If location is omitted, the search may take on the location of the proxy.
	/// Use the StateCapitals Dictionary to get the capital of the state for localization, local cities might not exist. 
	/// </summary>
	public string Location { get; set; } = string.Empty;

	//Localization
	/// <summary>
	/// Parameter defines the country to use for the Google search. It's a two-letter country code. (e.g., us for the United States, uk for United Kingdom, or fr for France). Head to the Google countries page for a full list of supported Google countries.
	/// </summary>
	public string Country { get; set; } = "us";
	/// <summary>
	/// Parameter defines the Google domain to use.It defaults to google.com.Head to the Google domains page for a full list of supported Google domains.
	/// </summary>
	public string Google_Domain { get; set; } = string.Empty;

	//Advanced Filters
	/// <summary>
	/// (to be searched) parameter defines advanced search parameters that aren't possible in the regular query field. (e.g., advanced search for patents, dates, news, videos, images, apps, or text contents).
	/// </summary>
	public string ToBeSearched { get; set; } = string.Empty;
	/// <summary>
	/// Parameter defines the level of filtering for adult content. It can be set to active or off, by default Google will blur explicit content.
	/// </summary>
	public string SafeSearch { get; set; } = "off";
	/// <summary>
	/// Parameter defines the exclusion of results from an auto-corrected query when the original query is spelled wrong. It can be set to 1 to exclude these results, or 0 to include them (default). Note that this parameter may not prevent Google from returning results for an auto-corrected query if no other results are available.
	/// </summary>
	public int Nfpr { get; set; }
	/// <summary>
	/// Parameter defines if the filters for 'Similar Results' and 'Omitted Results' are on or off. It can be set to 1 (default) to enable these filters, or 0 to disable these filters.
	/// </summary>
	public int Filter { get; set; } = 1;

	//Pagination
	/// <summary>
	/// Parameter defines the result offset. It skips the given number of results. It's used for pagination. (e.g., 0 (default) is the first page of results, 10 is the 2nd page of results, 20 is the 3rd page of results, etc.).
	/// Google Local Results only accepts multiples of 20(e.g. 20 for the second page results, 40 for the third page results, etc.) as the start value.
	/// </summary>
	public int Start { get; set; }
	/// <summary>
	/// Parameter defines the maximum number of results to return. (e.g., 10 (default) returns 10 results, 40 returns 40 results, and 100 returns 100 results).
	/// </summary>
	public int Num { get; set; } = 10;

	//Serpapi Parameters
	/// <summary>
	/// Set parameter to google (default) to use the Google API engine.
	/// </summary>
	public string Engine { get; set; } = "google";
	/// <summary>
	/// Parameter defines the device to use to get the results. It can be set to desktop (default) to use a regular browser, tablet to use a tablet browser (currently using iPads), or mobile to use a mobile browser (currently using iPhones)..
	/// </summary>
	public string Device { get; set; } = string.Empty;
	/// <summary>
	/// 	Parameter will force to fetch the Google results even if a cached version is already present. A cache is served only if the query and all parameters are exactly the same.Cache expires after 1h.Cached searches are free, and are not counted towards your searches per month.It can be set to false (default) to allow results from the cache, or true to disallow results from the cache.no_cache and async parameters should not be used together.
	/// </summary>
	public bool No_cache { get; set; }
}
