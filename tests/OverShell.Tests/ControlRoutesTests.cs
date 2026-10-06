using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OverShell.Core.Integrations;
using Xunit;

namespace OverShell.Tests;

public class ControlRoutesTests
{
    [Fact]
    public async Task Without_a_control_surface_the_control_routes_answer_403_and_the_rest_still_works()
    {
        using var endpoint = new IntegrationEndpoint();
        endpoint.Start();
        endpoint.TabsProvider = () => [new TabSummary("t1", "one", null, "Unknown", "proj", "no signal yet")];
        using var http = new HttpClient();

        var health = await http.GetAsync($"{endpoint.BaseUrl}/v1/health");
        Assert.Equal(200, (int)health.StatusCode);

        var unauthorised = await http.GetAsync($"{endpoint.BaseUrl}/v1/tabs/t1/screen");
        Assert.Equal(401, (int)unauthorised.StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        foreach (var (method, path) in new[] { ("GET", "/v1/tabs/t1"), ("GET", "/v1/tabs/t1/screen"), ("GET", "/v1/tabs/t1/wait"), ("POST", "/v1/tabs"), ("POST", "/v1/tabs/t1/input"), ("POST", "/v1/tabs/t1/reply"), ("DELETE", "/v1/tabs/t1") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.BaseUrl + path);
            if (method == "POST")
            {
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request);
            Assert.Equal(403, (int)response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
            Assert.Contains("endpoint.control", body?["error"]?.GetValue<string>() ?? string.Empty);
        }

        // The summary list and the command poll are not control routes.
        var tabs = JsonNode.Parse(await http.GetStringAsync($"{endpoint.BaseUrl}/v1/tabs")) as JsonObject;
        Assert.Equal("one", tabs?["tabs"]?[0]?["label"]?.GetValue<string>());
        var poll = JsonNode.Parse(await http.GetStringAsync($"{endpoint.BaseUrl}/v1/tabs/t1/commands?after=0&holdMs=0")) as JsonObject;
        Assert.NotNull(poll?["commands"]);
    }
}
