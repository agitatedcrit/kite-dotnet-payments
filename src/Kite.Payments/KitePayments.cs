using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kite.Payments;

public sealed record KiteOptions(string Origin, string Network, string Asset, string PayTo, string PriceUsd,
    int Decimals, string Facilitator, string UpstreamUrl, string DataDirectory, string DataKey,
    string? UpstreamBearer = null, JsonObject? Extra = null);

public static class Prices
{
    public static string Units(string price, int decimals)
    {
        if (decimals is < 0 or > 18 || !Regex.IsMatch(price, @"^(0|[1-9][0-9]*)(\.[0-9]+)?$")) throw new ArgumentException("Invalid price");
        var p = price.Split('.'); var fraction = p.Length == 2 ? p[1] : "";
        if (fraction.Length > decimals) throw new ArgumentException("Price precision exceeds asset decimals");
        return (BigInteger.Parse(p[0]) * BigInteger.Pow(10, decimals) + BigInteger.Parse("0" + fraction.PadRight(decimals, '0'))).ToString();
    }
}

public sealed class Journal
{
    private readonly string directory;
    private readonly byte[] key;
    public Journal(string directory, string key) { this.directory = Path.GetFullPath(directory); this.key = Convert.FromHexString(key); if (this.key.Length != 32) throw new ArgumentException("DATA_KEY must be 32 bytes"); Directory.CreateDirectory(this.directory); }
    public FileStream Acquire() => new(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public JsonObject Read()
    {
        var path = Path.Combine(directory, "state.enc"); if (!File.Exists(path)) return new JsonObject();
        var bytes = File.ReadAllBytes(path); var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(key, 16); aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
        return JsonNode.Parse(plain)!.AsObject();
    }
    public void Save(JsonObject state)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(state); var output = new byte[plain.Length + 28]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16); aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16));
        var path = Path.Combine(directory, "state.enc"); using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(output); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}

