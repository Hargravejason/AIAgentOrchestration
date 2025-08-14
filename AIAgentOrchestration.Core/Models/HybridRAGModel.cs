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

public sealed record RetrievedChunk(
    Guid DocId,
    Guid ChunkId,
    string Title,
    string Snippet,
    int? PageNumber,                      // optional
    double DenseScore,                    // ∈ [0,1]
    double BM25,                          // raw; will normalize
    bool IsFaq,
    IReadOnlyCollection<string> Tags
);

public sealed record RankedChunk(
    Guid DocId,
    Guid ChunkId,
    string Title,
    string Snippet,
    double FusedScore,
    bool PhraseHit
);

public sealed record RankedDoc(
    Guid DocId,
    string Title,
    double DocScore,
    bool IsFaq,
    IReadOnlyCollection<string> Tags,
    IReadOnlyList<RankedChunk> TopChunks   // chunks you’ll pass to the LLM
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

  IReadOnlyList<RetrievedChunk> MergeByChunkId(IList<RetrievedChunk> bm25, IList<RetrievedChunk> dense)
  {
    var byChunk = new Dictionary<Guid, RetrievedChunk>();

    // Prefer to seed with dense side (keeps DenseScore), but either order works.
    foreach (var d in dense)
      byChunk[d.ChunkId] = d;

    foreach (var b in bm25)
    {
      if (byChunk.TryGetValue(b.ChunkId, out var ex))
      {
        // keep the best of each score dimension
        byChunk[b.ChunkId] = ex with { BM25 = Math.Max(ex.BM25, b.BM25) };
      }
      else
      {
        byChunk[b.ChunkId] = b;
      }
    }

    return byChunk.Values.ToList();
  }

  static void NormalizeBm25(IList<RetrievedChunk> chunks)
  {
    var max = chunks.Count == 0 ? 1.0 : chunks.Max(c => c.BM25);
    if (max <= 0) max = 1.0;
    for (int i = 0; i < chunks.Count; i++)
      chunks[i] = chunks[i] with { BM25 = Math.Min(1.0, chunks[i].BM25 / max) };
  }

  static double FusedChunkScore(RetrievedChunk c, bool phraseHit,
                                double wDense = 0.55, double wBm25 = 0.35, double wPhrase = 0.25, double wTag = 0.15)
  {
    var tagPrior = c.IsFaq ? wTag : 0.0;
    return wDense * c.DenseScore
         + wBm25 * c.BM25
         + (phraseHit ? wPhrase : 0.0)
         + tagPrior;
  }

  // Smooth max over top-k chunks: tau higher → closer to max
  static double SmoothMax(IEnumerable<double> xs, double tau = 8.0)
  {
    var arr = xs as double[] ?? xs.ToArray();
    if (arr.Length == 0) return 0.0;
    var m = arr.Max();
    var sum = 0.0;
    foreach (var x in arr) sum += Math.Exp(tau * (x - m));
    return m + Math.Log(sum) / tau;
  }

  IReadOnlyList<RankedDoc> ScoreChunksAndAggregateDocs(
      string userQuery,
      string[] queryVariants,
      IReadOnlyList<RetrievedChunk> candidates,
      int topPerDoc = 3,
      double tau = 8.0) // for SmoothMax
  {
    if (candidates.Count == 0) return Array.Empty<RankedDoc>();

    NormalizeBm25((IList<RetrievedChunk>)candidates);

    var phraseQ = queryVariants.FirstOrDefault(v => v.StartsWith("\"") && v.EndsWith("\""))
                  ?? $"\"{userQuery}\"";
    var phrase = phraseQ.Trim('"');

    // 1) Compute chunk-level fused scores
    var rankedChunks = new List<RankedChunk>(candidates.Count);
    foreach (var c in candidates)
    {
      bool phraseHit = (!string.IsNullOrEmpty(phrase) &&
         (c.Title?.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0 ||
          c.Snippet?.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0));

      var fused = FusedChunkScore(c, phraseHit);
      rankedChunks.Add(new RankedChunk(c.DocId, c.ChunkId, c.Title, c.Snippet, fused, phraseHit));
    }

    // 2) Group by doc and aggregate top-k chunks per doc
    var byDoc = rankedChunks
        .GroupBy(rc => rc.DocId)
        .Select(g =>
        {
          // Top-k chunks per doc
          var topK = g.OrderByDescending(x => x.FusedScore).Take(topPerDoc).ToList();

          // SmoothMax pooling of their fused scores
          var pooled = SmoothMax(topK.Select(x => x.FusedScore), tau);

          // Multi-hit bonus: small boost per extra good chunk
          // (tune 0.03, cap 0.12)
          var multiHitBonus = Math.Min(0.12, 0.03 * Math.Max(0, topK.Count - 1));

          // Phrase doc bonus: if any top chunk had a phrase hit, add tiny doc-level nudge
          var anyPhrase = topK.Any(x => x.PhraseHit);
          var docPhraseBonus = anyPhrase ? 0.05 : 0.0;

          var docScore = pooled + multiHitBonus + docPhraseBonus;

          // Pick a title/snippet (from best chunk)
          var best = topK[0];

          // We need IsFaq/Tags; carry them by looking back to any candidate from this doc
          var sample = candidates.First(c => c.ChunkId == best.ChunkId);

          return new RankedDoc(
              DocId: g.Key,
              Title: best.Title,
              DocScore: docScore,
              IsFaq: sample.IsFaq,
              Tags: sample.Tags,
              TopChunks: topK
          );
        })
        .OrderByDescending(d => d.DocScore)
        .ToList();

    return byDoc;
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

  public sealed class Lexicon
  {
    // canonical -> regex matchers for surface forms (data-driven: your thesaurus fills these)
    public Dictionary<string, List<Regex>> PatternsByCanonical { get; init; } = new();

    // canonical -> contrast set id (e.g., "employment_class")
    public Dictionary<string, string> ContrastSetOfCanonical { get; init; } = new();

    // contrast set id -> members (canonicals) in that set
    public Dictionary<string, HashSet<string>> MembersBySet { get; init; } = new();
  }

  public static HashSet<string> CanonicalsInText(string text, Lexicon lex)
  {
    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(text)) return found;

    foreach (var (canonical, patterns) in lex.PatternsByCanonical)
    {
      foreach (var rx in patterns)
      {
        if (rx.IsMatch(text))
        {
          found.Add(canonical);
          break; // next canonical
        }
      }
    }
    return found;
  }

  public sealed record AlignmentWeights(
    double BoostSame = 0.18,    // small positive nudge if same member appears
    double PenaltyAlt = 0.22,   // penalty if an alternate appears but primary is absent
    double BothPresentDamp = 0.10 // dampen when both appear (optional)
);

  public static double LexicalAlignmentAdjust(
      string queryText,
      string chunkText,
      Lexicon lex,
      AlignmentWeights w)
  {
    var qCans = CanonicalsInText(queryText, lex);
    if (qCans.Count == 0) return 0;

    var cCans = CanonicalsInText(chunkText, lex);
    if (cCans.Count == 0) return 0;

    // Group query canonicals by their contrast set
    var desiredBySet = qCans
        .Where(c => lex.ContrastSetOfCanonical.ContainsKey(c))
        .GroupBy(c => lex.ContrastSetOfCanonical[c])
        .ToDictionary(g => g.Key, g => g.ToHashSet(StringComparer.OrdinalIgnoreCase));

    if (desiredBySet.Count == 0) return 0;

    double adj = 0;

    foreach (var (setId, desiredSet) in desiredBySet)
    {
      if (!lex.MembersBySet.TryGetValue(setId, out var members)) continue;

      // What does the chunk mention from this set?
      var chunkHits = cCans.Where(c => members.Contains(c))
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

      if (chunkHits.Count == 0) continue;

      // If chunk has the same member(s) as query → boost
      bool hasSame = chunkHits.Overlaps(desiredSet);

      // If chunk has alternates (members in set but not desired) → penalty
      bool hasAlternate = chunkHits.Any(c => !desiredSet.Contains(c));

      if (hasSame && hasAlternate)
      {
        // Mixed mentions — dampen (optional)
        adj += w.BoostSame - w.BothPresentDamp;
      }
      else if (hasSame)
      {
        adj += w.BoostSame;
      }
      else if (hasAlternate)
      {
        adj -= w.PenaltyAlt;
      }
    }

    return adj;
  }

  static double FusedChunkScore(
    RetrievedChunk c,
    bool phraseHit,
    string userQuery,
    Lexicon lex,
    AlignmentWeights aw,
    double wDense = 0.55,
    double wBm25 = 0.35,
    double wPhrase = 0.25,
    double wTag = 0.15)
  {
    var tagPrior = c.IsFaq ? wTag : 0.0;

    var baseScore = wDense * c.DenseScore
                  + wBm25 * c.BM25
                  + (phraseHit ? wPhrase : 0.0)
                  + tagPrior;

    // NEW: data-driven lexical alignment tweak
    var text = (c.Title ?? "") + " " + (c.Snippet ?? "");
    var alignAdj = LexicalAlignmentAdjust(userQuery, text, lex, aw);

    return baseScore + alignAdj;
  }
}


