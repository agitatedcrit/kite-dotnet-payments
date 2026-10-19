using Kite.Payments;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
string Env(string k) => Environment.GetEnvironmentVariable(k) ?? throw new Exception($"Missing {k}");
var builder = WebApplication.CreateBuilder(args);
var options = new KiteOptions(Env("PUBLIC_ORIGIN"), Env("KITE_NETWORK"), Env("ASSET"), Env("PAY_TO"), Env("PRICE_USD"), int.Parse(Env("ASSET_DECIMALS")), Env("FACILITATOR_URL"), Env("UPSTREAM_URL"), Environment.GetEnvironmentVariable("DATA_DIR") ?? ".data", Env("DATA_KEY"), Environment.GetEnvironmentVariable("UPSTREAM_BEARER"), JsonNode.Parse(Environment.GetEnvironmentVariable("ASSET_EXTRA_JSON") ?? "{}")!.AsObject());
builder.Services.AddKitePayments(options);
var app = builder.Build(); app.UseKitePayments(); app.MapGet("/healthz", () => new { status = "ok", mode = "x402-v2", live_verified = false });
app.MapGet("/admin/operations/{id}", (HttpRequest req, string id) => {
    var expected = "Bearer " + Env("OPERATOR_TOKEN"); var got = req.Headers.Authorization.ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(got))) return Results.Unauthorized();
    var journal = new Journal(options.DataDirectory, options.DataKey); var item = journal.Read()[id];
    return item is null ? Results.NotFound() : Results.Json(item);
});
app.Run();
