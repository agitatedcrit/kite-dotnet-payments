using Kite.Payments;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

int passed = 0;
void Check(bool result, string label) { if (!result) throw new Exception(label); passed++; }
Check(Prices.Units("9007199254740993.123456", 6) == "9007199254740993123456", "arbitrary precision");
try { Prices.Units("0.0000001", 6); throw new Exception("precision accepted"); } catch (ArgumentException) { passed++; }
foreach (var mode in new[] { "success", "invalid_signature", "upstream_500", "bad_json", "unknown", "wrong_network" })
{
    var folder = Path.Combine(Path.GetTempPath(), "kite-dotnet-" + Guid.NewGuid()); var events = new List<string>();
    var options = new KiteOptions("https://seller.example", "eip155:2368", "0x" + new string('b', 40), "0x" + new string('c', 40), "0.01", 6, "https://facilitator.example/v2", "https://upstream.example/query", folder, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    var handler = new Stub(mode, events); var middleware = new KiteMiddleware(_ => Task.CompletedTask, options, new HttpClient(handler));
    async Task<DefaultHttpContext> Request(bool paid, string id = "purchase-one", int value = 1)
    {
        var context = new DefaultHttpContext(); context.Request.Path = "/v1/sample/query"; context.Request.Method = "POST"; context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new JsonObject { ["operation_id"] = id, ["value"] = value }.ToJsonString())); context.Response.Body = new MemoryStream();
        if (paid)
        {
            var terms = middleware.Requirement(); if (mode == "wrong_network") terms["network"] = "eip155:1";
            var payload = new JsonObject { ["x402Version"] = 2, ["accepted"] = terms, ["resource"] = new JsonObject { ["url"] = "https://seller.example/v1/sample/query" }, ["payload"] = new JsonObject { ["signature"] = "synthetic-test-only" } };
            context.Request.Headers["PAYMENT-SIGNATURE"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        }
        await middleware.InvokeAsync(context); return context;
    }
    try
    {
        var challenge = await Request(false); Check(challenge.Response.StatusCode == 402 && events.Count == 0, mode + " quote");
        var response = await Request(true);
        if (mode == "success")
        {
            Check(response.Response.StatusCode == 200, "success response"); Check(events.SequenceEqual(new[] { "verify", "business", "settle" }), "correct settlement order");
            var repeated = await Request(true); Check(repeated.Response.StatusCode == 409 && events.Count == 3, "idempotency");
            Check((await Request(true, value: 2)).Response.StatusCode == 409, "body conflict");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "state.enc"))).Contains("answer"), "encrypted result");
        }
        else if (mode == "unknown") { Check(response.Response.StatusCode == 503, "unknown returned"); Check((await Request(true)).Response.StatusCode == 409, "unknown no retry"); Check(events.Count(x => x == "settle") == 1, "settle exactly one attempt"); }
        else if (mode is "upstream_500" or "bad_json") { Check(response.Response.StatusCode == 502, "upstream failure"); Check(!events.Contains("settle"), "no early settle"); }
        else { Check(response.Response.StatusCode == 402, "invalid payment refused"); Check(!events.Contains("business"), "no unauthorized business"); }
    }
    finally { Directory.Delete(folder, true); }
}
Console.WriteLine($"PASS {passed} assertions; E1 only, no live payment");

sealed class Stub(string mode, List<string> events) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string json; var status = HttpStatusCode.OK;
        if (request.RequestUri!.AbsolutePath.EndsWith("verify")) { events.Add("verify"); json = new JsonObject { ["isValid"] = mode != "invalid_signature", ["payer"] = "0x" + new string('a', 40) }.ToJsonString(); }
        else if (request.RequestUri.AbsolutePath.EndsWith("settle")) { events.Add("settle"); if (mode == "unknown") throw new HttpRequestException("response lost"); json = "{\"success\":true,\"transaction\":\"mock-settlement\",\"network\":\"eip155:2368\"}"; }
        else { events.Add("business"); status = mode == "upstream_500" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK; json = mode == "bad_json" ? "invalid" : "{\"answer\":42}"; }
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