public sealed class ContextPacker
{
  public int BudgetTokens { get; }
  public int MaxDocs { get; }
  public int MaxChunksPerDoc { get; }

  public ContextPacker(int budgetTokens = 1800, int maxDocs = 4, int maxChunksPerDoc = 3)
  {
    BudgetTokens = budgetTokens;
    MaxDocs = maxDocs;
    MaxChunksPerDoc = maxChunksPerDoc;
  }

  public IReadOnlyList<(Guid DocId, Guid ChunkId, string PackedText)> Pack(
      IReadOnlyList<RankedDoc> rankedDocs, string userQuery)
  {
    var packed = new List<(Guid, Guid, string)>();
    int used = 0;

    foreach (var doc in rankedDocs.Take(MaxDocs))
    {
      int added = 0;
      foreach (var ch in doc.TopChunks.OrderByDescending(c => c.FusedScore))
      {
        if (added >= MaxChunksPerDoc) break;

        var txt = BuildWindow(userQuery, ch.Snippet, windowChars: 900); // ~200 tokens
        if (string.IsNullOrWhiteSpace(txt)) continue;

        int t = EstimateTokens(txt);
        if (used + t > BudgetTokens) return packed;

        packed.Add((doc.DocId, ch.ChunkId, txt));
        used += t;
        added++;
      }
    }
    return packed;
  }

