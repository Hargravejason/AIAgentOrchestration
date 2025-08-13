using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;

namespace AIAgentOrchestration.Core.Models;

public record RetrievalInput(
    string UserQuery,
    IReadOnlyCollection<Guid> PrefetchDocIds,             // from your prefetch step
    IReadOnlySet<Guid> FaqDocIds,                         // subset of Prefetch filtered by tag=FAQ
    int K = 8                                             // how many final docs to keep
);

public record RetrievedDoc(
    Guid Id,
    string Title,
    string Snippet,                                       // small body/chunk for display
    double DenseScore,                                    // cosine ∈ [0,1]
   
    bool IsFaq,
    IReadOnlyCollection<string> Tags
)
{
  public double BM25 { get; set; }                        // raw ft rank, normalize later
}

public record RankedDoc(
    Guid Id,
    string Title,
    string Snippet,
    double FinalScore,
    bool IsFaq,
    IReadOnlyCollection<string> Tags
);

public record RetrievalResult(
    IReadOnlyList<RankedDoc> TopDocs,
    string[] QueryVariantsUsed,                           // for observability
    string Stage                                          // "A", "B", or "C"
);

static class RetrievalCfg
{
  // Fusion weights
  public const double WDense = 0.55;
  public const double WBm25 = 0.35;
  public const double WPhrase = 0.25; // bonus, applied conditionally
  public const double WProx = 0.10; // optional small bonus
  public const double WTag = 0.15; // FAQ prior

  // Stage acceptance thresholds (tune on your eval set)
  public const double TExact = 0.86;  // cosine or phrase certainty within FAQ
  public const double TFaq = 0.62;  // fused score threshold for Stage B
  public const double TMin = 0.48;  // fused score floor for Stage C

  // RRF
  public const int RrfK = 60;

  // Caps
  public const int CandidateCapPerList = 50;  // from each source before fusion
}

public class HybridRAGModel
{

  #region The Three Stages (Deterministic Flow)
  public RetrievalResult Retrieve(RetrievalInput input)
  {
    var qNorm = Normalize(input.UserQuery);
    var qCanon = Canonicalize(input.UserQuery);
    var variants = ExpandQueryWithLLM(input.UserQuery);

    // ==== Stage A: FAQ Exact/Near-Exact ====
    var aCandidates = QueryFaqExact(input.UserQuery, variants, input.FaqDocIds);
    var aRanked = ScoreAndRank(input.UserQuery, variants, aCandidates);

    // Accept if any doc clearly passes exact certainty:
    // (either cosine high within FAQ OR phrase bonus hit + decent fused)
    if (aRanked.Count > 0 && (
           aCandidates.Any(d => d.IsFaq && d.DenseScore >= RetrievalCfg.TExact)
        || (aRanked[0].IsFaq && aRanked[0].FinalScore >= RetrievalCfg.TFaq + 0.05)))
    {
      return new RetrievalResult(
          TopDocs: aRanked.Take(input.K).ToList(),
          QueryVariantsUsed: variants,
          Stage: "A"
      );
    }

    // ==== Stage B: FAQ-only Hybrid ====
    var bCandidates = QueryFaqHybrid(variants, input.FaqDocIds);
    var bRanked = ScoreAndRank(input.UserQuery, variants, bCandidates);

    if (bRanked.Count > 0 && bRanked[0].FinalScore >= RetrievalCfg.TFaq)
    {
      return new RetrievalResult(
          TopDocs: bRanked.Take(input.K).ToList(),
          QueryVariantsUsed: variants,
          Stage: "B"
      );
    }

    // ==== Stage C: General Hybrid (fallback; never return "no results" here) ====
    var cCandidates = QueryAllHybrid(variants, input.PrefetchDocIds);
    var cRanked = ScoreAndRank(input.UserQuery, variants, cCandidates);

    // Enforce a minimum floor; if below, still return top-K but mark confidence low (handled by caller)
    var top = cRanked.Take(input.K).ToList();
    return new RetrievalResult(
        TopDocs: top,
        QueryVariantsUsed: variants,
        Stage: "C"
    );
  }

  #endregion

  #region Fusion, Priors, and Final Scoring
  // Normalize BM25 to [0,1] within a candidate pool
  void NormalizeBm25(IList<RetrievedDoc> docs)
  {
    var max = docs.Count == 0 ? 1.0 : docs.Max(d => d.BM25);
    if (max <= 0) max = 1.0;
    foreach (var d in docs) d.BM25 = Math.Min(1.0, d.BM25 / max);
  }

