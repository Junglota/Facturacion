using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Client;

public static class ApiClient
{
    public static async Task<int> ExecuteAsync(string method, string resource, object? body = null)
    {
        var apiKey = Environment.GetEnvironmentVariable("API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Configure API_KEY con la clave de integración establecida en el gateway.");
            return 1;
        }
        using var client = new HttpClient {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("API_URL") ?? "https://localhost:62702").TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "api/v1/" + resource);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
            if (text.Length > 0)
            {
                using var json = JsonDocument.Parse(text);
                Console.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Error de comunicación con la API: {error.Message}");
            return 1;
        }
    }
}