public sealed class KiteMiddleware
{
    private readonly RequestDelegate next;
    private readonly KiteOptions options;
    private readonly HttpClient client;
    private readonly Journal journal;
    private readonly SemaphoreSlim gate = new(1, 1);
    public KiteMiddleware(RequestDelegate next, KiteOptions options, HttpClient client)
    {
        this.next = next; this.options = options; this.client = client;
        if (options.Network is not ("eip155:2366" or "eip155:2368") || !Regex.IsMatch(options.Asset, "^0x[0-9a-fA-F]{40}$") || !Regex.IsMatch(options.PayTo, "^0x[0-9a-fA-F]{40}$") || options.PayTo == "0x" + new string('0', 40)) throw new ArgumentException("Invalid network or asset/payTo");
        if (new Uri(options.Facilitator).Scheme != "https" || new Uri(options.UpstreamUrl).Scheme != "https") throw new ArgumentException("HTTPS required");
        journal = new Journal(options.DataDirectory, options.DataKey);
    }
    public JsonObject Requirement() => new() { ["scheme"] = "exact", ["network"] = options.Network, ["asset"] = options.Asset, ["amount"] = Prices.Units(options.PriceUsd, options.Decimals), ["payTo"] = options.PayTo, ["maxTimeoutSeconds"] = 120, ["extra"] = options.Extra?.DeepClone() ?? new JsonObject() };
    public async Task<bool> InspectSupportAsync()
    {
        using var response = await client.GetAsync(options.Facilitator.TrimEnd('/') + "/supported");
        response.EnsureSuccessStatusCode(); var supported = JsonNode.Parse(await Bounded(response.Content, 1048576));
        return supported?["kinds"]?.AsArray().Any(k => k?["x402Version"]?.GetValue<int>() == 2 && k?["scheme"]?.GetValue<string>() == "exact" && k?["network"]?.GetValue<string>() == options.Network) == true;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private async Task<JsonObject> Call(string endpoint, JsonObject wire)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Facilitator.TrimEnd('/') + "/" + endpoint) { Content = new StringContent(wire.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        response.EnsureSuccessStatusCode(); var bytes = await Bounded(response.Content, 1048576); return JsonNode.Parse(bytes)!.AsObject();
    }
    private static async Task<byte[]> Bounded(HttpContent content, int maximum)
    {
        await using var input = await content.ReadAsStreamAsync(); using var output = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = await input.ReadAsync(buffer)) > 0) { if (output.Length + n > maximum) throw new InvalidDataException("Response limit"); output.Write(buffer, 0, n); } return output.ToArray();
    }
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1")) { await next(context); return; }
        if (context.Request.Path != "/v1/sample/query" || context.Request.Method != "POST") { context.Response.StatusCode = 404; return; }
        if (context.Request.ContentType?.StartsWith("application/json") != true || context.Request.ContentLength > 1048576) { context.Response.StatusCode = 415; return; }
        JsonObject input;
        try { using var requestContent = new StreamContent(context.Request.Body); input = JsonNode.Parse(await Bounded(requestContent, 1048576))!.AsObject(); }
        catch { await Error(context, 400, "invalid_json"); return; }
        var id = input["operation_id"]?.GetValue<string>() ?? ""; if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{4,100}$")) { await Error(context, 400, "operation_id_required"); return; }
        var terms = Requirement(); var resource = new JsonObject { ["url"] = options.Origin.TrimEnd('/') + context.Request.Path, ["mimeType"] = "application/json" };
        if (!context.Request.Headers.TryGetValue("PAYMENT-SIGNATURE", out var header))
        {
            var challenge = new JsonObject { ["x402Version"] = 2, ["resource"] = resource, ["accepts"] = new JsonArray(terms) };
            context.Response.Headers["PAYMENT-REQUIRED"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(challenge.ToJsonString())); context.Response.StatusCode = 402; await context.Response.WriteAsJsonAsync(challenge); return;
        }
        JsonObject payload;
        try { if (header.ToString().Length > 20000) throw new FormatException(); payload = JsonNode.Parse(Convert.FromBase64String(header.ToString()))!.AsObject(); }
        catch { await Error(context, 400, "invalid_payment"); return; }
        if (payload["x402Version"]?.GetValue<int>() != 2 || !JsonNode.DeepEquals(payload["accepted"], terms) || payload["resource"]?["url"]?.GetValue<string>() != resource["url"]!.GetValue<string>()) { await Error(context, 402, "payment_terms_mismatch"); return; }
        await gate.WaitAsync();
        try
        {
            using var processLock = journal.Acquire(); var state = journal.Read(); var requestHash = Hash(input.ToJsonString()); var proofHash = Hash(payload.ToJsonString());
            if (state[id] is JsonObject existing) { await Error(context, 409, existing["request_hash"]?.GetValue<string>() == requestHash ? "original_operation_requires_operator_recovery" : "operation_conflict"); return; }
            if (state.Any(kv => kv.Value?["proof_hash"]?.GetValue<string>() == proofHash)) { await Error(context, 409, "payment_replay"); return; }
            var attempt = new JsonObject { ["request_hash"] = requestHash, ["proof_hash"] = proofHash, ["payment_state"] = "UNKNOWN", ["fulfillment_state"] = "NOT_STARTED" }; state[id] = attempt; journal.Save(state);
            var wire = new JsonObject { ["x402Version"] = 2, ["paymentPayload"] = payload, ["paymentRequirements"] = terms };
            JsonObject verified; try { verified = await Call("verify", wire); } catch { await Error(context, 503, "verification_unknown"); return; }
            var payer = verified["payer"]?.GetValue<string>() ?? "";
            if (verified["isValid"]?.GetValue<bool>() != true || !Regex.IsMatch(payer, "^0x[0-9a-fA-F]{40}$")) { attempt["payment_state"] = "FAILED"; journal.Save(state); await Error(context, 402, "verification_rejected"); return; }
            attempt["payer"] = payer.ToLowerInvariant(); attempt["payment_state"] = "VERIFIED"; journal.Save(state);
            JsonNode result;
            try
            {
                using var upstream = new HttpRequestMessage(HttpMethod.Post, options.UpstreamUrl) { Content = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json") };
                if (options.UpstreamBearer is not null) upstream.Headers.Authorization = new("Bearer", options.UpstreamBearer);
                using var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException();
                result = JsonNode.Parse(await Bounded(response.Content, 2097152)) ?? throw new InvalidDataException();
                if (result is not (JsonObject or JsonArray)) throw new InvalidDataException();
            }
            catch { attempt["fulfillment_state"] = "FAILED"; journal.Save(state); await Error(context, 502, "business_failed_no_settle"); return; }
            attempt["result"] = result; attempt["fulfillment_state"] = "READY"; attempt["payment_state"] = "UNKNOWN"; journal.Save(state);
            JsonObject settled; try { settled = await Call("settle", wire); if (settled["success"]?.GetValue<bool>() != true || settled["network"]?.GetValue<string>() != options.Network || string.IsNullOrEmpty(settled["transaction"]?.GetValue<string>())) throw new InvalidDataException(); }
            catch { await Error(context, 503, "payment_unknown_do_not_repurchase"); return; }
            attempt["payment_state"] = "CONFIRMED"; attempt["receipt"] = new JsonObject { ["transaction"] = settled["transaction"]!.DeepClone(), ["network"] = options.Network, ["success"] = true }; journal.Save(state);
            context.Response.Headers["PAYMENT-RESPONSE"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(attempt["receipt"]!.ToJsonString()));
            await context.Response.WriteAsJsonAsync(new { operation_id = id, result, payment_state = "CONFIRMED" });
        }
        catch (IOException) { await Error(context, 503, "writer_busy"); }
        finally { gate.Release(); }
    }
    private static async Task Error(HttpContext c, int code, string message) { c.Response.StatusCode = code; await c.Response.WriteAsJsonAsync(new { error = message }); }
}

public static class KiteExtensions
{
    public static IServiceCollection AddKitePayments(this IServiceCollection services, KiteOptions options) { services.AddSingleton(options); services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) }); return services; }
    public static IApplicationBuilder UseKitePayments(this IApplicationBuilder app) => app.UseMiddleware<KiteMiddleware>();
}