  // Compute RRF ranks for tie-breaking / secondary signal
  Dictionary<Guid, double> RrfScores(IList<RetrievedDoc> denseRank, IList<RetrievedDoc> bm25Rank)
  {
    var r = new Dictionary<Guid, double>();
    for (int i = 0; i < denseRank.Count; i++)
      r[denseRank[i].Id] = r.GetValueOrDefault(denseRank[i].Id) + 1.0 / (RetrievalCfg.RrfK + i + 1);
    for (int i = 0; i < bm25Rank.Count; i++)
      r[bm25Rank[i].Id] = r.GetValueOrDefault(bm25Rank[i].Id) + 1.0 / (RetrievalCfg.RrfK + i + 1);
    return r;
  }

  IReadOnlyList<RetrievedDoc> MergeDedup(IReadOnlyList<RetrievedDoc> bm25, IReadOnlyList<RetrievedDoc> dense)
  {
    // Dedup by Id, combine available scores; retain order lists for RRF later
    var byId = new Dictionary<Guid, RetrievedDoc>();
    foreach (var d in dense) byId[d.Id] = d;
    foreach (var b in bm25)
    {
      if (byId.TryGetValue(b.Id, out var ex))
        byId[b.Id] = ex with { BM25 = Math.Max(ex.BM25, b.BM25) };
      else
        byId[b.Id] = b;
    }
    return byId.Values.ToList();
  }

  IReadOnlyList<RankedDoc> ScoreAndRank(string userQuery, string[] queryVariants, IReadOnlyList<RetrievedDoc> candidates)
  {
    if (candidates.Count == 0) return Array.Empty<RankedDoc>();

    NormalizeBm25((IList<RetrievedDoc>)candidates);

    var phraseQ = queryVariants.FirstOrDefault(v => v.StartsWith("\"") && v.EndsWith("\""))
                  ?? $"\"{userQuery}\"";

    var denseOrder = candidates.OrderByDescending(d => d.DenseScore).ToList();
    var bm25Order = candidates.OrderByDescending(d => d.BM25).ToList();
    var rrf = RrfScores(denseOrder, bm25Order);

    var ranked = new List<RankedDoc>(candidates.Count);
    foreach (var c in candidates)
    {
      var phraseBonus = PhraseHit(TrimQuotes(phraseQ), c) ? RetrievalCfg.WPhrase : 0.0;
      var tagPrior = c.IsFaq ? RetrievalCfg.WTag : 0.0;

      var fused = RetrievalCfg.WDense * c.DenseScore
                + RetrievalCfg.WBm25 * c.BM25
                + phraseBonus
                + tagPrior
                + 0.05 * rrf.GetValueOrDefault(c.Id); // small weight on RRF signal

      ranked.Add(new RankedDoc(
          c.Id, c.Title, c.Snippet, FinalScore: fused, c.IsFaq, c.Tags));
    }

    return ranked.OrderByDescending(r => r.FinalScore).ToList();
  }

  string TrimQuotes(string s) => s.Trim().Trim('"');

  #endregion

  #region Stage Query Surfaces (restricted to Prefetch IDs)
  // 1) FAQ exact / near-exact surface (Stage A)
  // Use phrase queries on (Title, Question) for FAQ docs only.
  // Also compute cosine on FAQ vectors and check against TExact.
  IReadOnlyList<RetrievedDoc> QueryFaqExact(string userQuery, string[] queryVariants, IReadOnlyCollection<Guid> faqDocIds)
  {
    // a) FREETEXTTABLE / CONTAINSTABLE on FAQ table joined with @Ids TVP
    // b) Dense cosine over FAQ vectors (filtered by faqDocIds)
    // c) Return top-N with both BM25 and DenseScore populated
    return ExecStageA(userQuery, queryVariants, faqDocIds);
  }

  // 2) FAQ-only hybrid (Stage B)
  IReadOnlyList<RetrievedDoc> QueryFaqHybrid(string[] queryVariants, IReadOnlyCollection<Guid> faqDocIds)
  {
    var bm25 = ExecFts(queryVariants, faqDocIds, top: RetrievalCfg.CandidateCapPerList);
    var dense = ExecDense(queryVariants[0], faqDocIds, top: RetrievalCfg.CandidateCapPerList); // use original for embeddings
    return MergeDedup(bm25, dense);
  }

  // 3) General corpus hybrid (Stage C)
  IReadOnlyList<RetrievedDoc> QueryAllHybrid(string[] queryVariants, IReadOnlyCollection<Guid> prefetchIds)
  {
    var bm25 = ExecFts(queryVariants, prefetchIds, top: RetrievalCfg.CandidateCapPerList);
    var dense = ExecDense(queryVariants[0], prefetchIds, top: RetrievalCfg.CandidateCapPerList);
    return MergeDedup(bm25, dense);
  }

  #endregion

  #region Utilities (normalization, expansion, and helpers)
  string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();

  string Canonicalize(string s) => Regex.Replace(Normalize(s), @"[^\p{L}\p{Nd}\s]", ""); // drop punctuation

