using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PogSplit.TwitchBridge;

/// <summary>
/// HTTP calls to the PogSplit Extension Backend Service. The protocol is described in this component's README.
/// </summary>
public class EbsClient : IDisposable
{
    private readonly HttpClient http;

    public string BaseUrl { get; }

    public EbsClient(string baseUrl, TimeSpan timeout)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PogSplit-TwitchBridge/1.0");
    }

    public async Task<SendResult> PutStateAsync(string token, BridgeMessage message, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BaseUrl + "/v1/state")
        {
            Content = Json(message),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Lets the EBS move timer.at onto its own clock, whatever the PC's clock says.
        request.Headers.Add("X-Sent-At", MessageBuilder.NowMs().ToString(CultureInfo.InvariantCulture));

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return SendResult.Ok;
            }

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? SendResult.Unauthorized
                : SendResult.Retry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return SendResult.Retry;
        }
    }

    /// <summary>
    /// Stores the run history for the overlay's history tabs, or removes it when <paramref name="history"/> is null
    /// (the streamer stopped sharing it).
    /// </summary>
    public async Task<SendResult> PutHistoryAsync(string token, HistoryMessage history, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(history == null ? HttpMethod.Delete : HttpMethod.Put, BaseUrl + "/v1/history")
        {
            Content = history == null ? null : Json(history),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return SendResult.Ok;
            }

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? SendResult.Unauthorized
                : SendResult.Retry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return SendResult.Retry;
        }
    }

    public async Task<DeviceCode> StartPairingAsync(CancellationToken cancel)
    {
        using HttpResponseMessage response = await http.PostAsync(BaseUrl + "/v1/device", Json(new { }), cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await Read<DeviceCode>(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the pairing once the streamer has entered the code, or null while it is still pending.
    /// Throws <see cref="PairingFailedException"/> when the code expired or was refused.
    /// </summary>
    public async Task<Pairing> PollPairingAsync(string deviceCode, CancellationToken cancel)
    {
        using HttpResponseMessage response = await http.PostAsync(BaseUrl + "/v1/device/token", Json(new { device_code = deviceCode }), cancel).ConfigureAwait(false);
        if (response.StatusCode == (HttpStatusCode)428)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new PairingFailedException("The code expired or was refused. Try connecting again.");
        }

        response.EnsureSuccessStatusCode();
        return await Read<Pairing>(response).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BaseUrl + "/v1/device/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Revoking is best effort; the local token is deleted either way.
        }
    }

    public void Dispose()
    {
        http.Dispose();
    }

    private static StringContent Json(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body);
    }
}

public enum SendResult
{
    Ok,
    Retry,
    Unauthorized,
}

public class DeviceCode
{
    [JsonPropertyName("device_code")]
    public string Code { get; set; }

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; }

    [JsonPropertyName("interval")]
    public int IntervalSeconds { get; set; } = 3;

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; set; } = 600;
}

public class Pairing
{
    [JsonPropertyName("token")]
    public string Token { get; set; }

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; }

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; }
}

public class PairingFailedException : Exception
{
    public PairingFailedException(string message)
        : base(message)
    {
    }
}
