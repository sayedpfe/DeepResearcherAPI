using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DeepResearcher
{
    public class TavilySearchResult
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class TavilyResponse
    {
        [JsonPropertyName("answer")]
        public string Answer { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public List<TavilySearchResult> Results { get; set; } = new();
    }

    public class TavilyConnector
    {
        private readonly HttpClient _client;

        public TavilyConnector(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentNullException(nameof(apiKey));

            _client = new HttpClient { BaseAddress = new Uri("https://api.tavily.com/") };
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public async Task<TavilyResponse> SearchAsync(string query)
        {
            var payload = new
            {
                query = query,
                search_depth = "advanced",
                include_answer = true
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, "search")
            {
                Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
            };

            using var response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var tavilyResponse = JsonSerializer.Deserialize<TavilyResponse>(json, options)
                                 ?? throw new InvalidOperationException("Invalid Tavily response.");

            return tavilyResponse;
        }
    }
}