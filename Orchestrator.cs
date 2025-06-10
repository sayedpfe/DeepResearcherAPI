#nullable enable

using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Threading;

namespace DeepResearcher
{
    public class Orchestrator
    {
        private readonly Kernel _kernel;
        private readonly KernelFunction _decomposer;
        private readonly KernelFunction _summarizer;
        private readonly KernelFunction _combiner;
        private readonly KernelFunction _reviewer;
        private readonly KernelFunction _miniCombiner;
        private readonly KernelFunction _clarifier;
        private readonly TavilyConnector _tavilyConnector;
        
        // Configuration options
        private readonly bool _verboseLogging;
        private readonly int _maxRetries = 2;
        private readonly int _maxClarificationAttempts = 3;
        
        // JSON serializer options
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        { 
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public Orchestrator(Kernel kernel, TavilyConnector tavilyConnector, bool verboseLogging = false)
        {
            _kernel = kernel;
            _tavilyConnector = tavilyConnector;
            _verboseLogging = verboseLogging;

            // Initialize all plugin functions
            _decomposer = kernel.Plugins.GetFunction("decomposer", "DecomposePrompt");
            _summarizer = kernel.Plugins.GetFunction("summarizer", "SummarizeResults");
            _combiner = kernel.Plugins.GetFunction("combiner", "CombineSummaries");
            _reviewer = kernel.Plugins.GetFunction("reviewer", "ReviewAnswer");
            _miniCombiner = kernel.Plugins.GetFunction("miniCombiner", "MergeRefinements");
            _clarifier = kernel.Plugins.GetFunction("clarifier", "ClarifyPrompt");
            
            LogDebug("Orchestrator initialized with kernel instance: " + kernel.GetHashCode());
        }

        /// <summary>
        /// Main research workflow that processes a user's request and returns a comprehensive answer
        /// </summary>
        /// <param name="userPrompt">The initial user research prompt</param>
        /// <returns>A comprehensive research answer</returns>
        public async Task<string> RunDeepResearchAsync(string userPrompt)
        {
            try
            {
                // Phase 1: Clarification and initial setup
                var researchContext = await ClarifyUserPromptAsync(userPrompt);
                if (researchContext.HasErrors)
                    return FormatErrorResponse(researchContext.ErrorMessage);

                // Phase 2: Decomposition of the research prompt
                await DecomposeResearchPromptAsync(researchContext);
                if (researchContext.HasErrors)
                    return FormatErrorResponse(researchContext.ErrorMessage);

                // Phase 3: Research on each subtask
                await ResearchSubtasksAsync(researchContext);
                if (researchContext.HasErrors)
                    return FormatErrorResponse(researchContext.ErrorMessage);

                // Phase 4: Combine research into initial draft
                await CombineResearchIntoAnswerAsync(researchContext);
                if (researchContext.HasErrors)
                    return FormatErrorResponse(researchContext.ErrorMessage);

                // Phase 5: Review and identify gaps
                await ReviewAnswerForGapsAsync(researchContext);
                if (researchContext.HasErrors)
                    return FormatErrorResponse(researchContext.ErrorMessage);

                // Phase 6: Research any gaps and refine (if needed)
                if (researchContext.HasFollowUpTasks)
                {
                    await ResearchFollowUpTasksAsync(researchContext);
                    if (researchContext.HasErrors)
                        return FormatErrorResponse(researchContext.ErrorMessage);

                    await RefineAnswerWithFollowUpResearchAsync(researchContext);
                    if (researchContext.HasErrors)
                        return FormatErrorResponse(researchContext.ErrorMessage);
                }
                else
                {
                    DisplayStep("Gap Analysis", "No significant gaps found. Answer is complete.");
                }

                // Phase 7: Final touches and user feedback
                var finalAnswer = await FinalizeDraftWithUserFeedbackAsync(researchContext);
                return finalAnswer;
            }
            catch (Exception ex)
            {
                DisplayError("Unhandled Exception", $"An unexpected error occurred: {ex.Message}");
                LogDebug($"Stack trace: {ex.StackTrace}");
                return FormatErrorResponse("An unexpected error occurred during research. Please try again.");
            }
        }

        #region ─── Research Pipeline Steps ───────────────────────────────────────────

        /// <summary>
        /// Phase 1: Clarifies the user's research prompt through interactive dialog
        /// </summary>
        private async Task<ResearchContext> ClarifyUserPromptAsync(string initialPrompt)
        {
            DisplayStep("Research Process Initiated", "Starting deep research...");
            
            var context = new ResearchContext
            {
                OriginalPrompt = initialPrompt,
                CurrentPrompt = initialPrompt
            };

            int clarificationAttempt = 0;
            bool clarificationNeeded = true;

            while (clarificationNeeded && clarificationAttempt < _maxClarificationAttempts)
            {
                clarificationAttempt++;
                DisplayStep($"Clarification (Attempt {clarificationAttempt})", 
                    $"Analyzing your request: \"{context.CurrentPrompt.Truncate(100)}\"");
                
                try
                {
                    // Invoke clarifier plugin
                    LogDebug($"Sending to clarifier: '{context.CurrentPrompt}'");
                    var args = new KernelArguments { ["user_prompt"] = context.CurrentPrompt };
                    var clarifyResult = await _clarifier.InvokeAsync(_kernel, args);
                    var clarification = clarifyResult.GetValue<string>() ?? string.Empty;
                    
                    // Parse clarifier response
                    var clarifierOutput = await ParseJsonOutputAsync<ClarifierOutput>(
                        clarification, 
                        "Clarifier Output", 
                        displayRaw: false);

                    if (clarifierOutput == null)
                    {
                        context.SetError("Failed to parse clarifier output. The AI returned an invalid response.");
                        return context;
                    }

                    // Update research context with clarifier results
                    context.UnifiedPrompt = clarifierOutput.UnifiedResearchPrompt ?? context.CurrentPrompt;
                    context.ClarifyingQuestions = clarifierOutput.ClarifyingQuestions ?? new List<string>();
                    context.ReadyToProceedMessage = clarifierOutput.ReadyToProceedMessage ?? "Ready to proceed with research.";
                    
                    // Display clarification results
                    DisplayStep("Clarified Research Topic", context.UnifiedPrompt);
                    
                    if (context.ClarifyingQuestions.Any())
                    {
                        DisplayStep("Clarifying Questions", 
                            string.Join("\n", context.ClarifyingQuestions.Select((q, i) => $"{i+1}. {q}")));
                    }
                    
                    DisplayStep("Status", context.ReadyToProceedMessage);
                    
                    // Check if the prompt is valid or needs further clarification
                    if (IsPromptEmpty(context.UnifiedPrompt))
                    {
                        DisplayStep("Invalid Prompt", "The prompt appears to be empty or invalid.");
                        context.CurrentPrompt = await GetUserInputAsync("Please enter a specific research topic:");
                        
                        if (string.IsNullOrWhiteSpace(context.CurrentPrompt))
                        {
                            context.SetError("No valid research prompt provided.");
                            return context;
                        }
                        
                        context.OriginalPrompt = context.CurrentPrompt;
                        continue;
                    }
                    
                    // Check if clarification is needed
                    clarificationNeeded = NeedsClarification(context);
                    
                    if (clarificationNeeded)
                    {
                        DisplayStep("Clarification Requested", "Please provide additional information to improve the research quality:");
                        
                        foreach (var question in context.ClarifyingQuestions)
                        {
                            Console.WriteLine($"• {question}");
                        }
                        
                        string userClarification = await GetUserInputAsync("\nYour response (or press Enter to proceed with current understanding):");
                        
                        if (!string.IsNullOrWhiteSpace(userClarification))
                        {
                            // User provided clarification, update the prompt
                            context.CurrentPrompt = $"{context.UnifiedPrompt}\n\nAdditional information: {userClarification}";
                        }
                        else
                        {
                            // User skipped clarification, proceed with unified prompt
                            clarificationNeeded = false;
                            context.CurrentPrompt = context.UnifiedPrompt;
                        }
                    }
                    else
                    {
                        // Clarification not needed, proceed with unified prompt
                        context.CurrentPrompt = context.UnifiedPrompt;
                    }
                }
                catch (Exception ex)
                {
                    DisplayError("Clarification Error", $"Error during clarification: {ex.Message}");
                    
                    if (clarificationAttempt >= _maxClarificationAttempts)
                    {
                        context.SetError("Failed to clarify the research prompt after multiple attempts.");
                        return context;
                    }
                    
                    // Try again with original prompt
                    context.CurrentPrompt = context.OriginalPrompt;
                }
            }
            
            // Final prompt is set
            context.MainResearchTopic = context.CurrentPrompt;
            DisplayStep("Final Research Topic", context.MainResearchTopic);
            
            return context;
        }

        /// <summary>
        /// Phase 2: Decomposes the main research topic into specific subtasks
        /// </summary>
        private async Task DecomposeResearchPromptAsync(ResearchContext context)
        {
            DisplayStep("Research Decomposition", $"Breaking down research topic into subtasks...");
            
            try
            {
                // Invoke decomposer plugin
                var args = new KernelArguments { ["research_prompt"] = context.MainResearchTopic };
                var decompositionResult = await _decomposer.InvokeAsync(_kernel, args);
                var rawDecomposition = decompositionResult.GetValue<string>() ?? string.Empty;
                
                // Parse decomposer output
                var decomposerOutput = await ParseJsonOutputAsync<DecomposerOutput>(
                    rawDecomposition, 
                    "Decomposition Output",
                    preprocess: true,
                    displayRaw: false);
                
                if (decomposerOutput == null || decomposerOutput.Subtasks == null || !decomposerOutput.Subtasks.Any())
                {
                    context.SetError("Decomposition failed: No valid subtasks were generated.");
                    return;
                }
                
                // Filter out low-quality subtasks
                context.Subtasks = decomposerOutput.Subtasks
                    .Where(st => !string.IsNullOrWhiteSpace(st.Description) && st.Description.Length > 30)
                    .ToList();
                
                if (!context.Subtasks.Any())
                {
                    context.SetError("All generated subtasks were invalid or too short.");
                    return;
                }
                
                // Display results
                DisplayStep("Research Plan", $"Breaking down '{context.MainResearchTopic}' into {context.Subtasks.Count} subtasks:");
                
                foreach (var subtask in context.Subtasks)
                {
                    Console.WriteLine($"• [{subtask.Id}] {subtask.Description}");
                }
            }
            catch (Exception ex)
            {
                context.SetError($"Failed to decompose the research topic: {ex.Message}");
            }
        }

        /// <summary>
        /// Phase 3: Performs research on each subtask
        /// </summary>
        private async Task ResearchSubtasksAsync(ResearchContext context)
        {
            DisplayStep("Research Execution", $"Researching {context.Subtasks.Count} subtasks...");
            context.SubtaskSummaries = new List<Dictionary<string, object>>();
            
            int completedTasks = 0;
            var failedTasks = new List<string>();
            
            foreach (var subtask in context.Subtasks)
            {
                DisplayStep($"Researching Subtask {++completedTasks}/{context.Subtasks.Count}", 
                    $"[{subtask.Id}] {subtask.Description}");
                
                try
                {
                    // Search for information using Tavily
                    DisplayProgress("Searching web sources...");
                    var tavilyResponse = await _tavilyConnector.SearchAsync(subtask.Description);
                    
                    if (tavilyResponse == null || string.IsNullOrWhiteSpace(tavilyResponse.Answer))
                    {
                        throw new Exception("Search returned no results");
                    }
                    
                    DisplayProgress($"Found {tavilyResponse.Results.Count} relevant sources");
                    LogDebug($"Tavily Answer: {tavilyResponse.Answer.Truncate(200)}...");
                    
                    // Summarize the research findings
                    DisplayProgress("Analyzing and summarizing findings...");
                    var summarizerInput = new KernelArguments
                    {
                        ["subtask_id"] = subtask.Id,
                        ["tavily_answer"] = tavilyResponse.Answer.Replace("\"", "\\\""),
                        ["urls"] = tavilyResponse.Results.Select(r => r.Url).ToList(),
                        ["research_prompt"] = context.MainResearchTopic
                    };
                    
                    var summaryResult = await _summarizer.InvokeAsync(_kernel, summarizerInput);
                    var rawSummary = summaryResult.GetValue<string>() ?? string.Empty;
                    
                    // Parse summarizer output
                    var summaryOutput = await ParseJsonOutputAsync<SummarizerOutput>(
                        rawSummary, 
                        $"Summary for {subtask.Id}",
                        preprocess: true,
                        displayRaw: false);
                    
                    if (summaryOutput == null || string.IsNullOrWhiteSpace(summaryOutput.Summary))
                    {
                        throw new Exception("Failed to generate summary");
                    }
                    
                    // Add to research context
                    var summaryData = new Dictionary<string, object>
                    {
                        { "subtask_id", subtask.Id },
                        { "summary", summaryOutput.Summary },
                        { "urls", tavilyResponse.Results.Select(r => r.Url).ToList() }
                    };
                    
                    context.SubtaskSummaries.Add(summaryData);
                    DisplayProgress($"Completed research on subtask {subtask.Id}");
                }
                catch (Exception ex)
                {
                    DisplayError($"Subtask {subtask.Id} Error", ex.Message);
                    failedTasks.Add(subtask.Id);
                    
                    // Add placeholder for failed subtask
                    context.SubtaskSummaries.Add(new Dictionary<string, object>
                    {
                        { "subtask_id", subtask.Id },
                        { "summary", $"[Error: Could not complete research on this subtask: {ex.Message.Truncate(100)}]" },
                        { "urls", new List<string>() }
                    });
                }
            }
            
            if (context.SubtaskSummaries.Count == 0)
            {
                context.SetError("Research failed: No subtask summaries could be generated.");
                return;
            }
            
            if (failedTasks.Any())
            {
                DisplayWarning("Research Partial Completion", 
                    $"Completed {context.SubtaskSummaries.Count - failedTasks.Count} out of {context.Subtasks.Count} subtasks. " +
                    $"Failed subtasks: {string.Join(", ", failedTasks)}");
            }
            else
            {
                DisplayStep("Research Complete", $"Successfully researched all {context.Subtasks.Count} subtasks");
            }
        }

        /// <summary>
        /// Phase 4: Combines all research summaries into a coherent draft answer
        /// </summary>
        private async Task CombineResearchIntoAnswerAsync(ResearchContext context)
        {
            DisplayStep("Research Synthesis", "Combining all research into a comprehensive answer...");
            
            try
            {
                // Prepare input for combiner
                var combineInput = new KernelArguments
                {
                    ["original_prompt"] = context.OriginalPrompt,
                    ["summaries"] = JsonSerializer.Serialize(context.SubtaskSummaries, _jsonOptions),
                    ["research_prompt"] = context.MainResearchTopic
                };
                
                // Invoke combiner plugin
                var combinerResult = await _combiner.InvokeAsync(_kernel, combineInput);
                var rawCombiner = combinerResult.GetValue<string>() ?? string.Empty;
                
                // Parse combiner output
                var combinerOutput = await ParseJsonOutputAsync<CombinerOutput>(
                    rawCombiner, 
                    "Synthesis Output",
                    preprocess: true,
                    displayRaw: false);
                
                if (combinerOutput == null || string.IsNullOrWhiteSpace(combinerOutput.FinalAnswer))
                {
                    context.SetError("Failed to synthesize research: Combiner returned empty or invalid output.");
                    return;
                }
                
                // Update research context
                context.DraftAnswer = combinerOutput.FinalAnswer;
                
                DisplayStep("Initial Draft", $"Generated {context.DraftAnswer.CountWords()} word draft answer");
                DisplayPreview(context.DraftAnswer);
            }
            catch (Exception ex)
            {
                context.SetError($"Failed to combine research results: {ex.Message}");
            }
        }

        /// <summary>
        /// Phase 5: Reviews the draft answer for gaps or missing information
        /// </summary>
        private async Task ReviewAnswerForGapsAsync(ResearchContext context)
        {
            DisplayStep("Quality Review", "Evaluating draft answer for completeness and gaps...");
            
            try
            {
                // Prepare input for reviewer
                var reviewInput = new KernelArguments
                {
                    ["original_prompt"] = context.OriginalPrompt,
                    ["final_answer"] = context.DraftAnswer,
                    ["summaries"] = JsonSerializer.Serialize(context.SubtaskSummaries, _jsonOptions),
                    ["research_prompt"] = context.MainResearchTopic
                };
                
                // Invoke reviewer plugin
                var reviewResult = await _reviewer.InvokeAsync(_kernel, reviewInput);
                var rawReview = reviewResult.GetValue<string>() ?? string.Empty;
                
                // Parse reviewer output
                var reviewOutput = await ParseJsonOutputAsync<ReviewerOutput>(
                    rawReview, 
                    "Review Output",
                    preprocess: true,
                    displayRaw: false);
                
                if (reviewOutput == null)
                {
                    DisplayWarning("Review Failed", "Could not analyze answer for gaps. Proceeding with current draft.");
                    return;
                }
                
                // Update research context
                context.FollowUpSubtasks = reviewOutput.FollowUpSubtasks ?? new List<FollowUpSubtask>();
                
                if (context.FollowUpSubtasks.Any())
                {
                    DisplayStep("Identified Gaps", $"Found {context.FollowUpSubtasks.Count} topics that need additional research:");
                    
                    foreach (var subtask in context.FollowUpSubtasks)
                    {
                        Console.WriteLine($"• [{subtask.Id}] {subtask.Description}");
                    }
                }
                else
                {
                    DisplayStep("Review Complete", "No significant gaps found in the draft answer.");
                }
            }
            catch (Exception ex)
            {
                DisplayWarning("Review Error", $"Error during answer review: {ex.Message}");
                context.FollowUpSubtasks = new List<FollowUpSubtask>();
            }
        }

        /// <summary>
        /// Phase 6A: Researches follow-up tasks to fill identified gaps
        /// </summary>
        private async Task ResearchFollowUpTasksAsync(ResearchContext context)
        {
            DisplayStep("Gap Research", $"Researching {context.FollowUpSubtasks.Count} follow-up topics...");
            context.FollowUpSummaries = new List<Dictionary<string, object>>();
            
            int completedTasks = 0;
            
            foreach (var followUpTask in context.FollowUpSubtasks)
            {
                DisplayStep($"Researching Gap {++completedTasks}/{context.FollowUpSubtasks.Count}", 
                    $"[{followUpTask.Id}] {followUpTask.Description}");
                
                try
                {
                    // Search for additional information
                    DisplayProgress("Searching additional sources...");
                    var tavilyResponse = await _tavilyConnector.SearchAsync(followUpTask.Description);
                    
                    if (tavilyResponse == null || string.IsNullOrWhiteSpace(tavilyResponse.Answer))
                    {
                        throw new Exception("Search returned no results");
                    }
                    
                    DisplayProgress($"Found {tavilyResponse.Results.Count} relevant sources");
                    
                    // Summarize the additional findings
                    DisplayProgress("Summarizing findings...");
                    var summaryInput = new KernelArguments
                    {
                        ["subtask_id"] = followUpTask.Id,
                        ["tavily_answer"] = tavilyResponse.Answer.Replace("\"", "\\\""),
                        ["urls"] = tavilyResponse.Results.Select(r => r.Url).ToList(),
                        ["research_prompt"] = context.MainResearchTopic
                    };
                    
                    var summaryResult = await _summarizer.InvokeAsync(_kernel, summaryInput);
                    var rawSummary = summaryResult.GetValue<string>() ?? string.Empty;
                    
                    // Parse summarizer output
                    var summaryOutput = await ParseJsonOutputAsync<SummarizerOutput>(
                        rawSummary, 
                        $"Follow-up Summary for {followUpTask.Id}",
                        preprocess: true,
                        displayRaw: false);
                    
                    if (summaryOutput == null || string.IsNullOrWhiteSpace(summaryOutput.Summary))
                    {
                        throw new Exception("Failed to generate summary");
                    }
                    
                    // Add to research context
                    var summaryData = new Dictionary<string, object>
                    {
                        { "subtask_id", followUpTask.Id },
                        { "summary", summaryOutput.Summary },
                        { "urls", tavilyResponse.Results.Select(r => r.Url).ToList() }
                    };
                    
                    context.FollowUpSummaries.Add(summaryData);
                    DisplayProgress($"Completed additional research on topic {followUpTask.Id}");
                }
                catch (Exception ex)
                {
                    DisplayWarning($"Follow-up {followUpTask.Id} Error", ex.Message);
                    
                    // Add placeholder for failed follow-up
                    context.FollowUpSummaries.Add(new Dictionary<string, object>
                    {
                        { "subtask_id", followUpTask.Id },
                        { "summary", $"[Could not complete additional research on this topic: {ex.Message.Truncate(100)}]" },
                        { "urls", new List<string>() }
                    });
                }
            }
            
            DisplayStep("Gap Research Complete", 
                $"Researched {context.FollowUpSummaries.Count} additional topics to improve the answer");
        }

        /// <summary>
        /// Phase 6B: Refines the draft answer with additional research
        /// </summary>
        private async Task RefineAnswerWithFollowUpResearchAsync(ResearchContext context)
        {
            if (!context.FollowUpSummaries.Any())
            {
                DisplayWarning("Refinement Skipped", "No follow-up research available to refine the answer.");
                return;
            }
            
            DisplayStep("Answer Refinement", "Integrating additional research into the draft answer...");
            
            try
            {
                // Prepare input for mini-combiner
                var miniCombineInput = new KernelArguments
                {
                    ["original_answer"] = context.DraftAnswer,
                    ["new_summaries"] = JsonSerializer.Serialize(context.FollowUpSummaries, _jsonOptions),
                    ["research_prompt"] = context.MainResearchTopic
                };
                
                // Invoke mini-combiner plugin
                var miniCombineResult = await _miniCombiner.InvokeAsync(_kernel, miniCombineInput);
                var rawMini = miniCombineResult.GetValue<string>() ?? string.Empty;
                
                // Parse mini-combiner output
                var miniCombinerOutput = await ParseJsonOutputAsync<MiniCombinerOutput>(
                    rawMini, 
                    "Refinement Output",
                    preprocess: true,
                    displayRaw: false);
                
                if (miniCombinerOutput == null || string.IsNullOrWhiteSpace(miniCombinerOutput.UpdatedAnswer))
                {
                    DisplayWarning("Refinement Failed", "Could not integrate additional research. Using original draft.");
                    return;
                }
                
                // Update research context
                string previousDraft = context.DraftAnswer;
                context.DraftAnswer = miniCombinerOutput.UpdatedAnswer;
                
                // Calculate improvement metrics
                int oldWordCount = previousDraft.CountWords();
                int newWordCount = context.DraftAnswer.CountWords();
                int difference = newWordCount - oldWordCount;
                string changeDescription = difference > 0 
                    ? $"Added {difference} words" 
                    : difference < 0 
                        ? $"Condensed by {Math.Abs(difference)} words" 
                        : "Restructured with same length";
                
                DisplayStep("Refinement Complete", 
                    $"Updated draft answer: {changeDescription} ({newWordCount} words total)");
                DisplayPreview(context.DraftAnswer);
            }
            catch (Exception ex)
            {
                DisplayWarning("Refinement Error", $"Error refining answer: {ex.Message}");
            }
        }

        /// <summary>
        /// Phase 7: Finalizes the draft with user feedback and final polish
        /// </summary>
        private async Task<string> FinalizeDraftWithUserFeedbackAsync(ResearchContext context)
        {
            DisplayStep("Final Draft", "Research complete. Preparing final answer...");
            DisplayPreview(context.DraftAnswer);
            
            // Ask for user feedback
            Console.WriteLine("\nWould you like to refine or clarify the answer further? (y/n)");
            var response = Console.ReadLine()?.Trim().ToLower();
            
            if (response == "y")
            {
                Console.WriteLine("Please enter your feedback or additional requests:");
                var userFeedback = Console.ReadLine();
                
                if (!string.IsNullOrWhiteSpace(userFeedback))
                {
                    DisplayStep("User Feedback", $"Incorporating feedback: {userFeedback}");
                    
                    try
                    {
                        // Prepare expanded feedback prompt
                        var feedbackPrompt = @$"
You are a research editor. The user has provided feedback on a research article. 
Revise the article to incorporate this feedback while maintaining the article's structure and depth.

Original Article:
{context.DraftAnswer}

User Feedback:
{userFeedback}

Provide the complete revised article.";

                        // Create and invoke function
                        var feedbackFunction = KernelFunctionFactory.CreateFromPrompt(
                            feedbackPrompt,
                            functionName: "IncorporateFeedback",
                            description: "Incorporates user feedback into a research article."
                        );
                        
                        var feedbackResult = await feedbackFunction.InvokeAsync(_kernel, new KernelArguments());
                        var revisedDraft = feedbackResult.GetValue<string>() ?? string.Empty;
                        
                        if (!string.IsNullOrWhiteSpace(revisedDraft))
                        {
                            context.DraftAnswer = revisedDraft;
                            DisplayStep("Feedback Incorporated", 
                                $"Updated draft based on your feedback ({revisedDraft.CountWords()} words)");
                        }
                    }
                    catch (Exception ex)
                    {
                        DisplayWarning("Feedback Error", 
                            $"Error incorporating feedback: {ex.Message}. Using previous draft.");
                    }
                }
            }
            
            // Check if the answer needs expansion
            if (context.DraftAnswer.CountWords() < 2000 && 
                !context.OriginalPrompt.Contains("brief", StringComparison.OrdinalIgnoreCase) &&
                !context.OriginalPrompt.Contains("summary", StringComparison.OrdinalIgnoreCase))
            {
                DisplayStep("Content Expansion", "Expanding answer with additional detail...");
                
                try
                {
                    var expandedDraft = await ExpandContentAsync(context.DraftAnswer, context.MainResearchTopic);
                    
                    if (!string.IsNullOrWhiteSpace(expandedDraft))
                    {
                        context.DraftAnswer = expandedDraft;
                        DisplayStep("Expansion Complete", 
                            $"Expanded answer to {expandedDraft.CountWords()} words");
                    }
                }
                catch (Exception ex)
                {
                    DisplayWarning("Expansion Error", 
                        $"Error expanding content: {ex.Message}. Using original draft.");
                }
            }
            
            // Add sources and references
            var finalAnswer = await AddSourcesAndFormatAsync(context);
            
            DisplayStep("Research Complete", 
                $"Final answer ready ({finalAnswer.CountWords()} words with {context.AllUrls.Count} sources)");
            
            return finalAnswer;
        }

        #endregion

        #region ─── Helper Methods ─────────────────────────────────────────────────────

        /// <summary>
        /// Parses a JSON string into a strongly-typed object
        /// </summary>
        private async Task<T?> ParseJsonOutputAsync<T>(
            string jsonString, 
            string label, 
            bool preprocess = false, 
            bool displayRaw = false)
        {
            if (string.IsNullOrWhiteSpace(jsonString))
                return default;

            try
            {
                string processedJson = jsonString;
                
                // Apply preprocessing if needed (clean, escape newlines)
                if (preprocess)
                {
                    processedJson = CleanJson(processedJson);
                    processedJson = EscapeJsonStringNewlines(processedJson);
                }
                
                // Display raw output for debugging if requested and debug mode is on
                if ((displayRaw || _verboseLogging) && Environment.GetEnvironmentVariable("DEEP_RESEARCHER_DEBUG") == "true")
                {
                    LogDebug($"Raw {label}: {processedJson}");
                }
                
                // Pretty print for UI if debug mode is on
                if (Environment.GetEnvironmentVariable("DEEP_RESEARCHER_DEBUG") == "true")
                {
                    try
                    {
                        var parsedJson = JsonDocument.Parse(processedJson);
                        DisplayJson(label, JsonSerializer.Serialize(parsedJson, _jsonOptions));
                    }
                    catch
                    {
                        // If we can't parse as JSON for display, show as is
                        if (!displayRaw && Environment.GetEnvironmentVariable("DEEP_RESEARCHER_DEBUG") == "true")
                        {
                            DisplayJson($"{label} (Raw Output)", processedJson);
                        }
                    }
                }
                
                // Actually parse to strongly-typed object
                return JsonSerializer.Deserialize<T>(processedJson, _jsonOptions);
            }
            catch (Exception ex)
            {
                LogDebug($"JSON parsing error for {label}: {ex.Message}");
                LogDebug($"Raw text: {jsonString}");
                return default;
            }
        }

        /// <summary>
        /// Cleans and normalizes JSON output from LLM
        /// </summary>
        private static string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("LLM returned empty output!", nameof(raw));

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

        /// <summary>
        /// Escapes newlines within JSON string values
        /// </summary>
        private static string EscapeJsonStringNewlines(string json)
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

        /// <summary>
        /// Checks if a prompt is empty or just asks for a prompt
        /// </summary>
        private static bool IsPromptEmpty(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return true;

            var lowerPrompt = prompt.ToLower();
            
            return lowerPrompt.Contains("provide a prompt") ||
                   lowerPrompt.Contains("please provide") ||
                   lowerPrompt.Contains("no prompt") ||
                   lowerPrompt.Contains("research request");
        }

        /// <summary>
        /// Determines if a research context needs clarification
        /// </summary>
        private static bool NeedsClarification(ResearchContext context)
        {
            // Check if readiness message indicates need for clarification
            if (!string.IsNullOrWhiteSpace(context.ReadyToProceedMessage))
            {
                var lowerMessage = context.ReadyToProceedMessage.ToLower();
                if (lowerMessage.Contains("need") || 
                    lowerMessage.Contains("require") || 
                    lowerMessage.Contains("clarif") ||
                    lowerMessage.Contains("more information"))
                {
                    return true;
                }
            }
            
            // Check if there are clarifying questions
            return context.ClarifyingQuestions?.Count > 0;
        }

        /// <summary>
        /// Formats an error response for the user
        /// </summary>
        private static string FormatErrorResponse(string errorMessage)
        {
            return $"ERROR: {errorMessage}\n\nPlease try again with a more specific research prompt.";
        }

        /// <summary>
        /// Waits for user input with a prompt
        /// </summary>
        private static Task<string> GetUserInputAsync(string prompt)
        {
            Console.WriteLine(prompt);
            // Add a minimal await to make the compiler happy
            return Task.FromResult(Console.ReadLine() ?? string.Empty);
        }

        /// <summary>
        /// Expands content to be more detailed and comprehensive
        /// </summary>
        private async Task<string> ExpandContentAsync(string content, string topic)
        {
            try
            {
                // Split content into sections
                var sections = SplitIntoSections(content);
                var expandedSections = new List<string>();
                
                // Expand each section
                foreach (var section in sections)
                {
                    if (string.IsNullOrWhiteSpace(section.Trim()))
                        continue;
                    
                    var isConclusion = section.TrimStart().StartsWith("Conclusion", StringComparison.OrdinalIgnoreCase);
                    
                    // Create appropriate expansion prompt
                    string expansionPrompt = isConclusion 
                        ? CreateConclusionExpansionPrompt(section, topic)
                        : CreateSectionExpansionPrompt(section, topic);
                    
                    // Create and invoke function
                    var expansionFunction = KernelFunctionFactory.CreateFromPrompt(
                        expansionPrompt,
                        functionName: isConclusion ? "ExpandConclusion" : "ExpandSection",
                        description: $"Expands a {(isConclusion ? "conclusion" : "section")} with more detail."
                    );
                    
                    var expansionResult = await expansionFunction.InvokeAsync(_kernel, new KernelArguments());
                    var expandedSection = expansionResult.GetValue<string>() ?? string.Empty;
                    
                    if (!string.IsNullOrWhiteSpace(expandedSection))
                    {
                        expandedSections.Add(expandedSection);
                    }
                    else
                    {
                        // Fallback to original if expansion failed
                        expandedSections.Add(section);
                    }
                }
                
                return string.Join("\n\n", expandedSections);
            }
            catch (Exception ex)
            {
                LogDebug($"Content expansion error: {ex.Message}");
                return content;  // Return original content if expansion fails
            }
        }

        /// <summary>
        /// Splits content into sections for expansion
        /// </summary>
        private static List<string> SplitIntoSections(string content)
        {
            // Split on common section markers
            var sections = new List<string>();
            var splits = content.Split(
                new[] { "\n## ", "\n# ", "\n### ", "\n---", "\n***" }, 
                StringSplitOptions.RemoveEmptyEntries
            );
            
            if (splits.Length <= 1)
            {
                // If no section markers, try to split on paragraph breaks
                sections.AddRange(content.Split(
                    new[] { "\n\n", "\r\n\r\n" }, 
                    StringSplitOptions.RemoveEmptyEntries
                ));
            }
            else
            {
                foreach (var (section, index) in splits.Select((s, i) => (s, i)))
                {
                    if (index == 0 && !section.StartsWith("#"))
                    {
                        sections.Add(section);  // First section
                    }
                    else
                    {
                        // Add appropriate heading marker
                        sections.Add($"## {section}");
                    }
                }
            }
            
            return sections;
        }

        /// <summary>
        /// Creates a prompt for expanding a regular section
        /// </summary>
        private static string CreateSectionExpansionPrompt(string section, string topic)
        {
            return @$"
You are an expert technical writer. Expand the following section into a comprehensive, detailed analysis of at least 1000 words.
Focus on the topic: {topic}

Do not summarize or add a conclusion. Write as if for a technical book chapter. Include:
- Technical explanations with specific details
- Real-world examples and case studies
- Relevant data and statistics where appropriate
- Citations for factual claims when possible

Section:
{section}

Return only the expanded section as formatted text with proper headings and paragraphs.
";
        }

        /// <summary>
        /// Creates a prompt for expanding a conclusion section
        /// </summary>
        private static string CreateConclusionExpansionPrompt(string section, string topic)
        {
            return @$"
You are an expert technical writer. Expand the following conclusion into a comprehensive, insightful final section for a research article on {topic}.

The conclusion should:
- Summarize key findings
- Present insights about future implications and trends
- Identify remaining challenges or open questions
- End with meaningful closing thoughts

Section:
{section}

Return only the expanded conclusion as formatted text.
";
        }

        /// <summary>
        /// Adds sources and formats the final answer
        /// </summary>
        private async Task<string> AddSourcesAndFormatAsync(ResearchContext context)
        {
            // Collect all URLs from research
            var allUrls = context.AllUrls;
            
            // Add references section if URLs are available
            string finalAnswer = context.DraftAnswer;
            
            if (allUrls.Any())
            {
                // Check if a references section already exists
                bool hasReferencesSection = finalAnswer.Contains("# References", StringComparison.OrdinalIgnoreCase) ||
                                            finalAnswer.Contains("## References", StringComparison.OrdinalIgnoreCase) ||
                                            finalAnswer.Contains("References:", StringComparison.OrdinalIgnoreCase);
                
                if (!hasReferencesSection)
                {
                    finalAnswer += "\n\n## References\n";
                    foreach (var url in allUrls)
                    {
                        finalAnswer += $"- {url}\n";
                    }
                }
            }
            
            // Add final polish if needed
            if (finalAnswer.CountWords() > 1000)
            {
                try
                {
                    // Final polish prompt
                    string polishPrompt = $"The following is a draft research article. Please edit it for clarity, coherence, and professionalism. Fix any grammatical errors or awkward phrasing. Ensure the structure is logical and the tone is appropriate for an academic audience. Return only the polished article.\n\n{finalAnswer}";
                    
                    // Create and invoke function
                    var polishFunction = KernelFunctionFactory.CreateFromPrompt(
                        polishPrompt,
                        functionName: "PolishArticle",
                        description: "Polishes a research article for grammar, clarity, and professionalism."
                    );
                    
                    var polishResult = await polishFunction.InvokeAsync(_kernel, new KernelArguments());
                    var polishedAnswer = polishResult.GetValue<string>() ?? string.Empty;
                    
                    if (!string.IsNullOrWhiteSpace(polishedAnswer) && polishedAnswer.CountWords() >= finalAnswer.CountWords() * 0.9)
                    {
                        // Only use the polished version if it's not significantly shorter
                        finalAnswer = polishedAnswer;
                        DisplayStep("Final Polish", "Applied final editing and formatting improvements");
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"Error during final polish: {ex.Message}");
                }
            }
            
            return finalAnswer;
        }

        #endregion

        #region ─── Display Methods ─────────────────────────────────────────────────────

        /// <summary>
        /// Displays a research step with a title and description
        /// </summary>
        private void DisplayStep(string stepName, string content, bool isError = false)
        {
            // Skip displaying technical steps entirely
            if (ShouldSkipStep(stepName, content))
                return;

            Console.ForegroundColor = isError ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine($"\n[RESEARCH STEP] {stepName}");
            Console.ResetColor();
            
            if (!string.IsNullOrEmpty(content))
            {
                // Hide JSON content
                if (content.StartsWith("{") && content.EndsWith("}"))
                {
                    // Don't display raw JSON
                    return;
                }
                
                // Clean up content before displaying
                var cleanedContent = CleanupStepContent(content);
                Console.WriteLine(cleanedContent);
            }
        }

        /// <summary>
        /// Determines if a step should be skipped from display
        /// </summary>
        private bool ShouldSkipStep(string stepName, string content)
        {
            // Skip technical steps
            var technicalSteps = new[] {
                "Tavily Search",
                "Progress",
                "Debug",
                "Raw",
                "JSON"
            };
            
            foreach (var term in technicalSteps)
            {
                if (stepName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            // Skip steps with technical content
            if (content.Contains("Tavily") || 
                (content.Contains("{") && content.Contains("}")) ||
                content.Contains("[DEBUG]"))
                return true;
                
            return false;
        }

        /// <summary>
        /// Cleans up step content for display
        /// </summary>
        private string CleanupStepContent(string content)
        {
            // Remove debug lines
            var lines = content.Split('\n')
                .Where(l => !l.Contains("[DEBUG]") && 
                            !l.Contains("Tavily Answer:") &&
                            !l.Contains("Raw") && 
                            !l.StartsWith("{"))
                .ToArray();
            
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Displays progress within a step
        /// </summary>
        private void DisplayProgress(string message)
        {
            // Skip progress messages related to technical details
            if (message.Contains("Tavily") || 
                message.Contains("web search", StringComparison.OrdinalIgnoreCase) || 
                message.Contains("Found") || 
                message.Contains("q1") || message.Contains("q2") ||
                message.Contains("Summarizing") || 
                message.Contains("parsing"))
            {
                return;
            }
            
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"  → {message}");
            Console.ResetColor();
        }

        /// <summary>
        /// Displays an error message
        /// </summary>
        private void DisplayError(string title, string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] {title}");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        /// <summary>
        /// Displays a warning message
        /// </summary>
        private void DisplayWarning(string title, string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[WARNING] {title}");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        /// <summary>
        /// Displays JSON data with formatting
        /// </summary>
        private void DisplayJson(string label, string jsonContent)
        {
            // Only display if DEBUG is enabled
            if (Environment.GetEnvironmentVariable("DEEP_RESEARCHER_DEBUG") != "true")
                return;
                
            try
            {
                // Attempt to pretty-print the JSON
                var options = new JsonSerializerOptions { WriteIndented = true };
                var element = JsonSerializer.Deserialize<JsonElement>(jsonContent);
                var formattedJson = JsonSerializer.Serialize(element, options);
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n[{label}]");
                Console.WriteLine(formattedJson);
                Console.ResetColor();
            }
            catch
            {
                // If can't parse as JSON, just display raw
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n[{label}]");
                Console.WriteLine(jsonContent);
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Displays a preview of content (truncated if too long)
        /// </summary>
        private void DisplayPreview(string content, int maxPreviewLength = 500)
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            string preview = content.Length <= maxPreviewLength 
                ? content 
                : content.Substring(0, maxPreviewLength) + "...\n[Content truncated for preview]";
            
            Console.WriteLine("\n--- Preview ---");
            Console.WriteLine(preview);
            Console.WriteLine("--- End Preview ---");
        }

        /// <summary>
        /// Logs debug information if verbose logging is enabled
        /// </summary>
        private void LogDebug(string message)
        {
            // Only display debug messages when explicitly requested with special flag
            if (_verboseLogging && Environment.GetEnvironmentVariable("DEEP_RESEARCHER_DEBUG") == "true")
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[DEBUG] {message}");
                Console.ResetColor();
            }
        }

        #endregion

        #region ─── Data Models ─────────────────────────────────────────────────────────

        /// <summary>
        /// Class to track the full research context throughout the process
        /// </summary>
        private class ResearchContext
        {
            // Original inputs
            public string OriginalPrompt { get; set; } = string.Empty;
            public string CurrentPrompt { get; set; } = string.Empty;
            
            // Clarification phase
            public string UnifiedPrompt { get; set; } = string.Empty;
            public List<string> ClarifyingQuestions { get; set; } = new List<string>();
            public string ReadyToProceedMessage { get; set; } = string.Empty;
            public string MainResearchTopic { get; set; } = string.Empty;
            
            // Decomposition phase
            public List<Subtask> Subtasks { get; set; } = new List<Subtask>();
            
            // Research phase
            public List<Dictionary<string, object>> SubtaskSummaries { get; set; } = new List<Dictionary<string, object>>();
            
            // Synthesis phase
            public string DraftAnswer { get; set; } = string.Empty;
            
            // Review phase
            public List<FollowUpSubtask> FollowUpSubtasks { get; set; } = new List<FollowUpSubtask>();
            
            // Refinement phase
            public List<Dictionary<string, object>> FollowUpSummaries { get; set; } = new List<Dictionary<string, object>>();
            
            // Error tracking
            public bool HasErrors => !string.IsNullOrEmpty(ErrorMessage);
            public string ErrorMessage { get; private set; } = string.Empty;
            
            // Helper properties
            public bool HasFollowUpTasks => FollowUpSubtasks?.Count > 0;
            
            public List<string> AllUrls
            {
                get
                {
                    var urls = new List<string>();
                    
                    // Collect URLs from initial research
                    foreach (var summary in SubtaskSummaries)
                    {
                        if (summary.TryGetValue("urls", out var urlsObj) && urlsObj is List<string> urlsList)
                        {
                            urls.AddRange(urlsList);
                        }
                    }
                    
                    // Collect URLs from follow-up research
                    foreach (var summary in FollowUpSummaries)
                    {
                        if (summary.TryGetValue("urls", out var urlsObj) && urlsObj is List<string> urlsList)
                        {
                            urls.AddRange(urlsList);
                        }
                    }
                    
                    return urls.Distinct().ToList();
                }
            }
            
            // Sets an error message
            public void SetError(string message)
            {
                ErrorMessage = message;
            }
        }

        // JSON output models with JsonPropertyName attributes
        public record ClarifierOutput(
            [property: JsonPropertyName("unifiedResearchPrompt")] string? UnifiedResearchPrompt,
            [property: JsonPropertyName("clarifyingQuestions")] List<string>? ClarifyingQuestions,
            [property: JsonPropertyName("readyToProceedMessage")] string? ReadyToProceedMessage
        );

        public record DecomposerOutput(
            [property: JsonPropertyName("subtasks")] List<Subtask>? Subtasks
        );

        public record Subtask(
            [property: JsonPropertyName("id")] string Id,
            [property: JsonPropertyName("description")] string Description
        );

        public record SummarizerOutput(
            [property: JsonPropertyName("subtask_id")] string SubtaskId,
            [property: JsonPropertyName("summary")] string Summary
        );

        public record CombinerOutput(
            [property: JsonPropertyName("final_answer")] string FinalAnswer
        );

        public record ReviewerOutput(
            [property: JsonPropertyName("follow_up_subtasks")] List<FollowUpSubtask>? FollowUpSubtasks
        );

        public record FollowUpSubtask(
            [property: JsonPropertyName("id")] string Id,
            [property: JsonPropertyName("description")] string Description
        );

        public record MiniCombinerOutput(
            [property: JsonPropertyName("updated_answer")] string UpdatedAnswer
        );

        #endregion
    }

    /// <summary>
    /// Extension methods for string operations
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// Truncates a string to a maximum length
        /// </summary>
        public static string Truncate(this string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
        
        /// <summary>
        /// Counts words in a string
        /// </summary>
        public static int CountWords(this string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
                
            return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }
}