  // ~very rough token estimate: chars/4 (tweak per model)
  private static int EstimateTokens(string s) => Math.Max(1, s.Length / 4);

  // Grab sentences near keyword hits; fallback to head of snippet
  // === Replace your BuildWindow with this pair ===

  static bool IsHeaderLike(string s)
  {
    if (string.IsNullOrWhiteSpace(s)) return false;
    var trimmed = s.Trim();

    // short line, ends with ":" or all caps or Title Case → header-ish
    bool veryShort = trimmed.Length <= 80;
    bool endsColon = trimmed.EndsWith(":") || trimmed.EndsWith("?");
    bool manyCaps = trimmed.Count(char.IsUpper) >= Math.Max(6, trimmed.Length / 2);
    bool titleCase = Regex.IsMatch(trimmed, @"^([A-Z][a-z]+)(\s+[A-Z][a-z]+){0,6}\s*:?$");

    // bullets/numbering
    bool bullet = Regex.IsMatch(trimmed, @"^(\d+\.|\-|\u2022)\s");

    return veryShort && (endsColon || manyCaps || titleCase || bullet);
  }

  static IReadOnlyList<string> BuildWindows(
      string query,
      string snippet,
      int windows = 2,                 // allow up to 2 windows per chunk
      int windowChars = 800,           // ~180–220 tokens
      int minGapChars = 500,           // keep windows apart
      int earlyHeaderThreshold = 300)  // if top hit is very early, skip to a deeper anchor
  {
    if (string.IsNullOrWhiteSpace(snippet)) return Array.Empty<string>();

    var sentences = Regex.Split(snippet, @"(?<=[\.!\?])\s+")
                         .Select(s => s.Trim())
                         .Where(s => !string.IsNullOrEmpty(s))
                         .ToArray();

    // Keyword overlap scoring
    var qTerms = Regex.Matches(query.ToLowerInvariant(), @"\b[a-z0-9]{3,}\b")
                      .Select(m => m.Value)
                      .ToHashSet();

    int CharIndexOfSentence(int idx)
    {
      int pos = 0;
      for (int i = 0; i < idx; i++) pos += sentences[i].Length + 1;
      return pos;
    }

    // Score sentences; penalize obvious headers and very-early hits
    var scored = sentences.Select((s, i) =>
    {
      var toks = Regex.Matches(s.ToLowerInvariant(), @"\b[a-z0-9]{3,}\b")
                      .Select(m => m.Value);
      int overlap = toks.Count(t => qTerms.Contains(t));

      bool header = IsHeaderLike(s);
      int charPos = CharIndexOfSentence(i);

      double score = overlap;
      if (header) score -= 1.0;                            // downweight headers
      if (charPos < earlyHeaderThreshold) score -= 0.5;    // bias away from very top

      return (i, charPos, score);
    })
    .OrderByDescending(x => x.score)
    .ToList();

    // Greedy selection of anchors (far apart)
    var anchors = new List<(int i, int pos)>();
    foreach (var cand in scored)
    {
      if (anchors.Count >= windows) break;
      if (cand.score <= 0 && anchors.Count > 0) break; // no meaningful hits left

      bool farEnough = anchors.All(a => Math.Abs(a.pos - cand.charPos) >= minGapChars);
      if (farEnough)
      {
        // If this anchor is too close to the top and header-ish, try the next best
        if (cand.charPos < earlyHeaderThreshold && IsHeaderLike(sentences[cand.i]))
          continue;

        anchors.Add((cand.i, cand.charPos));
      }
    }

    // Fallback: if we found nothing, take a mid-chunk slice
    if (anchors.Count == 0)
    {
      int midStart = Math.Max(0, snippet.Length / 2 - windowChars / 2);
      return new[] { snippet.Substring(midStart, Math.Min(windowChars, snippet.Length - midStart)).Trim() };
    }

    // Build windows around anchors
    string SliceAround(int i)
    {
      int start = CharIndexOfSentence(i);
      int left = Math.Max(0, start - windowChars / 3); // slightly above the anchor
      int len = Math.Min(windowChars, snippet.Length - left);
      return snippet.Substring(left, len).Trim();
    }

    var windowsOut = anchors.Select(a => SliceAround(a.i)).ToList();

    // If we only got 1 window and budget allows, add a fallback later window
    if (windowsOut.Count == 1 && snippet.Length > windowsOut[0].Length + minGapChars)
    {
      int altStart = Math.Min(snippet.Length - windowChars, windowsOut[0].Length + minGapChars);
      if (altStart > 0)
        windowsOut.Add(snippet.Substring(altStart, Math.Min(windowChars, snippet.Length - altStart)).Trim());
    }

    return windowsOut;
  }


  private static int ScoreSentence(string s, HashSet<string> qTerms)
  {
    var toks = Regex.Matches(s.ToLowerInvariant(), @"\b\w+\b").Select(m => m.Value);
    int hits = 0; foreach (var t in toks) if (qTerms.Contains(t)) hits++;
    return hits;
  }
}



