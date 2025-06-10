using DeepResearcher.Api.Models;
using DeepResearcher.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DeepResearcher.Api.Controllers
{
    [ApiController]
    [Route("api/research")]
    public class ResearchController : ControllerBase
    {
        private readonly IResearchService _researchService;

        public ResearchController(IResearchService researchService)
        {
            _researchService = researchService;
        }

        [HttpPost("start")]
        public async Task<ActionResult<StartResearchResponse>> StartResearch([FromBody] StartResearchRequest request)
        {
            var sessionId = await _researchService.StartResearchSessionAsync(request.Query);
            return Ok(new StartResearchResponse
            {
                SessionId = sessionId,
                Message = "Research session started successfully"
            });
        }

        [HttpGet("status/{sessionId}")]
        public async Task<ActionResult<ResearchStatusResponse>> GetResearchStatus(string sessionId)
        {
            var status = await _researchService.GetResearchStatusAsync(sessionId);
            return Ok(status);
        }

        [HttpPost("clarify")]
        public async Task<ActionResult<ClarificationResponse>> SubmitClarification([FromBody] ClarificationRequest request)
        {
            var result = await _researchService.SubmitClarificationAsync(request.SessionId, request.ClarificationText);
            return Ok(result);
        }

        [HttpPost("proceed/{sessionId}")]
        public async Task<ActionResult<ResearchStatusResponse>> ProceedWithResearch(string sessionId)
        {
            await _researchService.ProceedToNextPhaseAsync(sessionId);
            var status = await _researchService.GetResearchStatusAsync(sessionId);
            return Ok(status);
        }

        [HttpGet("results/{sessionId}")]
        public async Task<ActionResult<ResearchResultResponse>> GetResearchResults(string sessionId)
        {
            var result = await _researchService.GetFinalResultsAsync(sessionId);
            return Ok(result);
        }

        [HttpPost("feedback")]
        public async Task<ActionResult<FeedbackResponse>> SubmitFeedback([FromBody] FeedbackRequest request)
        {
            var result = await _researchService.SubmitFeedbackAsync(request.SessionId, request.FeedbackText);
            return Ok(result);
        }
    }
}