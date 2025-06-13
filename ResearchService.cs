using DeepResearcher.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DeepResearcher.Api.Models;
using DeepResearcher.Api.Services;

namespace DeepResearcher.Api.Services
{
    public class ResearchService : IResearchService
    {
        private readonly IMemoryCache _cache;
        private readonly Kernel _kernel;
        private readonly TavilyConnector _tavilyConnector;
        private readonly IServiceProvider _serviceProvider;

        public ResearchService(IMemoryCache cache, Kernel kernel, TavilyConnector tavilyConnector, IServiceProvider serviceProvider)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            _tavilyConnector = tavilyConnector ?? throw new ArgumentNullException(nameof(tavilyConnector));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public async Task<string> StartResearchSessionAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("Query cannot be empty", nameof(query));
            }
            
            // Create a new session ID
            string sessionId = Guid.NewGuid().ToString("N");
            
            // Create initial session state
            var sessionState = new ResearchSessionState
            {
                SessionId = sessionId,
                InitialQuery = query,
                CurrentPhase = ResearchPhase.Clarification,
                Status = "Research session started",
                Messages = new List<string> { "Session initialized" }
            };
            
            // Store in cache with a sliding expiration
            _cache.Set(sessionId, sessionState, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(1)
            });
            
            // Check if we have semantic cache hit for similar research
            var researchCache = _serviceProvider.GetRequiredService<ResearchCache>();
            var cachedResult = await researchCache.GetOrCreateResearchAsync(
                query,
                async () => {
                    // Create a new orchestrator for this session
                    var orchestrator = new ResearchOrchestrator(_kernel, _tavilyConnector);
                    sessionState.Orchestrator = orchestrator;
                    
                    // Start the clarification process asynchronously
                    await orchestrator.InitializeResearchAsync(query);
                    return ""; // Actual research happens later
                });
            
            if (cachedResult.IsCacheHit)
            {
                sessionState.Status = "Found similar previous research (will offer as starting point)";
                sessionState.CachedResearch = cachedResult;
            }
            
            // Continue with clarification as normal
            sessionState.ClarificationState = await sessionState.Orchestrator.GetClarificationStateAsync();
            
            return sessionId;
        }

        public async Task<ResearchStatusResponse> GetResearchStatusAsync(string sessionId)
        {
            if (!_cache.TryGetValue(sessionId, out ResearchSessionState session))
            {
                throw new KeyNotFoundException("Research session not found");
            }

            return await Task.FromResult(new ResearchStatusResponse
            {
                SessionId = sessionId,
                CurrentPhase = session.CurrentPhase.ToString(),
                Progress = session.Progress,
                Status = session.Status,
                IsComplete = session.IsComplete,
                Subtasks = session.Subtasks?.Select(s => new ResearchSubtask 
                {
                    Id = s.Id,
                    Description = s.Description,
                    IsComplete = s.IsComplete
                }).ToList() ?? new List<ResearchSubtask>(),
                Messages = session.Messages
            });
        }

        public async Task<ClarificationResponse> SubmitClarificationAsync(string sessionId, string clarificationText)
        {
            if (!_cache.TryGetValue(sessionId, out ResearchSessionState session))
            {
                throw new KeyNotFoundException("Research session not found");
            }

            if (session.CurrentPhase != ResearchPhase.Clarification)
            {
                throw new InvalidOperationException("The session is not in clarification phase");
            }

            await session.Orchestrator.SubmitClarificationAsync(clarificationText);
            session.ClarificationState = await session.Orchestrator.GetClarificationStateAsync();
            
            return new ClarificationResponse
            {
                SessionId = sessionId,
                Questions = session.ClarificationState.Questions,
                NeedsClarification = session.ClarificationState.NeedsClarification,
                Status = session.ClarificationState.ReadyMessage
            };
        }

        public async Task ProceedToNextPhaseAsync(string sessionId)
        {
            if (!_cache.TryGetValue(sessionId, out ResearchSessionState? session) || session == null)
            {
                throw new KeyNotFoundException("Research session not found");
            }
            
            switch (session.CurrentPhase)
            {
                case ResearchPhase.Clarification:
                    session.CurrentPhase = ResearchPhase.Decomposition;
                    session.Status = "Decomposing research topic";
                    
                    // Process asynchronously
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.Orchestrator.DecomposeResearchPromptAsync();
                            session.Subtasks = session.Orchestrator.GetSubtasks();
                            session.CurrentPhase = ResearchPhase.Research;
                            session.Status = "Researching subtasks";
                            
                            // Continue to research phase
                            await session.Orchestrator.ResearchSubtasksAsync(progress =>
                            {
                                session.Progress = progress;
                                session.Messages.Add($"Research progress: {progress}%");
                                return Task.CompletedTask;
                            });
                            
                            // Move to synthesis phase
                            session.CurrentPhase = ResearchPhase.Synthesis;
                            session.Status = "Synthesizing research";
                            
                            await session.Orchestrator.CombineResearchAsync();
                            session.CurrentPhase = ResearchPhase.Review;
                            session.Status = "Reviewing answer for gaps";
                            
                            await session.Orchestrator.ReviewAndRefineAsync();
                            session.CurrentPhase = ResearchPhase.Final;
                            session.Status = "Research complete";
                            session.IsComplete = true;
                        }
                        catch (Exception ex)
                        {
                            session.Status = $"Error: {ex.Message}";
                            session.Messages.Add($"Error occurred: {ex.Message}");
                        }
                    });
                    break;
                    
                case ResearchPhase.Decomposition:
                    // If already in Decomposition, move directly to Research phase
                    session.CurrentPhase = ResearchPhase.Research;
                    session.Status = "Researching subtasks";
                    
                    // Process asynchronously
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Continue research process from this phase
                            await session.Orchestrator.ResearchSubtasksAsync(progress =>
                            {
                                session.Progress = progress;
                                session.Messages.Add($"Research progress: {progress}%");
                                return Task.CompletedTask;
                            });
                            
                            session.CurrentPhase = ResearchPhase.Synthesis;
                            session.Status = "Synthesizing research";
                            
                            await session.Orchestrator.CombineResearchAsync();
                            session.CurrentPhase = ResearchPhase.Review;
                            session.Status = "Reviewing answer for gaps";
                            
                            await session.Orchestrator.ReviewAndRefineAsync();
                            session.CurrentPhase = ResearchPhase.Final;
                            session.Status = "Research complete";
                            session.IsComplete = true;
                        }
                        catch (Exception ex)
                        {
                            session.Status = $"Error: {ex.Message}";
                            session.Messages.Add($"Error occurred: {ex.Message}");
                        }
                    });
                    break;
                    
                case ResearchPhase.Research:
                    // If in Research phase, move to Synthesis
                    session.CurrentPhase = ResearchPhase.Synthesis;
                    session.Status = "Synthesizing research";
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.Orchestrator.CombineResearchAsync();
                            session.CurrentPhase = ResearchPhase.Review;
                            session.Status = "Reviewing answer for gaps";
                            
                            await session.Orchestrator.ReviewAndRefineAsync();
                            session.CurrentPhase = ResearchPhase.Final;
                            session.Status = "Research complete";
                            session.IsComplete = true;
                        }
                        catch (Exception ex)
                        {
                            session.Status = $"Error: {ex.Message}";
                            session.Messages.Add($"Error occurred: {ex.Message}");
                        }
                    });
                    break;
                    
                case ResearchPhase.Synthesis:
                    // If in Synthesis phase, move to Review
                    session.CurrentPhase = ResearchPhase.Review;
                    session.Status = "Reviewing answer for gaps";
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.Orchestrator.ReviewAndRefineAsync();
                            session.CurrentPhase = ResearchPhase.Final;
                            session.Status = "Research complete";
                            session.IsComplete = true;
                        }
                        catch (Exception ex)
                        {
                            session.Status = $"Error: {ex.Message}";
                            session.Messages.Add($"Error occurred: {ex.Message}");
                        }
                    });
                    break;
                    
                case ResearchPhase.Review:
                    // If in Review phase, move to Final
                    session.CurrentPhase = ResearchPhase.Final;
                    session.Status = "Research complete";
                    session.IsComplete = true;
                    break;
                    
                case ResearchPhase.Final:
                    // If already in Final phase, do nothing
                    session.Status = "Research already complete";
                    break;
                    
                default:
                    throw new InvalidOperationException($"Unexpected research phase: {session.CurrentPhase}");
            }
            
            await Task.CompletedTask; // Just to make this method async
        }

        public async Task<ResearchResultResponse> GetFinalResultsAsync(string sessionId)
        {
            if (!_cache.TryGetValue(sessionId, out ResearchSessionState session))
            {
                throw new KeyNotFoundException("Research session not found");
            }

            string answer;
            bool isFinal;
            
            if (session.CurrentPhase == ResearchPhase.Final)
            {
                answer = await session.Orchestrator.GetFinalAnswerAsync();
                isFinal = true;
            }
            else if (session.CurrentPhase == ResearchPhase.Synthesis || session.CurrentPhase == ResearchPhase.Review)
            {
                answer = await session.Orchestrator.GetDraftAnswerAsync();
                isFinal = false;
            }
            else
            {
                answer = "Research is still in progress. No results available yet.";
                isFinal = false;
            }
            
            return new ResearchResultResponse
            {
                SessionId = sessionId,
                Answer = answer,
                WordCount = CountWords(answer),
                Sources = await session.Orchestrator.GetSourcesAsync(),
                IsFinal = isFinal
            };
        }

        public async Task<FeedbackResponse> SubmitFeedbackAsync(string sessionId, string feedbackText)
        {
            if (!_cache.TryGetValue(sessionId, out ResearchSessionState session))
            {
                throw new KeyNotFoundException("Research session not found");
            }

            if (session.CurrentPhase != ResearchPhase.Final && session.CurrentPhase != ResearchPhase.Review)
            {
                throw new InvalidOperationException("Feedback can only be provided in review or final phases");
            }

            await session.Orchestrator.IncorporateFeedbackAsync(feedbackText);
            session.Status = "Feedback incorporated";
            
            return new FeedbackResponse
            {
                SessionId = sessionId,
                Status = "Feedback processed successfully"
            };
        }

        public async Task<string> CancelResearchSessionAsync(string sessionId)
        {
            if (!_cache.TryGetValue(sessionId, out ResearchSessionState session))
            {
                throw new KeyNotFoundException("Research session not found");
            }

            // Remove from cache
            _cache.Remove(sessionId);
            
            return await Task.FromResult($"Research session {sessionId} canceled successfully");
        }
        
        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
                
            return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        
        // Define additional types needed by the service
        private enum ResearchPhase
        {
            Clarification,
            Decomposition,
            Research,
            Synthesis,
            Review,
            Final
        }
        
        private class ResearchSessionState
        {
            public string SessionId { get; set; }
            public string InitialQuery { get; set; }
            public ResearchPhase CurrentPhase { get; set; }
            public ResearchOrchestrator Orchestrator { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public bool IsComplete { get; set; }
            public ClarificationState ClarificationState { get; set; } = new();
            public List<SubtaskState> Subtasks { get; set; } = new();
            public List<string> Messages { get; set; } = new();
            public CachedResearchResult CachedResearch { get; set; }
        }
        
        public class ClarificationState
        {
            public List<string> Questions { get; set; } = new();
            public bool NeedsClarification { get; set; }
            public string ReadyMessage { get; set; }
        }
        
        public class SubtaskState
        {
            public string Id { get; set; }
            public string Description { get; set; }
            public bool IsComplete { get; set; }
        }
    }
}