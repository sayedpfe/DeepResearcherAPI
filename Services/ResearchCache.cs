using Microsoft.SemanticKernel;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using System.Security.Cryptography;

namespace DeepResearcher.Api.Services
{
    public class ResearchCache
    {
        private readonly IMemoryCache _cache;
        private readonly Kernel _kernel;
        
        public ResearchCache(IMemoryCache cache, Kernel kernel)
        {
            _cache = cache;
            _kernel = kernel;
        }
        
        public async Task<CachedResearchResult> GetOrCreateResearchAsync(
            string query, 
            Func<Task<string>> researchFunction)
        {
            // Generate semantic hash of the query
            var semanticKey = await GenerateSemanticKeyAsync(query);
            
            // Check if we have cached results for semantically similar queries
            if (_cache.TryGetValue(semanticKey, out CachedResearchResult cachedResult))
            {
                return new CachedResearchResult
                {
                    Result = cachedResult.Result,
                    IsCacheHit = true,
                    OriginalQuery = cachedResult.OriginalQuery,
                    CurrentQuery = query
                };
            }
            
            // Perform the research
            string researchResult = await researchFunction();
            
            // Cache the result
            var result = new CachedResearchResult
            {
                Result = researchResult,
                IsCacheHit = false,
                OriginalQuery = query,
                CurrentQuery = query
            };
            
            _cache.Set(semanticKey, result, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(24)
            });
            
            return result;
        }
        
        private async Task<string> GenerateSemanticKeyAsync(string query)
        {
            // Create a semantic embedding of the query for similarity-based caching
            var embeddingPrompt = $@"
Analyze this research query and generate a condensed version that represents its core semantic meaning.
Remove specific details while keeping the essential research question.

Query: {query}

Return only the core semantic representation, no commentary.
";
            
            var embeddingFunction = KernelFunctionFactory.CreateFromPrompt(
                embeddingPrompt,
                functionName: "GenerateSemanticKey",
                description: "Generates a semantic key from a research query"
            );
            
            var result = await embeddingFunction.InvokeAsync(_kernel, new KernelArguments());
            string semanticKey = result.GetValue<string>().Trim();
            
            // Hash the semantic key
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(semanticKey));
            return Convert.ToBase64String(hash);
        }
    }
    
    public class CachedResearchResult
    {
        public string Result { get; set; }
        public bool IsCacheHit { get; set; }
        public string OriginalQuery { get; set; }
        public string CurrentQuery { get; set; }
    }
}