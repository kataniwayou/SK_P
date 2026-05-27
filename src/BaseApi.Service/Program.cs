using BaseApi.Core.DependencyInjection;
using BaseApi.Service;
using BaseApi.Service.Composition;

var builder = WebApplication.CreateBuilder(args);
builder.AddBaseApiObservability(builder.Configuration);
builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
builder.Services.AddAppFeatures();

var app = builder.Build();
app.UseBaseApi();
app.MapControllers();
app.Run();

// Marker type for WebApplicationFactory<Program> in tests (Phase 1 D-10).
// Top-level statements generate an internal Program by default; this partial class
// declaration promotes it to public so tests/BaseApi.Tests can target it.
public partial class Program { }
