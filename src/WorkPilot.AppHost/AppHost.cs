var builder = DistributedApplication.CreateBuilder(args);

// Local Postgres container for development. In production this same role
// is played by Supabase's own Postgres container (see docker-compose.yml
// at the repo root) — both point at the same database, per
// docs/specs/0001-stack-architecture.md.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();

var workpilotDb = postgres.AddDatabase("workpilotdb");

var api = builder.AddProject<Projects.WorkPilot_Api>("api")
    .WithReference(workpilotDb)
    .WaitFor(workpilotDb);

builder.AddProject<Projects.WorkPilot_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
