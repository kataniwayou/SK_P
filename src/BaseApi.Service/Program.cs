using BaseApi.Core.DependencyInjection;
using BaseApi.Service;
using BaseApi.Service.Composition;

var builder = WebApplication.CreateBuilder(args);
builder.AddBaseApiObservability(builder.Configuration);
builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
builder.Services.AddBaseApiMessaging(builder.Configuration);   // Phase 19 MSG-WEBAPI-01: publish-only bus join.
builder.Services.AddAppFeatures();
builder.Services.AddBaseApiFallbackHandler();   // Phase 14 D-04: catch-all LAST, after all domain handlers.

var app = builder.Build();
app.UseBaseApi();
app.MapControllers();
app.Run();

// Marker type for WebApplicationFactory<Program> in tests (Phase 1 D-10).
// Top-level statements generate an internal Program by default; this partial class
// declaration promotes it to public so tests/BaseApi.Tests can target it.
public partial class Program { }
