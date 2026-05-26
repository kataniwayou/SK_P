// BaseApi.Service — application entry point.
//
// Phase 1 scaffold per CONTEXT.md D-10. The host boots, registers controllers
// (none exist yet — Phase 8 adds the 5 concrete controllers), and runs. Every
// HTTP path returns 404 until later phases register routes.
//
// Phase 7 will replace the body with:
//   builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
//   app.UseBaseApi();
//   app.MapControllers();
// (See .planning/research/ARCHITECTURE.md Pattern 1 — Composition Root.)

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

// Marker type for WebApplicationFactory<Program> in Phase 8 integration tests.
// (Top-level statements generate an internal Program class by default; partial
// class declaration here promotes it to public so the test project can target it.)
public partial class Program { }
