using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace Shared.Services;

public class MidpointService(HttpClient http, ILogger<MidpointService> logger)
{
    private readonly string basePath = "/midpoint/ws/rest";
    public async Task CreateUser(string name, string email, string password, List<string> groups)
    {
        var user = new
        {
            user = new
            {
                name = email,
                fullName = name,
                emailAddress = email,
                credentials = new
                {
                    password = new
                    {
                        value = new
                        {
                            clearValue = password
                        }
                    }
                }
            }
        };

        var res = await http.PostAsJsonAsync($"{basePath}/users", user);
        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadAsStringAsync();
            logger.LogError("Midpoint create user failed: {Error}", error);
            throw new Exception($"Midpoint error: {error}");
        }

        var location = res.Headers.Location?.ToString();

        if (string.IsNullOrEmpty(location))
            throw new Exception("No Location header returned from midpoint");

        var userOid = location.Split('/').Last();

        foreach (var groupOid in groups)
        {
            await AssignGroup(userOid, groupOid);
        }
    }

    private async Task AssignGroup(string userOid, string groupOid)
    {
        var patch = new
        {
            @object = new
            {
                assignment = new[]
                {
                    new
                    {
                        targetRef = new
                        {
                            oid = groupOid,
                            type = "RoleType"
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Patch, $"{basePath}/users/{userOid}")
        {
            Content = JsonContent.Create(patch)
        };

        var response = await http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            logger.LogError("Assign group failed: {Error}", error);
            throw new Exception($"Midpoint group assign failed: {error}");
        }
    }

    public async Task<List<(string Oid, string Name)>> GetRoles()
    {
        var response = await http.GetAsync($"{basePath}/roles");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var result = new List<(string, string)>();

        foreach (var obj in json.GetProperty("objects").EnumerateArray())
        {
            var oid = obj.GetProperty("oid").GetString();
            var name = obj.GetProperty("name").GetString();

            result.Add((oid!, name!));
        }

        return result;
    }
}
