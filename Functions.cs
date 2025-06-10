using System.Threading.Tasks;

namespace DeepResearcher
{
    public static class Functions
    {
        public static async Task RegisterAsync(Microsoft.SemanticKernel.Kernel kernel)
        {
            // No-op: plugin registration is now in Program.cs
            await Task.CompletedTask;
        }
    }
}