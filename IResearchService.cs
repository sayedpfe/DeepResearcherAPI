using DeepResearcher.Api.Models;
using System.Threading.Tasks;

namespace DeepResearcher.Api.Services
{
    public interface IResearchService
    {
        Task<string> StartResearchSessionAsync(string query);
        Task<ResearchStatusResponse> GetResearchStatusAsync(string sessionId);
        Task<ClarificationResponse> SubmitClarificationAsync(string sessionId, string clarificationText);
        Task ProceedToNextPhaseAsync(string sessionId);
        Task<ResearchResultResponse> GetFinalResultsAsync(string sessionId);
        Task<FeedbackResponse> SubmitFeedbackAsync(string sessionId, string feedbackText);
        Task<string> CancelResearchSessionAsync(string sessionId);
    }
}