using System.Collections.Generic;

namespace DeepResearcher.Api.Models
{
    public class StartResearchRequest
    {
        public string Query { get; set; }
    }

    public class StartResearchResponse
    {
        public string SessionId { get; set; }
        public string Message { get; set; }
    }

    public class ClarificationRequest
    {
        public string SessionId { get; set; }
        public string ClarificationText { get; set; }
    }

    public class ClarificationResponse
    {
        public string SessionId { get; set; }
        public List<string> Questions { get; set; } = new();
        public bool NeedsClarification { get; set; }
        public string Status { get; set; }
    }

    public class ResearchStatusResponse
    {
        public string SessionId { get; set; }
        public string CurrentPhase { get; set; }
        public int Progress { get; set; } // 0-100%
        public string Status { get; set; }
        public bool IsComplete { get; set; }
        public List<ResearchSubtask> Subtasks { get; set; } = new();
        public List<string> Messages { get; set; } = new();
    }

    public class ResearchSubtask
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public bool IsComplete { get; set; }
    }

    public class ResearchResultResponse
    {
        public string SessionId { get; set; }
        public string Answer { get; set; }
        public int WordCount { get; set; }
        public List<string> Sources { get; set; } = new();
        public bool IsFinal { get; set; }
    }

    public class FeedbackRequest
    {
        public string SessionId { get; set; }
        public string FeedbackText { get; set; }
    }

    public class FeedbackResponse
    {
        public string SessionId { get; set; }
        public string Status { get; set; }
    }
}