  // LLM only suggests variants; code decides how to use them.
  // IMPORTANT: filter the LLM output against a whitelist if you maintain one.
  string[] ExpandQueryWithLLM(string userQuery)
  {
    // System prompt: "Return up to 5 keyword variants + 1 quoted phrase. Do not change order of operations."
    // Implementation omitted — just return strings.
    var variants = new List<string> {
        userQuery,
        $"\"{userQuery}\"",                 // quoted phrase
        ExtractKeywords(userQuery),         // your own noun-phrase extractor or LLM
        // + optional domain synonyms/acronyms (filtered)
    };
    return variants.Distinct().Where(v => !string.IsNullOrWhiteSpace(v)).Take(6).ToArray();
  }

  bool PhraseHit(string query, RetrievedDoc d) =>
      // Very simple: full phrase in Title or Snippet (improve with positions/proximity)
      d.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
      d.Snippet.Contains(query, StringComparison.OrdinalIgnoreCase);

  double ProximityBonus(string query, RetrievedDoc d) =>
      // optional small bonus if ≥80% tokens are within a short window in Title
      0.0;

  #endregion

  #region SQL processing
  IReadOnlyList<RetrievedDoc> ExecStageA(string userQuery, string[] queryVariants, IReadOnlyCollection<Guid> faqDocIds)
  {
    //var quotedPhrase = queryVariants.FirstOrDefault(v => v.StartsWith("\"") && v.EndsWith("\""))
    //                   ?? $"\"{userQuery}\"";

    //// Phrase search on FAQ docs in SQL Server
    //var bm25Results = SqlFtsSearch(
    //    tableName: "FAQ",
    //    fields: new[] { "Title", "Question" },
    //    query: quotedPhrase,
    //    restrictIds: faqDocIds);

    //// Dense cosine similarity on FAQ vectors (filter by faqDocIds in memory or in vector index)
    //var denseResults = DenseVectorSearch(
    //    query: userQuery,
    //    restrictIds: faqDocIds);

    //// Merge them into RetrievedDoc[]
    //return MergeDedup(bm25Results, denseResults);
  }

  IReadOnlyList<RetrievedDoc> ExecFts(string[] queryVariants, IReadOnlyCollection<Guid> restrictIds, int top)
  {
    var results = new List<RetrievedDoc>();

    //foreach (var variant in queryVariants)
    //{
    //  // Example FREETEXTTABLE call using a TVP for restrictIds
    //  results.AddRange(SqlFtsSearch(
    //      tableName: "Docs",
    //      fields: new[] { "Title", "Body" },
    //      query: variant,
    //      restrictIds: restrictIds,
    //      top: top));
    //}

    return results;
  }

  IReadOnlyList<RetrievedDoc> SqlFtsSearch(string tableName, string[] fields, string query, IReadOnlyCollection<Guid> restrictIds, int top = 50)
  {
    //// Pass restrictIds as a TVP dbo.GuidList and JOIN in query
    //var fieldList = string.Join(",", fields);
    //var sql = $@"
    //    SELECT TOP (@top) d.Id, ft.[RANK] AS BM25, d.Title, LEFT(d.Body, 300) AS Snippet
    //    FROM FREETEXTTABLE({tableName}, ({fieldList}), @query, @top*2) ft
    //    JOIN {tableName} d ON d.Id = ft.[KEY]
    //    JOIN @Ids r ON r.Id = d.Id
    //    ORDER BY ft.[RANK] DESC;";

    //// Execute and map to RetrievedDoc (BM25 only; DenseScore=0 for now)
    //return ExecuteSql(sql, new { top, query, Ids = restrictIds });
  }

  IReadOnlyList<RetrievedDoc> ExecDense(string query, IReadOnlyCollection<Guid> restrictIds, int top)
  {
    //var queryVector = Embeddings.Embed(query); // your embedding API

    //// If you store vectors in SQL 2019: load vectors for restrictIds into memory, compute cosine.
    //var candidates = LoadVectors(restrictIds);

    //return candidates
    //    .Select(c => new RetrievedDoc(
    //        c.Id, c.Title, c.Snippet,
    //        DenseScore: CosineSimilarity(queryVector, c.Vector),
    //        BM25: 0,
    //        IsFaq: c.IsFaq,
    //        Tags: c.Tags))
    //    .OrderByDescending(c => c.DenseScore)
    //    .Take(top)
    //    .ToList();
  }

  string ExtractKeywords(string text)
  {
    var stopwords = new HashSet<string>(new[] {
        "the","a","an","in","on","for","is","are","do","if","my","of","and","or","to","with"
    }, StringComparer.OrdinalIgnoreCase);

    var tokens = Regex.Matches(text, @"\b\w+\b")
                      .Select(m => m.Value)
                      .Where(t => !stopwords.Contains(t))
                      .Distinct(StringComparer.OrdinalIgnoreCase);

    return string.Join(" ", tokens);
  }

  #endregion
}