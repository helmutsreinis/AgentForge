namespace AgentForge.Host.Http;

internal static class OpenApiDocumentFactory
{
    public static object Create() => new
    {
        openapi = "3.1.0",
        info = new { title = "AgentForge Control Plane", version = "1.0.0" },
        servers = new[] { new { url = "/", description = "Current authenticated AgentForge host" } },
        paths = new Dictionary<string, object>
        {
            ["/api/v1/tasks"] = new
            {
                post = new
                {
                    operationId = "createTask",
                    summary = "Persist a bounded planned task",
                    security = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
                    parameters = new[]
                    {
                        new { name = "Idempotency-Key", @in = "header", required = true,
                            schema = new { type = "string", maxLength = 256 } },
                        new { name = "X-Correlation-Id", @in = "header", required = false,
                            schema = new { type = "string", maxLength = 128 } },
                    },
                    responses = Responses(created: true),
                },
            },
            ["/api/v1/tasks/{taskId}"] = new
            {
                get = new
                {
                    operationId = "getTask",
                    security = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
                    responses = Responses(created: false),
                },
            },
            ["/api/v1/tasks/{taskId}/events"] = new
            {
                get = new
                {
                    operationId = "streamTaskEvents",
                    summary = "Read authenticated task progress as Server-Sent Events",
                    security = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "text/event-stream" },
                        ["401"] = ProblemResponse(),
                        ["404"] = ProblemResponse(),
                    },
                },
            },
        },
        components = new
        {
            securitySchemes = new
            {
                bearerAuth = new { type = "http", scheme = "bearer", bearerFormat = "opaque-local-admin" },
            },
            schemas = new
            {
                ProblemDetails = new
                {
                    type = "object",
                    required = new[] { "type", "title", "status", "correlationId" },
                    additionalProperties = false,
                },
            },
        },
    };

    private static Dictionary<string, object> Responses(bool created) => new()
    {
        [created ? "201" : "200"] = new { description = created ? "Created or exact replay" : "Current snapshot" },
        ["400"] = ProblemResponse(),
        ["401"] = ProblemResponse(),
        ["403"] = ProblemResponse(),
        ["409"] = ProblemResponse(),
    };

    private static object ProblemResponse() => new
    {
        description = "RFC Problem Details with correlation identity",
        content = new Dictionary<string, object>
        {
            ["application/problem+json"] = new
            {
                schema = new { @ref = "#/components/schemas/ProblemDetails" },
            },
        },
    };
}
