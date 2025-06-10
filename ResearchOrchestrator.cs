using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DeepResearcher.Api.Services
{
    public class ResearchOrchestrator
    {
        private readonly Kernel _kernel;
        private readonly TavilyConnector _tavilyConnector;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
        private readonly List<string> _messages = new();
        
        // Research state
        private string _originalPrompt;
        private string _currentPrompt;
        private string _mainResearchTopic;
        private List<SubtaskModel> _subtasks = new();
        private List<Dictionary<string, object>> _subtaskSummaries = new();
        private string _draftAnswer;
        private string _finalAnswer;
        private List<string> _sources = new();
        private List<string> _clarifyingQuestions = new();
        private bool _needsClarification;
        private string _readyToProceedMessage;
        
        // Plugin functions
        private readonly KernelFunction _decomposer;
        private readonly KernelFunction _summarizer;
        private readonly KernelFunction _combiner;
        private readonly KernelFunction _reviewer;
        private readonly KernelFunction _miniCombiner;
        private readonly KernelFunction _clarifier;

        public ResearchOrchestrator(Kernel kernel, TavilyConnector tavilyConnector)
        {
            _kernel = kernel;
            _tavilyConnector = tavilyConnector;
            
            // Initialize all plugin functions - similar to the original Orchestrator
            _decomposer = kernel.Plugins.GetFunction("decomposer", "DecomposePrompt");
            _summarizer = kernel.Plugins.GetFunction("summarizer", "SummarizeResults");
            _combiner = kernel.Plugins.GetFunction("combiner", "CombineSummaries");
            _reviewer = kernel.Plugins.GetFunction("reviewer", "ReviewAnswer");
            _miniCombiner = kernel.Plugins.GetFunction("miniCombiner", "MergeRefinements");
            _clarifier = kernel.Plugins.GetFunction("clarifier", "ClarifyPrompt");
        }

        public async Task InitializeResearchAsync(string query)
        {
            _originalPrompt = query;
            _currentPrompt = query;
            
            await PerformClarificationAsync();
        }

        public async Task<ResearchService.ClarificationState> GetClarificationStateAsync()
        {
            return await Task.FromResult(new ResearchService.ClarificationState
            {
                Questions = _clarifyingQuestions,
                NeedsClarification = _needsClarification,
                ReadyMessage = _readyToProceedMessage
            });
        }

        public async Task SubmitClarificationAsync(string clarificationText)
        {
            if (!string.IsNullOrWhiteSpace(clarificationText))
            {
                _currentPrompt = $"{_currentPrompt}\n\nAdditional information: {clarificationText}";
                await PerformClarificationAsync();
            }
            else
            {
                _mainResearchTopic = _currentPrompt;
                _needsClarification = false;
            }
        }
        
        public List<ResearchService.SubtaskState> GetSubtasks()
        {
            return _subtasks.Select(s => new ResearchService.SubtaskState
            {
                Id = s.Id,
                Description = s.Description,
                IsComplete = false
            }).ToList();
        }
        
        public async Task<string> GetDraftAnswerAsync() => await Task.FromResult(_draftAnswer);
        
        public async Task<string> GetFinalAnswerAsync() => await Task.FromResult(_finalAnswer ?? _draftAnswer);
        
        public async Task<List<string>> GetSourcesAsync() => await Task.FromResult(_sources);

        // The following methods are adapted from the original Orchestrator methods
        
        private async Task PerformClarificationAsync()
        {
            try
            {
                var args = new KernelArguments { ["user_prompt"] = _currentPrompt };
                var clarifyResult = await _clarifier.InvokeAsync(_kernel, args);
                var clarification = clarifyResult.GetValue<string>() ?? string.Empty;
                
                // Parse clarification JSON
                var clarifierOutput = await ParseJsonOutputAsync<ClarifierModel>(clarification);
                
                if (clarifierOutput != null)
                {
                    _clarifyingQuestions = clarifierOutput.ClarifyingQuestions ?? new List<string>();
                    _readyToProceedMessage = clarifierOutput.ReadyToProceedMessage ?? "Ready to proceed with research.";
                    _needsClarification = NeedsClarification();
                    
                    // Store the unified prompt if available
                    if (!string.IsNullOrEmpty(clarifierOutput.UnifiedResearchPrompt))
                    {
                        _currentPrompt = clarifierOutput.UnifiedResearchPrompt;
                    }
                }
            }
            catch (Exception ex)
            {
                _messages.Add($"Clarification error: {ex.Message}");
                _needsClarification = false; // Force proceed to avoid getting stuck
            }
        }
        
        public async Task DecomposeResearchPromptAsync()
        {
            _mainResearchTopic = _currentPrompt;
            _messages.Add($"Decomposing research topic: {_mainResearchTopic}");
            
            try
            {
                var args = new KernelArguments { ["research_prompt"] = _mainResearchTopic };
                var decompositionResult = await _decomposer.InvokeAsync(_kernel, args);
                var rawDecomposition = decompositionResult.GetValue<string>() ?? string.Empty;
                
                var decomposerOutput = await ParseJsonOutputAsync<DecomposerModel>(rawDecomposition);
                
                if (decomposerOutput?.Subtasks != null && decomposerOutput.Subtasks.Any())
                {
                    _subtasks = decomposerOutput.Subtasks
                        .Where(st => !string.IsNullOrWhiteSpace(st.Description) && st.Description.Length > 30)
                        .ToList();
                    
                    _messages.Add($"Decomposed into {_subtasks.Count} subtasks");
                }
                else
                {
                    throw new InvalidOperationException("Failed to decompose research topic into subtasks");
                }
            }
            catch (Exception ex)
            {
                _messages.Add($"Decomposition error: {ex.Message}");
                throw;
            }
        }
        
        public async Task ResearchSubtasksAsync(Func<int, Task> progressCallback)
        {
            _messages.Add($"Researching {_subtasks.Count} subtasks");
            _subtaskSummaries = new List<Dictionary<string, object>>();
            
            int completed = 0;
            foreach (var subtask in _subtasks)
            {
                try
                {
                    var tavilyResponse = await _tavilyConnector.SearchAsync(subtask.Description);
                    
                    if (tavilyResponse != null && !string.IsNullOrEmpty(tavilyResponse.Answer))
                    {
                        // Add sources to the collection
                        _sources.AddRange(tavilyResponse.Results.Select(r => r.Url));
                        
                        // Summarize findings
                        var summarizerInput = new KernelArguments
                        {
                            ["subtask_id"] = subtask.Id,
                            ["tavily_answer"] = tavilyResponse.Answer.Replace("\"", "\\\""),
                            ["urls"] = tavilyResponse.Results.Select(r => r.Url).ToList(),
                            ["research_prompt"] = _mainResearchTopic
                        };
                        
                        var summaryResult = await _summarizer.InvokeAsync(_kernel, summarizerInput);
                        var rawSummary = summaryResult.GetValue<string>() ?? string.Empty;
                        
                        var summaryOutput = await ParseJsonOutputAsync<SummarizerModel>(rawSummary);
                        
                        if (summaryOutput != null && !string.IsNullOrEmpty(summaryOutput.Summary))
                        {
                            var summaryData = new Dictionary<string, object>
                            {
                                { "subtask_id", subtask.Id },
                                { "summary", summaryOutput.Summary },
                                { "urls", tavilyResponse.Results.Select(r => r.Url).ToList() }
                            };
                            
                            _subtaskSummaries.Add(summaryData);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _messages.Add($"Error researching subtask {subtask.Id}: {ex.Message}");
                    
                    // Add placeholder for the failed subtask
                    _subtaskSummaries.Add(new Dictionary<string, object>
                    {
                        { "subtask_id", subtask.Id },
                        { "summary", $"[Error researching this subtask: {ex.Message}]" },
                        { "urls", new List<string>() }
                    });
                }
                
                completed++;
                
                // Report progress
                int progressPercentage = (completed * 100) / _subtasks.Count;
                await progressCallback(progressPercentage);
            }
            
            _messages.Add($"Completed research on {completed} subtasks");
        }
        
        public async Task CombineResearchAsync()
        {
            _messages.Add("Synthesizing research findings");
            
            try
            {
                // Check if we have any summaries to combine
                if (_subtaskSummaries.Count == 0)
                {
                    _draftAnswer = "No research findings available to synthesize.";
                    _messages.Add("Warning: No summaries available to combine");
                    return;
                }

                // For large sets of summaries, we may need to batch them to avoid token limits
                if (_subtaskSummaries.Count > 10)
                {
                     await CombineResearchInBatchesAsync();
                    return;
                }
                
                // Add timeout control
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                
                var combineInput = new KernelArguments
                {
                    ["original_prompt"] = _originalPrompt,
                    ["summaries"] = JsonSerializer.Serialize(_subtaskSummaries, _jsonOptions),
                    ["research_prompt"] = _mainResearchTopic
                };
                
                // Add additional logging
                _messages.Add($"Combining {_subtaskSummaries.Count} research summaries");
                
                var combinerResult = await _combiner.InvokeAsync(_kernel, combineInput, cts.Token);
                var rawCombiner = combinerResult.GetValue<string>() ?? string.Empty;
                
                if (string.IsNullOrWhiteSpace(rawCombiner))
                {
                    throw new InvalidOperationException("No response received from combiner function");
                }
                
                var combinerOutput = await ParseJsonOutputAsync<CombinerModel>(rawCombiner);
                
                if (combinerOutput != null && !string.IsNullOrEmpty(combinerOutput.FinalAnswer))
                {
                    _draftAnswer = combinerOutput.FinalAnswer;
                    _messages.Add($"Generated initial draft answer ({CountWords(_draftAnswer)} words)");
                }
                else
                {
                    // Fall back to a basic combination if JSON parsing fails
                    _messages.Add("Warning: Structured output parsing failed, using raw output");
                    _draftAnswer = rawCombiner.Length > 500 ? rawCombiner : "Failed to generate a coherent answer.";
                }
            }
            catch (Exception ex)
            {
                _messages.Add($"Synthesis error: {ex.Message}");
                _messages.Add("Attempting to generate a basic synthesis as fallback");
                
                try {
                    // Fallback to a simpler approach
                    await GenerateFallbackSynthesisAsync();
                }
                catch (Exception fallbackEx) {
                    _messages.Add($"Fallback synthesis also failed: {fallbackEx.Message}");
                    throw;
                }
            }
        }

        // New method to handle batched processing for large sets
        private async Task CombineResearchInBatchesAsync()
        {
            _messages.Add("Large number of summaries detected - processing in batches");
            
            // Process in batches of 5
            var allResults = new List<string>();
            var batches = BatchList(_subtaskSummaries, 5);
            int batchNum = 1;
            
            foreach (var batch in batches)
            {
                try
                {
                    _messages.Add($"Processing batch {batchNum}/{batches.Count}");
                    
                    var batchInput = new KernelArguments
                    {
                        ["original_prompt"] = _originalPrompt,
                        ["summaries"] = JsonSerializer.Serialize(batch, _jsonOptions),
                        ["research_prompt"] = _mainResearchTopic
                    };
                    
                    var batchResult = await _combiner.InvokeAsync(_kernel, batchInput);
                    var rawBatch = batchResult.GetValue<string>() ?? string.Empty;
                    
                    if (!string.IsNullOrWhiteSpace(rawBatch))
                    {
                        var batchOutput = await ParseJsonOutputAsync<CombinerModel>(rawBatch);
                        if (batchOutput != null && !string.IsNullOrEmpty(batchOutput.FinalAnswer))
                        {
                            allResults.Add(batchOutput.FinalAnswer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _messages.Add($"Error processing batch {batchNum}: {ex.Message}");
                }
                
                batchNum++;
            }
            
            // Now combine all batch results
            if (allResults.Count > 0)
            {
                _draftAnswer = string.Join("\n\n", allResults);
                _messages.Add($"Generated combined draft from {allResults.Count} batches ({CountWords(_draftAnswer)} words)");
                return;
            }
            
            throw new InvalidOperationException("Failed to generate any batch results");
        }

        // Helper method for batching lists
        private List<List<T>> BatchList<T>(List<T> source, int batchSize)
        {
            var result = new List<List<T>>();
            for (int i = 0; i < source.Count; i += batchSize)
            {
                result.Add(source.Skip(i).Take(batchSize).ToList());
            }
            return result;
        }

        // Fallback synthesis method
        private async Task GenerateFallbackSynthesisAsync()
        {
            _messages.Add("Generating fallback synthesis");
            
            // Create a simpler prompt directly
            var summariesText = new StringBuilder();
            foreach (var summary in _subtaskSummaries)
            {
                if (summary.TryGetValue("subtask_id", out var id) && 
                    summary.TryGetValue("summary", out var content))
                {
                    summariesText.AppendLine($"Topic: {id}");
                    summariesText.AppendLine($"{content}");
                    summariesText.AppendLine();
                }
            }
            
            var fallbackPrompt = $@"
Synthesize the following research information into a coherent answer:

ORIGINAL QUESTION: {_originalPrompt}

RESEARCH FINDINGS:
{summariesText}

Provide a comprehensive, well-structured answer based only on the information above.
";

            var fallbackFunction = KernelFunctionFactory.CreateFromPrompt(
                fallbackPrompt, 
                functionName: "FallbackSynthesis", 
                description: "Synthesizes research when normal methods fail"
            );
            
            var result = await fallbackFunction.InvokeAsync(_kernel, new KernelArguments());
            var fallbackAnswer = result.GetValue<string>();
            
            if (!string.IsNullOrWhiteSpace(fallbackAnswer))
            {
                _draftAnswer = fallbackAnswer;
                _messages.Add($"Generated fallback draft answer ({CountWords(_draftAnswer)} words)");
            }
            else
            {
                throw new InvalidOperationException("Fallback synthesis also failed to generate content");
            }
        }
        
        public async Task ReviewAndRefineAsync()
        {
            _messages.Add("Reviewing answer for gaps");
            
            try
            {
                // Review logic from original Orchestrator's ReviewAnswerForGapsAsync
                var reviewInput = new KernelArguments
                {
                    ["original_prompt"] = _originalPrompt,
                    ["final_answer"] = _draftAnswer,
                    ["summaries"] = JsonSerializer.Serialize(_subtaskSummaries, _jsonOptions),
                    ["research_prompt"] = _mainResearchTopic
                };
                
                var reviewResult = await _reviewer.InvokeAsync(_kernel, reviewInput);
                var rawReview = reviewResult.GetValue<string>() ?? string.Empty;
                
                var reviewOutput = await ParseJsonOutputAsync<ReviewerModel>(rawReview);
                
                if (reviewOutput != null && reviewOutput.FollowUpSubtasks?.Any() == true)
                {
                    _messages.Add($"Identified {reviewOutput.FollowUpSubtasks.Count} topics for additional research");
                    
                    // Similar to original Orchestrator's ResearchFollowUpTasksAsync
                    var followUpSummaries = new List<Dictionary<string, object>>();
                    
                    foreach (var followUpTask in reviewOutput.FollowUpSubtasks)
                    {
                        try
                        {
                            var tavilyResponse = await _tavilyConnector.SearchAsync(followUpTask.Description);
                            
                            if (tavilyResponse != null && !string.IsNullOrEmpty(tavilyResponse.Answer))
                            {
                                _sources.AddRange(tavilyResponse.Results.Select(r => r.Url));
                                
                                var summaryInput = new KernelArguments
                                {
                                    ["subtask_id"] = followUpTask.Id,
                                    ["tavily_answer"] = tavilyResponse.Answer.Replace("\"", "\\\""),
                                    ["urls"] = tavilyResponse.Results.Select(r => r.Url).ToList(),
                                    ["research_prompt"] = _mainResearchTopic
                                };
                                
                                var summaryResult = await _summarizer.InvokeAsync(_kernel, summaryInput);
                                var rawSummary = summaryResult.GetValue<string>() ?? string.Empty;
                                
                                var summaryOutput = await ParseJsonOutputAsync<SummarizerModel>(rawSummary);
                                
                                if (summaryOutput != null && !string.IsNullOrEmpty(summaryOutput.Summary))
                                {
                                    var summaryData = new Dictionary<string, object>
                                    {
                                        { "subtask_id", followUpTask.Id },
                                        { "summary", summaryOutput.Summary },
                                        { "urls", tavilyResponse.Results.Select(r => r.Url).ToList() }
                                    };
                                    
                                    followUpSummaries.Add(summaryData);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _messages.Add($"Error researching follow-up task {followUpTask.Id}: {ex.Message}");
                        }
                    }
                    
                    if (followUpSummaries.Any())
                    {
                        _messages.Add("Refining answer with additional research");
                        
                        // Similar to original Orchestrator's RefineAnswerWithFollowUpResearchAsync
                        var miniCombineInput = new KernelArguments
                        {
                            ["original_answer"] = _draftAnswer,
                            ["new_summaries"] = JsonSerializer.Serialize(followUpSummaries, _jsonOptions),
                            ["research_prompt"] = _mainResearchTopic
                        };
                        
                        var miniCombineResult = await _miniCombiner.InvokeAsync(_kernel, miniCombineInput);
                        var rawMini = miniCombineResult.GetValue<string>() ?? string.Empty;
                        
                        var miniCombinerOutput = await ParseJsonOutputAsync<MiniCombinerModel>(rawMini);
                        
                        if (miniCombinerOutput != null && !string.IsNullOrEmpty(miniCombinerOutput.UpdatedAnswer))
                        {
                            _draftAnswer = miniCombinerOutput.UpdatedAnswer;
                            _messages.Add($"Updated answer with additional research (now {CountWords(_draftAnswer)} words)");
                        }
                    }
                }
                
                // Format and polish the final answer
                _finalAnswer = await AddReferencesAndPolishAsync(_draftAnswer);
                _messages.Add($"Finalized research answer ({CountWords(_finalAnswer)} words)");
            }
            catch (Exception ex)
            {
                _messages.Add($"Review and refine error: {ex.Message}");
                _finalAnswer = _draftAnswer; // Use draft as fallback
            }
        }
        
        public async Task IncorporateFeedbackAsync(string feedback)
        {
            if (string.IsNullOrWhiteSpace(feedback))
                return;
                
            _messages.Add("Incorporating user feedback");
            
            try
            {
                var feedbackPrompt = $@"
You are a research editor. The user has provided feedback on a research article.
Revise the article to incorporate this feedback while maintaining the article's structure and depth.

Original Article:
{_finalAnswer ?? _draftAnswer}

User Feedback:
{feedback}

Provide the complete revised article.";

                var feedbackFunction = KernelFunctionFactory.CreateFromPrompt(
                    feedbackPrompt,
                    functionName: "IncorporateFeedback",
                    description: "Incorporates user feedback into a research article."
                );
                
                var feedbackResult = await feedbackFunction.InvokeAsync(_kernel, new KernelArguments());
                var revisedDraft = feedbackResult.GetValue<string>() ?? string.Empty;
                
                if (!string.IsNullOrWhiteSpace(revisedDraft))
                {
                    _finalAnswer = revisedDraft;
                    _messages.Add($"Updated answer based on feedback ({CountWords(_finalAnswer)} words)");
                }
            }
            catch (Exception ex)
            {
                _messages.Add($"Error incorporating feedback: {ex.Message}");
            }
        }
        
        // Helper methods similar to original Orchestrator
        
        private bool NeedsClarification()
        {
            if (!string.IsNullOrEmpty(_readyToProceedMessage))
            {
                var lowerMessage = _readyToProceedMessage.ToLower();
                if (lowerMessage.Contains("need") || 
                    lowerMessage.Contains("require") || 
                    lowerMessage.Contains("clarif") ||
                    lowerMessage.Contains("more information"))
                {
                    return true;
                }
            }
            
            return _clarifyingQuestions.Any();
        }
        
        private async Task<string> AddReferencesAndPolishAsync(string content)
        {
            // Add references section
            string result = content;
            
            // Check if a references section already exists
            bool hasReferencesSection = result.Contains("# References", StringComparison.OrdinalIgnoreCase) ||
                                      result.Contains("## References", StringComparison.OrdinalIgnoreCase);
            
            if (_sources.Any() && !hasReferencesSection)
            {
                result += "\n\n## References\n";
                foreach (var url in _sources.Distinct())
                {
                    result += $"- {url}\n";
                }
            }
            
            // Optional: add polish for longer content
            if (CountWords(result) > 1000)
            {
                try
                {
                    var polishPrompt = "The following is a draft research article. Please edit it for clarity, coherence, and professionalism. Fix any grammatical errors or awkward phrasing. Ensure the structure is logical and the tone is appropriate for an academic audience. Return only the polished article.\n\n" + result;
                    
                    var polishFunction = KernelFunctionFactory.CreateFromPrompt(
                        polishPrompt,
                        functionName: "PolishArticle",
                        description: "Polishes a research article for grammar, clarity, and professionalism."
                    );
                    
                    var polishResult = await polishFunction.InvokeAsync(_kernel, new KernelArguments());
                    var polished = polishResult.GetValue<string>() ?? string.Empty;
                    
                    if (!string.IsNullOrWhiteSpace(polished) && CountWords(polished) >= CountWords(result) * 0.9)
                    {
                        return polished;
                    }
                }
                catch
                {
                    // Fall back to unpolished version
                }
            }
            
            return result;
        }
        
        private async Task<T?> ParseJsonOutputAsync<T>(string jsonString) where T : class
        {
            if (string.IsNullOrWhiteSpace(jsonString))
                return null;

            try
            {
                // Clean and preprocess the JSON string
                var processedJson = CleanJson(jsonString);
                processedJson = EscapeJsonStringNewlines(processedJson);
                
                // Deserialize to the target type
                return JsonSerializer.Deserialize<T>(processedJson, _jsonOptions);
            }
            catch (Exception ex)
            {
                _messages.Add($"JSON parsing error: {ex.Message}");
                return null;
            }
        }
        
        private string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "{}";

            raw = raw.Trim();

            // Extract JSON from markdown code blocks if present
            if (raw.StartsWith("```"))
            {
                int firstBrace = raw.IndexOf('{');
                int lastBrace = raw.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                {
                    raw = raw.Substring(firstBrace, lastBrace - firstBrace + 1);
                }
            }
            
            // Remove any remaining markdown indicators
            raw = raw.Trim('`', '\n', '\r', ' ');
            
            // Sometimes LLMs add explanation text before or after JSON
            int jsonStart = raw.IndexOf('{');
            int jsonEnd = raw.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                raw = raw.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            return raw;
        }
        
        private string EscapeJsonStringNewlines(string json)
        {
            bool inString = false;
            bool escape = false;
            var result = new System.Text.StringBuilder(json.Length);
            
            foreach (char c in json)
            {
                if (c == '"' && !escape)
                    inString = !inString;
                
                if (inString && c == '\n')
                    result.Append("\\n");
                else if (inString && c == '\r')
                    result.Append("\\r");
                else
                    result.Append(c);
                
                escape = (c == '\\' && !escape);
            }
            
            return result.ToString();
        }
        
        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
                
            return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        
        // Data models for JSON parsing
        private class ClarifierModel
        {
            [JsonPropertyName("unifiedResearchPrompt")]
            public string UnifiedResearchPrompt { get; set; }
            
            [JsonPropertyName("clarifyingQuestions")]
            public List<string> ClarifyingQuestions { get; set; } = new();
            
            [JsonPropertyName("readyToProceedMessage")]
            public string ReadyToProceedMessage { get; set; }
        }
        
        private class DecomposerModel
        {
            [JsonPropertyName("subtasks")]
            public List<SubtaskModel> Subtasks { get; set; } = new();
        }
        
        private class SubtaskModel
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }
            
            [JsonPropertyName("description")]
            public string Description { get; set; }
        }
        
        private class SummarizerModel
        {
            [JsonPropertyName("subtask_id")]
            public string SubtaskId { get; set; }
            
            [JsonPropertyName("summary")]
            public string Summary { get; set; }
        }
        
        private class CombinerModel
        {
            [JsonPropertyName("final_answer")]
            public string FinalAnswer { get; set; }
        }
        
        private class ReviewerModel
        {
            [JsonPropertyName("follow_up_subtasks")]
            public List<SubtaskModel> FollowUpSubtasks { get; set; } = new();
        }
        
        private class MiniCombinerModel
        {
            [JsonPropertyName("updated_answer")]
            public string UpdatedAnswer { get; set; }
        }
    }
}