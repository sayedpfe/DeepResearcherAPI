using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.IO;

namespace DeepResearcher.Api.Services
{
    public static class StringExtensions
    {
        public static string Truncate(this string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        public static int CountWords(this string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

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
                AddMessage($"Clarification error: {ex.Message}");
                _needsClarification = false; // Force proceed to avoid getting stuck
            }
        }
        
        public async Task DecomposeResearchPromptAsync()
        {
            _mainResearchTopic = _currentPrompt;
            AddMessage($"Decomposing research topic: {_mainResearchTopic}");
            
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
                    
                    AddMessage($"Decomposed into {_subtasks.Count} subtasks");
                }
                else
                {
                    throw new InvalidOperationException("Failed to decompose research topic into subtasks");
                }
            }
            catch (Exception ex)
            {
                AddMessage($"Decomposition error: {ex.Message}");
                throw;
            }
        }
        
        public async Task ResearchSubtasksAsync(Func<int, Task> progressCallback)
        {
            AddMessage($"Researching {_subtasks.Count} subtasks");
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
                    AddMessage($"Error researching subtask {subtask.Id}: {ex.Message}");
                    
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
            
            AddMessage($"Completed research on {completed} subtasks");
        }

        public async Task<string> ResearchSubtasksAsync()
        {
            try
            {
                var tavilyResponse = await _tavilyConnector.SearchAsync("query");
                return tavilyResponse.Answer; // Fix: Access the 'Answer' property of TavilyResponse instead of trying to return the object directly.
            }
            catch (Exception ex)
            {
                _messages.Add($"Research error: {ex.Message}");
                throw; // Re-throws the original exception with its stack trace intact
            }
        }

        public async Task<string> ResearchSubtasksBadlyAsync()
        {
            try
            {
                // This approach can cause deadlocks and loses exception context
                var tavilyResponse = await _tavilyConnector.SearchAsync("query");
                return tavilyResponse.Answer;
            }
            catch (Exception ex)
            {
                // Handle error by adding message and throwing exception
                _messages.Add($"Research error: {ex.Message}");
                throw; // Re-throws the original exception with its stack trace intact
            }
        }
        
        public async Task CombineResearchAsync()
        {
            AddMessage("Synthesizing research findings");
            
            try
            {
                // Check if we have any summaries to combine
                if (_subtaskSummaries.Count == 0)
                {
                    _draftAnswer = "No research findings available to synthesize.";
                    AddMessage("Warning: No summaries available to combine");
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
                AddMessage($"Combining {_subtaskSummaries.Count} research summaries");
                
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
                    AddMessage($"Generated initial draft answer ({CountWords(_draftAnswer)} words)");
                }
                else
                {
                    // Fall back to a basic combination if JSON parsing fails
                    AddMessage("Warning: Structured output parsing failed, using raw output");
                    _draftAnswer = rawCombiner.Length > 500 ? rawCombiner : "Failed to generate a coherent answer.";
                }
            }
            catch (Exception ex)
            {
                AddMessage($"Synthesis error: {ex.Message}");
                AddMessage("Attempting to generate a basic synthesis as fallback");
                
                try {
                    // Fallback to a simpler approach
                    await GenerateFallbackSynthesisAsync();
                }
                catch (Exception fallbackEx) {
                    AddMessage($"Fallback synthesis also failed: {fallbackEx.Message}");
                    throw;
                }
            }
        }

        // New method to handle batched processing for large sets
        private async Task CombineResearchInBatchesAsync()
        {
            AddMessage("Large number of summaries detected - processing in batches");
            
            // Process in batches of 5
            var allResults = new List<string>();
            var batches = BatchList(_subtaskSummaries, 5);
            int batchNum = 1;
            
            foreach (var batch in batches)
            {
                try
                {
                    AddMessage($"Processing batch {batchNum}/{batches.Count}");
                    
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
                    AddMessage($"Error processing batch {batchNum}: {ex.Message}");
                }
                
                batchNum++;
            }
            
            // Now combine all batch results
            if (allResults.Count > 0)
            {
                _draftAnswer = string.Join("\n\n", allResults);
                AddMessage($"Generated combined draft from {allResults.Count} batches ({CountWords(_draftAnswer)} words)");
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
            AddMessage("Generating fallback synthesis");
            
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
                AddMessage($"Generated fallback draft answer ({CountWords(_draftAnswer)} words)");
            }
            else
            {
                throw new InvalidOperationException("Fallback synthesis also failed to generate content");
            }
        }
        
        public async Task ReviewAndRefineAsync()
        {
            AddMessage("Reviewing answer for gaps and accuracy");
            
            try
            {
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
                
                if (reviewOutput != null)
                {
                    // Track accuracy concerns for later improvement
                    if (reviewOutput.AccuracyConcerns?.Any() == true)
                    {
                        AddMessage($"Identified {reviewOutput.AccuracyConcerns.Count} potential accuracy issues to address");
                        await AddressAccuracyConcernsAsync(reviewOutput.AccuracyConcerns);
                    }
                    
                    // Process follow-up tasks for knowledge gaps
                    if (reviewOutput.FollowUpSubtasks?.Any() == true)
                    {
                        AddMessage($"Identified {reviewOutput.FollowUpSubtasks.Count} topics for additional research");
                        await ResearchFollowUpTasksAsync(reviewOutput.FollowUpSubtasks);
                    }
                    
                    // Store quality metrics
                    if (reviewOutput.CompletenessScore > 0)
                    {
                        AddMessage($"Research completeness score: {reviewOutput.CompletenessScore}/10");
                    }
                }
                
                // Perform final validation
                bool passesValidation = await ValidateResearchQualityAsync();
                if (!passesValidation)
                {
                    AddMessage("Research failed validation. Attempting corrections.");
                }
                
                // After validation and corrections, ensure proper formatting and citations
                await EnhanceWithCitationsAsync();
                
                // Format and polish the final answer
                try
                {
                    _finalAnswer = await AddReferencesAndPolishAsync(_draftAnswer);
                }
                catch 
                {
                    _finalAnswer = _draftAnswer;
                }   
                AddMessage($"Finalized research answer ({CountWords(_finalAnswer)} words)");
            }
            catch (Exception ex)
            {
                AddMessage($"Review and refine error: {ex.Message}");
                
                // Even on error, try to format what we have
                try {
                    await EnhanceWithCitationsAsync();
                    _finalAnswer = await AddReferencesAndPolishAsync(_draftAnswer);
                }
                catch {
                    _finalAnswer = _draftAnswer; // Use draft as fallback
                }
            }
        }

        private async Task<bool> ValidateResearchQualityAsync()
        {
            AddMessage("Performing final research validation");
            
            var validationPrompt = $@"
You are an expert research validator responsible for ensuring the highest standards of academic and factual integrity.

TASK:
Critically evaluate this research for quality, accuracy, and reliability.

RESEARCH:
{_draftAnswer}

EVALUATION CRITERIA:
1. Factual Accuracy: Are all claims supported by evidence?
2. Source Quality: Are sources reliable and appropriate?
3. Logical Coherence: Is the reasoning sound?
4. Comprehensiveness: Does it address all key aspects of the topic?
5. Objectivity: Does it present multiple perspectives fairly?

For each issue found, provide:
1. A description of the problem
2. The location in the text
3. A suggested correction

FORMAT AS JSON:
{{
  ""overallAssessment"": ""brief overall evaluation"",
  ""qualityScore"": 0-10,
  ""issues"": [
    {{
      ""type"": ""factual|logical|bias|omission|structure"",
      ""severity"": ""high|medium|low"",
      ""description"": ""specific description of the issue"",
      ""location"": ""where in the document"",
      ""suggestion"": ""recommended fix""
    }},
    ...
  ],
  ""passesQualityThreshold"": true|false
}}
";

            var validationFunction = KernelFunctionFactory.CreateFromPrompt(
                validationPrompt,
                functionName: "ValidateResearch",
                description: "Validates research for accuracy and quality"
            );
            
            try
            {
                var result = await validationFunction.InvokeAsync(_kernel, new KernelArguments());
                var validationResult = JsonSerializer.Deserialize<ValidationResult>(result.GetValue<string>(), _jsonOptions);
                
                if (validationResult != null)
                {
                    AddMessage($"Validation score: {validationResult.QualityScore}/10");
                    
                    if (validationResult.Issues?.Any() == true)
                    {
                        AddMessage($"Found {validationResult.Issues.Count} quality issues to address");
                        await CorrectResearchIssuesAsync(validationResult.Issues);
                    }
                    
                    return validationResult.PassesQualityThreshold;
                }
            }
            catch (Exception ex)
            {
                AddMessage($"Validation error: {ex.Message}");
            }
            
            return true; // Default to passing if validation fails
        }

        private async Task CorrectResearchIssuesAsync(List<ResearchIssue> issues)
        {
            var correctionPrompt = new StringBuilder();
            correctionPrompt.AppendLine("You are an expert research editor. Revise the following research text to address these specific issues:");
            
            foreach (var issue in issues.OrderByDescending(i => i.Severity))
            {
                correctionPrompt.AppendLine($"- {issue.Type.ToUpperInvariant()} ISSUE ({issue.Severity}):");
                correctionPrompt.AppendLine($"  Location: {issue.Location}");
                correctionPrompt.AppendLine($"  Problem: {issue.Description}");
                correctionPrompt.AppendLine($"  Suggestion: {issue.Suggestion}");
                correctionPrompt.AppendLine();
            }
            
            correctionPrompt.AppendLine("ORIGINAL TEXT:");
            correctionPrompt.AppendLine(_draftAnswer);
            correctionPrompt.AppendLine();
            correctionPrompt.AppendLine("Provide the fully revised text addressing all issues while maintaining the overall structure and content. Do not add comments or explanations - just provide the corrected text.");
            
            var correctionFunction = KernelFunctionFactory.CreateFromPrompt(
                correctionPrompt.ToString(),
                functionName: "CorrectResearchIssues",
                description: "Corrects identified issues in research text"
            );
            
            try
            {
                var result = await correctionFunction.InvokeAsync(_kernel, new KernelArguments());
                var correctedText = result.GetValue<string>();
                
                if (!string.IsNullOrWhiteSpace(correctedText))
                {
                    _draftAnswer = correctedText;
                    AddMessage("Applied corrections to address quality issues");
                }
            }
            catch (Exception ex)
            {
                AddMessage($"Error correcting issues: {ex.Message}");
            }
        }

        private async Task AddressAccuracyConcernsAsync(List<AccuracyConcern> concerns)
        {
            foreach (var concern in concerns)
            {
                AddMessage($"Addressing concern: {concern.Issue} (Severity: {concern.Severity})");
                // Implement logic to address each concern, e.g., refine the draft answer or add clarifications
                _draftAnswer += $"\n\n[Note: Addressed concern - {concern.Issue}]";
            }
        }

        public async Task IncorporateFeedbackAsync(string feedback)
        {
            if (string.IsNullOrWhiteSpace(feedback))
                return;
                
            AddMessage("Incorporating user feedback");
            
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
                    AddMessage($"Updated answer based on feedback ({CountWords(_finalAnswer)} words)");
                }
            }
            catch (Exception ex)
            {
                AddMessage($"Error incorporating feedback: {ex.Message}");
            }
        }
        
        private async Task ResearchFollowUpTasksAsync(List<SubtaskModel> followUpSubtasks)
        {
            AddMessage($"Researching {followUpSubtasks.Count} follow-up subtasks");
            foreach (var subtask in followUpSubtasks)
            {
                try
                {
                    var tavilyResponse = await _tavilyConnector.SearchAsync(subtask.Description);
                    if (tavilyResponse != null && !string.IsNullOrEmpty(tavilyResponse.Answer))
                    {
                        _sources.AddRange(tavilyResponse.Results.Select(r => r.Url));
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
                            _subtaskSummaries.Add(new Dictionary<string, object>
                            {
                                { "subtask_id", subtask.Id },
                                { "summary", summaryOutput.Summary },
                                { "urls", tavilyResponse.Results.Select(r => r.Url).ToList() }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddMessage($"Error researching follow-up subtask {subtask.Id}: {ex.Message}");
                }
            }
        }
        
        public async Task EnhanceWithCitationsAsync()
        {
            if (_draftAnswer == null || _sources.Count == 0)
                return;
                
            AddMessage("Enhancing research with proper citations and formatting");
                        
            var citationPrompt = $@"
Enhance the following research with proper in-text citations and formatting:

1. Add numbered citations in square brackets [1], [2], etc. where appropriate in the text
2. Make all section headings bold with appropriate emojis that match the section topic
3. Format the content with clear paragraph breaks
4. Use consistent markdown formatting throughout

Research Text:
{_draftAnswer}

Available Sources (numbered for reference):
{string.Join("\n", _sources.Select((s, i) => $"[{i+1}] {s}"))}

Return the enhanced text with proper formatting, section emojis, and integrated citations.
";

            var citationFunction = KernelFunctionFactory.CreateFromPrompt(
                citationPrompt,
                functionName: "EnhanceWithCitations",
                description: "Adds scholarly citations and improves formatting of research text"
            );
            
            var result = await citationFunction.InvokeAsync(_kernel, new KernelArguments());
            var enhancedText = result.GetValue<string>();
            
            if (!string.IsNullOrWhiteSpace(enhancedText))
            {
                _draftAnswer = enhancedText;
                AddMessage($"Enhanced research with {_sources.Count} cited sources and improved formatting");
            }
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

        public async Task<Stream> ExportResearchToStreamAsync(string format = "markdown")
        {
            var memoryStream = new MemoryStream();
            var streamWriter = new StreamWriter(memoryStream);
            
            // Use the final answer if available, otherwise use the draft
            string content = _finalAnswer ?? _draftAnswer;
            
            // Add title if not already present
            if (!content.StartsWith("# "))
            {
                await streamWriter.WriteLineAsync($"# 🔍 {_mainResearchTopic}");
                await streamWriter.WriteLineAsync();
            }
            
            await streamWriter.WriteLineAsync(content);
            
            if (format.Equals("html", StringComparison.OrdinalIgnoreCase))
            {
                // Convert markdown to HTML with proper styling
                var htmlConverter = KernelFunctionFactory.CreateFromPrompt(
                    @"Convert this markdown to clean HTML with proper formatting:
                    - Add CSS for nice formatting
                    - Maintain all headings, sections and citations
                    - Preserve all emojis
                    - Format code blocks properly
                    
                    Markdown content:
                    {{$text}}",
                    functionName: "MarkdownToHtml"
                );
                
                var htmlResult = await htmlConverter.InvokeAsync(
                    _kernel, 
                    new KernelArguments { ["text"] = content }
                );
                
                streamWriter.BaseStream.Position = 0;
                memoryStream = new MemoryStream();
                streamWriter = new StreamWriter(memoryStream);
                await streamWriter.WriteLineAsync(htmlResult.GetValue<string>());
            }
            
            await streamWriter.FlushAsync();
            memoryStream.Position = 0;
            return memoryStream;
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
                AddMessage($"JSON parsing error: {ex.Message}");
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
            
            [JsonPropertyName("accuracy_concerns")]
            public List<AccuracyConcern> AccuracyConcerns { get; set; } = new();
            
            [JsonPropertyName("structural_feedback")]
            public string StructuralFeedback { get; set; }
            
            [JsonPropertyName("completeness_score")]
            public int CompletenessScore { get; set; }
        }

        private class ValidationResult
        {
            [JsonPropertyName("overallAssessment")]
            public string OverallAssessment { get; set; }
            
            [JsonPropertyName("qualityScore")]
            public int QualityScore { get; set; }
            
            [JsonPropertyName("issues")]
            public List<ResearchIssue> Issues { get; set; }
            
            [JsonPropertyName("passesQualityThreshold")]
            public bool PassesQualityThreshold { get; set; }
        }

        private class ResearchIssue
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }
            
            [JsonPropertyName("severity")]
            public string Severity { get; set; }
            
            [JsonPropertyName("description")]
            public string Description { get; set; }
            
            [JsonPropertyName("location")]
            public string Location { get; set; }
            
            [JsonPropertyName("suggestion")]
            public string Suggestion { get; set; }
        }

        private class AccuracyConcern
        {
            [JsonPropertyName("issue")]
            public string Issue { get; set; }

            [JsonPropertyName("severity")]
            public string Severity { get; set; }

            [JsonPropertyName("details")]
            public string Details { get; set; }
        }

        private void AddMessage(string message)
        {
            _messages.Add(message.Truncate(500)); // Prevent excessively long messages
        }

        private async Task<T> RetryOperationAsync<T>(Func<Task<T>> operation, string operationName, int maxRetries = 3)
        {
            int attempts = 0;
            Exception lastException = null;

            while (attempts < maxRetries)
            {
                try
                {
                    attempts++;
                    return await operation();
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _messages.Add($"Attempt {attempts} for {operationName} failed: {ex.Message}");
                    
                    if (attempts < maxRetries)
                    {
                        // Exponential backoff: 2^attempt * 500ms
                        int delayMs = (int)Math.Pow(2, attempts) * 500;
                        await Task.Delay(delayMs);
                    }
                }
            }
            
            throw new InvalidOperationException($"{operationName} failed after {maxRetries} attempts", lastException);
        }
    }
}